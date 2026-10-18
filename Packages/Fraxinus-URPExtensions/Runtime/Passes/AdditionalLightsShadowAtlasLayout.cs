using System;
using System.Collections.Generic;
using Unity.Collections;

namespace UnityEngine.Rendering.Universal
{
    internal struct AdditionalLightsShadowAtlasLayout
    {
        internal struct ShadowResolutionRequest
        {
            public ushort visibleLightIndex;
            public ushort perLightShadowSliceIndex;
            public ushort requestedResolution;
            public ushort offsetX;             // x coordinate of the square area allocated in the atlas for this shadow map
            public ushort offsetY;             // y coordinate of the square area allocated in the atlas for this shadow map
            public ushort allocatedResolution; // width of the square area allocated in the atlas for this shadow map

            [Flags]
            private enum SettingsOptions : ushort
            {
                None = 0,
                SoftShadow = (1 << 0),
                PointLightShadow = (1 << 1),
                All = 0xFFFF
            }
            private SettingsOptions m_ShadowProperties;

            public bool softShadow
            {
                get => (m_ShadowProperties & SettingsOptions.SoftShadow) != 0; // otherwise it's hard-shadow (no filtering)
                set
                {
                    if (value)
                        m_ShadowProperties |= SettingsOptions.SoftShadow;
                    else
                        m_ShadowProperties &= ~SettingsOptions.SoftShadow;
                }
            }

            public bool pointLightShadow
            {
                get => (m_ShadowProperties & SettingsOptions.PointLightShadow) != 0; // otherwise it's spot light shadow (1 shadow slice instead of 6)
                set
                {
                    if (value)
                        m_ShadowProperties |= SettingsOptions.PointLightShadow;
                    else
                        m_ShadowProperties &= ~SettingsOptions.PointLightShadow;
                }
            }
        }

        // Static fields used to avoid GC allocs of intermediate computations
        static List<ShadowResolutionRequest> s_ShadowResolutionRequests; // intermediate array used to compute the final resolution of each shadow slice rendered in the frame
        static float[] s_VisibleLightIndexToCameraSquareDistance; // stores for each shadowed additional light its (squared) distance to camera ; used to sub-sort shadow requests according to how close their casting light is
        static Func<ShadowResolutionRequest, ShadowResolutionRequest, int> s_CompareShadowResolutionRequest;
        static ShadowResolutionRequest[] s_SortedShadowResolutionRequests;
        static List<AtlasBuddyBlock>[] s_AtlasBuddyFreeLists;
        static List<RectInt> s_AtlasFreeRectangles;

        // Authored resolutions are normally powers of two up to 4096. A finite cap keeps
        // malformed atlas dimensions or saturated request sums from spinning forever.
        const int k_MaxShadowScaleFactor = 1 << 20;
        // Shared by all non-power-of-two retries for one layout. Giving every retry a fresh
        // budget allows adversarial custom resolutions to multiply the nominal MaxRects cost.
        const long k_MaxAtlasFreeRectangleWork = 4_000_000L;

        NativeArray<ShadowResolutionRequest> m_SortedShadowResolutionRequests;
        NativeArray<int> m_VisibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex; // for each visible light, store the index of its first shadow slice in m_SortedShadowResolutionRequests (for quicker access)
        int m_TotalShadowSlicesCount;
        int m_TotalShadowResolutionRequestCount;
        bool m_TooManyShadowMaps;
        int m_ShadowSlicesScaleFactor;
        int m_AtlasSize;

