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

    // Only allocated for modes that have no GPU-prepared texture (Color).
    private Texture lastActiveTexture;


    /// <summary>
    /// Alias property for cross-subsystem ROI texture compatibility.
    /// </summary>
    public Texture CroppedTexture => GetActiveBaseTexture();
    public Texture RoiTexture => CroppedTexture;

    private RenderTexture roiCanvas;

    private Material instantiatedMat;
    private Texture2D heightLUT;

    // Internal trackers to detect changes
    private SourceMode lastMode = (SourceMode)(-1);
    private float lastDepthMin = -1f;
    private float lastDepthMax = -1f;
    private Vector4 lastBounds = Vector4.zero;
    private bool boundsDirty = false;
    private Vector4 pendingBounds;

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
        BuildHeightLUT();
        UpdateMaterialProperties(true);
    }

    private void LateUpdate()
    {
        if (boundsDirty && instantiatedMat != null)
        {
            instantiatedMat.SetVector("_Bounds", pendingBounds);
            lastBounds = pendingBounds;
            boundsDirty = false;
        }
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

        Texture display = GetDisplayTexture();
        if (display != null) instantiatedMat.SetTexture("_MainTex", display);

        CheckSettingsBounds();

        if (display != lastActiveTexture) // prepared texture appears / gets reallocated on crop resize
        {
            lastActiveTexture = display;
            OnActiveTextureChanged?.Invoke(display);
        }
    }

    public Texture GetActiveBaseTexture()
    {
        if (SourceManager.instance == null) return null;

        Texture prepared = SourceManager.instance.GetPreparedDisplayTexture(currentMode);
        return prepared != null ? prepared : SourceManager.instance.GetTexture(currentMode);
    }

    // UseBounds off: prepared texture already IS the full frame -> show it as is.
    // UseBounds on: place the ROI at its display-space position on a black full-frame canvas.
    private Texture GetDisplayTexture()
    {
        Texture prepared = GetActiveBaseTexture();
        var sm = SourceManager.instance;

        if (prepared == null || sm == null || sm.GetPreparedDisplayTexture(currentMode) == null)
        {
            ReleaseCanvas();
            return prepared;
        }

        var region = sm.GetPreparedRegion(currentMode);
        var dc = region.DisplayCrop;
        Vector2Int full = new Vector2Int(region.FullWidth, region.FullHeight);

        bool regionMatchesTexture = region.IsValid && dc.Width == prepared.width && dc.Height == prepared.height;
        bool coversFullFrame = regionMatchesTexture && dc.Width == full.x && dc.Height == full.y;

        if (coversFullFrame)
        {
            ReleaseCanvas();
            return prepared;
        }

        if (!regionMatchesTexture)
        {
            // SourceManager hasn't reallocated `prepared` for the new crop size yet.
            // Hold the previous frame's display instead of snapping to an unpositioned, stale-size texture.
            if (roiCanvas != null) return roiCanvas;
            if (lastActiveTexture != null) return lastActiveTexture;
            return prepared; // first-ever frame, nothing to hold onto yet
        }

        RenderTextureFormat fmt = prepared is RenderTexture preparedRT ? preparedRT.format : RenderTextureFormat.R16;

        if (roiCanvas == null || roiCanvas.width != full.x || roiCanvas.height != full.y || roiCanvas.format != fmt)
        {
            ReleaseCanvas();
            roiCanvas = new RenderTexture(full.x, full.y, 0, fmt);
            roiCanvas.filterMode = prepared.filterMode;
            roiCanvas.Create();
        }

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = roiCanvas;
        GL.Clear(false, true, Color.black);
        RenderTexture.active = prev;

        int w = Mathf.Min(prepared.width, roiCanvas.width - dc.X);
        int h = Mathf.Min(prepared.height, roiCanvas.height - dc.Y);
        if (w > 0 && h > 0)
            Graphics.CopyTexture(prepared, 0, 0, 0, 0, w, h, roiCanvas, 0, 0, dc.X, dc.Y);

        return roiCanvas;
    }

    private void ReleaseCanvas()
    {
        if (roiCanvas == null) return;
        roiCanvas.Release();
        Destroy(roiCanvas);
        roiCanvas = null;
    }

    private void UpdateMaterialProperties(bool modeChanged)
    {
        if (instantiatedMat == null) return;

        if (modeChanged || currentMode != lastMode)
        {
            instantiatedMat.SetFloat("_IsDepthMode", currentMode == SourceMode.Depth ? 1f : 0f);
            instantiatedMat.SetFloat("_IsColorMode", currentMode == SourceMode.Color ? 1f : 0f);

            Texture active = GetActiveBaseTexture();
            if (active != null) instantiatedMat.SetTexture("_MainTex", active);

            lastMode = currentMode;
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

            var sm = SourceManager.instance;
            float fw = (sm != null && sm.SourceWidth > 1) ? sm.SourceWidth : 512f;
            float fh = (sm != null && sm.SourceHeight > 1) ? sm.SourceHeight : 424f;

            Vector4 normalizedBounds = new Vector4(b.xMin / fw, b.yMin / fh, b.xMax / fw, b.yMax / fh);

            if (normalizedBounds != lastBounds)
            {
                pendingBounds = normalizedBounds;
                boundsDirty = true;
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
        if (heightLUT != null)
        {
            Destroy(heightLUT);
            heightLUT = null;
        }

        ReleaseCanvas();
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