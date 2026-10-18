using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class APRTSceneLightTrackerTests
    {
        readonly List<GameObject> m_Owners = new List<GameObject>();
        readonly List<Light> m_MutedLights = new List<Light>();
        readonly Vector4[] m_Colors = new Vector4[4];
        readonly Vector4[] m_Directions = new Vector4[4];
        static readonly Action<Vector4[], Vector4[]> s_Collect = (Action<Vector4[], Vector4[]>)Delegate.CreateDelegate(
            typeof(Action<Vector4[], Vector4[]>),
            typeof(APRTRelightPass).GetMethod("CollectReferenceDirectionalLights", BindingFlags.Static | BindingFlags.NonPublic));

        [SetUp]
        public void SetUp()
        {
            APRTRelightPass.Cleanup();
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (light.type == LightType.Directional && light.enabled)
                {
                    m_MutedLights.Add(light);
                    light.enabled = false;
                }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var owner in m_Owners) if (owner != null) Object.DestroyImmediate(owner);
            foreach (var light in m_MutedLights) if (light != null) light.enabled = true;
            m_Owners.Clear(); m_MutedLights.Clear();
            APRTRelightPass.Cleanup();
        }

        [Test]
        public void MoreThanFourLightsPreserveNativeOrderThroughActivationAndMembershipChanges()
        {
            var lights = new Light[6];
            for (int i = 0; i < lights.Length; ++i) lights[i] = CreateLight(i);
            AssertMatchesNativeOrder();
            lights[0].enabled = false;
            AssertMatchesNativeOrder();
            lights[1].gameObject.SetActive(false);
            AssertMatchesNativeOrder();
            lights[0].enabled = true;
            lights[1].gameObject.SetActive(true);
            AssertMatchesNativeOrder();
            lights[2].type = LightType.Point;
            AssertMatchesNativeOrder();
            lights[2].type = LightType.Directional;
            AssertMatchesNativeOrder();
            Object.DestroyImmediate(lights[1].gameObject);
            AssertMatchesNativeOrder();
            CreateLight(7);
            AssertMatchesNativeOrder();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HideFlagsChangesMatchNativeQueryWithoutChangingOtherLightProperties(bool onGameObject)
        {
            var light = CreateLight(0);
            Object target = onGameObject ? light.gameObject : light;
            target.hideFlags = HideFlags.DontSave;
            AssertMatchesNativeOrder();
            target.hideFlags = HideFlags.None;
            AssertMatchesNativeOrder();
            target.hideFlags = HideFlags.DontSave;
            AssertMatchesNativeOrder();
            target.hideFlags = HideFlags.None;
            AssertMatchesNativeOrder();
            target.hideFlags = HideFlags.HideAndDontSave;
            AssertMatchesNativeOrder();
            target.hideFlags = HideFlags.None;
            AssertMatchesNativeOrder();
        }

        [Test]
        public void AnimatedHiddenLightDoesNotAllocateOrRepeatSceneQueries()
        {
            var light = CreateLight(0);
            light.hideFlags = HideFlags.HideAndDontSave;
            var transform = light.transform;
            for (int i = 0; i < 16; ++i)
            {
                light.intensity = i + 1;
                transform.localRotation = Quaternion.Euler(0, i, 0);
                s_Collect(m_Colors, m_Directions);
            }
            using var recorder = new APRTAllocationRecorder();
            recorder.Begin();
            for (int i = 0; i < 64; ++i)
            {
                light.intensity = i + 1;
                transform.localRotation = Quaternion.Euler(0, i, 0);
                s_Collect(m_Colors, m_Directions);
            }
            int allocations = recorder.End();
            Assert.That(allocations, Is.Zero, "Changes to an excluded light must not allocate new query arrays.");
            light.hideFlags = HideFlags.None;
            AssertMatchesNativeOrder();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConvertingPointLightToDirectionalReconcilesVisibilityChanges(bool initiallyHidden)
        {
            var light = CreateLight(0);
            light.type = LightType.Point;
            light.gameObject.hideFlags = initiallyHidden ? HideFlags.DontSave : HideFlags.None;
            AssertMatchesNativeOrder();
            light.gameObject.hideFlags = initiallyHidden ? HideFlags.None : HideFlags.DontSave;
            light.type = LightType.Directional;
            AssertMatchesNativeOrder();

            light.type = LightType.Point;
            AssertMatchesNativeOrder();
            light.gameObject.hideFlags = HideFlags.None;
            light.hideFlags = HideFlags.DontSave;
            light.type = LightType.Directional;
            AssertMatchesNativeOrder();
            light.hideFlags = HideFlags.None;
            AssertMatchesNativeOrder();
        }

        [Test]
        public void ParentActivationAndTrackerRecreationPreserveNativeInputs()
        {
            var parent = new GameObject("APRT light parent");
            m_Owners.Add(parent);
            var light = CreateLight(0);
            light.transform.SetParent(parent.transform);
            AssertMatchesNativeOrder();
            parent.SetActive(false);
            AssertMatchesNativeOrder();
            parent.SetActive(true);
            AssertMatchesNativeOrder();
            APRTRelightPass.Cleanup();
            AssertMatchesNativeOrder();
        }

        [Test]
        public void MissingSkySunDoesNotAllocateAndFindsNewInactiveDirectionalLight()
        {
            var find = (Func<Light>)Delegate.CreateDelegate(typeof(Func<Light>),
                typeof(APRTRelightPass).GetMethod("FindSkySun", BindingFlags.Static | BindingFlags.NonPublic));
            var previousSun = RenderSettings.sun;
            var changedTypes = new List<Light>();
            using var recorder = new APRTAllocationRecorder();
            try
            {
                foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (light.type == LightType.Directional)
                    {
                        changedTypes.Add(light);
                        light.type = LightType.Point;
                    }
                RenderSettings.sun = null;
                APRTRelightPass.Cleanup();
                Assert.That(find(), Is.Null);
                for (int i = 0; i < 16; ++i) find();

                recorder.Begin();
                for (int i = 0; i < 64; ++i) find();
                int allocations = recorder.End();
                Assert.That(allocations, Is.Zero, "An absent sun must not trigger new scene-query arrays.");

                var inactiveSun = CreateLight(0);
                inactiveSun.enabled = false;
                inactiveSun.gameObject.SetActive(false);
                Assert.That(find(), Is.SameAs(inactiveSun), "A newly created disabled/inactive sun still orients the atmosphere.");
            }
            finally
            {
                foreach (var light in changedTypes) if (light != null) light.type = LightType.Directional;
                RenderSettings.sun = previousSun;
            }
        }

        Light CreateLight(int index)
        {
            var owner = new GameObject($"APRT tracked directional {index}");
            m_Owners.Add(owner);
            var light = owner.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color((index + 1) / 10f, .2f, .3f);
            light.intensity = index + 1;
            light.transform.rotation = Quaternion.Euler(index * 11, index * 17, 0);
            return light;
        }

        void AssertMatchesNativeOrder()
        {
            s_Collect(m_Colors, m_Directions);
            int count = 0;
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (!light.isActiveAndEnabled || light.type != LightType.Directional) continue;
                Vector4 color = light.color.linear * light.intensity;
                color.w = 1;
                Vector3 direction = -light.transform.forward;
                Assert.That(m_Colors[count], Is.EqualTo(color), $"Native scene order differs at light {count}: {light.name}.");
                Assert.That(m_Directions[count], Is.EqualTo(new Vector4(direction.x, direction.y, direction.z, 0)));
                if (++count == m_Colors.Length) break;
            }
            for (int i = count; i < m_Colors.Length; ++i)
            {
                Assert.That(m_Colors[i], Is.EqualTo(Vector4.zero), "A hidden, inactive or destroyed light must not remain in the inputs.");
                Assert.That(m_Directions[i], Is.EqualTo(Vector4.zero));
            }
        }
    }
}