        public AdditionalLightsShadowAtlasLayout(UniversalLightData lightData, UniversalShadowData shadowData, UniversalCameraData cameraData)
        {
            bool useStructuredBuffer = RenderingUtils.useStructuredBuffer;
            NativeArray<VisibleLight> visibleLights = lightData.visibleLights;
            int numberOfVisibleLights = visibleLights.Length;

            if (s_ShadowResolutionRequests == null)
                s_ShadowResolutionRequests = new List<ShadowResolutionRequest>();

            if (s_VisibleLightIndexToCameraSquareDistance == null || s_VisibleLightIndexToCameraSquareDistance.Length < numberOfVisibleLights)
                s_VisibleLightIndexToCameraSquareDistance = new float[numberOfVisibleLights];

            if (s_CompareShadowResolutionRequest == null)
                s_CompareShadowResolutionRequest = CreateCompareShadowResolutionRequesPredicate();

            if (!useStructuredBuffer)
            {
                int newCapacity = UniversalRenderPipeline.maxVisibleAdditionalLights;

                if (s_ShadowResolutionRequests.Count < numberOfVisibleLights)
                {
                    s_ShadowResolutionRequests.Capacity = numberOfVisibleLights;
                    int diff = numberOfVisibleLights - s_ShadowResolutionRequests.Count + 1;
                    for (int i = 0; i < diff; i++)
                        s_ShadowResolutionRequests.Add(new ShadowResolutionRequest());
                }

            }

            int totalShadowResolutionRequestsCount = 0; // Number of shadow slices that we would need for all shadowed additional (punctual) lights in the scene. We might have to ignore some of those requests if they do not fit in the shadow atlas.
            for (int visibleLightIndex = 0; visibleLightIndex < visibleLights.Length; ++visibleLightIndex)
            {
                // Skip main directional light as it is not packed into the shadow atlas
                if (visibleLightIndex == lightData.mainLightIndex)
                {
                    s_VisibleLightIndexToCameraSquareDistance[visibleLightIndex] = float.MaxValue;
                    continue;
                }

                ref VisibleLight vl = ref visibleLights.UnsafeElementAt(visibleLightIndex);
                Light light = vl.light;
                LightType lightType = vl.lightType;
                LightShadows lightShadows = light.shadows;
                float shadowStrength = light.shadowStrength;
                if (!ShadowUtils.IsValidShadowCastingLight(lightData, visibleLightIndex, lightType, lightShadows, shadowStrength))
                {
                    s_VisibleLightIndexToCameraSquareDistance[visibleLightIndex] = float.MaxValue;
                    continue;
                }

                bool softShadows = (lightShadows == LightShadows.Soft);
                bool pointLightShadow = (lightType == LightType.Point);
                ushort visibleLightIndexUshort = (ushort)visibleLightIndex;
                ushort requestedResolution = (ushort)shadowData.resolution[visibleLightIndex];
                int shadowSlicesCountForThisLight = ShadowUtils.GetPunctualLightShadowSlicesCount(lightType);
                for (ushort perLightShadowSliceIndex = 0; perLightShadowSliceIndex < shadowSlicesCountForThisLight; ++perLightShadowSliceIndex)
                {
                    if (totalShadowResolutionRequestsCount >= s_ShadowResolutionRequests.Count)
                        s_ShadowResolutionRequests.Add(new ShadowResolutionRequest());

                    ShadowResolutionRequest request = s_ShadowResolutionRequests[totalShadowResolutionRequestsCount];
                    request.visibleLightIndex = visibleLightIndexUshort;
                    request.perLightShadowSliceIndex = perLightShadowSliceIndex;
                    request.requestedResolution = requestedResolution;
                    request.softShadow = softShadows;
                    request.pointLightShadow = pointLightShadow;
                    s_ShadowResolutionRequests[totalShadowResolutionRequestsCount] = request;
                    totalShadowResolutionRequestsCount++;
                }

                // mark this light as casting shadows
                s_VisibleLightIndexToCameraSquareDistance[visibleLightIndex] = (cameraData.worldSpaceCameraPos - light.transform.position).sqrMagnitude;
            }

            if (s_SortedShadowResolutionRequests == null || s_SortedShadowResolutionRequests.Length < totalShadowResolutionRequestsCount)
                s_SortedShadowResolutionRequests = new ShadowResolutionRequest[totalShadowResolutionRequestsCount];

            for (int i = 0; i < totalShadowResolutionRequestsCount; ++i)
                s_SortedShadowResolutionRequests[i] = s_ShadowResolutionRequests[i];

            using (new ProfilingScope(Sorting.s_QuickSortSampler))
            {
                Sorting.QuickSort(s_SortedShadowResolutionRequests, 0, totalShadowResolutionRequestsCount - 1, s_CompareShadowResolutionRequest);
            }

            m_SortedShadowResolutionRequests = new NativeArray<ShadowResolutionRequest>(s_SortedShadowResolutionRequests, Allocator.Temp);

            // To avoid visual artifacts when there is not enough place in the atlas, we remove shadow slices that would be allocated a too small resolution.
            // When not using structured buffers, m_AdditionalLightShadowSliceIndexTo_WorldShadowMatrix.Length maps to _AdditionalLightsWorldToShadow in Shadows.hlsl
            // In that case we have to limit its size because uniform buffers cannot be higher than 64kb for some platforms.
            int totalShadowSlicesCount = useStructuredBuffer ? totalShadowResolutionRequestsCount : Math.Min(totalShadowResolutionRequestsCount, UniversalRenderPipeline.maxVisibleAdditionalLights);  // Number of shadow slices that we will actually be able to fit in the shadow atlas without causing visual artifacts.
            // The allocator is square, so constrain it to the smaller authored axis. Using only
            // the width can place slices beyond a custom non-square atlas height.
            int atlasSize = Math.Max(0, Math.Min(
                shadowData.additionalLightsShadowmapWidth,
                shadowData.additionalLightsShadowmapHeight));

            // Never split a point light's six faces at the constant-buffer boundary. Requests
            // from one light are contiguous after sorting, so discard the partially included
            // light as one unit.
            if (totalShadowSlicesCount < totalShadowResolutionRequestsCount)
            {
                int firstExcludedLightIndex = m_SortedShadowResolutionRequests[totalShadowSlicesCount].visibleLightIndex;
                while (totalShadowSlicesCount > 0 &&
                       m_SortedShadowResolutionRequests[totalShadowSlicesCount - 1].visibleLightIndex == firstExcludedLightIndex)
                    --totalShadowSlicesCount;
            }

            // Find the largest useful prefix. Keep the texel sum incremental while whole lights
            // are removed, and saturate rather than wrapping if a future request representation
            // grows beyond today's ushort resolution range.
            long totalShadowRequestTexels = SumShadowRequestTexels(
                m_SortedShadowResolutionRequests, totalShadowSlicesCount,
                out int firstOverflowingTexelRequestIndex);
            bool allShadowsAfterStartIndexHaveEnoughResolution = false;
            int estimatedScaleFactor = 1;
            while (!allShadowsAfterStartIndexHaveEnoughResolution && totalShadowSlicesCount > 0)
            {
                ShadowResolutionRequest request = m_SortedShadowResolutionRequests[totalShadowSlicesCount - 1];
                estimatedScaleFactor = EstimateScaleFactorNeededToFitShadowTexels(totalShadowRequestTexels, atlasSize);

                // check if resolution of the least priority shadow slice request would be acceptable
                if (request.requestedResolution >= estimatedScaleFactor * ShadowUtils.MinimalPunctualLightShadowResolution(request.softShadow))
                    allShadowsAfterStartIndexHaveEnoughResolution = true;
                else // Skip shadow requests for this light ; their resolution is too small to look any good
                {
                    int removedLightIndex = request.visibleLightIndex;
                    do
                    {
                        --totalShadowSlicesCount;
                        if (firstOverflowingTexelRequestIndex < 0)
                            totalShadowRequestTexels -= ShadowRequestTexelCount(
                                m_SortedShadowResolutionRequests[totalShadowSlicesCount]);
                    }
                    while (totalShadowSlicesCount > 0 &&
                           m_SortedShadowResolutionRequests[totalShadowSlicesCount - 1].visibleLightIndex == removedLightIndex);

                    // Once the overflowing request leaves the retained prefix, rebuild the now
                    // finite sum exactly. This transition can happen at most once.
                    if (firstOverflowingTexelRequestIndex >= 0 &&
                        totalShadowSlicesCount <= firstOverflowingTexelRequestIndex)
                    {
                        totalShadowRequestTexels = SumShadowRequestTexels(
                            m_SortedShadowResolutionRequests, totalShadowSlicesCount,
                            out firstOverflowingTexelRequestIndex);
                    }
                }
            }

            for (int sortedArrayIndex = totalShadowSlicesCount; sortedArrayIndex < m_SortedShadowResolutionRequests.Length; ++sortedArrayIndex)
                m_SortedShadowResolutionRequests[sortedArrayIndex] = default; // Reset entries that we cannot fit in the atlas

            m_VisibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex = new NativeArray<int>(visibleLights.Length, Allocator.Temp);

            // Reset the reverse lookup array
            for (int visibleLightIndex = 0; visibleLightIndex < m_VisibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex.Length; ++visibleLightIndex)
                m_VisibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex[visibleLightIndex] = -1;

            // Update the reverse lookup array (starting from the end of the array, in order to use index of slice#0 in case a same visibleLight has several shadowSlices)
            for (int sortedArrayIndex = totalShadowSlicesCount - 1; sortedArrayIndex >= 0; --sortedArrayIndex)
            {
                int visibleLightIndex = m_SortedShadowResolutionRequests[sortedArrayIndex].visibleLightIndex;
                m_VisibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex[visibleLightIndex] = sortedArrayIndex;
            }

            // Assigns to each of the first totalShadowSlicesCount items in m_SortedShadowResolutionRequests a location in the shadow atlas based on requested resolutions.
            // If necessary, scales down shadow maps active in the frame, to make all of them fit in the atlas.
            bool allShadowSlicesFitInAtlas = totalShadowSlicesCount == 0;
            bool tooManyShadows = false;
            int shadowSlicesScaleFactor = Mathf.Clamp(estimatedScaleFactor, 1, k_MaxShadowScaleFactor);
            long freeRectangleWorkBudget = GetAtlasFreeRectangleWorkBudget(totalShadowSlicesCount);

            if (atlasSize > 0 && totalShadowSlicesCount > 0)
            {
                while (!allShadowSlicesFitInAtlas && !tooManyShadows)
                {
                    ClearAtlasAllocations(m_SortedShadowResolutionRequests, totalShadowSlicesCount);
                    allShadowSlicesFitInAtlas = TryLayoutShadowSlices(
                        m_SortedShadowResolutionRequests, atlasSize, totalShadowSlicesCount,
                        shadowSlicesScaleFactor, ref freeRectangleWorkBudget,
                        out bool resolutionTooSmall, out bool workLimitReached);
                    tooManyShadows = resolutionTooSmall || workLimitReached;

                    if (!allShadowSlicesFitInAtlas && !tooManyShadows)
                    {
                        if (shadowSlicesScaleFactor >= k_MaxShadowScaleFactor)
                        {
                            tooManyShadows = true;
                            break;
                        }
                        shadowSlicesScaleFactor = Math.Min(
                            k_MaxShadowScaleFactor, shadowSlicesScaleFactor * 2);
                    }
                }
            }
            else if (totalShadowSlicesCount > 0)
                tooManyShadows = true;

            if (!allShadowSlicesFitInAtlas)
                ClearAtlasAllocations(m_SortedShadowResolutionRequests, totalShadowSlicesCount);

            m_TooManyShadowMaps = tooManyShadows;
            m_ShadowSlicesScaleFactor = shadowSlicesScaleFactor;
            m_TotalShadowSlicesCount = totalShadowSlicesCount;
            m_TotalShadowResolutionRequestCount = totalShadowResolutionRequestsCount;
            m_AtlasSize = atlasSize;
        }

