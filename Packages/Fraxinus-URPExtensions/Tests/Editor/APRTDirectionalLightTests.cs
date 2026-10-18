using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal.Tests
{
    public class APRTDirectionalLightTests
    {
        static readonly Action<Vector4[], Vector4[]> s_Collect = (Action<Vector4[], Vector4[]>)Delegate.CreateDelegate(
            typeof(Action<Vector4[], Vector4[]>),
            typeof(APRTRelightPass).GetMethod("CollectReferenceDirectionalLights", BindingFlags.Static | BindingFlags.NonPublic));

        [Test]
        public void LiveSceneDirectionalInputsFollowCreationRemovalAndPropertyChanges()
        {
            var muted = MuteExistingDirectionalLights();
            var owners = new List<GameObject>();
            var colors = new Vector4[4];
            var directions = new Vector4[4];
            try
            {
                AssertMatchesScene(colors, directions);
                var first = CreateLight(owners, "APRT dynamic directional", Color.red);
                AssertMatchesScene(colors, directions);
                var second = CreateLight(owners, "APRT baked masked directional", Color.green);
                second.gameObject.layer = 31;
                second.cullingMask = 0;
                second.lightmapBakeType = LightmapBakeType.Baked;
                AssertMatchesScene(colors, directions);

                first.intensity = 2.5f;
                first.color = new Color(.2f, .4f, .6f);
                first.transform.rotation = Quaternion.Euler(15, 40, 70);
                AssertMatchesScene(colors, directions);
                first.enabled = false;
                AssertMatchesScene(colors, directions);
                first.enabled = true;
                first.gameObject.SetActive(false);
                AssertMatchesScene(colors, directions);
                first.gameObject.SetActive(true);
                AssertMatchesScene(colors, directions);

                second.type = LightType.Point;
                AssertMatchesScene(colors, directions);
                second.type = LightType.Directional;
                AssertMatchesScene(colors, directions);
                Object.DestroyImmediate(first.gameObject);
                AssertMatchesScene(colors, directions);
                CreateLight(owners, "APRT replacement directional", Color.blue);
                AssertMatchesScene(colors, directions);

                for (int i = 0; i < 16; ++i) s_Collect(colors, directions);
                using var recorder = new APRTAllocationRecorder();
                recorder.Begin();
                for (int i = 0; i < 64; ++i) s_Collect(colors, directions);
                int allocated = recorder.End();
                Assert.That(allocated, Is.Zero, "Stable live scene-light inputs must not allocate managed arrays.");
            }
            finally
            {
                foreach (var owner in owners) if (owner != null) Object.DestroyImmediate(owner);
                foreach (var light in muted) if (light != null) light.enabled = true;
            }
        }

        [UnityTest]
        public IEnumerator PersistentAndAdditiveSceneLightsRemainGlobalInputs()
        {
            yield return new EnterPlayMode();
            var original = SceneManager.GetActiveScene();
            var scene = SceneManager.CreateScene("APRT global directional light test");
            var muted = MuteExistingDirectionalLights();
            var owners = new List<GameObject>();
            var colors = new Vector4[4];
            var directions = new Vector4[4];
            try
            {
                var persistent = CreateLight(owners, "APRT persistent sun", Color.red);
                Object.DontDestroyOnLoad(persistent.gameObject);
                var additive = CreateLight(owners, "APRT additive scene sun", Color.blue);
                SceneManager.MoveGameObjectToScene(additive.gameObject, scene);
                AssertMatchesScene(colors, directions);
                SceneManager.SetActiveScene(scene);
                AssertMatchesScene(colors, directions);
                SceneManager.SetActiveScene(original);
                yield return SceneManager.UnloadSceneAsync(scene);
                AssertMatchesScene(colors, directions);
                Assert.That(colors[0], Is.EqualTo((Vector4)(persistent.color.linear * persistent.intensity)),
                    "The persistent sun must survive unloading the other scene.");
            }
            finally
            {
                SceneManager.SetActiveScene(original);
                foreach (var owner in owners) if (owner != null) Object.DestroyImmediate(owner);
                foreach (var light in muted) if (light != null) light.enabled = true;
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
            yield return new ExitPlayMode();
        }

        static List<Light> MuteExistingDirectionalLights()
        {
            var muted = new List<Light>();
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (light.type == LightType.Directional && light.enabled)
                {
                    muted.Add(light);
                    light.enabled = false;
                }
            return muted;
        }

        static Light CreateLight(List<GameObject> owners, string name, Color color)
        {
            var owner = new GameObject(name);
            owners.Add(owner);
            var light = owner.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = color;
            light.intensity = 1;
            return light;
        }

        static void AssertMatchesScene(Vector4[] colors, Vector4[] directions)
        {
            s_Collect(colors, directions);
            int count = 0;
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (!light.isActiveAndEnabled || light.type != LightType.Directional) continue;
                Vector4 color = light.color.linear * light.intensity;
                color.w = 1;
                Vector3 direction = -light.transform.forward;
                Vector4 expectedDirection = new Vector4(direction.x, direction.y, direction.z, 0);
                bool found = false;
                for (int i = 0; i < colors.Length; ++i)
                    found |= colors[i] == color && directions[i] == expectedDirection;
                Assert.That(found, Is.True, $"Missing live contribution from {light.name}.");
                ++count;
            }
            Assert.That(count, Is.LessThanOrEqualTo(colors.Length), "This fixture compares the complete set of up to four lights.");
            for (int i = count; i < colors.Length; ++i)
            {
                Assert.That(colors[i], Is.EqualTo(Vector4.zero), "Removed lights must not survive scratch reuse.");
                Assert.That(directions[i], Is.EqualTo(Vector4.zero));
            }
        }
    }
}
