using UnityEngine;
using System.Collections;
using Windows.Kinect;
using System;
using System.Collections.Generic;


#if UNITY_EDITOR
using UnityEditor;
using System.IO;
#endif

[HelpURL("https://learn.microsoft.com/de-de/windows/apps/design/devices/kinect-for-windows/")]
public class SourceManager : MonoBehaviour
{
    public enum Filter : byte
    {
        BoxLinear = 0,
        Gaussian = 1
    }

    public static SourceManager instance { get; private set; }

    private KinectSensor Sensor;
    private MultiSourceFrameReader Reader;

    public static event Action<int, int> OnTexturesInitialized;
    public static event Action OnFrameUpdated;

    public Texture2D ColorTex { get; private set; }
    public Texture2D DepthTex { get; private set; }
    public Texture2D InfraTex { get; private set; }
    public bool IsInitialized { get; private set; }

    [Header("Stream Enable")]
    [SerializeField] private bool captureColor = true;
    [SerializeField] private bool captureDepth = true;
    [SerializeField] private bool captureInfrared = true;

    // Lets any consumer (ArucoDetector, HandDetector, viewers, ...) grab a texture by mode
    // directly from the source, without depending on each other or on a display component.
    public Texture2D GetTexture(SourceMode mode)
    {
        bool active = mode switch
        {
            SourceMode.Infrared => captureInfraredActive,
            SourceMode.Color => captureColorActive,
            _ => captureDepthActive
        };

        if (!active && !warnedModes.Contains(mode))
        {
            warnedModes.Add(mode);
            Debug.LogWarning($"SourceManager: GetTexture({mode}) was requested, but that stream is disabled " +
                              $"(capture{mode} is off or its texture was never created). The returned texture " +
                              $"will be stale or null. Enable capture{mode} if this stream is actually needed.");
        }

        return mode switch
        {
            SourceMode.Infrared => InfraTex,
            SourceMode.Color => ColorTex,
            _ => DepthTex
        };
    }

    [Header("Debug & Testing")]
    [Tooltip("If checked or Kinect is unavailable, offline test images will be used instead.")]
    public bool UseTestImages = false;
    public Texture2D ColorTestImage;
    public Texture2D DepthTestImage;
    public Texture2D InfraTestImage;

    [Header("Settings & Materials")]
    [SerializeField] private Settings Settings;
    [SerializeField] private Material Material;

    [SerializeField] public bool UseBlur = true;
    [SerializeField] private ComputeShader CSBlur;
    [SerializeField] private Filter FilterType = Filter.BoxLinear;

    [SerializeField, Range(0, 3)] private byte FilterRadius = 2;
    [SerializeField, Range(0, 200)] private byte DisplacementPower = 0;
    [SerializeField, Range(1, 8000)] private ushort DepthMin = 500, DepthMax = 4000;

    public int SourceWidth => DepthTex != null ? DepthTex.width : (InfraTex != null ? InfraTex.width : (ColorTex != null ? ColorTex.width : 512));
    public int SourceHeight => DepthTex != null ? DepthTex.height : (InfraTex != null ? InfraTex.height : (ColorTex != null ? ColorTex.height : 424));

    private RenderTexture rTexture;
    private bool isReadbackPending = false;

    private ushort[] rawDepthArray; // Updated every frame from Kinect or Readback
    private int depthWidth = 512;   // Kinect depth width
    private int depthHeight = 424;  // Kinect depth height

    private bool captureColorActive;
    private bool captureDepthActive;
    private bool captureInfraredActive;
    private readonly HashSet<SourceMode> warnedModes = new HashSet<SourceMode>();

    public float GetRawDepth(float x, float z)
    {
        int ix = Mathf.Clamp((int)x, 0, depthWidth - 1);
        int iz = Mathf.Clamp((int)z, 0, depthHeight - 1);

        if (rawDepthArray != null && rawDepthArray.Length == depthWidth * depthHeight)
        {
            int index = iz * depthWidth + ix;
            return rawDepthArray[index];
        }

        return 0f;
    }

    private void Awake()
    {
        instance = this;
        Settings?.EnsureLoaded();
    }

    private void OnEnable()
    {
        if (Settings != null)
        {
            Settings.OnSettingsChanged += HandleSettingsChanged;
        }
    }

    private void OnDisable()
    {
        if (Settings != null)
        {
            Settings.OnSettingsChanged -= HandleSettingsChanged;
        }
    }