        public int GetTotalShadowSlicesCount() => m_TotalShadowSlicesCount;

        public int GetTotalShadowResolutionRequestCount() => m_TotalShadowResolutionRequestCount;

        public bool HasTooManyShadowMaps() => m_TooManyShadowMaps;

        public int GetShadowSlicesScaleFactor() => m_ShadowSlicesScaleFactor;

        public int GetAtlasSize() => m_AtlasSize;

        public bool HasSpaceForLight(int originalVisibleLightIndex)
        {
            return m_VisibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex[originalVisibleLightIndex] != -1;
        }

        public ShadowResolutionRequest GetSortedShadowResolutionRequest(int sortedShadowResolutionRequestIndex)
        {
            return m_SortedShadowResolutionRequests[sortedShadowResolutionRequestIndex];
        }

        public ShadowResolutionRequest GetSliceShadowResolutionRequest(int originalVisibleLightIndex, int sliceIndex)
        {
            int sortedShadowResolutionRequestIndex = m_VisibleLightIndexToSortedShadowResolutionRequestsFirstSliceIndex[originalVisibleLightIndex];
            return m_SortedShadowResolutionRequests[sortedShadowResolutionRequestIndex + sliceIndex];
        }

        public static void ClearStaticCaches()
        {
            s_ShadowResolutionRequests = null;
            s_VisibleLightIndexToCameraSquareDistance = null;
            s_CompareShadowResolutionRequest = null;
            s_SortedShadowResolutionRequests = null;
            s_AtlasBuddyFreeLists = null;
            s_AtlasFreeRectangles = null;
        }

