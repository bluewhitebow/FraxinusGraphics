using System;
using UnityEngine;
using UnityEngine.Analytics;
using UnityEngine.Rendering.Universal;
#if UNITY_6000_0_OR_NEWER
using static UnityEngine.Analytics.IAnalytic;
#endif

namespace UnityEditor.Rendering.Universal.Analytics
{
#if UNITY_6000_0_OR_NEWER
    struct AnalyticsDataTypes
    {
        public const string k_LightDataString = "u2drendererlights";
        public const string k_Renderer2DDataString = "u2drendererdata";
        public const int k_MaxEventsPerHour = 1000;
        public const int k_MaxNumberOfElements = 1000;
        public const string k_VendorKey = "unity.renderpipelines.universal.editor";
        public const int k_Version = 1;
    }

    [AnalyticInfo(eventName: AnalyticsDataTypes.k_LightDataString, vendorKey: AnalyticsDataTypes.k_VendorKey, maxEventsPerHour: AnalyticsDataTypes.k_MaxEventsPerHour, maxNumberOfElements: AnalyticsDataTypes.k_MaxNumberOfElements)]
    internal class LightDataAnalytic : IAnalytic
    {
        public LightDataAnalytic(int instance_id, bool was_create_event, Light2D.LightType light_type)
        {
            m_Data = new Light2DData
            {
                instance_id = instance_id,
                was_create_event = was_create_event,
                light_type = light_type
            };
        }

        [Serializable]
        internal struct Light2DData : IAnalytic.IData
        {
            [SerializeField]
            public bool was_create_event;
            [SerializeField]
            public int instance_id;
            [SerializeField]
            public Light2D.LightType light_type;
        };
        public bool TryGatherData(out IAnalytic.IData data, out Exception error)
        {
            data = m_Data;
            error = null;
            return true;
        }
        Light2DData m_Data;
    }

    [AnalyticInfo(eventName: AnalyticsDataTypes.k_Renderer2DDataString, vendorKey: AnalyticsDataTypes.k_VendorKey, maxEventsPerHour: AnalyticsDataTypes.k_MaxEventsPerHour, maxNumberOfElements: AnalyticsDataTypes.k_MaxNumberOfElements)]
    internal class RenderAssetAnalytic : IAnalytic
    {
        public RenderAssetAnalytic(int instance_id, bool was_create_event, int blending_layers_count, int blending_modes_used)
        {
            m_Data = new RendererAssetData
            {
                instance_id = instance_id,
                was_create_event = was_create_event,
                blending_layers_count = blending_layers_count,
                blending_modes_used = blending_modes_used
            };
        }

        [Serializable]
        internal struct RendererAssetData : IAnalytic.IData
        {
            [SerializeField]
            public bool was_create_event;
            [SerializeField]
            public int instance_id;
            [SerializeField]
            public int blending_layers_count;
            [SerializeField]
            public int blending_modes_used;
        }
        public bool TryGatherData(out IAnalytic.IData data, out Exception error)
        {
            data = m_Data;
            error = null;
            return true;
        }
        RendererAssetData m_Data;

    }

    interface IAnalytics
    {
        AnalyticsResult SendData(IAnalytic analytic);
    }

    [InitializeOnLoad]
    internal class Renderer2DAnalytics : IAnalytics
    {
        static Renderer2DAnalytics m_Instance = new Renderer2DAnalytics();
        public static Renderer2DAnalytics instance
        {
            get
            {
                if (m_Instance == null)
                    m_Instance = new Renderer2DAnalytics();

                return m_Instance;
            }
        }

        public AnalyticsResult SendData(IAnalytic analytic)
        {
            return EditorAnalytics.SendAnalytic(analytic);
        }
    }
#else
    // Unity 2022 uses the registration-based EditorAnalytics API. The wrapper types
    // intentionally retain the Unity 6 call-site contract so the editor tools do not
    // need version-specific analytics code.
    struct AnalyticsDataTypes
    {
        public const string k_LightDataString = "u2drendererlights";
        public const string k_Renderer2DDataString = "u2drendererdata";
        public const int k_MaxEventsPerHour = 1000;
        public const int k_MaxNumberOfElements = 1000;
        public const string k_VendorKey = "unity.renderpipelines.universal.editor";
        public const int k_Version = 1;
    }

    internal interface IAnalyticsData
    {
        string eventName { get; }
        object payload { get; }
    }

    internal class LightDataAnalytic : IAnalyticsData
    {
        [Serializable]
        struct Light2DData
        {
            [SerializeField] public bool was_create_event;
            [SerializeField] public int instance_id;
            [SerializeField] public Light2D.LightType light_type;
        }

        readonly Light2DData m_Data;

        public LightDataAnalytic(int instance_id, bool was_create_event,
            Light2D.LightType light_type)
        {
            m_Data = new Light2DData
            {
                instance_id = instance_id,
                was_create_event = was_create_event,
                light_type = light_type
            };
        }

        string IAnalyticsData.eventName => AnalyticsDataTypes.k_LightDataString;
        object IAnalyticsData.payload => m_Data;
    }

    internal class RenderAssetAnalytic : IAnalyticsData
    {
        [Serializable]
        struct RendererAssetData
        {
            [SerializeField] public bool was_create_event;
            [SerializeField] public int instance_id;
            [SerializeField] public int blending_layers_count;
            [SerializeField] public int blending_modes_used;
        }

        readonly RendererAssetData m_Data;

        public RenderAssetAnalytic(int instance_id, bool was_create_event,
            int blending_layers_count, int blending_modes_used)
        {
            m_Data = new RendererAssetData
            {
                instance_id = instance_id,
                was_create_event = was_create_event,
                blending_layers_count = blending_layers_count,
                blending_modes_used = blending_modes_used
            };
        }

        string IAnalyticsData.eventName => AnalyticsDataTypes.k_Renderer2DDataString;
        object IAnalyticsData.payload => m_Data;
    }

    interface IAnalytics
    {
        AnalyticsResult SendData(IAnalyticsData analytic);
    }

    [InitializeOnLoad]
    internal class Renderer2DAnalytics : IAnalytics
    {
        static Renderer2DAnalytics m_Instance = new Renderer2DAnalytics();
        static bool s_Initialized;

        public static Renderer2DAnalytics instance
        {
            get
            {
                if (m_Instance == null)
                    m_Instance = new Renderer2DAnalytics();
                return m_Instance;
            }
        }

        public AnalyticsResult SendData(IAnalyticsData analytic)
        {
            if (!s_Initialized)
            {
                EditorAnalytics.RegisterEventWithLimit(
                    AnalyticsDataTypes.k_LightDataString,
                    AnalyticsDataTypes.k_MaxEventsPerHour,
                    AnalyticsDataTypes.k_MaxNumberOfElements,
                    AnalyticsDataTypes.k_VendorKey,
                    AnalyticsDataTypes.k_Version);
                EditorAnalytics.RegisterEventWithLimit(
                    AnalyticsDataTypes.k_Renderer2DDataString,
                    AnalyticsDataTypes.k_MaxEventsPerHour,
                    AnalyticsDataTypes.k_MaxNumberOfElements,
                    AnalyticsDataTypes.k_VendorKey,
                    AnalyticsDataTypes.k_Version);
                s_Initialized = true;
            }

            return EditorAnalytics.SendEventWithLimit(analytic.eventName,
                analytic.payload, AnalyticsDataTypes.k_Version);
        }
    }
#endif
}
