using System;
using System.Collections.Generic;
using Unity.Collections;

namespace UnityEngine.Rendering.Universal
{
    internal static class InternalEngineBridge
    {
#if !UNITY_6000_0_OR_NEWER
        // Unity 2022 exposes the add/remove callbacks but does not expose the
        // aggregate `SortingLayer.onLayerChanged` event that Unity 6 added.
        // Keep a single bridge subscription and fan the notification out to
        // all callers so the Light2D sorting-layer cache is invalidated when
        // layers are edited in a 2022 project.
        static Action s_LegacyLayerChangedCallbacks;
        static bool s_LegacyLayerEventsSubscribed;

        static void OnLegacyLayerChanged(SortingLayer _)
        {
            s_LegacyLayerChangedCallbacks?.Invoke();
        }

        static void EnsureLegacyLayerEventsSubscribed()
        {
            if (s_LegacyLayerEventsSubscribed)
                return;

            SortingLayer.onLayerAdded += OnLegacyLayerChanged;
            SortingLayer.onLayerRemoved += OnLegacyLayerChanged;
            s_LegacyLayerEventsSubscribed = true;
        }

        static void RemoveLegacyLayerEventsSubscriptionIfUnused()
        {
            if (s_LegacyLayerChangedCallbacks != null || !s_LegacyLayerEventsSubscribed)
                return;

            SortingLayer.onLayerAdded -= OnLegacyLayerChanged;
            SortingLayer.onLayerRemoved -= OnLegacyLayerChanged;
            s_LegacyLayerEventsSubscribed = false;
        }
#endif

        internal static void AddOnLayerchangedCallback(Action callback)
        {
#if UNITY_6000_0_OR_NEWER
            SortingLayer.onLayerChanged += callback;
#else
            if (callback == null)
                return;

            s_LegacyLayerChangedCallbacks += callback;
            EnsureLegacyLayerEventsSubscribed();
#endif
        }

        internal static void RemoveOnLayerchangedCallback(Action callback)
        {
#if UNITY_6000_0_OR_NEWER
            SortingLayer.onLayerChanged -= callback;
#else
            if (callback == null)
                return;

            s_LegacyLayerChangedCallbacks -= callback;
            RemoveLegacyLayerEventsSubscriptionIfUnused();
#endif
        }
    }

#if !UNITY_6000_0_OR_NEWER
    // Unity 6 moved its polygon clipping implementation into native engine APIs.
    // Unity 2022 does not expose those APIs, but this package still contains the
    // equivalent managed Clipper implementation used by earlier URP releases.
    // These facades preserve the Unity 6 call shape while executing real union
    // and offset operations through that implementation.
    internal static class Clipper2D
    {
        internal enum ClipType
        {
            ctIntersection,
            ctUnion,
            ctDifference,
            ctXor
        }

        internal enum PolyType
        {
            ptSubject,
            ptClip
        }

        internal enum PolyFillType
        {
            pftEvenOdd,
            pftNonZero,
            pftPositive,
            pftNegative
        }

        [Flags]
        internal enum InitOptions
        {
            ioDefault = 0,
            oReverseSolution = 1,
            ioStrictlySimple = 2,
            ioPreserveCollinear = 4
        }

        internal struct PathArguments
        {
            internal PolyType polyType;
            internal bool closed;

            internal PathArguments(PolyType inPolyType = PolyType.ptSubject, bool inClosed = false)
            {
                polyType = inPolyType;
                closed = inClosed;
            }
        }

        internal struct ExecuteArguments
        {
            internal InitOptions initOption;
            internal ClipType clipType;
            internal PolyFillType subjFillType;
            internal PolyFillType clipFillType;
            internal bool reverseSolution;
            internal bool strictlySimple;
            internal bool preserveColinear;

