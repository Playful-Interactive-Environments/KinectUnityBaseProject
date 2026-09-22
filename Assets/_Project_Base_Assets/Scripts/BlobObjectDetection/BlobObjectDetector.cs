using OpenCvSharp;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using static BlobObjectDetectorUtilities;
using static Settings;

public class BlobObjectDetector : MonoBehaviour
{
    public enum DebugStage
    {
        None,
        Grayscale,
        Threshold,
        Cleaned,
        Downscaled
    }

    public enum MeshSimplificationMode
    {
        None,
        ContourSimplify
    }

    public static readonly List<Hand> Hands = new List<Hand>();

    [Header("Dependencies")]
    [SerializeField] private Settings settings;
    [SerializeField] private PlaygroundPlane playgroundPlane;
    [SerializeField] private Transform originPivot;
    [SerializeField] private Material handMeshMaterial;

    [Header("Depth Range Filter")]
    [SerializeField, Range(0, 65535)] private float depthMin = 450f;
    [SerializeField, Range(0, 65535)] private float depthMax = 510f;

    [SerializeField] private float heightOffset = 1f;

    [Header("Blob Cleanup Settings")]
    [SerializeField] private int erosionIterations = 1;
    [SerializeField] private int dilationIterations = 1;
    [SerializeField] private int edgeBorderThickness = 0;
    [SerializeField] private double minContourArea = 40;
    [SerializeField] private double maxContourArea = 100000;

    [Header("3D Volume Settings")]
    [SerializeField] private bool generateWalls = true;
    [SerializeField] private bool generateTopCap = true;
    [SerializeField] private bool generateMeshCollider = true;
    [SerializeField, Tooltip("Flip bottom cap winding. Default has it facing upward; enable to face downward.")]
        private bool invertBottomCapWinding = false;

    [SerializeField, Tooltip("Flip top cap winding. Default has it facing upward; enable to face downward.")]
        private bool invertTopCapWinding = false;

    [SerializeField, Tooltip("Flip wall winding.")]
        private bool invertWallWinding = false;

    [SerializeField] private float extrusionDepth = 0.05f;

    [SerializeField, Tooltip("Downscale factor for faster contour processing (1 = full res, 2 = half res).")]
    [Range(1, 4)] private int processingDownscale = 1;

    [Header("Mesh Simplification (for Non-Vertex Height Displacement)")]
    [SerializeField] private MeshSimplificationMode simplificationMode = MeshSimplificationMode.None;
    [SerializeField, Range(0.0f, 10f)] private float contourSimplificationEpsilon = 0f;
    [SerializeField, Range(0, 5)] private int edgeSmoothingIterations = 0;

    [Header("Vertex Height Displacement")]
    [SerializeField] private bool enableDisplacement = false;
    [SerializeField] private float displacementMax = 100f;
    [SerializeField, Range(0, 100)] private int displacementSmoothingIterations = 2;
    [SerializeField, Range(0f, 1f), Tooltip("Blend weight for tangential position smoothing at the seam between real and dilation-filled depth.")]
    private float seamPositionSmoothingWeight = 0.15f;
    [SerializeField, Tooltip("Color ramp evaluated by the blob shader from each vertex's normalized color-depth value.")]
    private Gradient colorRampGradient = BuildDefaultColorRampGradient();
    [SerializeField, Range(16, 1024)] private int colorRampLUTSize = 256;
    [SerializeField, Range(0, 2000)] private float colorDepthMin = 450f;
    [SerializeField, Range(0, 2000)] private float colorDepthMax = 510f;
    [SerializeField] private bool invertColorMapping = false;

    [Header("Vertex Height Displacement - Adaptive Displacement Mesh")]
    [SerializeField, Tooltip("Use higher vertex density near the blob edge and a coarser grid in the interior, instead of a uniform per-pixel grid.")]
    private bool enableAdaptiveMesh = false;
    [SerializeField, Tooltip("Distance (in processed pixels) from the blob boundary that stays full resolution.")]
    private float adaptiveEdgeBandWidth = 6f;
    [SerializeField, Tooltip("Interior sampling stride in pixels — larger values mean fewer interior vertices.")]
    [Range(1, 16)] private int adaptiveInteriorStride = 4;

    [Header("Dynamic (Wall) Height Settings")]
    [SerializeField] private bool enableDynamicWallHeight = true;
    [SerializeField] private DynamicHeightMode dynamicHeightMode = DynamicHeightMode.DynamicInstanceHeight;
    [SerializeField] private float minInstanceHeight = 0.02f;
    [SerializeField] private float maxInstanceHeight = 0.15f;
    [SerializeField] private bool invertHeightMapping = false;
    [SerializeField] private float fixedWallHeight = 0.05f;
    [SerializeField] private float heightMultiplier = 1.0f;

    [Header("Pipeline Debug View")]
    [SerializeField] private DebugStage debugStage = DebugStage.None;
    [SerializeField] private Camera debugTargetCamera;
    [SerializeField, Range(0.1f, 0.5f)] private float debugOverlayScreenWidthFraction = 0.25f;
    [SerializeField] private float debugOverlayDistance = 1.0f;
    [SerializeField, Range(0f, 0.1f)] private float debugOverlayMarginFraction = 0.02f;

    public Texture2D RoiTexture { get; private set; }
    public Texture2D CroppedTexture => RoiTexture;
    public static event Action<Texture> OnActiveTextureChanged;

    private readonly List<GameObject> segmentObjs = new List<GameObject>();
    private readonly List<Mesh> segmentMeshes = new List<Mesh>();
    private Transform segmentsRoot;
    private Texture2D readbackTex;
    private Texture2D colorRampLUT;

    private readonly Dictionary<DebugStage, Texture2D> debugTextures = new Dictionary<DebugStage, Texture2D>();
    private GameObject debugOverlayGo;
    private DebugStage lastAppliedDebugStage = DebugStage.None;