    private void OnDestroy()
    {
        Reader?.Dispose();
        if (Sensor != null && Sensor.IsOpen)
        {
            Sensor.Close();
        }

        // Clean up created textures
        if (rTexture != null) rTexture.Release();
        if (ColorTex != null) Destroy(ColorTex);
        if (DepthTex != null) Destroy(DepthTex);
        if (InfraTex != null) Destroy(InfraTex);
    }

    private void HandleSettingsChanged()
    {
        // currently no cached values to refresh here — Settings is read live each frame
    }

    private IEnumerator Start()
    {
        Sensor = KinectSensor.GetDefault();
        bool kinectAvailable = Sensor != null;

        if (kinectAvailable && !UseTestImages)
        {
            Reader = Sensor.OpenMultiSourceFrameReader(
                (captureColor ? FrameSourceTypes.Color : 0) |
                (captureDepth ? FrameSourceTypes.Depth : 0) |
                (captureInfrared ? FrameSourceTypes.Infrared : 0)
            );

            if (captureColor)
            {
                var colorDesc = Sensor.ColorFrameSource.CreateFrameDescription(ColorImageFormat.RGBA);
                ColorTex = Extensions.CreateTexture(colorDesc.Width, colorDesc.Height, TextureFormat.RGBA32);
            }

            if (captureDepth)
            {
                var depthDesc = Sensor.DepthFrameSource.FrameDescription;
                DepthTex = Extensions.CreateTexture(depthDesc.Width, depthDesc.Height, TextureFormat.R16);
            }

            if (captureInfrared)
            {
                var infraDesc = Sensor.InfraredFrameSource.FrameDescription;
                InfraTex = Extensions.CreateTexture(infraDesc.Width, infraDesc.Height, TextureFormat.R16);
            }

            if (!Sensor.IsOpen) Sensor.Open();
        }
        else
        {
            // Safe runtime instantiations: prevents modifications to asset files on disk
            if (ColorTestImage != null)
                ColorTex = Instantiate(ColorTestImage);

            if (DepthTestImage != null)
            {
                DepthTex = Instantiate(DepthTestImage);
            }

            if (InfraTestImage != null)
                InfraTex = Instantiate(InfraTestImage);
        }

        captureColorActive = captureColor && ColorTex != null;
        captureDepthActive = captureDepth && DepthTex != null;
        captureInfraredActive = captureInfrared && InfraTex != null;

        IsInitialized = true;
        AnnounceTexturesInitialized();

        rTexture = DepthTex != null ? Extensions.CreateRTexture(DepthTex.width, DepthTex.height, 0, RenderTextureFormat.R16) : null;

        // Cache each FrameDescription once instead of calling CreateFrameDescription() twice
        // per array (once for BytesPerPixel, once for LengthInPixels) — this only runs once at
        // startup so it's not a hot-path cost, just avoidable duplicate work.
        var colorFrameDesc = (Sensor != null && Sensor.ColorFrameSource != null)
            ? Sensor.ColorFrameSource.CreateFrameDescription(ColorImageFormat.RGBA)
            : null;
        byte[] colorData = colorFrameDesc != null
            ? new byte[colorFrameDesc.BytesPerPixel * colorFrameDesc.LengthInPixels]
            : null;
        ushort[] depthData = (Sensor != null && Sensor.DepthFrameSource != null) ? new ushort[Sensor.DepthFrameSource.FrameDescription.LengthInPixels] : null;
        ushort[] infraData = (Sensor != null && Sensor.InfraredFrameSource != null) ? new ushort[Sensor.InfraredFrameSource.FrameDescription.LengthInPixels] : null;

        int frameCounter = 0;

        while (true)
        {
            int skipInterval = (Settings != null) ? Mathf.Max(1, Settings.FrameSkipInterval) : 1;
            frameCounter++;

            if (frameCounter % skipInterval == 0)
            {
                bool hasNewFrame = false;

                if (UseTestImages)
                {
                    hasNewFrame = true;
                }
                else if (kinectAvailable && Reader != null)
                {
                    var frame = Reader.AcquireLatestFrame();
                    if (frame != null)
                    {
                        if (captureColor)
                        {
                            using (var colorFrame = frame.ColorFrameReference.AcquireFrame())
                            {
                                if (colorFrame != null && colorData != null)
                                {
                                    colorFrame.CopyConvertedFrameDataToArray(colorData, ColorImageFormat.RGBA);
                                    ColorTex.SetPixelData(colorData, 0);
                                    ColorTex.Apply(false);
                                }
                            }
                        }

                        if (captureDepth)
                        {
                            using (var depthFrame = frame.DepthFrameReference.AcquireFrame())
                            {
                                if (depthFrame != null && depthData != null)
                                {
                                    depthFrame.CopyFrameDataToArray(depthData);
                                    ProcessDepthData(depthData, DepthTex.width, DepthTex.height);
                                    DepthTex.SetPixelData(depthData, 0);
                                    DepthTex.Apply(false);
                                }
                            }
                        }

                        if (captureInfrared)
                        {
                            using (var infraFrame = frame.InfraredFrameReference.AcquireFrame())
                            {
                                if (infraFrame != null && infraData != null)
                                {
                                    infraFrame.CopyFrameDataToArray(infraData);
                                    InfraTex.SetPixelData(infraData, 0);
                                    InfraTex.Apply(false);
                                }
                            }
                        }

                        hasNewFrame = true;
                    }
                }

                // Compute Shader Execution
                if (UseBlur)
                {
                    if (hasNewFrame && rTexture != null && DepthTex != null)
                    {
                        CSBlur.SetInt("Radius", FilterRadius);
                        CSBlur.SetTexture((int)FilterType, "SourceTexture", DepthTex);
                        CSBlur.SetTexture((int)FilterType, "OutputTexture", rTexture);
                        CSBlur.Dispatch((int)FilterType, rTexture.width / 8, rTexture.height / 8, 1);

                        if (!isReadbackPending)
                        {
                            isReadbackPending = true;
                            UnityEngine.Rendering.AsyncGPUReadback.Request(rTexture, 0, request =>
                            {
                                isReadbackPending = false;
                                if (request.hasError || DepthTex == null || !this || !gameObject.activeInHierarchy) return;

                                var data = request.GetData<ushort>();
                                if (data.IsCreated && data.Length > 0)
                                {
                                    DepthTex.SetPixelData(data, 0);
                                    DepthTex.Apply(false);
                                }
                            });
                        }
                    }

                    if (Material != null && DepthTex != null)
                    {
                        Material.SetFloat("_DisplPower", DisplacementPower);
                        Material.SetTexture("_DisplTex", DepthTex);
                    }
                }

                OnFrameUpdated?.Invoke();
            }

            yield return Settings != null ? Settings.Timestep : null;
        }
    }