            internal ExecuteArguments(
                InitOptions inInitOption = InitOptions.ioDefault,
                ClipType inClipType = ClipType.ctIntersection,
                PolyFillType inSubjFillType = PolyFillType.pftEvenOdd,
                PolyFillType inClipFillType = PolyFillType.pftEvenOdd,
                bool inReverseSolution = false,
                bool inStrictlySimple = false,
                bool inPreserveColinear = false)
            {
                initOption = inInitOption;
                clipType = inClipType;
                subjFillType = inSubjFillType;
                clipFillType = inClipFillType;
                reverseSolution = inReverseSolution;
                strictlySimple = inStrictlySimple;
                preserveColinear = inPreserveColinear;
            }
        }

        internal struct Solution : IDisposable
        {
            internal NativeArray<Vector2> points;
            internal NativeArray<int> pathSizes;
            internal NativeArray<Rect> boundingRect;

            internal Solution(int pointsBufferSize, int pathSizesBufferSize, Allocator allocator)
            {
                points = new NativeArray<Vector2>(pointsBufferSize, allocator);
                pathSizes = new NativeArray<int>(pathSizesBufferSize, allocator);
                boundingRect = new NativeArray<Rect>(1, allocator);
            }

            public void Dispose()
            {
                if (points.IsCreated)
                    points.Dispose();
                if (pathSizes.IsCreated)
                    pathSizes.Dispose();
                if (boundingRect.IsCreated)
                    boundingRect.Dispose();
            }
        }

        internal static void Execute(
            ref Solution solution,
            NativeArray<Vector2> inPoints,
            NativeArray<int> inPathSizes,
            NativeArray<PathArguments> inPathArguments,
            ExecuteArguments inExecuteArguments,
            Allocator inSolutionAllocator,
            int inIntScale = 65536,
            bool useRounding = false)
        {
            if (inPathSizes.Length != inPathArguments.Length)
                throw new ArgumentException("Path sizes and path arguments must have the same length.");

            double scale = inIntScale == 0 ? 1.0 : inIntScale;
            var clipper = new Clipper((int)inExecuteArguments.initOption)
            {
                ReverseSolution = inExecuteArguments.reverseSolution ||
                    (inExecuteArguments.initOption & InitOptions.oReverseSolution) != 0,
                StrictlySimple = inExecuteArguments.strictlySimple ||
                    (inExecuteArguments.initOption & InitOptions.ioStrictlySimple) != 0,
                PreserveCollinear = inExecuteArguments.preserveColinear ||
                    (inExecuteArguments.initOption & InitOptions.ioPreserveCollinear) != 0
            };

            bool hasOpenPaths = false;
            int pointOffset = 0;
            for (int pathIndex = 0; pathIndex < inPathSizes.Length; ++pathIndex)
            {
                int pathSize = inPathSizes[pathIndex];
                if (pathSize < 0 || pointOffset + pathSize > inPoints.Length)
                    throw new ArgumentException("A path size exceeds the supplied point buffer.");

                var path = new List<IntPoint>(pathSize);
                for (int pointIndex = 0; pointIndex < pathSize; ++pointIndex)
                {
                    Vector2 point = inPoints[pointOffset + pointIndex];
                    path.Add(new IntPoint(
                        ToClipperCoordinate(point.x, scale, useRounding),
                        ToClipperCoordinate(point.y, scale, useRounding)));
                }

                PathArguments pathArguments = inPathArguments[pathIndex];
                clipper.AddPath(path, (PolyTypes)(int)pathArguments.polyType, pathArguments.closed);
                hasOpenPaths |= !pathArguments.closed;
                pointOffset += pathSize;
            }

            if (pointOffset != inPoints.Length)
                throw new ArgumentException("The path sizes do not consume the supplied point buffer.");

            var paths = new List<List<IntPoint>>();
            bool succeeded;
            if (hasOpenPaths)
            {
                var polyTree = new PolyTree();
                succeeded = clipper.Execute(
                    (ClipTypes)(int)inExecuteArguments.clipType,
                    polyTree,
                    (PolyFillTypes)(int)inExecuteArguments.subjFillType,
                    (PolyFillTypes)(int)inExecuteArguments.clipFillType);
                if (succeeded)
                {
                    paths.AddRange(Clipper.ClosedPathsFromPolyTree(polyTree));
                    paths.AddRange(Clipper.OpenPathsFromPolyTree(polyTree));
                }
            }
            else
            {
                succeeded = clipper.Execute(
                    (ClipTypes)(int)inExecuteArguments.clipType,
                    paths,
                    (PolyFillTypes)(int)inExecuteArguments.subjFillType,
                    (PolyFillTypes)(int)inExecuteArguments.clipFillType);
            }

            if (!succeeded)
                paths.Clear();

            PopulateSolution(ref solution, paths, inSolutionAllocator, scale);
        }

