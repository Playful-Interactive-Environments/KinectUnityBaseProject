using OpenCvSharp;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static class BlobObjectDetectorUtilities
{
    public static Gradient BuildDefaultColorRampGradient()
    {
        var g = new Gradient();
        g.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.05f, 0.05f, 0.40f), 0.00f),
                new GradientColorKey(new Color(0.10f, 0.40f, 0.85f), 0.20f),
                new GradientColorKey(new Color(0.15f, 0.55f, 0.20f), 0.40f),
                new GradientColorKey(new Color(0.80f, 0.75f, 0.20f), 0.60f),
                new GradientColorKey(new Color(0.65f, 0.40f, 0.15f), 0.75f),
                new GradientColorKey(new Color(0.40f, 0.25f, 0.15f), 0.90f),
                new GradientColorKey(Color.white,                     1.00f),
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return g;
    }

    public static List<(int a, int b)> ComputeAllEdges(int[] triangles)
    {
        var seen = new HashSet<(int a, int b)>();
        var edges = new List<(int a, int b)>();

        void AddEdge(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (seen.Add(key)) edges.Add(key);
        }

        for (int t = 0; t < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            AddEdge(a, b); AddEdge(b, c); AddEdge(c, a);
        }

        return edges;
    }

    public static bool TryBuildContourTopMesh(Point[] contour, float epsilon, int smoothingIterations, out Vector2[] points, out int[] triangles)
    {
        Point[] simplified = Cv2.ApproxPolyDP(contour, epsilon, true);
        if (simplified.Length < 3)
        {
            points = Array.Empty<Vector2>();
            triangles = Array.Empty<int>();
            return false;
        }

        var asVectors = new Vector2[simplified.Length];
        for (int i = 0; i < simplified.Length; i++)
            asVectors[i] = new Vector2(simplified[i].X, simplified[i].Y);

        Vector2[] smoothed = ChaikinSmooth(asVectors, smoothingIterations);
        if (smoothed.Length < 3)
        {
            points = Array.Empty<Vector2>();
            triangles = Array.Empty<int>();
            return false;
        }

        points = smoothed;
        triangles = TriangulatePolygon(smoothed);
        return triangles.Length >= 3;
    }

    public static int[] TriangulatePolygon(Vector2[] polygon)
    {
        int n = polygon.Length;
        var indices = new List<int>(n);
        for (int i = 0; i < n; i++) indices.Add(i);
        if (SignedArea(polygon) < 0) indices.Reverse();

        var triangles = new List<int>((n - 2) * 3);
        int guard = 0;
        while (indices.Count > 3 && guard++ < n * n)
        {
            bool clipped = false;
            for (int i = 0; i < indices.Count; i++)
            {
                int i0 = indices[(i - 1 + indices.Count) % indices.Count];
                int i1 = indices[i];
                int i2 = indices[(i + 1) % indices.Count];
                Vector2 a = polygon[i0], b = polygon[i1], c = polygon[i2];
                if (!IsConvex(a, b, c)) continue;

                bool anyInside = false;
                for (int k = 0; k < indices.Count; k++)
                {
                    int idx = indices[k];
                    if (idx == i0 || idx == i1 || idx == i2) continue;
                    if (PointInTriangle(polygon[idx], a, b, c)) { anyInside = true; break; }
                }
                if (anyInside) continue;

                triangles.Add(i0); triangles.Add(i1); triangles.Add(i2);
                indices.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped) break;
        }
        if (indices.Count == 3) { triangles.Add(indices[0]); triangles.Add(indices[1]); triangles.Add(indices[2]); }
        return triangles.ToArray();
    }

    public static float SignedArea(Vector2[] poly)
    {
        float sum = 0f;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Length];
            sum += (a.x * b.y) - (b.x * a.y);
        }
        return sum * 0.5f;
    }

    public static bool IsConvex(Vector2 a, Vector2 b, Vector2 c) =>
        ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)) > 0f;

    public static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float Cross(Vector2 o, Vector2 e1, Vector2 e2) => (e1.x - o.x) * (e2.y - o.y) - (e1.y - o.y) * (e2.x - o.x);
        float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    public static float ComputeAverageDepthForContour(Mat grayMat, Point[] contourDownscaled, int downscale)
    {
        var scaled = new Point[contourDownscaled.Length];
        for (int i = 0; i < contourDownscaled.Length; i++)
            scaled[i] = new Point(contourDownscaled[i].X * downscale, contourDownscaled[i].Y * downscale);

        using (var mask = new Mat(grayMat.Size(), MatType.CV_8UC1, Scalar.Black))
        {
            Cv2.FillPoly(mask, new[] { scaled }, Scalar.White);
            return (float)Cv2.Mean(grayMat, mask).Val0;
        }
    }

    public static unsafe void ComputeDepthRange(Mat grayMat, Point[] blobPixelsDownscaled, int downscale, out ushort localMinDepth, out ushort localMaxDepth)
    {
        localMinDepth = ushort.MaxValue;
        localMaxDepth = ushort.MinValue;
        if (blobPixelsDownscaled.Length == 0) return;

        int w = grayMat.Width, h = grayMat.Height;
        int stride = Mathf.Max(1, blobPixelsDownscaled.Length / 200);

        ushort* grayPtr = (ushort*)grayMat.DataPointer;
        int rowStride = (int)(grayMat.Step() / sizeof(ushort));

        for (int i = 0; i < blobPixelsDownscaled.Length; i += stride)
        {
            int ox = blobPixelsDownscaled[i].X * downscale;
            int oy = blobPixelsDownscaled[i].Y * downscale;
            if (ox < 0 || ox >= w || oy < 0 || oy >= h) continue;
            ushort v = grayPtr[oy * rowStride + ox];
            if (v < localMinDepth) localMinDepth = v;
            if (v > localMaxDepth) localMaxDepth = v;
        }
    }

    private static Mat edgeArtifactKernel;

    public static void RemoveEdgeArtifacts(Mat binaryImage, int erosionIterations, int dilationIterations, int borderThickness)
    {
        if (binaryImage.Type() != MatType.CV_8UC1)
            binaryImage.ConvertTo(binaryImage, MatType.CV_8UC1);

        if (edgeArtifactKernel == null)
            edgeArtifactKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));

        if (erosionIterations > 0)
            Cv2.Erode(binaryImage, binaryImage, edgeArtifactKernel, iterations: erosionIterations);
        if (dilationIterations > 0)
            Cv2.Dilate(binaryImage, binaryImage, edgeArtifactKernel, iterations: dilationIterations);

        if (borderThickness >= 1)
        {
            byte colorValue = 255;
            binaryImage[new OpenCvSharp.Rect(0, 0, binaryImage.Cols, borderThickness)].SetTo(new Scalar(colorValue));
            binaryImage[new OpenCvSharp.Rect(0, binaryImage.Rows - borderThickness, binaryImage.Cols, borderThickness)].SetTo(new Scalar(colorValue));
            binaryImage[new OpenCvSharp.Rect(0, 0, borderThickness, binaryImage.Rows)].SetTo(new Scalar(colorValue));
            binaryImage[new OpenCvSharp.Rect(binaryImage.Cols - borderThickness, 0, borderThickness, binaryImage.Rows)].SetTo(new Scalar(colorValue));
        }

        Cv2.FloodFill(binaryImage, new Point(0, 0), Scalar.Black);
    }

    public static Vector2[] ChaikinSmooth(Vector2[] polygon, int iterations)
    {
        if (iterations <= 0 || polygon.Length < 3) return polygon;

        Vector2[] current = polygon;
        for (int iter = 0; iter < iterations; iter++)
        {
            int n = current.Length;
            var next = new Vector2[n * 2];
            for (int i = 0; i < n; i++)
            {
                Vector2 a = current[i];
                Vector2 b = current[(i + 1) % n];
                next[i * 2] = Vector2.Lerp(a, b, 0.25f);
                next[i * 2 + 1] = Vector2.Lerp(a, b, 0.75f);
            }
            current = next;
        }
        return current;
    }

    public static List<(int from, int to)> ComputeBoundaryEdges(int[] triangles)
    {
        var edgeCount = new Dictionary<(int a, int b), int>();
        var edgeDirection = new Dictionary<(int a, int b), (int from, int to)>();

        void AddEdge(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            edgeCount[key] = edgeCount.TryGetValue(key, out int c) ? c + 1 : 1;
            edgeDirection[key] = (a, b);
        }

        for (int t = 0; t < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            AddEdge(a, b); AddEdge(b, c); AddEdge(c, a);
        }

        var boundary = new List<(int from, int to)>();
        foreach (var kvp in edgeCount)
            if (kvp.Value == 1) boundary.Add(edgeDirection[kvp.Key]);
        return boundary;
    }

    public static List<Point[]> FindSimpleObjectBlobs(Mat binaryImage, double minArea, double maxArea)
    {
        var result = new List<Point[]>();

        using (var labels = new Mat())
        using (var stats = new Mat())
        using (var centroids = new Mat())
        {
            int numLabels = Cv2.ConnectedComponentsWithStats(binaryImage, labels, stats, centroids);
            if (numLabels <= 1) return result;

            var pixelBuckets = new List<Point>[numLabels];

            int w = labels.Cols, h = labels.Rows;

            unsafe
            {
                int* labelsPtr = (int*)labels.DataPointer;
                int stride = (int)(labels.Step() / sizeof(int));

                for (int y = 0; y < h; y++)
                {
                    int rowOffset = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int label = labelsPtr[rowOffset + x];
                        if (label == 0) continue;
                        (pixelBuckets[label] ??= new List<Point>()).Add(new Point(x, y));
                    }
                }
            }

            for (int label = 1; label < numLabels; label++)
            {
                double area = stats.At<int>(label, 4);
                if (area < minArea || area > maxArea) continue;
                if (pixelBuckets[label] == null) continue;

                result.Add(pixelBuckets[label].ToArray());
            }
        }

        return result;
    }

    public static unsafe float ComputeAverageDepth(Mat grayMat, Point[] blobPixelsDownscaled, int downscale)
    {
        if (blobPixelsDownscaled.Length == 0) return 0f;
        int w = grayMat.Width, h = grayMat.Height;
        int stride = Mathf.Max(1, blobPixelsDownscaled.Length / 200);

        long sum = 0;
        int count = 0;

        ushort* grayPtr = (ushort*)grayMat.DataPointer;
        int rowStride = (int)(grayMat.Step() / sizeof(ushort));

        for (int i = 0; i < blobPixelsDownscaled.Length; i += stride)
        {
            int ox = blobPixelsDownscaled[i].X * downscale;
            int oy = blobPixelsDownscaled[i].Y * downscale;
            if (ox < 0 || ox >= w || oy < 0 || oy >= h) continue;
            sum += grayPtr[oy * rowStride + ox];
            count++;
        }

        return count > 0 ? (float)sum / count : 0f;
    }

    public static void ExtrudePixelGridMesh(
        Mesh mesh,
        Vector3[] topVerts,
        int[] topTris,
        float[] vertexDepths,
        float depth,
        bool generateWalls,
        bool generateTopCap,
        bool invertWallWinding,
        bool invertBottomCapWinding,
        bool invertTopCapWinding,
        List<(int from, int to)> boundaryEdges = null)
    {
        mesh.Clear();
        int n = topVerts.Length;
        if (n < 3 || topTris.Length < 3) return;

        var down = new Vector3(0f, depth, 0f);
        var bottomVerts = new Vector3[n];
        for (int i = 0; i < n; i++) bottomVerts[i] = topVerts[i] - down;

        var vertices = new List<Vector3>(n * 2);
        vertices.AddRange(topVerts);
        int bottomStart = vertices.Count;
        vertices.AddRange(bottomVerts);

        // Build Vertex Colors matching position list
        var colors = new List<Color>(n * 2);
        for (int i = 0; i < n; i++)
        {
            float t = (vertexDepths != null && i < vertexDepths.Length) ? vertexDepths[i] : 0f;
            colors.Add(new Color(t, 0f, 0f, 1f)); // Store normalized depth in Red channel
        }
        // Mirror depth colors to bottom cap vertices
        for (int i = 0; i < n; i++)
        {
            colors.Add(colors[i]);
        }

        var triangles = new List<int>(topTris.Length * 2);

        if (invertBottomCapWinding)
        {
            for (int t = 0; t < topTris.Length; t += 3)
            {
                triangles.Add(topTris[t]);
                triangles.Add(topTris[t + 1]); //change 2 to 1 or inverse, to change the facing of the triangles!
                triangles.Add(topTris[t + 2]);
            }
        }
        else
        {
            for (int t = 0; t < topTris.Length; t += 3)
            {
                triangles.Add(topTris[t]);
                triangles.Add(topTris[t + 2]); //change 2 to 1 or inverse, to change the facing of the triangles!
                triangles.Add(topTris[t + 1]);
            }
        }

        if (generateTopCap)
        {
            if (invertTopCapWinding)
            {
                for (int t = 0; t < topTris.Length; t += 3)
                {
                    triangles.Add(bottomStart + topTris[t]);
                    triangles.Add(bottomStart + topTris[t + 1]); //change 2 to 1 or inverse, to change the facing of the triangles!
                    triangles.Add(bottomStart + topTris[t + 2]);
                }
            }
            else
            {
                for (int t = 0; t < topTris.Length; t += 3)
                {
                    triangles.Add(bottomStart + topTris[t]);
                    triangles.Add(bottomStart + topTris[t + 2]); //change 2 to 1 or inverse, to change the facing of the triangles!
                    triangles.Add(bottomStart + topTris[t + 1]);
                }
            }
        }

        if (generateWalls)
        {
            var edges = boundaryEdges ?? ComputeBoundaryEdges(topTris);

            if (invertWallWinding)
            {
                foreach (var (a, b) in edges)
                {
                    int ta = a, tb = b, ba = bottomStart + a, bb = bottomStart + b;
                    triangles.Add(ta); triangles.Add(ba); triangles.Add(tb);
                    triangles.Add(tb); triangles.Add(ba); triangles.Add(bb);
                }
            }
            else
            {
                foreach (var (a, b) in edges)
                {
                    int ta = a, tb = b, ba = bottomStart + a, bb = bottomStart + b;
                    triangles.Add(ta); triangles.Add(tb); triangles.Add(ba);
                    triangles.Add(tb); triangles.Add(bb); triangles.Add(ba);
                }
            }
        }

        mesh.SetVertices(vertices);
        mesh.SetColors(colors); // Assign color channel data
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }
}
