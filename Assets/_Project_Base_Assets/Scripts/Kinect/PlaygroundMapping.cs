using UnityEngine;
using OpenCvSharp;

// ---------------------------------------------------------------------------------------
// Single source of truth for: crop-rect resolution (from Settings.MinMaxRect), pixel <->
// world-space mapping onto PlaygroundPlane, GPU-blit flip transforms, and OpenCV flip
// mode — all driven by Settings.FlipX / Settings.FlipY. ArucoDetector, SourceTextureViewer,
// and BlobObjectDetector must all call THIS instead of reimplementing the flip math
// themselves. Fixing a flip/orientation bug here fixes it everywhere at once.
//
// FlipX mirrors columns (left-right, world X). FlipY mirrors rows (up-down, world Z on the
// floor plane). The two are independent and can be combined freely.
// ---------------------------------------------------------------------------------------
public static class PlaygroundMapping
{
    public struct CropRect
    {
        public int X, Y, Width, Height;
    }

    /// <summary>Scale/offset pair for a Graphics.Blit that crops and optionally flips X/Y on the GPU.</summary>
    public struct BlitTransform
    {
        public Vector2 Scale;
        public Vector2 Offset;
    }

    /// <summary>
    /// Resolves the active crop rectangle in full-resolution pixel space, applying
    /// FlipX/FlipY independently. xMin/yMin always &lt; xMax/yMax before rounding.
    /// </summary>
    public static CropRect GetCropBounds(Settings settings, int fullWidth, int fullHeight)
    {
        if (settings == null || !settings.UseBounds)
            return new CropRect { X = 0, Y = 0, Width = fullWidth, Height = fullHeight };

        UnityEngine.Rect rect = settings.MinMaxRect;
        bool isNormalized = rect.xMax <= 1.05f && rect.yMax <= 1.05f && rect.width <= 1.05f && rect.height <= 1.05f;

        float xMin = isNormalized ? rect.xMin * fullWidth : rect.xMin;
        float xMax = isNormalized ? rect.xMax * fullWidth : rect.xMax;
        float yMin = isNormalized ? rect.yMin * fullHeight : rect.yMin;
        float yMax = isNormalized ? rect.yMax * fullHeight : rect.yMax;

        if (settings.FlipX)
        {
            float newXMin = fullWidth - xMax;
            float newXMax = fullWidth - xMin;
            xMin = newXMin; xMax = newXMax;
        }

        if (settings.FlipY)
        {
            float newYMin = fullHeight - yMax;
            float newYMax = fullHeight - yMin;
            yMin = newYMin; yMax = newYMax;
        }

        int cropX = Mathf.Clamp(Mathf.RoundToInt(xMin), 0, fullWidth - 1);
        int cropXMax = Mathf.Clamp(Mathf.RoundToInt(xMax), 0, fullWidth);
        int cropY = Mathf.Clamp(Mathf.RoundToInt(yMin), 0, fullHeight - 1);
        int cropYMax = Mathf.Clamp(Mathf.RoundToInt(yMax), 0, fullHeight);

        return new CropRect
        {
            X = cropX, 
            Y = cropY,
            Width = Mathf.Max(1, Mathf.Min(cropXMax, fullWidth) - cropX),
            Height = Mathf.Max(1, Mathf.Min(cropYMax, fullHeight) - cropY)
        };
    }

    /// <summary>
    /// Maps a pixel coordinate (localX, localY) within the crop rect, in the SAME row/col
    /// convention as the raw source texture (row 0 = top, matching Texture2D.ReadPixels /
    /// GetPixel indexing), to a world-space position on PlaygroundPlane.
    ///
    /// This is THE definition of "pixel row 0 -> which world Z" for the whole project.
    /// Every consumer (Aruco, HandDetector, SourceTextureViewer crop) must go through here.
    /// Pass settings = null when the caller has already applied the flip upstream (e.g. via
    /// GetBlitTransform) to avoid flipping twice.
    /// </summary>
    public static Vector3 PixelToWorld(float localX, float localY, CropRect crop, int fullWidth, int fullHeight, Settings settings, PlaygroundPlane playgroundPlane)
    {
        float px = crop.X + localX;
        float pz = (crop.Y + crop.Height - 1) - localY;

        if (settings != null && settings.FlipX)
        {
            px = fullWidth - 1 - px;
        }
        if (settings != null && settings.FlipY)
        {
            pz = fullHeight - 1 - pz;
        }

        float depth = SourceManager.instance != null ? SourceManager.instance.GetHeightAbovePlane(px, pz) : 0f;

        if (playgroundPlane == null)
        {
            // No plane reference supplied — caller hasn't been updated yet, fall back to the old flat space.
            return new Vector3(px, depth, pz);
        }

        float normX = (px / fullWidth) - 0.5f;
        float normY = (pz / fullHeight) - 0.5f;
        Vector3 quadLocalPos = new Vector3(normX, normY, 0f);

        Vector3 worldPos = playgroundPlane.Transform.TransformPoint(quadLocalPos);
        worldPos += playgroundPlane.Transform.up * depth;

        return worldPos;
    }