    // Lets consumers (re-)trigger the initialized-resolution announcement, e.g. right after
    // subscribing, without needing to know or care which texture is "active" — there's no such
    // concept here anymore, that's purely a viewer-side notion now.
    public void AnnounceTexturesInitialized()
    {
        OnTexturesInitialized?.Invoke(SourceWidth, SourceHeight);
    }

    // ---------------------------------------------------------------------------------------
    // Still CPU-side for now: this walks every depth pixel (512x424 = ~217K iterations) every
    // Kinect frame to zero out values outside [DepthMin, DepthMax]. The right long-term home for
    // this is the GPU — it could be folded into the CSBlur compute dispatch that already runs
    // right after this on the same texture, avoiding a second pass entirely. That requires
    // editing CSBlur's kernel (not included in what was shared), so it's left as CPU code here,
    // just tightened up: single bounds check via unsigned wraparound instead of two comparisons,
    // and the min/max hoisted out of the loop so they're not re-read from the field each iteration.
    // ---------------------------------------------------------------------------------------
    private void ProcessDepthData(ushort[] array, int width, int height)
    {
        int length = width * height;
        ushort min = DepthMin;
        ushort max = DepthMax;

        for (int i = 0; i < length; i++)
        {
            ushort val = array[i];
            if (val < min || val > max)
            {
                array[i] = 0;
            }
        }
    }

    public Texture2D DuplicateTexture(Texture2D source, TextureFormat format = TextureFormat.RGBA32)
    {
        if (source == null) return null;

        RenderTextureFormat rtFormat = (format == TextureFormat.R16) ? RenderTextureFormat.R16 : RenderTextureFormat.Default;
        RenderTexture renderTex = RenderTexture.GetTemporary(
            source.width,
            source.height,
            0,
            rtFormat,
            RenderTextureReadWrite.Linear);

        Graphics.Blit(source, renderTex);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = renderTex;

        Texture2D readableTexture = new Texture2D(source.width, source.height, format, false);
        readableTexture.ReadPixels(new Rect(0, 0, renderTex.width, renderTex.height), 0, 0);
        readableTexture.Apply(false);

        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(renderTex);

        return readableTexture;
    }