        static long ToClipperCoordinate(float value, double scale, bool useRounding)
        {
            double scaledValue = value * scale;
            return useRounding
                ? (long)Math.Round(scaledValue, MidpointRounding.AwayFromZero)
                : (long)scaledValue;
        }

        static void PopulateSolution(
            ref Solution solution,
            List<List<IntPoint>> paths,
            Allocator allocator,
            double scale)
        {
            solution.Dispose();

            int pointCount = 0;
            for (int i = 0; i < paths.Count; ++i)
                pointCount += paths[i].Count;

            solution.points = new NativeArray<Vector2>(pointCount, allocator);
            solution.pathSizes = new NativeArray<int>(paths.Count, allocator);
            // Unity 6 keeps one aggregate bounding rectangle regardless of the
            // number of returned paths (its Solution constructor always creates
            // a one-element buffer).
            solution.boundingRect = new NativeArray<Rect>(1, allocator);

            float inverseScale = (float)(1.0 / scale);
            int pointOffset = 0;
            float boundsMinX = float.PositiveInfinity;
            float boundsMinY = float.PositiveInfinity;
            float boundsMaxX = float.NegativeInfinity;
            float boundsMaxY = float.NegativeInfinity;
            for (int pathIndex = 0; pathIndex < paths.Count; ++pathIndex)
            {
                List<IntPoint> path = paths[pathIndex];
                solution.pathSizes[pathIndex] = path.Count;
                for (int pointIndex = 0; pointIndex < path.Count; ++pointIndex)
                {
                    Vector2 point = new Vector2(
                        path[pointIndex].X * inverseScale,
                        path[pointIndex].Y * inverseScale);
                    solution.points[pointOffset++] = point;
                    boundsMinX = Math.Min(boundsMinX, point.x);
                    boundsMinY = Math.Min(boundsMinY, point.y);
                    boundsMaxX = Math.Max(boundsMaxX, point.x);
                    boundsMaxY = Math.Max(boundsMaxY, point.y);
                }
            }

            solution.boundingRect[0] = pointCount == 0
                ? default
                : Rect.MinMaxRect(boundsMinX, boundsMinY, boundsMaxX, boundsMaxY);
        }
    }

    internal static class ClipperOffset2D
    {
        internal enum JoinType
        {
            jtSquare,
            jtRound,
            jtMiter
        }

        internal enum EndType
        {
            etClosedPolygon,
            etClosedLine,
            etOpenButt,
            etOpenSquare,
            etOpenRound
        }

        internal struct PathArguments
        {
            internal JoinType joinType;
            internal EndType endType;

            internal PathArguments(JoinType inJoinType = JoinType.jtSquare, EndType inEndType = EndType.etClosedPolygon)
            {
                joinType = inJoinType;
                endType = inEndType;
            }
        }

        internal struct Solution
        {
            internal NativeArray<Vector2> points;
            internal NativeArray<int> pathSizes;