        static long ShadowRequestTexelCount(in ShadowResolutionRequest request)
        {
            long resolution = request.requestedResolution;
            return resolution * resolution;
        }

        internal static bool AccumulateShadowRequestTexels(ref long totalTexels, long requestTexels)
        {
            requestTexels = Math.Max(0, requestTexels);
            if (requestTexels > long.MaxValue - totalTexels)
            {
                totalTexels = long.MaxValue;
                return true;
            }

            totalTexels += requestTexels;
            return false;
        }

        static long SumShadowRequestTexels(
            in NativeArray<ShadowResolutionRequest> shadowResolutionRequests,
            int endIndex,
            out int firstOverflowingRequestIndex)
        {
            firstOverflowingRequestIndex = -1;
            if (!shadowResolutionRequests.IsCreated || endIndex <= 0)
                return 0;

            int requestCount = Math.Min(endIndex, shadowResolutionRequests.Length);
            long totalTexels = 0;
            for (int requestIndex = 0; requestIndex < requestCount; ++requestIndex)
            {
                long requestTexels = ShadowRequestTexelCount(shadowResolutionRequests[requestIndex]);
                if (firstOverflowingRequestIndex < 0 &&
                    AccumulateShadowRequestTexels(ref totalTexels, requestTexels))
                    firstOverflowingRequestIndex = requestIndex;
            }

            return totalTexels;
        }

