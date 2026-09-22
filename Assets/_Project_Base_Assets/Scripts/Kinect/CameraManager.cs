using UnityEngine;

[RequireComponent(typeof(Camera))]
public class CameraManager : MonoBehaviour
{
    [SerializeField]
    private Settings settings;
    [SerializeField] private Transform originPivot;
    [SerializeField] private PlaygroundPlane playgroundPlane;


    private Camera targetCamera;

    private Quaternion floorRotation;


    private void Awake()
    {
        targetCamera = GetComponent<Camera>();
        floorRotation = transform.rotation; // assumes the camera is set up top-down (Floor) in the scene
    }

    private void OnEnable()
    {
        if (settings != null)
        {
            settings.OnSettingsChanged += ApplyCameraSettings;
        }

        ApplyCameraSettings();
    }

    private void OnDisable()
    {
        if (settings != null)
        {
            settings.OnSettingsChanged -= ApplyCameraSettings;
        }
    }

    private void ApplyCameraSettings()
    {
        if (settings == null) return;

        OrientationUtility.GetPlaneBasis(settings.Orientation, originPivot, out Vector3 origin, out Vector3 normal, out Vector3 axisX, out Vector3 axisY);

        if (settings.Orientation == Settings.PlaneOrientation.Wall)
        {
            origin += settings.WallOriginOffset;
        }

        targetCamera.orthographic = !settings.UsePerspectiveCamera;

        if (settings.UsePerspectiveCamera)
        {
            targetCamera.fieldOfView = settings.PerspectiveFOV;
        }
        else
        {
            targetCamera.orthographicSize = settings.Playground.height / 2.0f;
        }

        Vector3 basePosition;
        if (settings.CameraFollowsRect)
        {
            basePosition = OrientationUtility.LocalToWorld(settings.Playground.center, origin, axisX, axisY);
        }
        else
        {
            int width = playgroundPlane != null ? playgroundPlane.Width : 512;
            int height = playgroundPlane != null ? playgroundPlane.Height : 424;
            basePosition = OrientationUtility.LocalToWorld(new Vector2(width / 2f, height / 2f), origin, axisX, axisY);
        }

        // Set position along plane normal
        transform.position = basePosition + normal * settings.CameraDistance;

        // Set rotation directly from plane basis vectors for both Floor and Wall
        Vector3 upVector = (settings.Orientation == Settings.PlaneOrientation.Wall) ? Vector3.up : axisY;
        transform.rotation = Quaternion.LookRotation(-normal, upVector);
    }
}