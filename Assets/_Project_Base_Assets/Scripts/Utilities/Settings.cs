using System;
using UnityEngine;
using ArucoUnity.Plugin;

[CreateAssetMenu(menuName = "ScriptableObjects/Settings", fileName = "Settings")]
public class Settings : ScriptableObject
{
    /// <summary>
    /// All ArUco detector tuning in one place, shared by every ArucoDetector instance that
    /// references this Settings asset — tune once here instead of per-component. Was
    /// previously a pile of [SerializeField] fields living directly on ArucoDetector, which
    /// meant two detector instances (e.g. a Kinect one + a webcam-debug one) could silently
    /// drift out of sync with each other.
    /// </summary>
    [Serializable]
    public class ArucoDetectionSettings
    {
        [Header("Dictionary")]
        public Aruco.PredefinedDictionaryName SelectedDictionary = Aruco.PredefinedDictionaryName.Dict4x4_50;

        [Header("Thresholding")]
        [Range(3, 100)] public int AdaptiveThreshWinSizeMin = 3;
        [Range(3, 100)] public int AdaptiveThreshWinSizeMax = 23;
        [Range(1, 100)] public int AdaptiveThreshWinSizeStep = 10;
        [Range(1, 100)] public int AdaptiveThreshConstant = 7;

        [Header("Contour Filtering")]
        [Range(0.01f, 1)] public float MinMarkerPerimeterRate = 0.03f;
        [Range(1, 100)] public float MaxMarkerPerimeterRate = 4.0f;
        [Range(0.01f, 1)] public float PolygonalApproxAccuracyRate = 0.05f;
        [Range(0.01f, 1)] public float MinCornerDistanceRate = 0.05f;
        [Range(0.01f, 1)] public float MinMarkerDistanceRate = 0.05f;
        [Range(1, 100)] public int MinDistanceToBorder = 3;

        [Header("Bits Extraction")]
        [Range(1, 100)] public int MarkerBorderBits = 1;
        [Range(1, 100)] public int MinOtsuStdDev = 5;
        [Range(1, 100)] public int PerspectiveRemovePixelPerCell = 4;
        [Range(0.01f, 0.7f)] public float PerspectiveRemoveIgnoredMarginPerCell = 0.13f;

        [Header("Marker Identification")]
        [Range(0.01f, 1)] public float MaxErroneousBitsInBorderRate = 0.35f;
        [Range(0.01f, 1)] public float ErrorCorrectionRate = 0.6f;

        [Header("Corner Refinement")]
        [Tooltip("Subpix/Contour refinement recovers extra precision once a marker's rough position is found — matters most exactly when a marker is small (few pixels across), since the raw corner estimate is coarser relative to the marker's own size. None is fastest but least accurate for small/distant markers.")]
        public Aruco.CornerRefineMethod CornerRefinementMethod = Aruco.CornerRefineMethod.Subpix;
        [Range(2, 15)] public int CornerRefinementWinSize = 5;
        [Range(1, 100)] public int CornerRefinementMaxIterations = 30;
        [Range(0.01f, 1f)] public float CornerRefinementMinAccuracy = 0.1f;

        [Header("Small / Distant Marker Detection")]
        [Tooltip("Runs detection on a software-upscaled copy of the source image instead of native Kinect resolution. Helps recover markers that only span a handful of native pixels (small markers, or the Kinect placed far away) at the cost of extra CPU per frame — detection cost scales roughly with pixel count, so factor 3 is ~9x the per-frame work. 1 = disabled (native resolution, cheapest).")]
        [Range(1, 4)] public int DetectionUpscaleFactor = 1;

        [Header("Temporal Averaging")]
        [Tooltip("Averages the grayscale image over the last N frames before detection, reducing sensor noise at the cost of latency and blurring genuinely fast motion. Most useful for small/distant markers, where noise is a bigger fraction of the marker's total pixel budget than it is for a large nearby one. 1 = disabled (use the current frame only, zero extra cost).")]
        [Range(1, 8)] public int TemporalAverageFrames = 1;

        [Header("Contrast Stretch")]
        [Tooltip("Stretches the frame's actual brightness range to fill 0-255 before detection (with a small percentile clip so a few outlier pixels can't collapse the stretch). Helps when a marker is technically visible but low-contrast — e.g. Kinect IR illumination falling off at distance — since a fixed adaptive threshold can miss detail a compressed dynamic range doesn't have room for. Off by default: well-lit setups don't need it, and it costs an extra full-frame pass.")]
        public bool EnableContrastStretch = false;
        [Range(0f, 10f)] public float ContrastStretchClipPercent = 1f;
    }

    [Header("Marker Detection")]
    public ArucoDetectionSettings MarkerDetection = new ArucoDetectionSettings();

