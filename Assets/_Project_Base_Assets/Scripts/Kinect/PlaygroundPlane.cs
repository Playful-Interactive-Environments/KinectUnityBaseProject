using UnityEngine;

// ---------------------------------------------------------------------------------------
// Single source of truth for the physical ground plane's transform. Lives on its own
// GameObject (separate from SourceManager). Auto-sizes itself from the Kinect resolution via
// SourceManager.OnTexturesInitialized, and re-centers if Settings.Playground changes.
//
// Other systems (CameraManager, HandDetector, CalibrationManager, ...) take a reference to
// this component instead of reaching for a raw Transform and re-deriving size/position
// themselves.
// ---------------------------------------------------------------------------------------
public class PlaygroundPlane : MonoBehaviour
{
    [SerializeField] private Settings settings;
    [SerializeField] private Transform originPivot;
    [SerializeField] private MeshCollider meshCollider; // Reference your MeshCollider here

    public Transform Transform => transform;
    public int Width { get; private set; } = 512;
    public int Height { get; private set; } = 424;

    private Quaternion floorRotation;

    private void Awake()
    {
        floorRotation = transform.rotation;
    }

    private void OnEnable()
    {
        SourceManager.OnTexturesInitialized += HandleTexturesInitialized;

        if (settings != null)
        {
            settings.OnSettingsChanged += HandleSettingsChanged;
        }
    }

    private void OnDisable()
    {
        SourceManager.OnTexturesInitialized -= HandleTexturesInitialized;

        if (settings != null)
        {
            settings.OnSettingsChanged -= HandleSettingsChanged;
        }
    }

    private void HandleTexturesInitialized(int width, int height)
    {
        Width = width;
        Height = height;

        SyncTransform();
    }

    private void HandleSettingsChanged()
    {
        ApplyOrientation();
    }

    private void SyncTransform()
    {
        transform.localScale = new Vector3(Width, Height, 1f);
        ApplyOrientation();

        // Force Unity's physics engine to update the collider's world-space bounds immediately
        Physics.SyncTransforms();
    }

    private void UpdateCollider()
    {
        if (meshCollider == null) return;

        // Re-bake the mesh collider
        Mesh mesh = meshCollider.sharedMesh;
        meshCollider.sharedMesh = null;
        meshCollider.sharedMesh = mesh;

        // Ensure the physics engine registers the rotation and scale change instantly
        Physics.SyncTransforms();
    }

    private void ApplyOrientation()
    {
        if (settings == null) return;

        OrientationUtility.GetPlaneBasis(settings.Orientation, originPivot, out Vector3 origin, out _, out Vector3 axisX, out Vector3 axisY);
        transform.position = OrientationUtility.LocalToWorld(new Vector2(Width / 2f, Height / 2f), origin, axisX, axisY);

        // Try changing 90f to -90f, or add 180f to the Y/Z axis if it's facing backward
        transform.rotation = settings.Orientation == Settings.PlaneOrientation.Wall
            ? floorRotation * Quaternion.Euler(-90f, 0f, 0f)
            : floorRotation;

        UpdateCollider();
    }

    public Vector3 WorldToLocal(Vector3 worldPos) => transform.InverseTransformPoint(worldPos);
}