        static int EstimateScaleFactorNeededToFitShadowTexels(
            long totalTexelsInShadowRequests, int atlasSize)
        {
            long totalTexelsInShadowAtlas = atlasSize <= 0
                ? 0
                : (long)atlasSize * atlasSize;

            int estimatedScaleFactor = 1;
            while (estimatedScaleFactor < k_MaxShadowScaleFactor &&
                   ShadowTexelsExceedScaledAtlas(
                       totalTexelsInShadowRequests, totalTexelsInShadowAtlas,
                       estimatedScaleFactor))
                estimatedScaleFactor *= 2;

            return estimatedScaleFactor;
        }

        // Compare requestTexels > atlasTexels * scaleFactor^2 without an overflowing
        // multiplication. Two ceiling divisions preserve the exact integer inequality.
        static bool ShadowTexelsExceedScaledAtlas(
            long requestTexels, long atlasTexels, int scaleFactor)
        {
            if (requestTexels <= 0)
                return false;
            if (atlasTexels <= 0 || scaleFactor <= 0)
                return true;

            long firstCeil = requestTexels / scaleFactor;
            if (requestTexels % scaleFactor != 0)
                ++firstCeil;

            long secondCeil = firstCeil / scaleFactor;
            if (firstCeil % scaleFactor != 0)
                ++secondCeil;

            return secondCeil > atlasTexels;
        }

        static bool TryLayoutShadowSlices(
            NativeArray<ShadowResolutionRequest> requests,
            int atlasSize,
            int shadowSlicesCount,
            int scaleFactor,
            ref long freeRectangleWorkBudget,
            out bool resolutionTooSmall,
            out bool workLimitReached)
        {
            workLimitReached = false;
            if (!CanUseBuddyAtlasLayout(atlasSize, requests, shadowSlicesCount, scaleFactor))
            {
                bool packed = TryLayoutShadowSlicesInFreeRectangles(
                    requests, atlasSize, shadowSlicesCount, scaleFactor,
                    ref freeRectangleWorkBudget, out resolutionTooSmall);
                workLimitReached = !packed && freeRectangleWorkBudget <= 0;
                return packed;
            }

            resolutionTooSmall = false;
            EnsureAtlasBuddyLevels(atlasSize);
            for (int level = 0; level < s_AtlasBuddyFreeLists.Length; ++level)
                s_AtlasBuddyFreeLists[level].Clear();
            s_AtlasBuddyFreeLists[0].Add(new AtlasBuddyBlock(0, 0, atlasSize));

            for (int requestIndex = 0; requestIndex < shadowSlicesCount; ++requestIndex)
            {
                ref ShadowResolutionRequest request = ref requests.UnsafeElementAtMutable(requestIndex);
                int resolution = request.requestedResolution / scaleFactor;
                if (resolution < ShadowUtils.MinimalPunctualLightShadowResolution(request.softShadow))
                {
                    resolutionTooSmall = true;
                    return false;
                }
                if (resolution > atlasSize)
                    return false;

                int blockSize = NextPowerOfTwoAtLeast(resolution);
                if (blockSize > atlasSize)
                    return false;
                int targetLevel = Log2(atlasSize / blockSize);
                int sourceLevel = targetLevel;
                while (sourceLevel >= 0 && s_AtlasBuddyFreeLists[sourceLevel].Count == 0)
                    --sourceLevel;
                if (sourceLevel < 0)
                    return false;

                AtlasBuddyBlock block = PopAtlasBuddyBlock(sourceLevel);
                while (sourceLevel < targetLevel)
                {
                    int halfSize = block.size >> 1;
                    ++sourceLevel;
                    s_AtlasBuddyFreeLists[sourceLevel].Add(
                        new AtlasBuddyBlock(block.x + halfSize, block.y + halfSize, halfSize));
                    s_AtlasBuddyFreeLists[sourceLevel].Add(
                        new AtlasBuddyBlock(block.x, block.y + halfSize, halfSize));
                    s_AtlasBuddyFreeLists[sourceLevel].Add(
                        new AtlasBuddyBlock(block.x + halfSize, block.y, halfSize));
                    block = new AtlasBuddyBlock(block.x, block.y, halfSize);
                }

                request.offsetX = (ushort)block.x;
                request.offsetY = (ushort)block.y;
                request.allocatedResolution = (ushort)resolution;
            }

            return true;
        }

        internal static bool CanUseBuddyAtlasLayout(
            int atlasSize,
            NativeArray<ShadowResolutionRequest> requests,
            int requestCount,
            int scaleFactor)
        {
            if (!IsPowerOfTwo(atlasSize) || atlasSize > ushort.MaxValue ||
                !requests.IsCreated || requestCount < 0 ||
                requestCount > requests.Length || scaleFactor <= 0)
                return false;

            for (int requestIndex = 0; requestIndex < requestCount; ++requestIndex)
            {
                int resolution = requests[requestIndex].requestedResolution / scaleFactor;
                if (!IsPowerOfTwo(resolution))
                    return false;
            }

            return true;
        }

