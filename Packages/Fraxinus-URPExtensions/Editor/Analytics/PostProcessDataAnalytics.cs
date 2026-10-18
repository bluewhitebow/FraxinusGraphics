using System;
using System.Collections.Generic;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Analytics;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace UnityEditor.Rendering.Universal
{
    class PostProcessDataAnalytics : IPostprocessBuildWithReport
    {
        const int k_MaxEventsPerHour = 1000;
        const int k_MaxNumberOfElements = 1000;
        const string k_VendorKey = "unity.universal";
        const string k_EventName = "uURPPostProcessAsset";
        // SCHEMA: com.unity3d.data.schemas.editor.analytics.uURPPostProcessAsset_v1
        // TAXONOMY : editor.analytics.uURPPostProcessAsset.v1

#if UNITY_6000_0_OR_NEWER
        [AnalyticInfo(eventName: k_EventName, vendorKey: k_VendorKey, maxEventsPerHour: k_MaxEventsPerHour, maxNumberOfElements: k_MaxNumberOfElements)]
        public class Analytic : IAnalytic
#else
        public class Analytic
#endif
        {
            [Serializable]
#if UNITY_6000_0_OR_NEWER
            public struct AnalyticsData : IAnalytic.IData
#else
            public struct AnalyticsData
#endif
            {
                public string property;
                public string usage;
            }

#if UNITY_6000_0_OR_NEWER
            public bool TryGatherData(out IAnalytic.IData data, out Exception error)
            {
                data = null;

                using (ListPool<PostProcessData>.Get(out var tmp))
                {
                    if (TryGatherPostProcessDataIncludedInBuild(tmp))
                        data = GatherDataToBeSent(ExtractData(tmp));
                }

                error = null;
                return true;
            }
#else
            public bool TryGatherData(out AnalyticsData[] data, out Exception error)
            {
                data = Array.Empty<AnalyticsData>();

                using (ListPool<PostProcessData>.Get(out var tmp))
                {
                    if (TryGatherPostProcessDataIncludedInBuild(tmp))
                        data = GatherDataToBeSent(ExtractData(tmp));
                }

                error = null;
                return true;
            }
#endif

            [Serializable]
            public class PropertyToGUIDs
            {
                public string propertyName;
                public string defaultGUID;
                public List<string> usedGUIDs = new();
            }

            static bool TryGatherPostProcessDataIncludedInBuild(List<PostProcessData> tmp)
            {
                foreach (var renderPipelineAsset in URPBuildData.instance.renderPipelineAssets)
                {
                    foreach (var rendererData in renderPipelineAsset.m_RendererDataList)
                    {
                        PostProcessData postProcessData = rendererData switch
                        {
                            Renderer2DData renderer2DData => renderer2DData.postProcessData,
                            UniversalRendererData universalRendererData => universalRendererData.postProcessData,
                            _ => null
                        };

                        if (postProcessData != null)
                            tmp.Add(postProcessData);
                    }
                }

                return tmp.Count != 0;
            }

            static List<(string property, string guid)> GetPropertyGUIDs(object instance)
            {
                List<(string property, string guid)> output = new();
                foreach (var field in instance.GetType().GetSerializableFields())
                {
                    var assetPath = AssetDatabase.GetAssetPath(field.GetValue(instance) as Object);
                    var guid = AssetDatabase.GUIDFromAssetPath(assetPath).ToString();
                    output.Add((field.Name, guid));
                }
                return output;
            }

            public static PropertyToGUIDs[] ExtractData(List<PostProcessData> postProcessDatas)
            {
                Dictionary<string, PropertyToGUIDs> output = CreateDictionaryWithDefaults();
                AddUsages(postProcessDatas, output);
                return ToPropertyToGUIDsArray(output);
            }

            private static Dictionary<string, PropertyToGUIDs> CreateDictionaryWithDefaults()
            {
                var defaultPostProcessData = ScriptableObject.CreateInstance<PostProcessData>();
                ResourceReloader.ReloadAllNullIn(defaultPostProcessData, UniversalRenderPipelineAsset.packagePath);
                Dictionary<string, PropertyToGUIDs> output = new();
                void AddDefaultsToDictionary(Dictionary<string, PropertyToGUIDs> dictionary,
                    List<(string property, string guid)> list)
                {
                    foreach (var item in list)
                        dictionary.Add(item.property,
                            new PropertyToGUIDs() { propertyName = item.property, defaultGUID = item.guid });
                }

                AddDefaultsToDictionary(output, GetPropertyGUIDs(defaultPostProcessData.shaders));
                AddDefaultsToDictionary(output, GetPropertyGUIDs(defaultPostProcessData.textures));
                ScriptableObject.DestroyImmediate(defaultPostProcessData);
                return output;
            }

            private static void AddUsages(List<PostProcessData> postProcessDatas, Dictionary<string, PropertyToGUIDs> output)
            {
                void AddUsageToDictionary(Dictionary<string, PropertyToGUIDs> dictionary, List<(string property, string guid)> list)
                {
                    foreach (var item in list)
                        dictionary[item.property].usedGUIDs.Add(item.guid);
                }

                foreach (var ppData in postProcessDatas)
                {
                    AddUsageToDictionary(output, GetPropertyGUIDs(ppData.shaders));
                    AddUsageToDictionary(output, GetPropertyGUIDs(ppData.textures));
                }
            }

            private static PropertyToGUIDs[] ToPropertyToGUIDsArray(Dictionary<string, PropertyToGUIDs> output)
            {
                PropertyToGUIDs[] valuesArray = new PropertyToGUIDs[output.Count];
                int index = 0;

                foreach (var value in output.Values)
                {
                    valuesArray[index] = value;
                    index++;
                }

                return valuesArray;
            }

#if UNITY_6000_0_OR_NEWER
            public static IAnalytic.DataList<AnalyticsData> GatherDataToBeSent(PropertyToGUIDs[] dictionary)
#else
            public static AnalyticsData[] GatherDataToBeSent(PropertyToGUIDs[] dictionary)
#endif
            {
                using (ListPool<AnalyticsData>.Get(out var tmp))
                {
                    var uniques = new HashSet<string>();

                    foreach (var i in dictionary)
                    {
                        foreach (var u in i.usedGUIDs)
                            uniques.Add(u);

                        switch (uniques.Count)
                        {
                            case 0:
                                continue;
                            case 1:
                                if (!uniques.Contains(i.defaultGUID))
                                {
                                    tmp.Add(new AnalyticsData()
                                    {
                                        property = i.propertyName,
                                        usage = Usage.ModifiedForTheProject.ToString()
                                    });
                                }
                                break;
                            default:
                                tmp.Add(new AnalyticsData()
                                {
                                    property = i.propertyName,
                                    usage = Usage.ModifiedForEachQualityLevel.ToString()
                                });
                                break;
                        }
                        uniques.Clear();
                    }

#if UNITY_6000_0_OR_NEWER
                    return new IAnalytic.DataList<AnalyticsData>(tmp.ToArray());
#else
                    return tmp.ToArray();
#endif
                }
            }

            public enum Usage
            {
                ModifiedForTheProject,
                ModifiedForEachQualityLevel,
            }
        }

        static void SendUniversalEvent()
        {
            //The event shouldn't be able to report if this is disabled but if we know we're not going to report
            //Lets early out and not waste time gathering all the data
            if (!EditorAnalytics.enabled)
                return;

#if UNITY_6000_0_OR_NEWER
            Analytic analytic = new Analytic();
            EditorAnalytics.SendAnalytic(analytic);
#else
            if (EditorAnalytics.RegisterEventWithLimit(k_EventName,
                    k_MaxEventsPerHour, k_MaxNumberOfElements, k_VendorKey) !=
                AnalyticsResult.Ok)
                return;

            Analytic analytic = new Analytic();
            if (!analytic.TryGatherData(out var data, out var error) || error != null)
                return;

            foreach (var item in data)
                EditorAnalytics.SendEventWithLimit(k_EventName, item);
#endif
        }

        public int callbackOrder { get; }
        public void OnPostprocessBuild(BuildReport report)
        {
            SendUniversalEvent();
        }
    }
}
