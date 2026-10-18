using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEditor.Rendering.Universal.Internal;
using UnityEngine.Experimental.Rendering.Universal;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

class NoLeaksOnEnterLeavePlaymode
{
    // This variable needs to be serialized, as going into play-mode will reload the domain and
    // wipe the state of all objects, including the content of this variable
    [SerializeField]
    string materialNames = "";
    [SerializeField]
    string meshNames = "";
    [SerializeField]
    string textureNames = "";

    void EnsureUniversalRPIsActivePipeline()
    {
        Camera.main.Render();

        // Skip test if project is not configured to be SRP project
        if (RenderPipelineManager.currentPipeline == null)
            Assert.Ignore("Test project has no SRP configured, skipping test");

        Assert.IsInstanceOf<UniversalRenderPipeline>(RenderPipelineManager.currentPipeline);
    }

    Dictionary<string, int> CountResources(string[] names)
    {
        var result = new Dictionary<string, int>();
        foreach (string name in names)
        {
            if (result.TryGetValue(name, out int materialCount))
            {
                result[name] = materialCount + 1;
            }
            else
            {
                result.Add(name, 1);
            }
        }
        return result;
    }

#if !UNITY_6000_0_OR_NEWER
    static Texture2D GetUnityVersionControlToolbarIcon()
    {
        const string imagesTypeName = "Unity.PlasticSCM.Editor.UI.Images";
        const string iconFieldName = "mPlasticViewIcon";

        foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var imagesType = assembly.GetType(imagesTypeName, false);
            if (imagesType == null)
                continue;

            var iconField = imagesType.GetField(
                iconFieldName,
                BindingFlags.Static | BindingFlags.NonPublic);
            return iconField?.GetValue(null) as Texture2D;
        }

        return null;
    }

    static bool IsUnityVersionControlToolbarIconClone(Texture texture, Texture2D currentIcon)
    {
        if (!(texture is Texture2D texture2D) || currentIcon == null)
            return false;

        return string.IsNullOrEmpty(texture2D.name) &&
            texture2D.width == currentIcon.width &&
            texture2D.height == currentIcon.height &&
            texture2D.mipmapCount == currentIcon.mipmapCount &&
            texture2D.format == currentIcon.format &&
            texture2D.imageContentsHash == currentIcon.imageContentsHash &&
            texture2D.hideFlags == currentIcon.hideFlags &&
            texture2D.filterMode == currentIcon.filterMode &&
            texture2D.wrapMode == currentIcon.wrapMode &&
            texture2D.isReadable == currentIcon.isReadable &&
            !EditorUtility.IsPersistent(texture2D) &&
            string.IsNullOrEmpty(AssetDatabase.GetAssetPath(texture2D));
    }
#endif

    Texture[] FindTrackedTextures()
    {
        var textures = Resources.FindObjectsOfTypeAll<Texture>();

#if !UNITY_6000_0_OR_NEWER
        // com.unity.collab-proxy 2.12 creates its toolbar PNG as an unnamed
        // HideAndDontSave Texture2D, but does not destroy it before a domain
        // reload. Unity 2022 therefore leaves one identical native clone per
        // PlayMode transition. Match the package's current icon fingerprint so
        // only those external editor-owned clones are excluded from this URP test.
        var currentIcon = GetUnityVersionControlToolbarIcon();
        if (currentIcon != null)
            textures = textures
                .Where(texture => !IsUnityVersionControlToolbarIconClone(texture, currentIcon))
                .ToArray();
#endif

        return textures;
    }

    void CompareResourceLists(Dictionary<string, int> oldList, Dictionary<string, int> newList, string [] blackList)
    {
        foreach (var newRes in newList)
        {
            // Ignore blacklisted materials
            if (blackList.Contains(newRes.Key)) continue;

            int oldCount = 0;
            oldList.TryGetValue(newRes.Key, out oldCount);
            if (newRes.Value > oldCount)
            {
                Debug.LogError("Leaked " + newRes.Key + "(" + (newRes.Value - oldCount) + "x)");
            }
        }
    }

    [UnityTest]
    public IEnumerator NoResourceLeaks()
    {
        // Ignoring OpenGL as it has now been deprecated and this test is unstable on that platform.
        GraphicsDeviceType gfxAPI = SystemInfo.graphicsDeviceType;
        if (gfxAPI == GraphicsDeviceType.OpenGLCore)
            yield break;

        // give it a chance to warm-up by entering play mode once
        // in theory this shouldn't be needed but I hope this avoids the worst instabilities.
        yield return new EnterPlayMode();
        yield return null;
        yield return new ExitPlayMode();
        yield return null;

        // Grab the list of existing objects
        var mats = Resources.FindObjectsOfTypeAll(typeof(Material));
        materialNames = string.Join(";", mats.Select(m => m.name));

        var meshes = Resources.FindObjectsOfTypeAll(typeof(Mesh));
        meshNames = string.Join(";", meshes.Select(m => m.name));

        var textures = FindTrackedTextures();
        textureNames = string.Join(";", textures.Select(m => m.name));

        yield return new EnterPlayMode();
        yield return null;
        yield return new ExitPlayMode();
        yield return null;

        // Grab lists of existing objects.
        // Note: resources created from code often reuse the same names so we have to both check the names
        // and the counts per name to ensure nothing leaked.
        var newMats = Resources.FindObjectsOfTypeAll(typeof(Material));
        var newMeshes = Resources.FindObjectsOfTypeAll(typeof(Mesh));
        var newTextures = FindTrackedTextures();

        string[] materialBlackList = {
            "Hidden/Universal/HDRDebugView",
            "Hidden/Universal Render Pipeline/Debug/DebugReplacement",
            "Roboto Mono - Regular Material", // Font leaks
            "Inter - Regular Material"        // https://jira.unity3d.com/browse/UUM-28555
        };

        var oldMaterialNames = materialNames.Split(";");
        var materialsPerNameOld = CountResources(oldMaterialNames);
        var newMaterialNames = newMats.Select(m => m.name).ToArray();
        var materialsPerNameNew = CountResources(newMaterialNames);
        CompareResourceLists(materialsPerNameOld, materialsPerNameNew, materialBlackList);

        string[] meshBlackList = {
        };

        var oldMeshNames = meshNames.Split(";");
        var meshesPerNameOld = CountResources(oldMeshNames);
        var newMeshNames = newMeshes.Select(m => m.name).ToArray();
        var meshesPerNameNew = CountResources(newMeshNames);
        CompareResourceLists(meshesPerNameOld, meshesPerNameNew, meshBlackList);

        string[] textureBlackList = {
            "Inter - Regular Atlas",       // more fonts leakage :-\
            "Roboto Mono - Regular Atlas", // more fonts leakage :-\
        };

        var oldTextureNames = textureNames.Split(";");
        var texturesPerNameOld = CountResources(oldTextureNames);
        var newTextureNames = newTextures.Select(m => m.name).ToArray();
        var texturesPerNameNew = CountResources(newTextureNames);
        CompareResourceLists(texturesPerNameOld, texturesPerNameNew, textureBlackList);
    }
}