            internal Solution(int pointsBufferSize, int pathSizesBufferSize, Allocator allocator)
            {
                points = new NativeArray<Vector2>(pointsBufferSize, allocator);
                pathSizes = new NativeArray<int>(pathSizesBufferSize, allocator);
            }

            public void Dispose()
            {
                if (points.IsCreated)
                    points.Dispose();
                if (pathSizes.IsCreated)
                    pathSizes.Dispose();
            }
        }

        internal static void Execute(
            ref Solution solution,
            NativeArray<Vector2> inPoints,
            NativeArray<int> inPathSizes,
            NativeArray<PathArguments> inPathArguments,
            Allocator inSolutionAllocator,
            double inDelta = 0.0,
            double inMiterLimit = 2.0,
            double inRoundPrecision = 0.25,
            double inArcTolerance = 0.0,
            double inIntScale = 65536.0,
            bool useRounding = false)
        {
            if (inPathSizes.Length != inPathArguments.Length)
                throw new ArgumentException("Path sizes and path arguments must have the same length.");

            double scale = Math.Abs(inIntScale) < double.Epsilon ? 1.0 : inIntScale;
            double arcTolerance = inArcTolerance > 0.0 ? inArcTolerance * scale : inRoundPrecision;
            var clipperOffset = new ClipperOffset(arcTolerance)
            {
                MiterLimit = inMiterLimit
            };

            int pointOffset = 0;
            for (int pathIndex = 0; pathIndex < inPathSizes.Length; ++pathIndex)
            {
                int pathSize = inPathSizes[pathIndex];
                if (pathSize < 0 || pointOffset + pathSize > inPoints.Length)
                    throw new ArgumentException("A path size exceeds the supplied point buffer.");

                var path = new List<IntPoint>(pathSize);
                for (int pointIndex = 0; pointIndex < pathSize; ++pointIndex)
                {
                    Vector2 point = inPoints[pointOffset + pointIndex];
                    path.Add(new IntPoint(
                        ToClipperCoordinate(point.x, scale, useRounding),
                        ToClipperCoordinate(point.y, scale, useRounding)));
                }

                PathArguments pathArguments = inPathArguments[pathIndex];
                clipperOffset.AddPath(
                    path,
                    (JoinTypes)(int)pathArguments.joinType,
                    (EndTypes)(int)pathArguments.endType);
                pointOffset += pathSize;
            }

            if (pointOffset != inPoints.Length)
                throw new ArgumentException("The path sizes do not consume the supplied point buffer.");

            var paths = new List<List<IntPoint>>();
            clipperOffset.Execute(ref paths, inDelta * scale, inPoints.Length);
            PopulateSolution(ref solution, paths, inSolutionAllocator, scale);
        }

        static long ToClipperCoordinate(float value, double scale, bool useRounding)
        {
            double scaledValue = value * scale;
            return useRounding
                ? (long)Math.Round(scaledValue, MidpointRounding.AwayFromZero)
                : (long)scaledValue;
        }

        static void PopulateSolution(
            ref Solution solution,
            List<List<IntPoint>> paths,
            Allocator allocator,
            double scale)
        {
            solution.Dispose();

            int pointCount = 0;
            for (int i = 0; i < paths.Count; ++i)
                pointCount += paths[i].Count;

            solution.points = new NativeArray<Vector2>(pointCount, allocator);
            solution.pathSizes = new NativeArray<int>(paths.Count, allocator);

            float inverseScale = (float)(1.0 / scale);
            int pointOffset = 0;
            for (int pathIndex = 0; pathIndex < paths.Count; ++pathIndex)
            {
                List<IntPoint> path = paths[pathIndex];
                solution.pathSizes[pathIndex] = path.Count;
                for (int pointIndex = 0; pointIndex < path.Count; ++pointIndex)
                {
                    solution.points[pointOffset++] = new Vector2(
                        path[pointIndex].X * inverseScale,
                        path[pointIndex].Y * inverseScale);
                }
            }
        }
    }
#endif
}
