using UnityEngine;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

public static class Extensions
{
    public static void DebugLog(string text)
    {
        // clear Editor-Console
        System.Type.GetType("UnityEditor.LogEntries, UnityEditor").GetMethod("Clear").Invoke(null, null);

        Debug.Log(text);
    }

    public static void PlayPopup(GameObject popup, Transform owner, float lifetime)
    {
        // instantiate popup as detached object
        var instance = Object.Instantiate(popup, owner.position, Quaternion.identity);

        // destroy popup after N seconds
        Object.Destroy(instance, lifetime);
    }

    public static void PlayPopup(GameObject popup, Vector3 position, float lifetime)
    {
        // instantiate popup as detached object
        var instance = Object.Instantiate(popup, position, Quaternion.identity);

        // destroy popup after N seconds
        Object.Destroy(instance, lifetime);
    }

    public static void Readback(RenderTexture render, Texture2D texture)
    {
        AsyncGPUReadback.Request(render, 0, callback => {
            if (texture != null)
            {
                texture.SetPixelData(callback.GetData<byte>(), 0);
                texture.Apply();
            }
        });
    }

    public static Vector2 RandomInCircle(float min, float max)
    {
        return Random.insideUnitCircle.normalized * Random.Range(min, max);
    }

    public static Vector2 RandomInRectangle(Rect rect)
    {
        return new Vector2(Random.Range(rect.min.x, rect.max.x), Random.Range(rect.min.y, rect.max.y));
    }

    public static Vector3 AverageVector(Vector3[] items)
    {
        var average = Vector3.zero;

        foreach (var item in items)
        {
            average += item;
        }

        return average / items.Length;
    }

    public static Quaternion AverageQuaternion(Quaternion[] items)
    {
        var average = items[0];

        foreach (Quaternion item in items)
        {
            average = Quaternion.Slerp(average, item, 0.5f);
        }

        return average;
    }

    public static Mesh CreateMesh(Vector2 size, Vector2Int resolution, Vector2 offset, string name, bool dynamic = true)
    {
        var xVerts = resolution.x + 1;
        var zVerts = resolution.y + 1;

        // set vertices & uv
        var vs = new Vector3[xVerts * zVerts];
        var uv = new Vector2[xVerts * zVerts];
        var vx = size.x / resolution.x;
        var vz = size.y / resolution.y;
        var uvx = 1.0f / resolution.x;
        var uvz = 1.0f / resolution.y;

        for (int i = 0, z = 0; z < zVerts; z++)
        {
            for (int x = 0; x < xVerts; x++, i++)
            {
                vs[i] = new Vector3(x * vx + offset.x, 0, z * vz + offset.y);
                uv[i] = new Vector2(x * uvx, z * uvz);
            }
        }

        // set triangles
        var ts = new int[(xVerts - 1) * (zVerts - 1) * 6];

        for (int i = 0, z = 0; z < (zVerts - 1); z++)
        {
            for (int x = 0; x < (xVerts - 1); x++)
            {
                ts[i++] = (z + 1) * xVerts + x;
                ts[i++] = z * xVerts + x + 1;
                ts[i++] = z * xVerts + x;
                ts[i++] = (z + 1) * xVerts + x + 1;
                ts[i++] = z * xVerts + x + 1;
                ts[i++] = (z + 1) * xVerts + x;
            }
        }

        // set mesh
        var mesh = new Mesh();
        if (dynamic) mesh.MarkDynamic();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.name = name;
        mesh.vertices = vs;
        mesh.triangles = ts;
        mesh.uv = uv;
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();

        return mesh;
    }

    public static Texture2D CreateTexture(int width, int height, TextureFormat format)
    {
        return new Texture2D(width, height, format, false, false)
        {
            anisoLevel = 0,
            filterMode = FilterMode.Point, // Point | Bilinear
            wrapMode = TextureWrapMode.Clamp
        };
    }

    public static RenderTexture CreateRTexture(int width, int height, int depth, RenderTextureFormat format)
    {
        return new RenderTexture(width, height, depth, format)
        {
            useMipMap = false,
            autoGenerateMips = false,
            enableRandomWrite = true,
            anisoLevel = 0,
            filterMode = FilterMode.Point, // Point | Bilinear
            wrapMode = TextureWrapMode.Clamp
        };
    }

    // map value from A..B to X..Y
    public static float Map(float x, float in_min, float in_max, float out_min, float out_max)
    {
        x = out_min + (x - in_min) * (out_max - out_min) / (in_max - in_min);

        return Mathf.Clamp(x, out_min, out_max);
    }

    // map value from X..Y to 0..1
    public static float Map01(float x, float in_min, float in_max)
    {
        x = (x - in_min) / (in_max - in_min);

        return Mathf.Clamp01(x);
    }

    // map  from 0..1 to X..Y
    public static float MapXY(float x, float out_min, float out_max)
    {
        x = out_min + x * (out_max - out_min);

        return Mathf.Clamp(x, out_min, out_max);
    }

    public static string DisplayTime(float time)
    {
        return string.Format("{0:00}.{1:00}", Mathf.FloorToInt(time / 60.0f), Mathf.FloorToInt(time % 60.0f));
    }

    public static Color LDR2HDR(Color color, float intensity)
    {
        return color * Mathf.Pow(2.0f, intensity);
    }

    /// <summary>
    /// Derives a world-space rotation from three of a planar quad's four corners (0=top-left,
    /// 1=top-right, 3=bottom-left — the same winding cv::aruco returns for a single marker).
    /// Works equally for a single marker's own corner points or for four independent marker
    /// centers standing in for the corners of a larger physical rectangle. Returns
    /// Quaternion.identity for a degenerate/zero-area quad so callers can just skip applying it
    /// that frame.
    /// </summary>
    public static Quaternion ComputeQuadRotation(Vector3 topLeft, Vector3 topRight, Vector3 bottomLeft)
    {
        Vector3 right = topRight - topLeft;
        Vector3 down = bottomLeft - topLeft;

        float rightLen = right.magnitude;
        float downLen = down.magnitude;
        if (rightLen < 1e-5f || downLen < 1e-5f) return Quaternion.identity;

        right /= rightLen;
        down /= downLen;

        Vector3 normal = Vector3.Cross(down, right).normalized;
        return Quaternion.LookRotation(normal, -down);
    }

    public static RenderTexture Crop(Texture source, Rect cropBounds, ref RenderTexture target)
    {
        if (source == null) return null;

        int targetWidth = Mathf.Max(1, Mathf.RoundToInt(source.width * cropBounds.width));
        int targetHeight = Mathf.Max(1, Mathf.RoundToInt(source.height * cropBounds.height));

        if (target == null || target.width != targetWidth || target.height != targetHeight)
        {
            if (target != null) target.Release();
            target = new RenderTexture(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
            target.Create();
        }

        Vector2 scale = new Vector2(cropBounds.width, cropBounds.height);
        Vector2 offset = new Vector2(cropBounds.x, cropBounds.y);

        Graphics.Blit(source, target, scale, offset);
        return target;
    }
}