    private bool[] gridOccupancy;
    private int[] gridIndex;
    private byte[] roiRawBytesBuffer;

    private int[] neighborDegree;
    private int[] neighborStart;
    private int[] neighborFlat;
    private int[] neighborCursor;

    private float[] seamAccumDts;
    private Vector3[] seamAccumPos;
    private int[] seamNeighborCounts;
    private int[] seamEdgeA, seamEdgeB;
    private int[] seamVertexArr;

    private float[] scratchDts;
    private bool[] scratchIsExtrapolated;
    private float[] scratchNeighborAvg;
    private int[] scratchCounts;
    private float[] scratchClippedDts;
    private float[] scratchOffsets;

    private Mat adaptiveMaskMat = new Mat();
    private Mat adaptiveDistMat = new Mat();
    private Subdiv2D adaptiveSubdiv = new Subdiv2D();
    private readonly List<Point> adaptiveSelectedPoints = new List<Point>();
    private readonly HashSet<long> adaptiveSeenKeys = new HashSet<long>();
    private readonly Dictionary<(int, int), int> adaptivePtMap = new Dictionary<(int, int), int>();
    private readonly List<int> adaptiveTriList = new List<int>();

    // new field, reused across frames (grow-only, same pattern as your other scratch buffers)
    private Point2f[] adaptiveInsertBuffer = new Point2f[256];

    private Mat roiDisplayMat = new Mat();
    private readonly Dictionary<DebugStage, byte[]> debugRawBytesBuffers = new Dictionary<DebugStage, byte[]>();
    private Mat debugDisplayMat = new Mat();

    public enum DynamicHeightMode
    {
        DynamicInstanceHeight,
        DynamicWallHeight
    }

    private void Awake()
    {
        SetupMeshComponents();
        BuildColorRampLUT();
    }

    private void OnEnable()
    {
        SourceManager.OnTexturesInitialized += HandleTexturesInitialized;
        SourceManager.OnFrameUpdated += ProcessFrame;

        if (settings != null)
        {
            settings.OnSettingsChanged += HandleSettingsChanged;
        }
    }

    private void OnDisable()
    {
        SourceManager.OnTexturesInitialized -= HandleTexturesInitialized;
        SourceManager.OnFrameUpdated -= ProcessFrame;

        if (settings != null)
        {
            settings.OnSettingsChanged -= HandleSettingsChanged;
        }

        CleanupTextures();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (Application.isPlaying)
        {
            BuildColorRampLUT();
        }
    }
#endif

    private void HandleSettingsChanged()
    {
        BuildColorRampLUT();
        ProcessFrame();
    }

    private void SetupMeshComponents()
    {
        Transform parentTransform = originPivot != null ? originPivot : transform;
        var rootObj = new GameObject("SegmentationOverlayRoot");
        rootObj.transform.SetParent(parentTransform, false);
        rootObj.transform.localPosition = Vector3.zero;
        rootObj.transform.localRotation = Quaternion.identity;
        rootObj.transform.localScale = Vector3.one;
        segmentsRoot = rootObj.transform;
    }

    private void EnsureSegmentsRootParent()
    {
        Transform targetParent = originPivot != null ? originPivot : transform;
        if (segmentsRoot != null)
        {
            if (segmentsRoot.parent != targetParent)
            {
                segmentsRoot.SetParent(targetParent, false);
                segmentsRoot.localPosition = Vector3.zero;
                segmentsRoot.localScale = Vector3.one;
            }

            // Apply 90-degree pitch shift if in Wall mode
            if (settings != null)
            {
                bool isWallMode = settings.Orientation == PlaneOrientation.Wall; // adjust to your enum name
                segmentsRoot.localRotation = isWallMode
                    ? Quaternion.Euler(-90f, 0f, 0f)
                    : Quaternion.identity;
            }
        }
    }

    private Vector3 GetPlaneNormal()
    {
        if (settings == null) return Vector3.up;
        OrientationUtility.GetPlaneBasis(settings.Orientation, originPivot, out _, out Vector3 normal, out _, out _);
        return normal;
    }