    /// <summary>Sub-pixel float variant, for callers (like Aruco corner detection) needing precision beyond whole pixels.</summary>
    public static Vector3 PixelToWorldF(float localX, float localY, CropRect crop, int fullWidth, int fullHeight, Settings settings, PlaygroundPlane playgroundPlane)
    {
        return PixelToWorld(localX, localY, crop, fullWidth, fullHeight, settings, playgroundPlane);
    }

    /// <summary>
    /// Crop rect in DISPLAY space (the space of a texture that was cropped + flipped on the GPU).
    /// GetCropBounds returns the sensor-space rect; this mirrors its origin back when flip is on.
    /// </summary>
    public static CropRect GetDisplayCropBounds(Settings settings, int fullWidth, int fullHeight)
    {
        CropRect c = GetCropBounds(settings, fullWidth, fullHeight);
        if (settings != null && settings.FlipX) c.X = fullWidth - c.X - c.Width;
        if (settings != null && settings.FlipY) c.Y = fullHeight - c.Y - c.Height;
        return c;
    }

    /// <summary>
    /// Pixel of an already cropped + flipped texture -> world position on the PlaygroundPlane.
    /// localY is counted from the BOTTOM row (memory order of the prepared texture).
    /// </summary>
    public static Vector3 PreparedPixelToWorld(float localX, float localY, SourceManager.PreparedRegion region, PlaygroundPlane playgroundPlane)
    {
        float px = region.DisplayCrop.X + localX;
        float pz = region.DisplayCrop.Y + localY;

        float height = SourceManager.instance != null ? SourceManager.instance.GetHeightAbovePlane(px, pz) : 0f;

        if (playgroundPlane == null)
            return new Vector3(px, height, pz);

        Vector3 quadLocalPos = new Vector3((px / region.FullWidth) - 0.5f, (pz / region.FullHeight) - 0.5f, 0f);
        Vector3 worldPos = playgroundPlane.Transform.TransformPoint(quadLocalPos);
        worldPos += playgroundPlane.Transform.up * height;
        return worldPos;
    }

    /// <summary>
    /// Computes the Graphics.Blit scale/offset for cropping to `crop` and applying
    /// Settings.FlipX/FlipY, in UV space. SourceTextureViewer and ArucoDetector both
    /// call this instead of computing scale/offset themselves.
    /// </summary>
    public static BlitTransform GetBlitTransform(Settings settings, CropRect crop, int fullWidth, int fullHeight)
    {
        bool flipX = settings != null && settings.FlipX;
        bool flipY = settings != null && settings.FlipY;

        float scaleX = (flipX ? -1f : 1f) * ((float)crop.Width / fullWidth);
        float scaleY = (flipY ? -1f : 1f) * ((float)crop.Height / fullHeight);

        float offsetX = flipX ? (float)(crop.X + crop.Width) / fullWidth : (float)crop.X / fullWidth;
        float offsetY = flipY ? (float)(crop.Y + crop.Height) / fullHeight : (float)crop.Y / fullHeight;

        return new BlitTransform { Scale = new Vector2(scaleX, scaleY), Offset = new Vector2(offsetX, offsetY) };
    }

    /// <summary>
    /// OpenCvSharp FlipMode matching Settings.FlipX/FlipY, for consumers (BlobObjectDetector)
    /// that flip an OpenCV Mat instead of blitting a texture. FlipMode.X flips vertically
    /// (matches FlipY's "flip the row order" meaning), FlipMode.Y flips horizontally (matches
    /// FlipX's "flip the column order" meaning) - the naming is swapped from ours on purpose,
    /// see BlobObjectDetector for the baseline-flip note.
    /// </summary>
    public static OpenCvSharp.FlipMode? GetOpenCvFlipMode(Settings settings)
    {
        bool flipX = settings != null && settings.FlipX;
        bool flipY = settings != null && settings.FlipY;

        if (flipX && flipY) return OpenCvSharp.FlipMode.XY;
        if (flipY) return OpenCvSharp.FlipMode.X;
        if (flipX) return OpenCvSharp.FlipMode.Y;
        return null;
    }
}