        internal static bool TryLayoutShadowSlicesInFreeRectangles(
            NativeArray<ShadowResolutionRequest> requests,
            int atlasSize,
            int shadowSlicesCount,
            int scaleFactor,
            out bool resolutionTooSmall)
        {
            long workBudget = GetAtlasFreeRectangleWorkBudget(shadowSlicesCount);
            return TryLayoutShadowSlicesInFreeRectangles(
                requests, atlasSize, shadowSlicesCount, scaleFactor,
                ref workBudget, out resolutionTooSmall);
        }

        static bool TryLayoutShadowSlicesInFreeRectangles(
            NativeArray<ShadowResolutionRequest> requests,
            int atlasSize,
            int shadowSlicesCount,
            int scaleFactor,
            ref long workBudget,
            out bool resolutionTooSmall)
        {
            resolutionTooSmall = false;
            if (atlasSize <= 0 || atlasSize > ushort.MaxValue || !requests.IsCreated ||
                shadowSlicesCount < 0 || shadowSlicesCount > requests.Length ||
                scaleFactor <= 0)
                return false;

            for (int requestIndex = 0; requestIndex < shadowSlicesCount; ++requestIndex)
            {
                if (--workBudget < 0)
                    return false;
                ref ShadowResolutionRequest request = ref requests.UnsafeElementAtMutable(requestIndex);
                int resolution = request.requestedResolution / scaleFactor;
                if (resolution < ShadowUtils.MinimalPunctualLightShadowResolution(request.softShadow))
                {
                    resolutionTooSmall = true;
                    return false;
                }
                if (resolution > atlasSize)
                    return false;
            }

            return TryPackShadowSlicesInFreeRectangles(
                requests, atlasSize, shadowSlicesCount, scaleFactor, ref workBudget);
        }

        // Pure geometry core, separate from the minimum-useful-resolution policy so small
        // integer counterexamples can be regression-tested directly.
        internal static bool TryPackShadowSlicesInFreeRectangles(
            NativeArray<ShadowResolutionRequest> requests,
            int atlasSize,
            int shadowSlicesCount,
            int scaleFactor)
        {
            long workBudget = GetAtlasFreeRectangleWorkBudget(shadowSlicesCount);
            return TryPackShadowSlicesInFreeRectangles(
                requests, atlasSize, shadowSlicesCount, scaleFactor, ref workBudget);
        }

