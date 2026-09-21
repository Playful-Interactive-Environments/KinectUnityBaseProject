using System;
using UnityEngine;

// ---------------------------------------------------------------------------------------
// Drives Assets/.../SandboxComposite.shader.
// Event-driven implementation: pushes properties to material only when values change
// or on new frame updates from SourceManager.
// ---------------------------------------------------------------------------------------
public class SourceTextureViewer : MonoBehaviour
{
    [Header("Target Plane")]
    [SerializeField] private Renderer planeRenderer;
    [SerializeField] private Material compositeMaterial;

    [Header("Mode")]
    [SerializeField] private SourceMode currentMode = SourceMode.Depth;

    [Header("Depth Height Ramp")]
    [SerializeField, Tooltip("Color ramp for Depth mode: leftmost stop = farthest/lowest, rightmost = nearest/highest.")]
    private Gradient heightGradient = BuildDefaultHeightGradient();
    [SerializeField, Range(16, 1024)] private int heightLUTSize = 256;
    [SerializeField, Range(450, 10000)] private float depthMin = 500f;
    [SerializeField, Range(450, 10000)] private float depthMax = 700f;

    [Header("References")]
    [SerializeField] private Settings settings;

    /// <summary>
    /// Dedicated Texture2D containing only the active cropped ROI image region.
    /// </summary>
    public Texture2D CroppedTexture { get; private set; }

    /// <summary>
    /// Alias property for cross-subsystem ROI texture compatibility.
    /// </summary>
    public Texture2D RoiTexture => CroppedTexture;

    private Material instantiatedMat;
    private Texture2D heightLUT;

    // Internal trackers to detect changes
    private SourceMode lastMode = (SourceMode)(-1);
    private float lastDepthMin = -1f;
    private float lastDepthMax = -1f;
    private Vector4 lastBounds = Vector4.zero;
    private bool lastUseBounds;
    private bool lastFlipX = false;
    private bool lastFlipY = false;

    public static event Action<Texture> OnActiveTextureChanged;

    // Public C# properties for runtime manipulation via UI/Scripts without polling
    public SourceMode CurrentMode
    {
        get => currentMode;
        set
        {
            if (currentMode != value)
            {
                currentMode = value;
                UpdateMaterialProperties(true);
                OnActiveTextureChanged?.Invoke(CroppedTexture != null ? CroppedTexture : GetActiveBaseTexture());
            }
        }
    }

    private static Gradient BuildDefaultHeightGradient()
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

    private void Awake()
    {
        if (planeRenderer == null) planeRenderer = gameObject.GetComponentInParent<Renderer>();

        if (compositeMaterial != null && planeRenderer != null)
        {
            instantiatedMat = new Material(compositeMaterial);
            planeRenderer.material = instantiatedMat;
        }
    }

    private void Start()
    {
        lastFlipX = !settings.FlipX;
        lastFlipY = !settings.FlipY;
        BuildHeightLUT();
        UpdateMaterialProperties(true);
    }

    private void OnEnable()
    {
        SourceManager.OnFrameUpdated += HandleFrameUpdated;
        SourceManager.OnTexturesInitialized += HandleTexturesInitialized;

        if (settings != null)
        {
            settings.OnSettingsChanged += HandleSettingsChanged;
        }
    }

    private void OnDisable()
    {
        SourceManager.OnFrameUpdated -= HandleFrameUpdated;
        SourceManager.OnTexturesInitialized -= HandleTexturesInitialized;

        if (settings != null)
        {
            settings.OnSettingsChanged -= HandleSettingsChanged;
        }

        CleanupTextures();
    }

    private void HandleSettingsChanged()
    {
        UpdateMaterialProperties(false);
    }

    private void HandleTexturesInitialized(int width, int height)
    {
        UpdateMaterialProperties(true);
        OnActiveTextureChanged?.Invoke(CroppedTexture != null ? CroppedTexture : GetActiveBaseTexture());
    }

    private void HandleFrameUpdated()
    {
        if (instantiatedMat == null || SourceManager.instance == null) return;

        Texture baseTex = GetActiveBaseTexture();
        if (baseTex != null)
        {
            UpdateCroppedTexture(baseTex);
            instantiatedMat.SetTexture("_MainTex", baseTex);
        }

        CheckSettingsBounds();
    }

    public Texture GetActiveBaseTexture()
    {
        return SourceManager.instance != null ? SourceManager.instance.GetTexture(currentMode) : null;
    }

