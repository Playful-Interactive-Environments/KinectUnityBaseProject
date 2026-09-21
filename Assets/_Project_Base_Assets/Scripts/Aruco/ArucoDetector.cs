using System;
using UnityEngine;
using Unity.Collections;
using System.Collections;
using System.Collections.Generic;
using ArucoUnity.Plugin;
using ArucoUnity.Utilities;
using static ArucoUnity.Plugin.Cv;
using UnityEngine.Rendering; // for AsyncGPUReadback
using Unity.Collections.LowLevel.Unsafe; // for GetUnsafeReadOnlyPtr

#if UNITY_EDITOR
using UnityEditor;
#endif

using Unity.Profiling;

[HelpURL("https://docs.opencv.org/4.x/d5/dae/tutorial_aruco_detection.html")]
public class ArucoDetector : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Settings settings;
    [SerializeField] private PlaygroundPlane playgroundPlane;

    public static readonly List<Marker> Markers = new List<Marker>();
    public static event Action OnMarkersUpdated;
    public static event Action<Texture> OnActiveTextureChanged;

    /// <summary>
    /// Dedicated Texture2D containing only the active cropped ROI image data.
    /// </summary>
    public Texture2D RoiTexture { get; private set; }

    /// <summary>
    /// Alias property for cross-subsystem texture compatibility.
    /// </summary>
    public Texture2D CroppedTexture => RoiTexture;

    private Aruco.DetectorParameters Parameters;
    private Aruco.Dictionary MarkerDictionary;

    private Cv.Mat ImageMat;
    private byte[] imageData;

    [Header("Marker Assignment")]
    [Tooltip("Shared registry of which marker IDs represent which object types.")]
    [SerializeField] private MarkerObjectRegistry objectRegistry;

    [Header("Image Source")]
    [Tooltip("Which raw Kinect texture to run detection against.")]
    [SerializeField] private SourceMode sourceMode = SourceMode.Infrared;

    private static readonly Settings.ArucoDetectionSettings fallbackDetectionSettings = new Settings.ArucoDetectionSettings();
    private Settings.ArucoDetectionSettings DetectionSettings => settings != null ? settings.MarkerDetection : fallbackDetectionSettings;

    private Cv.Mat DetectionMat;
    private byte[] detectionData;
    private int detectionWidth;
    private int detectionHeight;

    // Temporal averaging scratch state
    private byte[][] temporalRing;
    private int[] temporalSum;
    private byte[] temporalAveraged;
    private int temporalRingCount;
    private int temporalRingHead;
    private int temporalPixelCount;
    private int temporalConfiguredFrames;

    // Contrast stretch scratch state
    private readonly int[] contrastHistogram = new int[256];
    private readonly byte[] contrastLut = new byte[256];
    private byte[] contrastStretched;

    private bool readbackInFlight;
    private struct PendingFrameCrop
    {
        public int fullWidth, fullHeight, cropX, cropY, cropW, cropH;
    }

    [Header("Webcam Debug Source")]
    [SerializeField] private bool useWebcamInspector = false;
    [SerializeField] private bool useStaticWebcamSize = false;
    [SerializeField] private int webcamWidth = 640;
    [SerializeField] private int webcamHeight = 480;

    [Header("Debug Visualization")]
    [Tooltip("Check to create a child Quad object that displays the post-processed image fed into OpenCV.")]
    [SerializeField] private bool showDebugOverlay = false;

    [Tooltip("Target camera to attach overlay to. Leaves empty to auto-use Camera.main.")]
    [SerializeField] private Camera targetCamera;

    [Tooltip("Overlay size as a fraction of screen width (0.25 = 25% of screen width).")]
    [Range(0.1f, 0.5f)]
    [SerializeField] private float overlayScreenWidthFraction = 0.25f;

    [Tooltip("Distance in front of the camera plane.")]
    [SerializeField] private float overlayDistance = 1.0f;

    [Tooltip("Padding margin from screen edges as a fraction of screen size.")]
    [Range(0.0f, 0.1f)]
    [SerializeField] private float overlayMarginFraction = 0.02f;

    private GameObject debugOverlayGo;
    private Texture2D debugTex;
    private byte[] debugRgbaData;

    private bool useWebcam;
    private WebCamTexture webcamTex;
    private Color32[] webcamPixels;
    private bool webcamReady;
    private byte[] activeDetectionBuffer;

    private static readonly ProfilerMarker MarkerExtract = new ProfilerMarker("ArucoDetector.Extract");
    private static readonly ProfilerMarker MarkerTemporal = new ProfilerMarker("ArucoDetector.TemporalAvg");
    private static readonly ProfilerMarker MarkerContrast = new ProfilerMarker("ArucoDetector.ContrastStretch");
    private static readonly ProfilerMarker MarkerUpscale = new ProfilerMarker("ArucoDetector.Upscale");
    private static readonly ProfilerMarker MarkerDetect = new ProfilerMarker("ArucoDetector.DetectMarkers");
    private static readonly ProfilerMarker MarkerReadback = new ProfilerMarker("ArucoDetector.ReadbackWait");

    private static bool readbackNeedsYFlip;
    private static bool readbackNeedsYFlipInitialized;

    public bool UseWebcam
    {
        get => useWebcam;
        set
        {
            if (useWebcam == value) return;
            useWebcam = value;
            SwitchSource();
        }
    }

    private Std.VectorVectorPoint2f corners = new Std.VectorVectorPoint2f();
    private Std.VectorInt ids = new Std.VectorInt();
    private Std.VectorVectorPoint2f rejectedImgPoints = new Std.VectorVectorPoint2f();

    private Texture2D readbackTex;

    private void OnEnable()
    {
        SourceManager.OnTexturesInitialized += HandleTexturesInitialized;
        SourceManager.OnFrameUpdated += ProcessFrame;

        if (settings != null)
        {
            settings.OnSettingsChanged += HandleSettingsChanged;
        }

        useWebcam = useWebcamInspector;
        SwitchSource();
    }

    private void OnDisable() 
    {
        readbackInFlight = false;
        SourceManager.OnTexturesInitialized -= HandleTexturesInitialized;
        SourceManager.OnFrameUpdated -= ProcessFrame;
        CleanupMatResources();

        if (settings != null)
        {
            settings.OnSettingsChanged -= HandleSettingsChanged;
        }

        StopWebcam();
    }

    private void HandleSettingsChanged()
    {
        if (MarkerDictionary != null)
        {
            UpdateDictionary();
            ApplyParameters();
        }
    }

    private void Awake()
    {
        if (!readbackNeedsYFlipInitialized)
        {
            readbackNeedsYFlip = !SystemInfo.graphicsUVStartsAtTop;
            readbackNeedsYFlipInitialized = true;
            Debug.Log($"ArucoDetector: graphicsUVStartsAtTop={SystemInfo.graphicsUVStartsAtTop}, graphicsDeviceType={SystemInfo.graphicsDeviceType}");
        }
    }

    private void Start()
    {
        if (SourceManager.instance != null && SourceManager.instance.IsInitialized)
        {
            HandleTexturesInitialized(SourceManager.instance.SourceWidth, SourceManager.instance.SourceHeight);
        }

        if (useWebcamInspector)
            Debug.LogWarning("Warning (ArucoDetector): Webcam Debug Active! Disable to use Kinect feed.");
    }

    private void Update()
    {
        if (useWebcam)
        {
            ProcessWebcamFrame();
        }
    }

    private void SwitchSource()
    {
        CleanupMatResources();

        if (useWebcam)
        {
            StartWebcam();
        }
        else
        {
            StopWebcam();
            if (SourceManager.instance != null && SourceManager.instance.IsInitialized)
            {
                HandleTexturesInitialized(SourceManager.instance.SourceWidth, SourceManager.instance.SourceHeight);
            }
        }
    }

    private void StartWebcam()
    {
        if (webcamTex != null) return;

        webcamTex = useStaticWebcamSize
            ? new WebCamTexture(webcamWidth, webcamHeight)
            : new WebCamTexture();

        webcamTex.Play();
        webcamReady = false;
    }

    private void StopWebcam()
    {
        if (webcamTex == null) return;

        webcamTex.Stop();
        webcamTex = null;
        webcamPixels = null;
        webcamReady = false;
    }

    private void GetCropBounds(int fullWidth, int fullHeight, out int cropX, out int cropY, out int cropW, out int cropH)
    {
        var crop = PlaygroundMapping.GetCropBounds(settings, fullWidth, fullHeight);
        cropX = crop.X; cropY = crop.Y; cropW = crop.Width; cropH = crop.Height;
    }

    private void HandleTexturesInitialized(int width, int height)
    {
        if (useWebcam) return;

        Texture2D sourceTex = SourceManager.instance != null ? SourceManager.instance.GetTexture(sourceMode) : null;
        int texWidth = sourceTex != null ? sourceTex.width : width;
        int texHeight = sourceTex != null ? sourceTex.height : height;

        if (texWidth <= 0 || texHeight <= 0) return;

        GetCropBounds(texWidth, texHeight, out _, out _, out int cropW, out int cropH);
        ReallocateMatIfNeeded(cropW, cropH);
    }

    private void ProcessFrame()
    {
        if (useWebcam) return;
        if (readbackInFlight) return; // drop this frame if previous readback hasn't landed yet — avoids queueing stalls

        Texture2D sourceTex = SourceManager.instance != null ? SourceManager.instance.GetTexture(sourceMode) : null;
        if (sourceTex == null) return;

        int fullWidth = sourceTex.width;
        int fullHeight = sourceTex.height;

        GetCropBounds(fullWidth, fullHeight, out int cropX, out int cropY, out int cropW, out int cropH);
        ReallocateMatIfNeeded(cropW, cropH);

        // AsyncGPUReadback addresses raw GPU memory, which may have its Y origin at the
        // bottom (OpenGL/Vulkan-style) rather than the top Unity's texture-space assumes.
        // GetCropBounds gives us a top-down cropY; convert it to the GPU's native origin.
        int readbackY = readbackNeedsYFlip ? (fullHeight - cropY - cropH) : cropY;

        var crop = new PendingFrameCrop
        {
            fullWidth = fullWidth,
            fullHeight = fullHeight,
            cropX = cropX,
            cropY = cropY,
            cropW = cropW,
            cropH = cropH
        };

        readbackInFlight = true;
        MarkerReadback.Begin();

        AsyncGPUReadback.Request(
            sourceTex, 0,
            cropX, cropW, readbackY, cropH, 0, 1,
            request => OnFrameReadback(request, crop)
        );
    }

    private void OnFrameReadback(AsyncGPUReadbackRequest request, PendingFrameCrop crop)
    {
        readbackInFlight = false;
        MarkerReadback.End(); // pairs with Begin() in ProcessFrame below

        if (this == null || !isActiveAndEnabled) return;
        if (useWebcam) return;

        if (request.hasError)
        {
            Debug.LogWarning("ArucoDetector: AsyncGPUReadback failed.");
            OnMarkersUpdated?.Invoke();
            return;
        }

        Texture2D sourceTex = SourceManager.instance != null ? SourceManager.instance.GetTexture(sourceMode) : null;
        if (sourceTex == null) return;

        if (ImageMat == null || ImageMat.Cols != crop.cropW || ImageMat.Rows != crop.cropH)
            ReallocateMatIfNeeded(crop.cropW, crop.cropH);

        Markers.Clear();

        NativeArray<byte> rawBytes = request.GetData<byte>();

        using (MarkerExtract.Auto())
            ExtractGrayscaleDataFast(rawBytes, sourceTex.format, crop.cropW, crop.cropH);

        byte[] processed = PreprocessForDetection(imageData, crop.cropW, crop.cropH);
        ImageMat.DataByte = processed;
        BuildDetectionMat(crop.cropW, crop.cropH, processed);
        DetectAndBuildMarkers(crop.fullWidth, crop.fullHeight, crop.cropX, crop.cropY, crop.cropW, crop.cropH);
    }

    private unsafe void ExtractGrayscaleDataFast(NativeArray<byte> rawBytes, TextureFormat format, int cropW, int cropH)
    {
        byte* src = (byte*)rawBytes.GetUnsafeReadOnlyPtr();

        fixed (byte* dst = imageData)
        {
            switch (format)
            {
                case TextureFormat.R16:
                    for (int y = 0; y < cropH; y++)
                    {
                        int srcY = cropH - 1 - y;
                        ushort* srcRow = (ushort*)(src + (long)srcY * cropW * 2);
                        byte* dstRow = dst + (long)y * cropW;

                        for (int x = 0; x < cropW; x++)
                            dstRow[x] = (byte)(srcRow[x] >> 8);
                    }
                    break;

                case TextureFormat.R8:
                case TextureFormat.Alpha8:
                    // Statischer Flip: Zeilenweise Speicherübertragung ohne Laufzeit-Verzweigungen
                    for (int y = 0; y < cropH; y++)
                    {
                        int srcY = cropH - 1 - y;
                        byte* srcRow = src + (long)srcY * cropW;
                        byte* dstRow = dst + (long)y * cropW;

                        UnsafeUtility.MemCpy(dstRow, srcRow, cropW);
                    }
                    break;

                case TextureFormat.RGBA32: 
                case TextureFormat.ARGB32:
                    {
                        int rOff = format == TextureFormat.ARGB32 ? 1 : 0;
                        int gOff = format == TextureFormat.ARGB32 ? 2 : 1;
                        int bOff = format == TextureFormat.ARGB32 ? 3 : 2;

                        for (int y = 0; y < cropH; y++)
                        {
                            int srcY = cropH - 1 - y;
                            byte* srcRow = src + (long)srcY * cropW * 4;
                            byte* dstRow = dst + (long)y * cropW;

                            for (int x = 0; x < cropW; x++)
                            {
                                byte* px = srcRow + x * 4;
                                dstRow[x] = (byte)((px[rOff] * 299 + px[gOff] * 587 + px[bOff] * 114) / 1000);
                            }
                        }
                    }
                    break;

                default:
                    Debug.LogWarning($"ArucoDetector: unhandled source texture format {format}, clearing buffer.");
                    UnsafeUtility.MemClear(dst, cropW * cropH);
                    break;
            }
        }
    }

    private void ExtractCompressedOrComplexGrayscale(Texture2D sourceTex, int fullWidth, int fullHeight, int cropX, int cropY, int cropW, int cropH)
    {
        RenderTexture rt = RenderTexture.GetTemporary(fullWidth, fullHeight, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(sourceTex, rt);

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture.active = rt;

        if (readbackTex == null || readbackTex.width != cropW || readbackTex.height != cropH)
        {
            if (readbackTex != null) Destroy(readbackTex);
            readbackTex = new Texture2D(cropW, cropH, TextureFormat.RGBA32, false);
        }

        // Read back only the cropped region from GPU memory
        readbackTex.ReadPixels(new UnityEngine.Rect(cropX, cropY, cropW, cropH), 0, 0);
        readbackTex.Apply();

        RenderTexture.active = previousActive;
        RenderTexture.ReleaseTemporary(rt);

        NativeArray<byte> rawData = readbackTex.GetRawTextureData<byte>();

        for (int y = 0; y < cropH; y++)
        {
            int srcRowIndex = (cropH - 1 - y) * cropW;
            int dstRowIndex = y * cropW;
            for (int x = 0; x < cropW; x++)
            {
                int srcByteIndex = (srcRowIndex + x) * 4;
                byte r = rawData[srcByteIndex];
                byte g = rawData[srcByteIndex + 1];
                byte b = rawData[srcByteIndex + 2];

                imageData[dstRowIndex + x] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
            }
        }
    }

    private void ReallocateMatIfNeeded(int width, int height)
    {
        if (ImageMat != null && ImageMat.Cols == width && ImageMat.Rows == height)
            return;

        UpdateDictionary();
        Parameters = new Aruco.DetectorParameters();
        ApplyParameters();

        ImageMat = new Cv.Mat(height, width, Cv.Type.CV_8U);
        imageData = new byte[(int)(ImageMat.ElemSize() * ImageMat.Total())];
        ImageMat.DataByte = imageData;
    }

    private void ProcessWebcamFrame()
    {
        if (webcamTex == null || !webcamTex.didUpdateThisFrame) return;

        int fullWidth = webcamTex.width;
        int fullHeight = webcamTex.height;

        if (!webcamReady)
        {
            if (!useStaticWebcamSize && (fullWidth <= 16 || fullHeight <= 16)) return;
            webcamReady = true;
        }

        GetCropBounds(fullWidth, fullHeight, out int cropX, out int cropY, out int cropW, out int cropH);

        if (ImageMat == null || ImageMat.Cols != cropW || ImageMat.Rows != cropH)
        {
            ReallocateMatIfNeeded(cropW, cropH);
            webcamPixels = new Color32[fullWidth * fullHeight];
        }

        Markers.Clear();
        webcamTex.GetPixels32(webcamPixels);

        for (int y = 0; y < cropH; y++)
        {
            int unityY = (cropY + cropH - 1) - y;
            int srcRowIndex = unityY * fullWidth + cropX;
            int dstRowIndex = y * cropW;

            for (int x = 0; x < cropW; x++)
            {
                var c = webcamPixels[srcRowIndex + x];
                imageData[dstRowIndex + x] = (byte)((c.r + c.g + c.b) / 3);
            }
        }

        byte[] processed = PreprocessForDetection(imageData, cropW, cropH);
        ImageMat.DataByte = processed;
        BuildDetectionMat(cropW, cropH, processed);
        DetectAndBuildMarkers(fullWidth, fullHeight, cropX, cropY, cropW, cropH);
    }

    private void BuildDetectionMat(int cropW, int cropH, byte[] source)
    {
        int factor = Mathf.Max(1, DetectionSettings.DetectionUpscaleFactor);

        if (factor == 1)
        {
            DetectionMat = ImageMat;
            activeDetectionBuffer = source;
            return;
        }

        int detWidth = cropW * factor;
        int detHeight = cropH * factor;

        if (DetectionMat == null || detectionWidth != detWidth || detectionHeight != detHeight)
        {
            DetectionMat = new Cv.Mat(detHeight, detWidth, Cv.Type.CV_8U);
            detectionData = new byte[detWidth * detHeight];
            detectionWidth = detWidth;
            detectionHeight = detHeight;
        }

        using (MarkerUpscale.Auto())
            UpscaleBilinear(source, cropW, cropH, detectionData, detWidth, detHeight);
        DetectionMat.DataByte = detectionData;
        activeDetectionBuffer = detectionData;
    }

    private byte[] PreprocessForDetection(byte[] raw, int width, int height)
    {
        byte[] result;
        using (MarkerTemporal.Auto())
            result = ApplyTemporalAverage(raw, width, height);
        using (MarkerContrast.Auto())
            result = ApplyContrastStretch(result, width, height);
        return result;
    }

    private byte[] ApplyTemporalAverage(byte[] frame, int width, int height)
    {
        int frames = Mathf.Max(1, DetectionSettings.TemporalAverageFrames);

        if (frames <= 1)
        {
            temporalRingCount = 0;
            temporalRingHead = 0;
            return frame;
        }

        int pixelCount = width * height;

        if (temporalRing == null || temporalPixelCount != pixelCount || temporalConfiguredFrames != frames)
        {
            temporalRing = new byte[frames][];
            for (int i = 0; i < frames; i++) temporalRing[i] = new byte[pixelCount];
            temporalSum = new int[pixelCount];
            temporalAveraged = new byte[pixelCount];
            temporalRingCount = 0;
            temporalRingHead = 0;
            temporalPixelCount = pixelCount;
            temporalConfiguredFrames = frames;
        }

        if (temporalRingCount == frames)
        {
            byte[] evicted = temporalRing[temporalRingHead];
            for (int i = 0; i < pixelCount; i++) temporalSum[i] -= evicted[i];
        }
        else
        {
            temporalRingCount++;
        }

        byte[] slot = temporalRing[temporalRingHead];
        Array.Copy(frame, slot, pixelCount);
        for (int i = 0; i < pixelCount; i++) temporalSum[i] += slot[i];

        temporalRingHead = (temporalRingHead + 1) % frames;

        for (int i = 0; i < pixelCount; i++)
            temporalAveraged[i] = (byte)(temporalSum[i] / temporalRingCount);

        return temporalAveraged;
    }

    private byte[] ApplyContrastStretch(byte[] frame, int width, int height)
    {
        if (!DetectionSettings.EnableContrastStretch) return frame;

        int pixelCount = width * height;
        if (contrastStretched == null || contrastStretched.Length != pixelCount)
            contrastStretched = new byte[pixelCount];

        Array.Clear(contrastHistogram, 0, contrastHistogram.Length);
        for (int i = 0; i < pixelCount; i++) contrastHistogram[frame[i]]++;

        float clip = Mathf.Clamp01(DetectionSettings.ContrastStretchClipPercent / 100f);
        int clipCount = Mathf.RoundToInt(pixelCount * clip);

        int low = 0, lowAcc = 0;
        for (int v = 0; v < 256; v++)
        {
            lowAcc += contrastHistogram[v];
            if (lowAcc > clipCount) { low = v; break; }
        }

        int high = 255, highAcc = 0;
        for (int v = 255; v >= 0; v--)
        {
            highAcc += contrastHistogram[v];
            if (highAcc > clipCount) { high = v; break; }
        }

        if (high <= low)
        {
            Array.Copy(frame, contrastStretched, pixelCount);
            return contrastStretched;
        }

        float scale = 255f / (high - low);
        for (int v = 0; v < 256; v++)
            contrastLut[v] = (byte)Mathf.Clamp(Mathf.RoundToInt((v - low) * scale), 0, 255);

        for (int i = 0; i < pixelCount; i++)
            contrastStretched[i] = contrastLut[frame[i]];

        return contrastStretched;
    }

    private static void UpscaleBilinear(byte[] src, int srcW, int srcH, byte[] dst, int dstW, int dstH)
    {
        float xRatio = (srcW - 1) / (float)Mathf.Max(1, dstW - 1);
        float yRatio = (srcH - 1) / (float)Mathf.Max(1, dstH - 1);

        for (int y = 0; y < dstH; y++)
        {
            float srcYf = y * yRatio;
            int y0 = Mathf.FloorToInt(srcYf);
            int y1 = Mathf.Min(y0 + 1, srcH - 1);
            float fy = srcYf - y0;

            int dstRow = y * dstW;
            int srcRow0 = y0 * srcW;
            int srcRow1 = y1 * srcW;

            for (int x = 0; x < dstW; x++)
            {
                float srcXf = x * xRatio;
                int x0 = Mathf.FloorToInt(srcXf);
                int x1 = Mathf.Min(x0 + 1, srcW - 1);
                float fx = srcXf - x0;

                float top = Mathf.Lerp(src[srcRow0 + x0], src[srcRow0 + x1], fx);
                float bottom = Mathf.Lerp(src[srcRow1 + x0], src[srcRow1 + x1], fx);

                dst[dstRow + x] = (byte)Mathf.Lerp(top, bottom, fy);
            }
        }
    }

    private void DetectAndBuildMarkers(int fullWidth, int fullHeight, int cropX, int cropY, int cropW, int cropH)
    {
        using (MarkerDetect.Auto())
            Aruco.DetectMarkers(DetectionMat, MarkerDictionary, out corners, out ids, Parameters, out rejectedImgPoints);

        /*
        Debug.Log(
            $"ARUCO: detected={(ids != null ? ids.Size() : 0)}, " +
            $"rejected={(rejectedImgPoints != null ? rejectedImgPoints.Size() : 0)}, " +
            $"size={DetectionMat.Cols}x{DetectionMat.Rows}"
        );
        */

        int detW = DetectionMat != null ? DetectionMat.Cols : cropW;
        int detH = DetectionMat != null ? DetectionMat.Rows : cropH;

        UpdateDebugOverlay(detW, detH, activeDetectionBuffer, rejectedImgPoints);
        UpdateRoiTexture(cropW, cropH, imageData);

        float inv = 1f / Mathf.Max(1, DetectionSettings.DetectionUpscaleFactor);

        if (ids != null && ids.Size() > 0)
        {
            bool flipX = settings != null && settings.FlipX;
            bool flipY = settings != null && settings.FlipY;

            for (uint i = 0; i < ids.Size(); i++)
            {
                int rawId = ids.At(i);
                Std.VectorPoint2f cornerPair = corners.At(i);

                Vector3[] points = new Vector3[4];
                Vector3 center = Vector3.zero;

                for (uint c = 0; c < 4; c++)
                {
                    uint mappedIndex = c;

                    // 1. Fix Corner Ordering: Remap the OpenCV corner index back to the physical marker corner
                    if (!useWebcam)
                    {
                        if (flipX)
                        {
                            // Swap Left and Right (0=TL <-> 1=TR, 3=BL <-> 2=BR)
                            if (mappedIndex == 0) mappedIndex = 1;
                            else if (mappedIndex == 1) mappedIndex = 0;
                            else if (mappedIndex == 2) mappedIndex = 3;
                            else if (mappedIndex == 3) mappedIndex = 2;
                        }

                        if (flipY)
                        {
                            // Swap Top and Bottom (0=TL <-> 3=BL, 1=TR <-> 2=BR)
                            if (mappedIndex == 0) mappedIndex = 3;
                            else if (mappedIndex == 3) mappedIndex = 0;
                            else if (mappedIndex == 1) mappedIndex = 2;
                            else if (mappedIndex == 2) mappedIndex = 1;
                        }
                    }

                    Point2f pt = cornerPair.At(mappedIndex);

                    float pxInCrop = pt.X * inv;
                    float pyInCrop = pt.Y * inv;

                    // 2. Fix Spatial Projection: Un-flip the continuous coordinates back to the original texture space
                    if (!useWebcam)
                    {
                        if (flipX)
                        {
                            // Find absolute X, flip globally, convert back to relative
                            float absoluteX = cropX + pxInCrop;
                            pxInCrop = (fullWidth - absoluteX) - cropX;
                        }
                        if (flipY)
                        {
                            // 1. Convert OpenCV's bottom-up crop Y back to top-down crop space
                            float topDownPy = cropH - pyInCrop;

                            // 2. Calculate absolute top-down Y in the full image
                            float absoluteY = cropY + topDownPy;

                            // 3. Mirror globally across fullHeight
                            float flippedAbsoluteY = fullHeight - absoluteY;

                            // 4. Convert back to crop-relative space and re-apply OpenCV's bottom-up convention
                            float flippedTopDownPy = flippedAbsoluteY - cropY;
                            pyInCrop = cropH - flippedTopDownPy;
                        }
                    }

                    var crop = new PlaygroundMapping.CropRect { X = cropX, Y = cropY, Width = cropW, Height = cropH };

                    if (useWebcam)
                    {
                        // Webcam path bypasses CPU mirroring, apply settings normally
                        points[c] = PlaygroundMapping.PixelToWorldF(pxInCrop, pyInCrop, crop, fullWidth, fullHeight, settings, playgroundPlane);
                    }
                    else
                    {
                        // Corners are now fully restored to the original un-flipped pixel space and physical ordering
                        points[c] = PlaygroundMapping.PixelToWorldF(pxInCrop, pyInCrop, crop, fullWidth, fullHeight, null, playgroundPlane);
                    }

                    center += points[c];
                }
                center /= 4.0f;

                float sideLength = Vector3.Distance(points[0], points[1]);
                Quaternion rotation = ComputeMarkerRotation(points);
                bool assigned = objectRegistry != null && objectRegistry.IsAssigned((byte)rawId);

                Markers.Add(new Marker((byte)rawId, center, sideLength, points, rotation, assigned));
            }
        }

        OnMarkersUpdated?.Invoke();
    }

    private void UpdateRoiTexture(int w, int h, byte[] monoData)
    {
        if (monoData == null || monoData.Length < w * h) return;

        bool reallocated = false;
        if (RoiTexture == null || RoiTexture.width != w || RoiTexture.height != h)
        {
            if (RoiTexture != null) Destroy(RoiTexture);
            RoiTexture = new Texture2D(w, h, TextureFormat.R8, false);
            reallocated = true;
        }

        RoiTexture.LoadRawTextureData(monoData);
        RoiTexture.Apply();

        if (reallocated)
        {
            OnActiveTextureChanged?.Invoke(RoiTexture);
        }
    }

    private void DrawLineOnRgba(byte[] rgbaData, int width, int height, int x0, int y0, int x1, int y1, byte r, byte g, byte b, byte a = 255)
    {
        int dx = Mathf.Abs(x1 - x0);
        int dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height)
            {
                int idx = (y0 * width + x0) * 4;
                rgbaData[idx] = r;
                rgbaData[idx + 1] = g;
                rgbaData[idx + 2] = b;
                rgbaData[idx + 3] = a;
            }

            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    private void UpdateDebugOverlay(int w, int h, byte[] monoData, Std.VectorVectorPoint2f rejectedPoints = null)
    {
        if (!showDebugOverlay)
        {
            if (debugOverlayGo != null && debugOverlayGo.activeSelf)
            {
                debugOverlayGo.SetActive(false);
            }
            return;
        }

        Camera cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null) return;

        if (debugOverlayGo == null)
        {
            Transform existing = cam.transform.Find("ArucoDebugOverlay");
            if (existing != null)
            {
                debugOverlayGo = existing.gameObject;
            }
            else
            {
                debugOverlayGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
                debugOverlayGo.name = "ArucoDebugOverlay";
                Collider col = debugOverlayGo.GetComponent<Collider>();
                if (col != null) Destroy(col);
                Renderer rend = debugOverlayGo.GetComponent<Renderer>();
                Shader unlitShader = Shader.Find("Unlit/Texture");
                if (unlitShader == null) unlitShader = Shader.Find("Sprites/Default");
                rend.material = new Material(unlitShader);
            }
        }

        if (debugOverlayGo.transform.parent != cam.transform)
        {
            debugOverlayGo.transform.SetParent(cam.transform, false);
        }

        if (!debugOverlayGo.activeSelf)
        {
            debugOverlayGo.SetActive(true);
        }

        // Adjust Quad scale and position to match camera frustum
        float frustumH, frustumW;
        if (cam.orthographic)
        {
            frustumH = cam.orthographicSize * 2f;
            frustumW = frustumH * cam.aspect;
        }
        else
        {
            frustumH = 2f * overlayDistance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            frustumW = frustumH * cam.aspect;
        }

        float overlayAspect = (float)w / h;
        float quadW = frustumW * overlayScreenWidthFraction;
        float quadH = quadW / overlayAspect;

        float marginX = frustumW * overlayMarginFraction;
        float marginY = frustumH * overlayMarginFraction;

        float localX = -frustumW * 0.5f + quadW * 0.5f + marginX;
        float localY = -frustumH * 0.5f + quadH * 0.5f + marginY;

        debugOverlayGo.transform.localPosition = new Vector3(localX, localY, overlayDistance);
        debugOverlayGo.transform.localRotation = Quaternion.identity;
        debugOverlayGo.transform.localScale = new Vector3(quadW, quadH, 1f);

        Renderer overlayRenderer = debugOverlayGo.GetComponent<Renderer>();
        if (overlayRenderer != null && overlayRenderer.material != null)
        {
            // If there are no rejected candidate outlines to draw, use the already-correct CroppedTexture directly!
            if (rejectedPoints == null || rejectedPoints.Size() == 0)
            {
                if (CroppedTexture != null)
                {
                    overlayRenderer.material.mainTexture = CroppedTexture;
                }
                return;
            }

            // Fallback only used when drawing rejected points over the image data
            if (debugTex == null || debugTex.width != w || debugTex.height != h)
            {
                if (debugTex != null) Destroy(debugTex);
                debugTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                debugRgbaData = new byte[w * h * 4];
            }

            int pixelCount = w * h;
            for (int i = 0; i < pixelCount; i++)
            {
                byte b = monoData[i];
                int idx = i * 4;
                debugRgbaData[idx] = b;
                debugRgbaData[idx + 1] = b;
                debugRgbaData[idx + 2] = b;
                debugRgbaData[idx + 3] = 255;
            }

            for (uint i = 0; i < rejectedPoints.Size(); i++)
            {
                Std.VectorPoint2f poly = rejectedPoints.At(i);
                uint pCount = poly.Size();
                if (pCount < 2) continue;

                for (uint j = 0; j < pCount; j++)
                {
                    Point2f p1 = poly.At(j);
                    Point2f p2 = poly.At((j + 1) % pCount);

                    int x0 = Mathf.RoundToInt(p1.X);
                    int y0 = Mathf.RoundToInt(p1.Y);
                    int x1 = Mathf.RoundToInt(p2.X);
                    int y1 = Mathf.RoundToInt(p2.Y);

                    DrawLineOnRgba(debugRgbaData, w, h, x0, y0, x1, y1, 255, 0, 0);
                }
            }

            debugTex.LoadRawTextureData(debugRgbaData);
            debugTex.Apply();
            overlayRenderer.material.mainTexture = debugTex;
        }
    }

    private Quaternion ComputeMarkerRotation(Vector3[] points)
    {
        return Extensions.ComputeQuadRotation(points[0], points[1], points[3]);
    }

    public void UpdateDictionary()
    {
        MarkerDictionary = Aruco.GetPredefinedDictionary(DetectionSettings.SelectedDictionary);
    }

    private void ApplyParameters()
    {
        if (Parameters == null) return;
        var cfg = DetectionSettings;

        Parameters.MinMarkerPerimeterRate = Mathf.Max(0.03f, cfg.MinMarkerPerimeterRate);
        Parameters.MaxMarkerPerimeterRate = cfg.MaxMarkerPerimeterRate;
        Parameters.PolygonalApproxAccuracyRate = Mathf.Clamp(cfg.PolygonalApproxAccuracyRate, 0.01f, 0.1f);
        Parameters.MinCornerDistanceRate = Mathf.Max(0.05f, cfg.MinCornerDistanceRate);
        Parameters.MinDistanceToBorder = Mathf.Max(3, cfg.MinDistanceToBorder);

        int winMin = Mathf.Max(3, cfg.AdaptiveThreshWinSizeMin);
        int winMax = Mathf.Max(winMin, cfg.AdaptiveThreshWinSizeMax);
        Parameters.AdaptiveThreshWinSizeMin = winMin;
        Parameters.AdaptiveThreshWinSizeMax = winMax;
        Parameters.AdaptiveThreshWinSizeStep = Mathf.Max(1, cfg.AdaptiveThreshWinSizeStep);
        Parameters.AdaptiveThreshConstant = cfg.AdaptiveThreshConstant;

        Parameters.CornerRefinementMethod = cfg.CornerRefinementMethod;
        Parameters.CornerRefinementWinSize = Mathf.Max(3, cfg.CornerRefinementWinSize);
        Parameters.CornerRefinementMaxIterations = Mathf.Max(1, cfg.CornerRefinementMaxIterations);
        Parameters.CornerRefinementMinAccuracy = cfg.CornerRefinementMinAccuracy;

    }

    private void CleanupMatResources()
    {
        ImageMat = null;
        DetectionMat = null;
        activeDetectionBuffer = null;
        detectionWidth = 0;
        detectionHeight = 0;

        if (RoiTexture != null)
        {
            Destroy(RoiTexture);
            RoiTexture = null;
        }

        if (readbackTex != null)
        {
            Destroy(readbackTex);
            readbackTex = null;
        }

        if (debugTex != null)
        {
            Destroy(debugTex);
            debugTex = null;
        }

        if (debugOverlayGo != null)
        {
            Destroy(debugOverlayGo);
            debugOverlayGo = null;
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (Application.isPlaying)
        {
            UseWebcam = useWebcamInspector;

            if (MarkerDictionary != null)
            {
                UpdateDictionary();
                ApplyParameters();
            }
        }
    }

    private void OnDrawGizmos()
    {
        foreach (var marker in Markers)
        {
            if (marker.Points == null || marker.Points.Length < 4) continue;

            var points = marker.Points;

            Handles.color = Color.white;
            Handles.DrawLine(points[0], points[1], 2);
            Handles.DrawLine(points[1], points[2], 2);
            Handles.DrawLine(points[2], points[3], 2);
            Handles.DrawLine(points[3], points[0], 2);

            Handles.color = Color.magenta;
            Handles.DrawWireDisc(marker.Points[0], Vector3.up, 2, 2);

            Handles.color = Color.green;
            Handles.DrawSolidDisc(marker.Center, Vector3.up, 1);

            Handles.color = Color.cyan;
            float arrowLength = Mathf.Max(marker.Length * 0.75f, 5f);
            Handles.ArrowHandleCap(0, marker.Center, marker.Rotation, arrowLength, EventType.Repaint);

            Handles.Label(marker.Center + new Vector3(2, 0, -2), $"ID: {marker.Id}\nSize: {marker.Length:F1}px");
        }
    }
#endif
}