        static bool TryPackShadowSlicesInFreeRectangles(
            NativeArray<ShadowResolutionRequest> requests,
            int atlasSize,
            int shadowSlicesCount,
            int scaleFactor,
            ref long workBudget)
        {
            if (atlasSize <= 0 || atlasSize > ushort.MaxValue || !requests.IsCreated ||
                shadowSlicesCount < 0 || shadowSlicesCount > requests.Length ||
                scaleFactor <= 0)
                return false;

            int initialFreeRectangleCapacity = shadowSlicesCount < 63
                ? shadowSlicesCount + 1
                : 64;
            if (s_AtlasFreeRectangles == null)
                s_AtlasFreeRectangles = new List<RectInt>(initialFreeRectangleCapacity);
            else
            {
                s_AtlasFreeRectangles.Clear();
                if (s_AtlasFreeRectangles.Capacity < initialFreeRectangleCapacity)
                    s_AtlasFreeRectangles.Capacity = initialFreeRectangleCapacity;
            }

            List<RectInt> freeRectangles = s_AtlasFreeRectangles;
            freeRectangles.Add(new RectInt(0, 0, atlasSize, atlasSize));

            int maxFreeRectangleCount = (int)Math.Min(
                4_096L, Math.Max(64L, (long)shadowSlicesCount * 8L + 1L));

            for (int requestIndex = 0; requestIndex < shadowSlicesCount; ++requestIndex)
            {
                ref ShadowResolutionRequest request = ref requests.UnsafeElementAtMutable(requestIndex);
                int resolution = request.requestedResolution / scaleFactor;
                if (resolution <= 0 || resolution > atlasSize)
                    return false;

                int bestRectangleIndex = -1;
                long bestAreaWaste = long.MaxValue;
                int bestShortSideWaste = int.MaxValue;
                int bestLongSideWaste = int.MaxValue;
                int bestX = int.MaxValue;
                int bestY = int.MaxValue;
                for (int freeIndex = 0; freeIndex < freeRectangles.Count; ++freeIndex)
                {
                    if (--workBudget < 0)
                        return false;

                    RectInt free = freeRectangles[freeIndex];
                    if (free.width < resolution || free.height < resolution)
                        continue;

                    int shortSideWaste = Math.Min(
                        free.width - resolution, free.height - resolution);
                    int longSideWaste = Math.Max(
                        free.width - resolution, free.height - resolution);
                    long areaWaste = (long)free.width * free.height -
                                     (long)resolution * resolution;
                    if (areaWaste < bestAreaWaste ||
                        (areaWaste == bestAreaWaste &&
                         (shortSideWaste < bestShortSideWaste ||
                          (shortSideWaste == bestShortSideWaste &&
                           (longSideWaste < bestLongSideWaste ||
                            (longSideWaste == bestLongSideWaste &&
                             (free.y < bestY ||
                              (free.y == bestY && free.x < bestX))))))))
                    {
                        bestRectangleIndex = freeIndex;
                        bestAreaWaste = areaWaste;
                        bestShortSideWaste = shortSideWaste;
                        bestLongSideWaste = longSideWaste;
                        bestX = free.x;
                        bestY = free.y;
                    }
                }

                if (bestRectangleIndex < 0)
                    return false;

                RectInt placementArea = freeRectangles[bestRectangleIndex];
                RectInt placed = new RectInt(
                    placementArea.x, placementArea.y, resolution, resolution);
                request.offsetX = (ushort)placed.x;
                request.offsetY = (ushort)placed.y;
                request.allocatedResolution = (ushort)resolution;

                // Split every intersected free rectangle. Splitting only the chosen rectangle
                // leaves overlapping regions and can admit a later overlapping tile.
                for (int freeIndex = freeRectangles.Count - 1; freeIndex >= 0; --freeIndex)
                {
                    if (--workBudget < 0)
                        return false;

                    RectInt free = freeRectangles[freeIndex];
                    if (!AtlasRectanglesIntersect(free, placed))
                        continue;

                    RemoveAtlasFreeRectangleAt(freeRectangles, freeIndex);
                    if (placed.x > free.x &&
                        !AddAtlasFreeRectangle(
                            freeRectangles,
                            new RectInt(free.x, free.y, placed.x - free.x, free.height),
                            maxFreeRectangleCount, ref workBudget))
                        return false;
                    if (placed.xMax < free.xMax &&
                        !AddAtlasFreeRectangle(
                            freeRectangles,
                            new RectInt(placed.xMax, free.y,
                                free.xMax - placed.xMax, free.height),
                            maxFreeRectangleCount, ref workBudget))
                        return false;
                    if (placed.y > free.y &&
                        !AddAtlasFreeRectangle(
                            freeRectangles,
                            new RectInt(free.x, free.y, free.width, placed.y - free.y),
                            maxFreeRectangleCount, ref workBudget))
                        return false;
                    if (placed.yMax < free.yMax &&
                        !AddAtlasFreeRectangle(
                            freeRectangles,
                            new RectInt(free.x, placed.yMax,
                                free.width, free.yMax - placed.yMax),
                            maxFreeRectangleCount, ref workBudget))
                        return false;
                }

                if (!PruneContainedAtlasFreeRectangles(freeRectangles, ref workBudget))
                    return false;
            }

            return true;
        }

        static bool AddAtlasFreeRectangle(
            List<RectInt> freeRectangles,
            RectInt rectangle,
            int maxFreeRectangleCount,
            ref long workBudget)
        {
            if (rectangle.width <= 0 || rectangle.height <= 0)
                return true;
            if (--workBudget < 0)
                return false;
            if (freeRectangles.Count >= maxFreeRectangleCount)
            {
                workBudget = 0;
                return false;
            }

            freeRectangles.Add(rectangle);
            return true;
        }

        static long GetAtlasFreeRectangleWorkBudget(int shadowSlicesCount) =>
            Math.Min(k_MaxAtlasFreeRectangleWork,
                Math.Max(16_384L, Math.Max(0L, (long)shadowSlicesCount) * 512L));

        static void RemoveAtlasFreeRectangleAt(List<RectInt> freeRectangles, int index)
        {
            int last = freeRectangles.Count - 1;
            if (index != last)
                freeRectangles[index] = freeRectangles[last];
            freeRectangles.RemoveAt(last);
        }

        static bool PruneContainedAtlasFreeRectangles(
            List<RectInt> freeRectangles, ref long workBudget)
        {
            for (int first = 0; first < freeRectangles.Count; ++first)
            {
                for (int second = first + 1; second < freeRectangles.Count; ++second)
                {
                    if (--workBudget < 0)
                        return false;

                    RectInt firstRectangle = freeRectangles[first];
                    RectInt secondRectangle = freeRectangles[second];
                    if (AtlasRectangleContains(firstRectangle, secondRectangle))
                        RemoveAtlasFreeRectangleAt(freeRectangles, second--);
                    else if (AtlasRectangleContains(secondRectangle, firstRectangle))
                    {
                        RemoveAtlasFreeRectangleAt(freeRectangles, first--);
                        break;
                    }
                }
            }

            return true;
        }