    private void UpdateCroppedTexture(Texture sourceTex)
    {
        if (sourceTex == null) return;

        int fullWidth = sourceTex.width;
        int fullHeight = sourceTex.height;

        var crop = PlaygroundMapping.GetCropBounds(settings, fullWidth, fullHeight);
        int cropX = crop.X, cropY = crop.Y, cropW = crop.Width, cropH = crop.Height;

        RenderTexture rt = RenderTexture.GetTemporary(cropW, cropH, 0, RenderTextureFormat.ARGB32);

        // Matches the shader's UV flip behavior — same transform ArucoDetector uses for its readback.
        var blit = PlaygroundMapping.GetBlitTransform(settings, crop, fullWidth, fullHeight);
        Graphics.Blit(sourceTex, rt, blit.Scale, blit.Offset);

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture.active = rt;

        if (CroppedTexture == null || CroppedTexture.width != cropW || CroppedTexture.height != cropH)
        {
            if (CroppedTexture != null) Destroy(CroppedTexture);
            CroppedTexture = new Texture2D(cropW, cropH, TextureFormat.RGBA32, false);
        }

        CroppedTexture.ReadPixels(new Rect(0, 0, cropW, cropH), 0, 0);
        CroppedTexture.Apply();

        RenderTexture.active = previousActive;
        RenderTexture.ReleaseTemporary(rt);
    }

    private void UpdateMaterialProperties(bool modeChanged)
    {
        if (instantiatedMat == null) return;

        if (modeChanged || currentMode != lastMode)
        {
            bool isDepth = (currentMode == SourceMode.Depth);
            bool isColor = (currentMode == SourceMode.Color);
            instantiatedMat.SetFloat("_IsDepthMode", isDepth ? 1.0f : 0.0f);
            instantiatedMat.SetFloat("_IsColorMode", isColor ? 1.0f : 0.0f);

            Texture baseTex = GetActiveBaseTexture();
            if (baseTex != null)
            {
                UpdateCroppedTexture(baseTex);
                instantiatedMat.SetTexture("_MainTex", baseTex);
            }

            lastMode = currentMode;
        }

        if (settings.UseBounds != lastUseBounds)
        {
            instantiatedMat.SetFloat("_UseBounds", settings.UseBounds ? 1.0f : 0.0f);
            lastUseBounds = settings.UseBounds;
        }

        if (settings != null && (settings.FlipX != lastFlipX || settings.FlipY != lastFlipY))
        {
            instantiatedMat.SetFloat("_FlipX", settings.FlipX ? 1.0f : 0.0f);
            instantiatedMat.SetFloat("_FlipY", settings.FlipY ? 1.0f : 0.0f);
            lastFlipX = settings.FlipX;
            lastFlipY = settings.FlipY;
        }

        if (!Mathf.Approximately(depthMin, lastDepthMin))
        {
            instantiatedMat.SetFloat("_DepthMin", depthMin);
            lastDepthMin = depthMin;
        }

        if (!Mathf.Approximately(depthMax, lastDepthMax))
        {
            instantiatedMat.SetFloat("_DepthMax", depthMax);
            lastDepthMax = depthMax;
        }

        CheckSettingsBounds();
    }

    private void CheckSettingsBounds()
    {
        if (settings != null)
        {
            Rect b = settings.Playground;

            Texture depthTex = SourceManager.instance != null ? SourceManager.instance.GetTexture(SourceMode.Depth) : null;
            float texWidth = (depthTex != null && depthTex.width > 1) ? depthTex.width : 512f;
            float texHeight = (depthTex != null && depthTex.height > 1) ? depthTex.height : 424f;

            //bool invertY = settings.FlipY;
            //float yMinN = invertY ? 1f - (b.yMax / texHeight) : (b.yMin / texHeight);
            //float yMaxN = invertY ? 1f - (b.yMin / texHeight) : (b.yMax / texHeight);

            float yMinN = b.yMin / texHeight;
            float yMaxN = b.yMax / texHeight;

            Vector4 normalizedBounds = new Vector4(
                b.xMin / texWidth,
                yMinN,
                b.xMax / texWidth,
                yMaxN
            );

            if (normalizedBounds != lastBounds)
            {
                instantiatedMat.SetVector("_Bounds", normalizedBounds);
                lastBounds = normalizedBounds;
            }
        }
    }

    private void BuildHeightLUT()
    {
        if (heightLUT != null) Destroy(heightLUT);

        heightLUT = new Texture2D(heightLUTSize, 1, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp
        };
        var pixels = new Color[heightLUTSize];
        for (int x = 0; x < heightLUTSize; x++)
        {
            pixels[x] = heightGradient.Evaluate((float)x / (heightLUTSize - 1));
        }
        heightLUT.SetPixels(pixels);
        heightLUT.Apply();

        if (instantiatedMat != null)
        {
            instantiatedMat.SetTexture("_HeightLUT", heightLUT);
        }
    }

    private void CleanupTextures()
    {
        if (CroppedTexture != null)
        {
            Destroy(CroppedTexture);
            CroppedTexture = null;
        }

        if (heightLUT != null)
        {
            Destroy(heightLUT);
            heightLUT = null;
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (Application.isPlaying)
        {
            BuildHeightLUT();
            UpdateMaterialProperties(true);
        }
    }
#endif
}