    private GameObject GetOrCreateSegmentObject(int index)
    {
        while (segmentObjs.Count <= index)
        {
            var go = new GameObject($"Segment_{segmentObjs.Count}");
            go.transform.SetParent(segmentsRoot, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            var mesh = new Mesh { name = $"SegmentMesh_{segmentObjs.Count}" };
            mf.mesh = mesh;
            if (handMeshMaterial != null) mr.sharedMaterial = handMeshMaterial;

            segmentObjs.Add(go);
            segmentMeshes.Add(mesh);
        }

        return segmentObjs[index];
    }

    private void SyncMeshCollider(GameObject segObj, Mesh mesh)
    {
        MeshCollider mc = segObj.GetComponent<MeshCollider>();

        if (generateMeshCollider)
        {
            if (mc == null)
            {
                mc = segObj.AddComponent<MeshCollider>();
                mc.cookingOptions = MeshColliderCookingOptions.UseFastMidphase; // drop cleanup + weld passes — procedural mesh is already clean
            }
            mc.sharedMesh = null;
            mc.sharedMesh = mesh;
        }
        else if (mc != null)
        {
            Destroy(mc);
        }
    }

    private void HandleTexturesInitialized(int width, int height) { }

    private void EnsureScratchBuffers(int count)
    {
        if (scratchDts == null || scratchDts.Length < count)
        {
            scratchDts = new float[count];
            scratchIsExtrapolated = new bool[count];
            scratchNeighborAvg = new float[count];
            scratchCounts = new int[count];
            scratchClippedDts = new float[count];
            scratchOffsets = new float[count];
        }
    }

    private void ProcessFrame()
    {
        if (SourceManager.instance == null) return;

        var sm = SourceManager.instance;
        if (sm == null) return;

        Texture2D preparedDepth = sm.PreparedDepthTex;
        if (preparedDepth == null) return; // not allocated yet

        var region = sm.GetPreparedRegion(SourceMode.Depth);
        int cropW = preparedDepth.width;
        int cropH = preparedDepth.height;
        if (!region.IsValid || region.DisplayCrop.Width != cropW || region.DisplayCrop.Height != cropH) return;

        EnsureSegmentsRootParent();

        NativeArray<ushort> rawDepth = preparedDepth.GetPixelData<ushort>(0);
        if (!rawDepth.IsCreated || rawDepth.Length == 0) return;

        int fullWidth = region.FullWidth;
        int fullHeight = region.FullHeight;
        int cropX = region.DisplayCrop.X;
        int cropY = region.DisplayCrop.Y;
        Hands.Clear();

        unsafe
        {
            using (var preparedMat = new Mat(cropH, cropW, MatType.CV_16UC1, (IntPtr)rawDepth.GetUnsafeReadOnlyPtr()))
            using (var grayMat = preparedMat.Clone())
            using (var threshMat = new Mat())
            {
                UpdateRoiTexture(grayMat);
                CaptureDebugStage(DebugStage.Grayscale, grayMat);

                ushort minDepthVal = (ushort)Mathf.Clamp(depthMin, 0, 65535);
                ushort maxDepthVal = (ushort)Mathf.Clamp(depthMax, 0, 65535);
                Cv2.InRange(grayMat, new Scalar(minDepthVal), new Scalar(maxDepthVal), threshMat);
                CaptureDebugStage(DebugStage.Threshold, threshMat);

                RemoveEdgeArtifacts(threshMat, erosionIterations, dilationIterations, edgeBorderThickness);
                CaptureDebugStage(DebugStage.Cleaned, threshMat);

                Mat processMat = threshMat;
                int effectiveW = cropW;
                int effectiveH = cropH;

                if (processingDownscale > 1)
                {
                    effectiveW = cropW / processingDownscale;
                    effectiveH = cropH / processingDownscale;
                    processMat = new Mat();
                    Cv2.Resize(threshMat, processMat, new OpenCvSharp.Size(effectiveW, effectiveH));
                }
                CaptureDebugStage(DebugStage.Downscaled, processMat);

                UpdateDebugOverlayQuad();

                List<Vector3[]> dynamicMeshes = new List<Vector3[]>();
                List<int[]> dynamicTriangles = new List<int[]>();
                List<List<(int from, int to)>> dynamicBoundaryEdges = new List<List<(int from, int to)>>();
                List<float> dynamicHeights = new List<float>();
                List<float[]> dynamicVertexDepths = new List<float[]>();

                bool wantsPolygonSimplification = simplificationMode == MeshSimplificationMode.ContourSimplify || edgeSmoothingIterations > 0;
                bool usePolygonTopMesh = wantsPolygonSimplification && !enableDisplacement;

                if (usePolygonTopMesh)
                {
                    Cv2.FindContours(processMat, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                    foreach (var contour in contours)
                    {
                        double area = Cv2.ContourArea(contour);
                        if (area < minContourArea || area > maxContourArea) continue;
                        float epsilon = simplificationMode == MeshSimplificationMode.ContourSimplify ? contourSimplificationEpsilon : 0f;
                        if (!TryBuildContourTopMesh(contour, epsilon, edgeSmoothingIterations, out Vector2[] polyPoints, out int[] polyTris)) continue;

                        float t = 0f;
                        if (enableDynamicWallHeight)
                        {
                            float avgDepth = ComputeAverageDepthForContour(grayMat, contour, processingDownscale);
                            t = Mathf.InverseLerp(minDepthVal, maxDepthVal, avgDepth);
                            if (invertHeightMapping) t = 1f - t;
                        }

                        float verticalOffset = 0f;
                        if (enableDynamicWallHeight && dynamicHeightMode == DynamicHeightMode.DynamicInstanceHeight)
                            verticalOffset = Mathf.Lerp(minInstanceHeight, maxInstanceHeight, t);

                        Vector3[] localPoints = new Vector3[polyPoints.Length];
                        for (int i = 0; i < polyPoints.Length; i++)
                        {
                            float originalX = polyPoints[i].x * processingDownscale;
                            float originalY = polyPoints[i].y * processingDownscale;
                            Vector3 localPt = MapPointToPlaygroundSpace(originalX, originalY, cropX, cropY, cropW, cropH, fullWidth, fullHeight);
                            localPt += Vector3.up * verticalOffset;
                            localPoints[i] = localPt;
                        }

                        var boundaryEdges = ComputeBoundaryEdges(polyTris);

                        dynamicMeshes.Add(localPoints);
                        dynamicTriangles.Add(polyTris);
                        dynamicBoundaryEdges.Add(boundaryEdges);

                        float segmentHeight = extrusionDepth;
                        if (enableDynamicWallHeight && dynamicHeightMode == DynamicHeightMode.DynamicWallHeight)
                            segmentHeight = fixedWallHeight * t;
                        segmentHeight *= heightMultiplier;
                        dynamicHeights.Add(segmentHeight);
                    }
                }
                else
                {
                    List<Point[]> objectBlobs = FindSimpleObjectBlobs(processMat, minContourArea, maxContourArea);

                    ushort* grayPtr = (ushort*)grayMat.DataPointer;
                    int grayRowStride = (int)(grayMat.Step() / sizeof(ushort));

                    foreach (var blobPixels in objectBlobs)
                    {
                        Point[] finePoints;
                        int[] fineTriangles;

                        if (enableDisplacement && enableAdaptiveMesh)
                            BuildAdaptiveMesh(blobPixels, adaptiveEdgeBandWidth, adaptiveInteriorStride, out finePoints, out fineTriangles);
                        else
                            BuildPixelGridMesh(blobPixels, out finePoints, out fineTriangles);

                        List<(int from, int to)> boundaryEdges = ComputeBoundaryEdges(fineTriangles);

                        if (finePoints.Length < 3) continue;

                        float t = 0f;
                        if (enableDynamicWallHeight)
                        {
                            float avgDepth = ComputeAverageDepth(grayMat, blobPixels, processingDownscale);
                            t = Mathf.InverseLerp(minDepthVal, maxDepthVal, avgDepth);
                            if (invertHeightMapping) t = 1f - t;
                        }

                        float verticalOffset = 0f;
                        if (enableDynamicWallHeight && dynamicHeightMode == DynamicHeightMode.DynamicInstanceHeight)
                            verticalOffset = Mathf.Lerp(minInstanceHeight, maxInstanceHeight, t);

                        EnsureScratchBuffers(finePoints.Length);
                        float[] dts = scratchDts;
                        bool[] isExtrapolated = scratchIsExtrapolated;
                        Array.Clear(isExtrapolated, 0, finePoints.Length);

                        Vector3[] localPoints = new Vector3[finePoints.Length];
                        float[] colorDts = new float[finePoints.Length];
                        int offset = processingDownscale / 2;

                        for (int i = 0; i < finePoints.Length; i++)
                        {
                            int rawX = finePoints[i].X * processingDownscale;
                            int rawY = finePoints[i].Y * processingDownscale;

                            Vector3 pt = MapPointToPlaygroundSpace(rawX, rawY, cropX, cropY, cropW, cropH, fullWidth, fullHeight);
                            pt += Vector3.up * verticalOffset;
                            localPoints[i] = pt;

                            if (enableDisplacement)
                            {
                                int sampleX = Mathf.Clamp(rawX + offset, 0, cropW - 1);
                                int sampleY = Mathf.Clamp(rawY + offset, 0, cropH - 1);

                                ushort rawDepthVal = grayPtr[sampleY * grayRowStride + sampleX];
                                ushort depthValue = rawDepthVal;

                                if (rawDepthVal < minDepthVal || rawDepthVal > maxDepthVal)
                                {
                                    isExtrapolated[i] = true;

                                    int maxRadius = Mathf.Max(2, (dilationIterations + 1) * processingDownscale * 2);
                                    bool foundValid = false;

                                    for (int r = 1; r <= maxRadius; r++)
                                    {
                                        int validCount = 0;
                                        int depthSum = 0;

                                        for (int dy = -r; dy <= r; dy++)
                                        {
                                            for (int dx = -r; dx <= r; dx++)
                                            {
                                                if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r) continue;

                                                int nx = Mathf.Clamp(sampleX + dx, 0, cropW - 1);
                                                int ny = Mathf.Clamp(sampleY + dy, 0, cropH - 1);
                                                ushort neighborDepth = grayPtr[ny * grayRowStride + nx];

                                                if (neighborDepth >= minDepthVal && neighborDepth <= maxDepthVal)
                                                {
                                                    depthSum += neighborDepth;
                                                    validCount++;
                                                }
                                            }
                                        }

                                        if (validCount > 0)
                                        {
                                            depthValue = (ushort)(depthSum / validCount);
                                            foundValid = true;
                                            break;
                                        }
                                    }

                                    if (!foundValid)
                                    {
                                        depthValue = minDepthVal;
                                    }
                                }

                                float dt = Mathf.InverseLerp(minDepthVal, maxDepthVal, depthValue);
                                if (invertHeightMapping) dt = 1f - dt;

                                dts[i] = dt;

                                float colorDt = Mathf.InverseLerp(colorDepthMin, colorDepthMax, depthValue);
                                if (invertColorMapping) colorDt = 1f - colorDt;
                                colorDts[i] = colorDt;
                            }
                        }

                        if (enableDisplacement && fineTriangles.Length >= 3)
                        {
                            float[] neighborAvg = scratchNeighborAvg;
                            int[] counts = scratchCounts;
                            Array.Clear(neighborAvg, 0, finePoints.Length);
                            Array.Clear(counts, 0, finePoints.Length);

                            for (int tri = 0; tri < fineTriangles.Length; tri += 3)
                            {
                                int i0 = fineTriangles[tri];
                                int i1 = fineTriangles[tri + 1];
                                int i2 = fineTriangles[tri + 2];

                                neighborAvg[i0] += dts[i1] + dts[i2]; counts[i0] += 2;
                                neighborAvg[i1] += dts[i0] + dts[i2]; counts[i1] += 2;
                                neighborAvg[i2] += dts[i0] + dts[i1]; counts[i2] += 2;
                            }

                            for (int i = 0; i < finePoints.Length; i++)
                            {
                                if (counts[i] > 0)
                                {
                                    float avg = neighborAvg[i] / counts[i];
                                    if (dts[i] > avg + 0.15f)
                                    {
                                        dts[i] = avg;
                                    }
                                }
                            }
                        }

                        if (enableDisplacement && displacementSmoothingIterations > 0 && fineTriangles.Length >= 3)
                        {
                            boundaryEdges = ComputeBoundaryEdges(fineTriangles);

                            List<(int a, int b)> allEdges = ComputeAllEdges(fineTriangles);
                            var seamEdgeList = new List<(int a, int b)>();
                            var seamVertexSet = new HashSet<int>();

                            foreach (var (a, b) in allEdges)
                            {
                                if (!isExtrapolated[a] && !isExtrapolated[b]) continue;
                                seamEdgeList.Add((a, b));
                                seamVertexSet.Add(a);
                                seamVertexSet.Add(b);
                            }

                            int seamEdgeCount = seamEdgeList.Count;
                            int seamVertCount = seamVertexSet.Count;

                            EnsureSeamSmoothingBuffers(finePoints.Length, seamEdgeCount, seamVertCount);

                            for (int i = 0; i < seamEdgeCount; i++)
                            {
                                seamEdgeA[i] = seamEdgeList[i].a;
                                seamEdgeB[i] = seamEdgeList[i].b;
                            }
                            seamVertexSet.CopyTo(seamVertexArr, 0, seamVertCount);

                            // only touched indices need clearing/writing each iteration — not the full vertex array
                            for (int i = 0; i < seamVertCount; i++)
                            {
                                int idx = seamVertexArr[i];
                                seamAccumDts[idx] = 0f;
                                seamAccumPos[idx] = Vector3.zero;
                                seamNeighborCounts[idx] = 0;
                            }

                            for (int iter = 0; iter < displacementSmoothingIterations; iter++)
                            {
                                for (int i = 0; i < seamEdgeCount; i++)
                                {
                                    int a = seamEdgeA[i], b = seamEdgeB[i];

                                    seamAccumDts[a] += dts[b];
                                    seamAccumPos[a] += localPoints[b];
                                    seamNeighborCounts[a]++;

                                    seamAccumDts[b] += dts[a];
                                    seamAccumPos[b] += localPoints[a];
                                    seamNeighborCounts[b]++;
                                }

                                for (int i = 0; i < seamVertCount; i++)
                                {
                                    int idx = seamVertexArr[i];
                                    if (seamNeighborCounts[idx] == 0) continue;

                                    dts[idx] = (dts[idx] + (seamAccumDts[idx] / seamNeighborCounts[idx])) * 0.5f;

                                    Vector3 avgPos = seamAccumPos[idx] / seamNeighborCounts[idx];

                                    float curNormalDist = localPoints[idx].y;
                                    Vector3 curTangential = new Vector3(localPoints[idx].x, 0f, localPoints[idx].z);
                                    Vector3 avgTangential = new Vector3(avgPos.x, 0f, avgPos.z);

                                    Vector3 smoothedTangential = Vector3.Lerp(curTangential, avgTangential, seamPositionSmoothingWeight);
                                    localPoints[idx] = smoothedTangential + Vector3.up * curNormalDist;

                                    // reset accumulators in place for the next iteration — avoids a second full clear pass
                                    seamAccumDts[idx] = 0f;
                                    seamAccumPos[idx] = Vector3.zero;
                                    seamNeighborCounts[idx] = 0;
                                }
                            }
                        }

                        if (enableDisplacement && fineTriangles.Length >= 3)
                        {
                            int vCount = finePoints.Length;
                            int edgeSlots = fineTriangles.Length * 2; // 2 neighbor entries added per vertex per triangle

                            EnsureNeighborBuffers(vCount, edgeSlots);
                            Array.Clear(neighborDegree, 0, vCount);

                            for (int tri = 0; tri < fineTriangles.Length; tri += 3)
                            {
                                int i0 = fineTriangles[tri], i1 = fineTriangles[tri + 1], i2 = fineTriangles[tri + 2];
                                neighborDegree[i0] += 2; neighborDegree[i1] += 2; neighborDegree[i2] += 2;
                            }

                            neighborStart[0] = 0;
                            for (int i = 0; i < vCount; i++)
                                neighborStart[i + 1] = neighborStart[i] + neighborDegree[i];

                            Array.Copy(neighborStart, neighborCursor, vCount);

                            for (int tri = 0; tri < fineTriangles.Length; tri += 3)
                            {
                                int i0 = fineTriangles[tri], i1 = fineTriangles[tri + 1], i2 = fineTriangles[tri + 2];
                                neighborFlat[neighborCursor[i0]++] = i1; neighborFlat[neighborCursor[i0]++] = i2;
                                neighborFlat[neighborCursor[i1]++] = i0; neighborFlat[neighborCursor[i1]++] = i2;
                                neighborFlat[neighborCursor[i2]++] = i0; neighborFlat[neighborCursor[i2]++] = i1;
                            }

                            float maxSpikeThreshold = 0.05f;
                            float[] clippedDts = new float[vCount];
                            System.Array.Copy(dts, clippedDts, vCount);

                            for (int i = 0; i < vCount; i++)
                            {
                                int start = neighborStart[i], count = neighborDegree[i];
                                if (count == 0) continue;

                                float neighborSum = 0f;
                                for (int n = start; n < start + count; n++)
                                    neighborSum += dts[neighborFlat[n]];

                                float avgNeighborDt = neighborSum / count;
                                if (dts[i] > avgNeighborDt + maxSpikeThreshold)
                                    clippedDts[i] = avgNeighborDt;
                            }

                            dts = clippedDts;
                        }

                        if (enableDisplacement)
                        {
                            float minOffset = float.MaxValue;
                            float[] offsets = new float[finePoints.Length];

                            for (int i = 0; i < finePoints.Length; i++)
                            {
                                offsets[i] = dts[i] * displacementMax;
                                if (offsets[i] < minOffset) minOffset = offsets[i];
                                localPoints[i] += Vector3.up * (offsets[i] - minOffset);
                            }
                        }

                        dynamicMeshes.Add(localPoints);
                        dynamicTriangles.Add(fineTriangles);
                        dynamicBoundaryEdges.Add(boundaryEdges);
                        dynamicVertexDepths.Add(colorDts);

                        float segmentHeight = extrusionDepth;
                        if (enableDynamicWallHeight && dynamicHeightMode == DynamicHeightMode.DynamicWallHeight)
                            segmentHeight = fixedWallHeight * t;
                        segmentHeight *= heightMultiplier;
                        dynamicHeights.Add(segmentHeight);
                    }
                }

                if (processMat != threshMat) processMat.Dispose();

                UpdateSegmentMeshes(dynamicMeshes, dynamicTriangles, dynamicBoundaryEdges, dynamicVertexDepths, dynamicHeights, generateWalls);
            }
        }
    }

    private void BuildColorRampLUT()
    {
        if (colorRampLUT == null || colorRampLUT.width != colorRampLUTSize)
        {
            if (colorRampLUT != null) Destroy(colorRampLUT);
            colorRampLUT = new Texture2D(colorRampLUTSize, 1, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp
            };
        }

        var pixels = new Color[colorRampLUTSize];
        for (int x = 0; x < colorRampLUTSize; x++)
        {
            pixels[x] = colorRampGradient.Evaluate((float)x / (colorRampLUTSize - 1));
        }
        colorRampLUT.SetPixels(pixels);
        colorRampLUT.Apply();

        if (handMeshMaterial != null)
        {
            handMeshMaterial.SetTexture("_HeightLUT", colorRampLUT);
        }
    }

    private void BuildAdaptiveMesh(Point[] blobPixels, float edgeBandWidth, int interiorStride, out Point[] points, out int[] triangles)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int i = 0; i < blobPixels.Length; i++)
        {
            var p = blobPixels[i];
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        int width = maxX - minX + 1;
        int height = maxY - minY + 1;

        adaptiveMaskMat.Create(height, width, MatType.CV_8UC1);
        adaptiveMaskMat.SetTo(Scalar.All(0));

        unsafe
        {
            byte* maskPtr = (byte*)adaptiveMaskMat.DataPointer;
            int maskStride = (int)adaptiveMaskMat.Step();

            for (int i = 0; i < blobPixels.Length; i++)
            {
                var p = blobPixels[i];
                maskPtr[(p.Y - minY) * maskStride + (p.X - minX)] = 255;
            }
        }

        Cv2.DistanceTransform(adaptiveMaskMat, adaptiveDistMat, DistanceTypes.L2, DistanceMaskSize.Precise);

        adaptiveSelectedPoints.Clear();
        adaptiveSeenKeys.Clear();

        unsafe
        {
            float* distPtr = (float*)adaptiveDistMat.DataPointer;
            int distStride = (int)(adaptiveDistMat.Step() / sizeof(float));

            for (int i = 0; i < blobPixels.Length; i++)
            {
                var p = blobPixels[i];
                int lx = p.X - minX, ly = p.Y - minY;
                float d = distPtr[ly * distStride + lx];

                bool keep = d <= edgeBandWidth || (lx % interiorStride == 0 && ly % interiorStride == 0);
                if (!keep) continue;

                long key = ((long)lx << 32) | (uint)ly;
                if (adaptiveSeenKeys.Add(key)) adaptiveSelectedPoints.Add(p);
            }
        }

        points = adaptiveSelectedPoints.ToArray(); // still a fresh array — this one is the actual per-blob mesh output, needed downstream

        var rect = new OpenCvSharp.Rect(0, 0, width, height);
        adaptiveSubdiv.InitDelaunay(rect);

        adaptivePtMap.Clear();
        EnsureInsertBuffer(points.Length);

        for (int i = 0; i < points.Length; i++)
        {
            int lx = points[i].X - minX, ly = points[i].Y - minY;
            adaptiveInsertBuffer[i] = new Point2f(lx, ly);
            adaptivePtMap[(lx, ly)] = i;
        }

        adaptiveSubdiv.Insert(new ArraySegment<Point2f>(adaptiveInsertBuffer, 0, points.Length));

        Vec6f[] triangleList = adaptiveSubdiv.GetTriangleList(); // allocated by the OpenCvSharp wrapper itself — not something we control

        adaptiveTriList.Clear();

        foreach (var t in triangleList)
        {
            var pt0 = new Point(Mathf.RoundToInt(t.Item0), Mathf.RoundToInt(t.Item1));
            var pt1 = new Point(Mathf.RoundToInt(t.Item2), Mathf.RoundToInt(t.Item3));
            var pt2 = new Point(Mathf.RoundToInt(t.Item4), Mathf.RoundToInt(t.Item5));

            if (pt0.X < 0 || pt0.Y < 0 || pt0.X >= width || pt0.Y >= height) continue;
            if (pt1.X < 0 || pt1.Y < 0 || pt1.X >= width || pt1.Y >= height) continue;
            if (pt2.X < 0 || pt2.Y < 0 || pt2.X >= width || pt2.Y >= height) continue;

            if (!adaptivePtMap.TryGetValue((pt0.X, pt0.Y), out int i0)) continue;
            if (!adaptivePtMap.TryGetValue((pt1.X, pt1.Y), out int i1)) continue;
            if (!adaptivePtMap.TryGetValue((pt2.X, pt2.Y), out int i2)) continue;

            int cx = (pt0.X + pt1.X + pt2.X) / 3;
            int cy = (pt0.Y + pt1.Y + pt2.Y) / 3;

            unsafe
            {
                byte* maskPtr = (byte*)adaptiveMaskMat.DataPointer;
                int maskStride = (int)adaptiveMaskMat.Step();

                // inside the existing foreach (var t in triangleList) loop, replace the check with:
                if (maskPtr[cy * maskStride + cx] == 0) continue;
            }

            adaptiveTriList.Add(i0); adaptiveTriList.Add(i1); adaptiveTriList.Add(i2);
        }

        triangles = adaptiveTriList.ToArray(); // per-blob mesh output, needed downstream
    }