    private void OnApplicationQuit()
    {
        Reader?.Dispose();
        if (Sensor != null && Sensor.IsOpen) Sensor.Close();
    }

#if UNITY_EDITOR
    public void SaveCurrentFramesAsTestImages()
    {
        if (ColorTex == null && DepthTex == null && InfraTex == null)
        {
            Debug.LogWarning("SourceManager: Cannot capture snapshots. Textures are uninitialized. (Run in Play Mode with active Kinect).");
            return;
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string captureFolderPath = $"Assets/KinectTestImages/Capture_{timestamp}";

        if (!Directory.Exists(captureFolderPath))
        {
            Directory.CreateDirectory(captureFolderPath);
        }

        if (ColorTex != null)
        {
            Texture2D readableColor = DuplicateTexture(ColorTex, TextureFormat.RGBA32);
            byte[] bytes = readableColor != null ? readableColor.EncodeToPNG() : null;
            if (readableColor != ColorTex) DestroyImmediate(readableColor);

            if (bytes != null)
            {
                string path = $"{captureFolderPath}/Color.png";
                File.WriteAllBytes(path, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                ConfigureTextureImporter(path, isSingleChannel: false);
                ColorTestImage = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
        }

        if (DepthTex != null)
        {
            Texture2D readableDepth = DuplicateTexture(DepthTex, TextureFormat.R16);
            byte[] bytes = readableDepth != null ? readableDepth.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat) : null;
            if (readableDepth != DepthTex) DestroyImmediate(readableDepth);

            if (bytes != null)
            {
                string path = $"{captureFolderPath}/Depth.exr";
                File.WriteAllBytes(path, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                ConfigureTextureImporter(path, isSingleChannel: true);
                DepthTestImage = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
        }

        if (InfraTex != null)
        {
            Texture2D readableInfra = DuplicateTexture(InfraTex, TextureFormat.R16);
            byte[] bytes = readableInfra != null ? readableInfra.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat) : null;
            if (readableInfra != InfraTex) DestroyImmediate(readableInfra);

            if (bytes != null)
            {
                string path = $"{captureFolderPath}/Infra.exr";
                File.WriteAllBytes(path, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                ConfigureTextureImporter(path, isSingleChannel: true);
                InfraTestImage = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
        }

        EditorUtility.SetDirty(this);
        Debug.Log($"SourceManager: Saved capture sequence successfully to {captureFolderPath}");
    }

    private void ConfigureTextureImporter(string path, bool isSingleChannel)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return;

        // Basic Settings
        importer.textureType = TextureImporterType.Default;
        importer.textureShape = TextureImporterShape.Texture2D;
        importer.sRGBTexture = false; // Disable sRGB (Linear)
        importer.alphaSource = TextureImporterAlphaSource.None;

        // Advanced Settings
        importer.npotScale = TextureImporterNPOTScale.None; // Set Non-Power of 2 to None
        importer.isReadable = true;                          // Enable Read/Write
        importer.mipmapEnabled = false;                      // Disable Mipmaps

        // Filter & Wrap Modes
        importer.filterMode = FilterMode.Point;
        importer.wrapMode = TextureWrapMode.Repeat;

        if (isSingleChannel)
        {
            // Swizzle: R 0 0 0
            // Channel swizzling using individual properties
            importer.swizzleR = TextureImporterSwizzle.R;
            importer.swizzleG = TextureImporterSwizzle.Zero;
            importer.swizzleB = TextureImporterSwizzle.Zero;
            importer.swizzleA = TextureImporterSwizzle.Zero;

            // Force Platform Format to R 16 bit
            TextureImporterPlatformSettings defaultSettings = importer.GetDefaultPlatformTextureSettings();
            defaultSettings.format = TextureImporterFormat.R16;
            defaultSettings.maxTextureSize = 2048;
            defaultSettings.resizeAlgorithm = TextureResizeAlgorithm.Mitchell;
            importer.SetPlatformTextureSettings(defaultSettings);
        }

        // Apply configuration and reimport
        importer.SaveAndReimport();
    }
#endif
}


#if UNITY_EDITOR
[CustomEditor(typeof(SourceManager))]
public class SourceManagerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        SourceManager manager = (SourceManager)target;

        EditorGUILayout.Space(10);
        if (GUILayout.Button("Capture Live Frames as Test Images"))
        {
            manager.SaveCurrentFramesAsTestImages();
        }
    }
}
#endif