        static bool AtlasRectanglesIntersect(RectInt left, RectInt right) =>
            left.x < right.xMax && left.xMax > right.x &&
            left.y < right.yMax && left.yMax > right.y;

        static bool AtlasRectangleContains(RectInt outer, RectInt inner) =>
            outer.x <= inner.x && outer.y <= inner.y &&
            outer.xMax >= inner.xMax && outer.yMax >= inner.yMax;

        readonly struct AtlasBuddyBlock
        {
            internal readonly int x;
            internal readonly int y;
            internal readonly int size;

            internal AtlasBuddyBlock(int x, int y, int size)
            {
                this.x = x;
                this.y = y;
                this.size = size;
            }
        }

        static bool IsPowerOfTwo(int value) =>
            value > 0 && (value & (value - 1)) == 0;

        static int Log2(int value)
        {
            int result = 0;
            while (value > 1)
            {
                value >>= 1;
                ++result;
            }
            return result;
        }

        static int NextPowerOfTwoAtLeast(int value)
        {
            if (value <= 1)
                return 1;
            int result = 1;
            while (result < value && result <= (int.MaxValue >> 1))
                result <<= 1;
            return result;
        }

        static void EnsureAtlasBuddyLevels(int atlasSize)
        {
            int levelCount = Log2(atlasSize) + 1;
            if (s_AtlasBuddyFreeLists != null &&
                s_AtlasBuddyFreeLists.Length == levelCount)
                return;

            s_AtlasBuddyFreeLists = new List<AtlasBuddyBlock>[levelCount];
            for (int level = 0; level < levelCount; ++level)
                s_AtlasBuddyFreeLists[level] = new List<AtlasBuddyBlock>();
        }

        static AtlasBuddyBlock PopAtlasBuddyBlock(int level)
        {
            List<AtlasBuddyBlock> blocks = s_AtlasBuddyFreeLists[level];
            int last = blocks.Count - 1;
            AtlasBuddyBlock block = blocks[last];
            blocks.RemoveAt(last);
            return block;
        }

        static void ClearAtlasAllocations(
            NativeArray<ShadowResolutionRequest> requests, int shadowSlicesCount)
        {
            if (!requests.IsCreated)
                return;
            int requestCount = Math.Min(Math.Max(0, shadowSlicesCount), requests.Length);
            for (int requestIndex = 0; requestIndex < requestCount; ++requestIndex)
            {
                ref ShadowResolutionRequest request = ref requests.UnsafeElementAtMutable(requestIndex);
                request.offsetX = 0;
                request.offsetY = 0;
                request.allocatedResolution = 0;
            }
        }

        // Sort array in decreasing requestedResolution order,
        // sub-sorting in "HardShadow > SoftShadow",
        //   i.e place last requests that will be removed in priority to make room for the others,
        //   because their resolution is too small to produce good-looking shadows ; or because they take relatively more space in the atlas )
        // sub-sub-sorting in light distance to camera
        // then grouping in increasing visibleIndex (and sub-sorting each group in ShadowSliceIndex order)
        static Func<ShadowResolutionRequest, ShadowResolutionRequest, int> CreateCompareShadowResolutionRequesPredicate()
        {
            return (ShadowResolutionRequest curr, ShadowResolutionRequest other) =>
            {
                return (((curr.requestedResolution > other.requestedResolution)
                         || (curr.requestedResolution == other.requestedResolution && !curr.softShadow && other.softShadow)
                         || (curr.requestedResolution == other.requestedResolution && curr.softShadow == other.softShadow && s_VisibleLightIndexToCameraSquareDistance[curr.visibleLightIndex] < s_VisibleLightIndexToCameraSquareDistance[other.visibleLightIndex])
                         || (curr.requestedResolution == other.requestedResolution && curr.softShadow == other.softShadow && s_VisibleLightIndexToCameraSquareDistance[curr.visibleLightIndex] == s_VisibleLightIndexToCameraSquareDistance[other.visibleLightIndex] && curr.visibleLightIndex < other.visibleLightIndex)
                         || (curr.requestedResolution == other.requestedResolution && curr.softShadow == other.softShadow && s_VisibleLightIndexToCameraSquareDistance[curr.visibleLightIndex] == s_VisibleLightIndexToCameraSquareDistance[other.visibleLightIndex] && curr.visibleLightIndex == other.visibleLightIndex && curr.perLightShadowSliceIndex < other.perLightShadowSliceIndex)))
                    ? -1 : 1;
            };
        }
    }
}
