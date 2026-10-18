#if !UNITY_6000_0_OR_NEWER
using System;
using NUnit.Framework;
using Unity.Collections;

namespace UnityEngine.Rendering.Universal
{
    class Clipper2DCompatibilityTests
    {
        const float k_Tolerance = 0.002f;

        [Test]
        public void Union_NonZeroOverlappingPolygons_ReturnsMergedOutlineAndAggregateBounds()
        {
            using var points = CreateArray(
                new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 2), new Vector2(0, 2),
                new Vector2(1, 0), new Vector2(3, 0), new Vector2(3, 2), new Vector2(1, 2));
            using var pathSizes = CreateArray(4, 4);
            using var pathArguments = CreateArray(
                new Clipper2D.PathArguments(Clipper2D.PolyType.ptSubject, true),
                new Clipper2D.PathArguments(Clipper2D.PolyType.ptSubject, true));
            var solution = default(Clipper2D.Solution);

            try
            {
                Clipper2D.Execute(
                    ref solution,
                    points,
                    pathSizes,
                    pathArguments,
                    new Clipper2D.ExecuteArguments(
                        Clipper2D.InitOptions.ioDefault,
                        Clipper2D.ClipType.ctUnion,
                        Clipper2D.PolyFillType.pftNonZero,
                        Clipper2D.PolyFillType.pftNonZero),
                    Allocator.Temp,
                    useRounding: true);

                Assert.That(solution.pathSizes.Length, Is.EqualTo(1));
                Assert.That(Math.Abs(GetSignedArea(solution)), Is.EqualTo(6.0f).Within(k_Tolerance));
                Assert.That(solution.boundingRect.Length, Is.EqualTo(1));
                Assert.That(solution.boundingRect[0].xMin, Is.EqualTo(0.0f).Within(k_Tolerance));
                Assert.That(solution.boundingRect[0].xMax, Is.EqualTo(3.0f).Within(k_Tolerance));
                Assert.That(solution.boundingRect[0].yMin, Is.EqualTo(0.0f).Within(k_Tolerance));
                Assert.That(solution.boundingRect[0].yMax, Is.EqualTo(2.0f).Within(k_Tolerance));
            }
            finally
            {
                solution.Dispose();
            }
        }

        [Test]
        public void Union_EvenOddNestedPolygons_PreservesHole()
        {
            using var points = CreateArray(
                new Vector2(0, 0), new Vector2(4, 0), new Vector2(4, 4), new Vector2(0, 4),
                new Vector2(1, 1), new Vector2(3, 1), new Vector2(3, 3), new Vector2(1, 3));
            using var pathSizes = CreateArray(4, 4);
            using var pathArguments = CreateArray(
                new Clipper2D.PathArguments(Clipper2D.PolyType.ptSubject, true),
                new Clipper2D.PathArguments(Clipper2D.PolyType.ptSubject, true));
            var solution = default(Clipper2D.Solution);

            try
            {
                Clipper2D.Execute(
                    ref solution,
                    points,
                    pathSizes,
                    pathArguments,
                    new Clipper2D.ExecuteArguments(
                        inClipType: Clipper2D.ClipType.ctUnion,
                        inSubjFillType: Clipper2D.PolyFillType.pftEvenOdd,
                        inClipFillType: Clipper2D.PolyFillType.pftEvenOdd),
                    Allocator.Temp,
                    useRounding: true);

                Assert.That(solution.pathSizes.Length, Is.EqualTo(2));
                Assert.That(Math.Abs(GetSignedArea(solution)), Is.EqualTo(12.0f).Within(k_Tolerance));
            }
            finally
            {
                solution.Dispose();
            }
        }

        [Test]
        public void Union_OpenSubjectPath_ReturnsOpenPath()
        {
            using var points = CreateArray(new Vector2(0, 0), new Vector2(1, 1), new Vector2(2, 0));
            using var pathSizes = CreateArray(3);
            using var pathArguments = CreateArray(
                new Clipper2D.PathArguments(Clipper2D.PolyType.ptSubject, false));
            var solution = default(Clipper2D.Solution);

            try
            {
                Clipper2D.Execute(
                    ref solution,
                    points,
                    pathSizes,
                    pathArguments,
                    new Clipper2D.ExecuteArguments(inClipType: Clipper2D.ClipType.ctUnion),
                    Allocator.Temp,
                    useRounding: true);

                Assert.That(solution.pathSizes.Length, Is.EqualTo(1));
                Assert.That(solution.pathSizes[0], Is.EqualTo(3));
            }
            finally
            {
                solution.Dispose();
            }
        }