    private void BuildPixelGridMesh(Point[] blobPixels, out Point[] points, out int[] triangles)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int i = 0; i < blobPixels.Length; i++)
        {
            var p = blobPixels[i];
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        int width = maxX - minX + 1;
        int height = maxY - minY + 1;
        int cellCount = width * height;

        EnsureGridBuffers(cellCount);

        Array.Clear(gridOccupancy, 0, cellCount);

        for (int i = 0; i < cellCount; i++) gridIndex[i] = -1;

        for (int i = 0; i < blobPixels.Length; i++)
        {
            var p = blobPixels[i];
            int local = (p.Y - minY) * width + (p.X - minX);
            gridOccupancy[local] = true;
            gridIndex[local] = i;
        }

        var triList = new List<int>(blobPixels.Length * 6);

        for (int y = 0; y < height - 1; y++)
        {
            for (int x = 0; x < width - 1; x++)
            {
                int tlLocal = y * width + x;
                int trLocal = tlLocal + 1;
                int blLocal = tlLocal + width;
                int brLocal = blLocal + 1;

                if (!gridOccupancy[tlLocal] || !gridOccupancy[trLocal] ||
                    !gridOccupancy[blLocal] || !gridOccupancy[brLocal])
                    continue;

                int tl = gridIndex[tlLocal];
                int tr = gridIndex[trLocal];
                int bl = gridIndex[blLocal];
                int br = gridIndex[brLocal];

                triList.Add(tl); triList.Add(tr); triList.Add(bl);
                triList.Add(tr); triList.Add(br); triList.Add(bl);
            }
        }

        points = blobPixels;
        triangles = triList.ToArray();
    }

    private void EnsureInsertBuffer(int count)
    {
        if (adaptiveInsertBuffer.Length < count)
            adaptiveInsertBuffer = new Point2f[Mathf.NextPowerOfTwo(count)];
    }

    private void EnsureGridBuffers(int requiredCellCount)
    {
        if (gridOccupancy == null || gridOccupancy.Length < requiredCellCount)
        {
            int newSize = Mathf.NextPowerOfTwo(requiredCellCount);
            gridOccupancy = new bool[newSize];
            gridIndex = new int[newSize];
        }
    }

    private void EnsureNeighborBuffers(int vertexCount, int edgeSlotCount)
    {
        if (neighborDegree == null || neighborDegree.Length < vertexCount)
        {
            neighborDegree = new int[vertexCount];
            neighborStart = new int[vertexCount + 1];
            neighborCursor = new int[vertexCount];
        }
        if (neighborFlat == null || neighborFlat.Length < edgeSlotCount)
            neighborFlat = new int[edgeSlotCount];
    }

    private void EnsureSeamSmoothingBuffers(int vertexCount, int edgeCount, int seamVertCount)
    {
        if (seamAccumDts == null || seamAccumDts.Length < vertexCount)
        {
            seamAccumDts = new float[vertexCount];
            seamAccumPos = new Vector3[vertexCount];
            seamNeighborCounts = new int[vertexCount];
        }
        if (seamEdgeA == null || seamEdgeA.Length < edgeCount)
        {
            seamEdgeA = new int[edgeCount];
            seamEdgeB = new int[edgeCount];
        }
        if (seamVertexArr == null || seamVertexArr.Length < seamVertCount)
            seamVertexArr = new int[seamVertCount];
    }

    private void CaptureDebugStage(DebugStage stage, Mat mat)
    {
        if (debugStage != stage) return;

        int w = mat.Width, h = mat.Height;
        if (!debugTextures.TryGetValue(stage, out Texture2D tex) || tex == null || tex.width != w || tex.height != h)
        {
            if (tex != null) Destroy(tex);
            tex = new Texture2D(w, h, TextureFormat.R8, false);
            debugTextures[stage] = tex;
            debugRawBytesBuffers[stage] = new byte[w * h];
        }

        if (mat.Type() == MatType.CV_16UC1)
            mat.ConvertTo(debugDisplayMat, MatType.CV_8UC1, 255.0 / 65535.0);
        else
            mat.CopyTo(debugDisplayMat);

        byte[] rawBytes = debugRawBytesBuffers[stage];
        System.Runtime.InteropServices.Marshal.Copy(debugDisplayMat.Data, rawBytes, 0, rawBytes.Length);
        tex.LoadRawTextureData(rawBytes);
        tex.Apply();
    }

    private void UpdateDebugOverlayQuad()
    {
        if (debugStage == DebugStage.None)
        {
            if (debugOverlayGo != null && debugOverlayGo.activeSelf) debugOverlayGo.SetActive(false);
            lastAppliedDebugStage = DebugStage.None;
            return;
        }

        if (!debugTextures.TryGetValue(debugStage, out Texture2D tex) || tex == null) return;

        Camera cam = debugTargetCamera != null ? debugTargetCamera : Camera.main;
        if (cam == null) return;

        if (debugOverlayGo == null)
        {
            Transform existing = cam.transform.Find("HandDetectorDebugOverlay");
            if (existing != null)
            {
                debugOverlayGo = existing.gameObject;
            }
            else
            {
                debugOverlayGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
                debugOverlayGo.name = "HandDetectorDebugOverlay";

                Collider col = debugOverlayGo.GetComponent<Collider>();
                if (col != null) Destroy(col);

                Renderer rend = debugOverlayGo.GetComponent<Renderer>();
                Shader unlitShader = Shader.Find("Unlit/Texture");
                if (unlitShader == null) unlitShader = Shader.Find("Sprites/Default");
                rend.material = new Material(unlitShader);
            }
        }

        if (debugOverlayGo.transform.parent != cam.transform)
            debugOverlayGo.transform.SetParent(cam.transform, false);

        if (!debugOverlayGo.activeSelf) debugOverlayGo.SetActive(true);

        float frustumH, frustumW;
        if (cam.orthographic)
        {
            frustumH = cam.orthographicSize * 2f;
            frustumW = frustumH * cam.aspect;
        }
        else
        {
            frustumH = 2f * debugOverlayDistance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            frustumW = frustumH * cam.aspect;
        }

        float overlayAspect = (float)tex.width / tex.height;
        float quadW = frustumW * debugOverlayScreenWidthFraction;
        float quadH = quadW / overlayAspect;

        float marginX = frustumW * debugOverlayMarginFraction;
        float marginY = frustumH * debugOverlayMarginFraction;

        float localX = frustumW * 0.5f - quadW * 0.5f - marginX;
        float localY = -frustumH * 0.5f + quadH * 0.5f + marginY;

        debugOverlayGo.transform.localPosition = new Vector3(localX, localY, debugOverlayDistance);
        debugOverlayGo.transform.localRotation = Quaternion.identity;
        debugOverlayGo.transform.localScale = new Vector3(quadW, quadH, 1f);

        Renderer overlayRenderer = debugOverlayGo.GetComponent<Renderer>();
        if (overlayRenderer != null && overlayRenderer.material != null)
            overlayRenderer.material.mainTexture = tex;

        lastAppliedDebugStage = debugStage;
    }

    private void UpdateRoiTexture(Mat mat)
    {
        int w = mat.Width, h = mat.Height;
        if (RoiTexture == null || RoiTexture.width != w || RoiTexture.height != h)
        {
            if (RoiTexture != null) Destroy(RoiTexture);
            RoiTexture = new Texture2D(w, h, TextureFormat.R8, false);
            roiRawBytesBuffer = new byte[w * h];
        }

        if (mat.Type() == MatType.CV_16UC1)
            mat.ConvertTo(roiDisplayMat, MatType.CV_8UC1, 255.0 / 65535.0);
        else
            mat.CopyTo(roiDisplayMat);

        System.Runtime.InteropServices.Marshal.Copy(roiDisplayMat.Data, roiRawBytesBuffer, 0, roiRawBytesBuffer.Length);
        RoiTexture.LoadRawTextureData(roiRawBytesBuffer);
        RoiTexture.Apply();

        // Fire unconditionally so UI preview materials refresh when ROI position shifts
        OnActiveTextureChanged?.Invoke(RoiTexture);
    }

    private Vector3 MapPointToPlaygroundSpace(float localX, float localY, int cropX, int cropY, int cropW, int cropH, int fullWidth, int fullHeight)
    {
        // cropX/cropY are physical sensor coordinates — GetCropBounds may have relocated them
        // when Flip is on. localX/localY are already display-space (Cv2.Flip reorders grayMat's
        // pixels to match). Mirror cropX/cropY back to display space before combining, rather than
        // converting localX/localY to sensor space.
        float sensorX = cropX + localX + 0.5f;
        float sensorY = cropY + localY + 0.5f;

        // 2. Normalize to Quad Local Space (-0.5 to +0.5)
        // OpenCV Y is Top-to-Bottom; Quad local Y is Bottom-to-Top (+0.5 is top)
        float normX = (sensorX / (float)fullWidth) - 0.5f;
        float normY = (sensorY / (float)fullHeight) - 0.5f;

        Vector3 quadLocalPos = new Vector3(normX, normY, 0f);

        // 3. Quad Local -> World Space -> originPivot Local Space
        Vector3 worldPos = (playgroundPlane != null)
            ? playgroundPlane.transform.TransformPoint(quadLocalPos)
            : quadLocalPos;

        Transform targetRoot = segmentsRoot != null ? segmentsRoot : transform;
        Vector3 pivotLocalPos = targetRoot.InverseTransformPoint(worldPos);

        // 4. Apply height offset along local surface normal
        pivotLocalPos += Vector3.up * heightOffset;

        return pivotLocalPos;
    }

    private void UpdateSegmentMeshes(
        List<Vector3[]> polys,
        List<int[]> polyTriangles,
        List<List<(int from, int to)>> polyBoundaryEdges,
        List<float[]> polyDepths,
        List<float> heights,
        bool generateWalls)
    {
        for (int i = 0; i < polys.Count; i++)
        {
            GameObject segObj = GetOrCreateSegmentObject(i);
            if (!segObj.activeSelf) segObj.SetActive(true);

            Mesh mesh = segmentMeshes[i];
            float depth = i < heights.Count ? heights[i] : extrusionDepth;
            var boundaryEdges = i < polyBoundaryEdges.Count ? polyBoundaryEdges[i] : null;
            float[] vertexDepths = i < polyDepths.Count ? polyDepths[i] : null;

            bool orientationFlip = settings != null && settings.Orientation == PlaneOrientation.Wall;
            ExtrudePixelGridMesh(
                mesh, polys[i], polyTriangles[i], vertexDepths, depth,
                generateWalls, generateTopCap,
                invertWallWinding,
                invertBottomCapWinding ,
                invertTopCapWinding,
                boundaryEdges
            );

            SyncMeshCollider(segObj, mesh);
        }

        for (int i = polys.Count; i < segmentObjs.Count; i++)
            if (segmentObjs[i].activeSelf) segmentObjs[i].SetActive(false);
    }

    private void CleanupTextures()
    {
        if (RoiTexture != null) { Destroy(RoiTexture); RoiTexture = null; }
        if (readbackTex != null) { Destroy(readbackTex); readbackTex = null; }

        foreach (var tex in debugTextures.Values)
        {
            if (tex != null) Destroy(tex);
        }
        debugTextures.Clear();
        debugRawBytesBuffers.Clear();

        roiDisplayMat?.Dispose();
        debugDisplayMat?.Dispose();
        adaptiveMaskMat?.Dispose();
        adaptiveDistMat?.Dispose();
        adaptiveSubdiv?.Dispose();

        if (debugOverlayGo != null) { Destroy(debugOverlayGo); debugOverlayGo = null; }

        if (colorRampLUT != null) { Destroy(colorRampLUT); colorRampLUT = null; }
    }
}