    [Header("Performance")]
    [Range(1, 240), Tooltip("Target update rate for SourceManager's frame loop (ArUco/hand detection, texture pushes, etc). Match this to your actual source: 30 for real Kinect hardware, higher for test images or when there's no hardware cadence to respect.")]
    public int TargetFrameRate = 30;

    [Range(0, 30), Tooltip("Process depth and compute shaders only every N frames. (Kinect produces 30fps)")]
    public int FrameSkipInterval = 0;

    private WaitForSeconds cachedTimestep;
    private int cachedTimestepRate = -1;

    // Was a hardcoded `static readonly WaitForSeconds(1/30)` � unconditionally capped
    // SourceManager's loop at 30fps even when driving test images with no real hardware
    // cadence to respect. Now derives from TargetFrameRate and is rebuilt only when that
    // value actually changes, instead of allocating a new WaitForSeconds every call.
    public WaitForSeconds Timestep
    {
        get
        {
            int rate = Mathf.Max(1, TargetFrameRate);
            if (cachedTimestep == null || cachedTimestepRate != rate)
            {
                cachedTimestep = new WaitForSeconds(1.0f / rate);
                cachedTimestepRate = rate;
            }
            return cachedTimestep;
        }
    }

    public event Action OnSettingsChanged;

    public enum PlaneOrientation { Floor, Wall }

    [Header("Calibration")]
    public PlaneOrientation Orientation = PlaneOrientation.Floor;

    [SerializeField] private Vector3 wallOriginOffset = Vector3.zero;
    public Vector3 WallOriginOffset => wallOriginOffset;

    [Header("Camera")]
    public bool CameraFollowsRect = true;
    public bool UsePerspectiveCamera = false;
    [Range(1f, 179f)] public float PerspectiveFOV = 60f;
    public float CameraDistance = 300f;

    [Header("Display")]
    [Tooltip("Mirrors the image/world mapping along the X axis (columns/left-right).")]
    public bool FlipX = false;
    [Tooltip("Mirrors the image/world mapping along the Y axis (rows/up-down).")]
    public bool FlipY = false;

    [Header("Region of Interest")]
    public bool UseBounds = false;

    [Header("Calibration")]
    public Rect MinMaxRect = new Rect(0, 0, 512, 424);
    public Rect Projection = new Rect(0, 0, 512, 424);
    public Rect Playground = new Rect(0, 0, 512, 424);

    [SerializeField, HideInInspector]
    private bool playgroundManuallySet;

    [Min(0.25f), Tooltip("Ratio of the playground to the projection area.")]
    public Vector2 AreaRatio = Vector2.one;

    [Header("Fine Adjustment")]
    [Range(-50, 50)] public float CameraOffsetX;
    [Range(-50, 50)] public float CameraOffsetZ;
    // -------------------------------------------------------------------------

    public void SetMinMaxRectToResolution(int width, int height)
    {
        MinMaxRect = new Rect(0, 0, width, height);
        OnValidate();
    }
    public void SetAllBoundsToResolution(int width, int height)
    {
        // Assuming these are standard Rects or RectInts starting from 0,0
        // Adjust the variable names to match your actual variables in Settings.cs
        Rect fullResolutionRect = new Rect(0, 0, width, height);

        MinMaxRect = fullResolutionRect;
        Playground = fullResolutionRect;
        Projection = fullResolutionRect;

        OnValidate();
    }

    public void OnValidate()
    {
        if (!playgroundManuallySet)
        {
            Projection = new Rect()
            {
                size = MinMaxRect.size,
                center = MinMaxRect.center - new Vector2(CameraOffsetX, CameraOffsetZ)
            };

            Playground = new Rect()
            {
                size = Projection.size * AreaRatio,
                center = Projection.center
            };
        }

        // Notify listeners that values modified in Inspector have changed
        OnSettingsChanged?.Invoke();
    }

    // Sets Playground directly, bypassing the MinMaxRect/AreaRatio derivation in OnValidate()
    // (which would otherwise silently overwrite a manually-dragged rect). Used by the
    // interactive playground editor in CalibrationManager.
    public void SetPlaygroundManual(Rect playground)
    {
        Playground = playground;
        Projection = playground;

        // Keep MinMaxRect (the raw sensor-space crop input read by GetCropBounds) consistent
        // with the manual edit — undo the camera offset that OnValidate normally subtracts
        // when deriving Projection from MinMaxRect, so dragging behaves like editing MinMaxRect
        // directly would have.
        MinMaxRect = new Rect
        {
            size = playground.size,
            center = playground.center + new Vector2(CameraOffsetX, CameraOffsetZ)
        };

        playgroundManuallySet = true;
        OnSettingsChanged?.Invoke();
    }

    public void SetOrientation(PlaneOrientation orientation)
    {
        Orientation = orientation;
        OnSettingsChanged?.Invoke();
    }

    public void CycleOrientation()
    {
        SetOrientation(Orientation == PlaneOrientation.Floor ? PlaneOrientation.Wall : PlaneOrientation.Floor);
    }
}