        [Test]
        public void Offset_ClosedSquare_ExpandsAndContractsForSignedDelta()
        {
            using var points = CreateArray(
                new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 2), new Vector2(0, 2));
            using var pathSizes = CreateArray(4);
            using var pathArguments = CreateArray(new ClipperOffset2D.PathArguments());
            var expanded = default(ClipperOffset2D.Solution);
            var contracted = default(ClipperOffset2D.Solution);

            try
            {
                ClipperOffset2D.Execute(
                    ref expanded, points, pathSizes, pathArguments, Allocator.Temp,
                    inDelta: 0.5, useRounding: true);
                ClipperOffset2D.Execute(
                    ref contracted, points, pathSizes, pathArguments, Allocator.Temp,
                    inDelta: -0.5, useRounding: true);

                Assert.That(Math.Abs(GetSignedArea(expanded)), Is.GreaterThan(4.0f));
                Assert.That(Math.Abs(GetSignedArea(contracted)), Is.LessThan(4.0f));
                Assert.That(Math.Abs(GetSignedArea(contracted)), Is.GreaterThan(0.0f));
            }
            finally
            {
                expanded.Dispose();
                contracted.Dispose();
            }
        }

        [Test]
        public void Offset_OpenPath_SupportsButtSquareAndRoundCaps()
        {
            using var points = CreateArray(new Vector2(0, 0), new Vector2(2, 0));
            using var pathSizes = CreateArray(2);

            foreach (ClipperOffset2D.EndType endType in new[]
                     {
                         ClipperOffset2D.EndType.etOpenButt,
                         ClipperOffset2D.EndType.etOpenSquare,
                         ClipperOffset2D.EndType.etOpenRound
                     })
            {
                using var pathArguments = CreateArray(
                    new ClipperOffset2D.PathArguments(ClipperOffset2D.JoinType.jtSquare, endType));
                var solution = default(ClipperOffset2D.Solution);
                try
                {
                    ClipperOffset2D.Execute(
                        ref solution, points, pathSizes, pathArguments, Allocator.Temp,
                        inDelta: 0.25, useRounding: true);

                    Assert.That(solution.pathSizes.Length, Is.EqualTo(1), endType.ToString());
                    Assert.That(solution.points.Length, Is.GreaterThanOrEqualTo(4), endType.ToString());
                }
                finally
                {
                    solution.Dispose();
                }
            }
        }

        [Test]
        public void ClipEdges_ClosedThenOpenPath_RemapsBothOpenEdgeEndpoints()
        {
            using var vertices = CreateArray(
                new Vector3(0, 0), new Vector3(2, 0), new Vector3(2, 2), new Vector3(0, 2),
                new Vector3(4, 0), new Vector3(5, 1), new Vector3(6, 0));
            using var edges = CreateArray(
                new ShadowEdge(0, 1), new ShadowEdge(1, 2),
                new ShadowEdge(2, 3), new ShadowEdge(3, 0),
                new ShadowEdge(4, 5), new ShadowEdge(5, 6));
            using var shapeStartingEdges = CreateArray(0, 4);
            using var shapeIsClosed = CreateArray(true, false);

            var inputVertices = vertices;
            var inputEdges = edges;
            var inputShapeStartingEdges = shapeStartingEdges;
            var inputShapeIsClosed = shapeIsClosed;
            ShadowUtility.ClipEdges(
                ref inputVertices,
                ref inputEdges,
                ref inputShapeStartingEdges,
                ref inputShapeIsClosed,
                0.0f,
                out NativeArray<Vector3> clippedVertices,
                out NativeArray<ShadowEdge> clippedEdges,
                out NativeArray<int> clippedShapeStartingEdges);

            try
            {
                Assert.That(clippedShapeStartingEdges.Length, Is.EqualTo(2));
                int openPathStart = clippedShapeStartingEdges[1];
                Assert.That(openPathStart, Is.GreaterThan(0));
                Assert.That(clippedVertices[openPathStart], Is.EqualTo(vertices[4]));
                Assert.That(clippedVertices[openPathStart + 1], Is.EqualTo(vertices[5]));
                Assert.That(clippedVertices[openPathStart + 2], Is.EqualTo(vertices[6]));
                Assert.That(clippedEdges[openPathStart].v0, Is.EqualTo(openPathStart));
                Assert.That(clippedEdges[openPathStart].v1, Is.EqualTo(openPathStart + 1));
                Assert.That(clippedEdges[openPathStart + 1].v0, Is.EqualTo(openPathStart + 1));
                Assert.That(clippedEdges[openPathStart + 1].v1, Is.EqualTo(openPathStart + 2));
            }
            finally
            {
                clippedVertices.Dispose();
                clippedEdges.Dispose();
                clippedShapeStartingEdges.Dispose();
            }
        }

        [Test]
        public void EmptyInputAndInvalidLayout_HaveDeterministicLifetimeBehavior()
        {
            using var noPoints = new NativeArray<Vector2>(0, Allocator.Temp);
            using var noPathSizes = new NativeArray<int>(0, Allocator.Temp);
            using var noPathArguments = new NativeArray<Clipper2D.PathArguments>(0, Allocator.Temp);
            var emptySolution = default(Clipper2D.Solution);

            Clipper2D.Execute(
                ref emptySolution,
                noPoints,
                noPathSizes,
                noPathArguments,
                new Clipper2D.ExecuteArguments(inClipType: Clipper2D.ClipType.ctUnion),
                Allocator.Temp);

            Assert.That(emptySolution.points.IsCreated, Is.True);
            Assert.That(emptySolution.points.Length, Is.Zero);
            Assert.That(emptySolution.pathSizes.Length, Is.Zero);
            Assert.That(emptySolution.boundingRect.Length, Is.EqualTo(1));
            emptySolution.Dispose();
            Assert.That(emptySolution.points.IsCreated, Is.False);
            Assert.That(emptySolution.pathSizes.IsCreated, Is.False);
            Assert.That(emptySolution.boundingRect.IsCreated, Is.False);

            using var onePoint = CreateArray(Vector2.zero);
            using var mismatchedSizes = CreateArray(1);
            var existingSolution = new Clipper2D.Solution(1, 1, Allocator.Temp);
            try
            {
                Assert.Throws<ArgumentException>(() => Clipper2D.Execute(
                    ref existingSolution,
                    onePoint,
                    mismatchedSizes,
                    noPathArguments,
                    default,
                    Allocator.Temp));
                Assert.That(existingSolution.points.IsCreated, Is.True);
            }
            finally
            {
                existingSolution.Dispose();
            }
        }

        static NativeArray<T> CreateArray<T>(params T[] values) where T : struct
        {
            return new NativeArray<T>(values, Allocator.Temp);
        }

        static float GetSignedArea(Clipper2D.Solution solution)
        {
            return GetSignedArea(solution.points, solution.pathSizes);
        }

        static float GetSignedArea(ClipperOffset2D.Solution solution)
        {
            return GetSignedArea(solution.points, solution.pathSizes);
        }

        static float GetSignedArea(NativeArray<Vector2> points, NativeArray<int> pathSizes)
        {
            float area = 0.0f;
            int pointOffset = 0;
            for (int pathIndex = 0; pathIndex < pathSizes.Length; ++pathIndex)
            {
                int pathSize = pathSizes[pathIndex];
                for (int pointIndex = 0; pointIndex < pathSize; ++pointIndex)
                {
                    Vector2 current = points[pointOffset + pointIndex];
                    Vector2 next = points[pointOffset + (pointIndex + 1) % pathSize];
                    area += current.x * next.y - next.x * current.y;
                }

                pointOffset += pathSize;
            }

            return area * 0.5f;
        }
    }
}
#endif
