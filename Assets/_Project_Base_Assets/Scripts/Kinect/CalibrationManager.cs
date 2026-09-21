using UnityEngine;
using UnityEngine.InputSystem;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class CalibrationManager : MonoBehaviour
{
    [SerializeField]
    private Settings Settings;

    [SerializeField]
    private Key CalibrationKey = Key.F1;

    // Sizing/position of the actual ground plane now lives entirely in PlaygroundPlane; this
    // reference is only used to read back the current resolution for the debug gizmo below.
    [SerializeField] private PlaygroundPlane playgroundPlane;

    [Header("Playground Edit Mode")]
    [Tooltip("Camera used to raycast the mouse onto the ground plane at runtime. Defaults to Camera.main.")]
    [SerializeField] private Camera runtimeCamera;

    [Tooltip("Extra world-space margin added around the playground rect + handle knobs when auto-framing the camera in edit mode.")]
    [Range(0f, 1f)]
    [SerializeField] private float cameraFitPaddingRatio = 0.15f;

    [SerializeField] private Transform originPivot;

    [Tooltip("Handle knob size as a fraction of the rect's largest dimension. Shared by both the drawn gizmo spheres and their hover/click hit radius, so they always match visually.")]
    [Range(0.01f, 0.2f)]
    [SerializeField] private float handleSizeRatio = 0.03f;

    private enum DragHandle { None, Move, Left, Right, Top, Bottom }

    // Floor: rect lies flat, normal is the reference transform's up (gravity sits on the plane).
    // Wall: rect lies upright, normal is the reference transform's forward (gravity runs along the plane).
    private Settings.PlaneOrientation currentOrientation = Settings.PlaneOrientation.Floor;
    private bool isEditModeActive;
    private DragHandle activeDragHandle = DragHandle.None;
    private DragHandle hoveredHandle = DragHandle.None;
    private Vector2 dragStartMouseWorld;
    private Rect dragStartRect;
    private Vector3 dragCamPosition;
    private Quaternion dragCamRotation;
    private Vector3 dragOrigin, dragNormal, dragAxisX, dragAxisY;

    private bool cachedCameraPoseValid;
    private Vector3 cachedCamPosition;
    private Quaternion cachedCamRotation;
    private bool cachedCamOrthographic;
    private float cachedCamOrthoSize;
    private float cachedCamFov;


    [System.Serializable]
    private class CalibrationSnapshot
    {
        public float playgroundX, playgroundY, playgroundWidth, playgroundHeight;
        public Settings.PlaneOrientation orientation;
        public Vector3 wallOriginOffset;
    }

    private void OnEnable()
    {
        if (Settings != null)
        {
            Settings.EnsureLoaded();
            Settings.OnSettingsChanged += HandleSettingsChanged;
            currentOrientation = Settings.Orientation;
        }

        #if UNITY_EDITOR
                SceneView.duringSceneGui += OnSceneGUI;
        #endif
    }

    private void OnDisable()
    {
        if (Settings != null)
        {
            Settings.OnSettingsChanged -= HandleSettingsChanged;
        }

#if UNITY_EDITOR
        SceneView.duringSceneGui -= OnSceneGUI;
#endif
    }

    private void HandleSettingsChanged()
    {
        if (Settings != null)
        {
            currentOrientation = Settings.Orientation;
        }
    }

    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current[CalibrationKey].wasPressedThisFrame)
        {
            if (!isEditModeActive && Keyboard.current.tabKey.isPressed)
            {
                Debug.Log("EditModeActive");
                EnterEditMode();
   
            }
            else if (isEditModeActive)
            {
                Debug.Log("EditModeExit");
                ExitEditMode();

            }
        }

        if (isEditModeActive)
        {
            if (Keyboard.current[Key.C].wasPressedThisFrame)
            {
                CycleOrientation();
            }

            HandleRuntimeDrag();
        }
    }

    private void EnterEditMode()
    {
        isEditModeActive = true;
        activeDragHandle = DragHandle.None;

        CacheCameraPose();
        FitCameraToPlayground();

        Extensions.DebugLog("Playground edit mode <color=magenta>ENTERED</color>. Drag the rect to move it, drag an edge to resize. Press F1 to exit.");
    }

    private void ExitEditMode()
    {
        isEditModeActive = false;
        activeDragHandle = DragHandle.None;

        SaveSettingsToAsset();
        RestoreCameraPose();

        Settings.NotifySettingsChanged();

        Extensions.DebugLog("Playground edit mode <color=magenta>EXITED</color>. Changes saved to Settings asset.");
    }

    private void CacheCameraPose()
    {
        Camera cam = runtimeCamera != null ? runtimeCamera : Camera.main;
        if (cam == null)
        {
            cachedCameraPoseValid = false;
            return;
        }

        cachedCamPosition = cam.transform.position;
        cachedCamRotation = cam.transform.rotation;
        cachedCamOrthographic = cam.orthographic;
        cachedCamOrthoSize = cam.orthographicSize;
        cachedCamFov = cam.fieldOfView;
        cachedCameraPoseValid = true;
    }

    private void RestoreCameraPose()
    {
        if (!cachedCameraPoseValid) return;

        Camera cam = runtimeCamera != null ? runtimeCamera : Camera.main;
        if (cam == null) { cachedCameraPoseValid = false; return; }

        cam.transform.SetPositionAndRotation(cachedCamPosition, cachedCamRotation);
        if (cachedCamOrthographic) cam.orthographicSize = cachedCamOrthoSize;
        else cam.fieldOfView = cachedCamFov;

        cachedCameraPoseValid = false;
    }

    // Frames the camera so the playground rect (plus its handle knobs, plus padding) stays fully
    // visible regardless of rect size/aspect or the camera's own type (ortho vs perspective) or
    // aspect ratio. Orthographic: adjusts orthographicSize, keeps existing viewing distance along
    // the plane normal. Perspective: keeps FOV, dollies the camera along the normal to a distance
    // that fits both the vertical and horizontal extents.
    private void FitCameraToPlayground()
    {
        Camera cam = runtimeCamera != null ? runtimeCamera : Camera.main;
        if (cam == null || Settings == null) return;

        GetPlaneBasis(out Vector3 origin, out Vector3 normal, out Vector3 axisX, out Vector3 axisY);

        Rect rect = Settings.Playground;
        Vector3 center = LocalToWorld(new Vector2(rect.center.x, rect.center.y), origin, axisX, axisY);

        // Pad by the handle knob radius so a handle sitting exactly on the rect edge doesn't clip
        // at the frustum border, then add the configurable extra margin on top.
        float handlePad = GetHandleRadius(rect);
        float padding = 1f + cameraFitPaddingRatio;
        float halfW = (rect.width * 0.5f + handlePad) * padding;
        float halfH = (rect.height * 0.5f + handlePad) * padding;

        float aspect = cam.aspect;

        if (cam.orthographic)
        {
            // Any distance along the normal frames the same view for an orthographic camera —
            // preserve the current one (falling back to a safe default) rather than picking an
            // arbitrary value that could clip through near/far planes.
            float currentDistance = Vector3.Dot(cam.transform.position - origin, normal);
            if (currentDistance < 0.01f) currentDistance = 10f;

            cam.orthographicSize = Mathf.Max(halfH, halfW / aspect);
            cam.transform.SetPositionAndRotation(center + normal * currentDistance, Quaternion.LookRotation(-normal, axisY));
        }
        else
        {
            float vFovRad = cam.fieldOfView * Mathf.Deg2Rad;
            float distV = halfH / Mathf.Tan(vFovRad * 0.5f);

            float hFovRad = 2f * Mathf.Atan(Mathf.Tan(vFovRad * 0.5f) * aspect);
            float distH = halfW / Mathf.Tan(hFovRad * 0.5f);

            float distance = Mathf.Max(distV, distH);
            cam.transform.SetPositionAndRotation(center + normal * distance, Quaternion.LookRotation(-normal, axisY));
        }
    }

    private void SaveSettingsToAsset()
    {
        if (Settings == null)
        {
            return;
        }

#if UNITY_EDITOR
        EditorUtility.SetDirty(Settings);
        AssetDatabase.SaveAssets();
#endif

        Settings.SaveToFile();
    }


    private void CycleOrientation()
    {
        activeDragHandle = DragHandle.None;
        Settings.CycleOrientation();
        FitCameraToPlayground();
        Extensions.DebugLog($"Playground orientation switched to <color=magenta>{currentOrientation}</color>.");
    }

    // ---------------------------------------------------------------------
    // Plane basis - derived from the reference transform's own rotation, so
    // "Wall" isn't a fixed world axis, it's just a different pair of local
    // axes on whatever object the playground plane is parented/aligned to.
    // ---------------------------------------------------------------------

    private void GetPlaneBasis(out Vector3 origin, out Vector3 normal, out Vector3 axisX, out Vector3 axisY)
    {
        OrientationUtility.GetPlaneBasis(currentOrientation, originPivot, out origin, out normal, out axisX, out axisY);

        if (currentOrientation == Settings.PlaneOrientation.Wall && Settings != null)
        {
            origin += Settings.WallOriginOffset;
        }
    }

    private static Vector2 WorldToLocal(Vector3 world, Vector3 origin, Vector3 axisX, Vector3 axisY)
    {
        Vector3 offset = world - origin;
        return new Vector2(Vector3.Dot(offset, axisX), Vector3.Dot(offset, axisY));
    }

    private static Vector3 LocalToWorld(Vector2 local, Vector3 origin, Vector3 axisX, Vector3 axisY)
    {
        return origin + axisX * local.x + axisY * local.y;
    }

    private static Vector3 ProjectOntoPlane(Vector3 point, Vector3 origin, Vector3 normal)
    {
        return point - Vector3.Dot(point - origin, normal) * normal;
    }

    // ---------------------------------------------------------------------
    // Runtime (Game view / mouse) dragging
    // ---------------------------------------------------------------------

    private void HandleRuntimeDrag()
    {
        if (Settings == null)
        {
            return;
        }

        GetPlaneBasis(out Vector3 origin, out Vector3 normal, out Vector3 axisX, out Vector3 axisY);

        Vector2? mouseLocal = null;
        if (TryRaycastPlane(origin, normal, out Vector3 worldPoint))
        {
            mouseLocal = WorldToLocal(worldPoint, origin, axisX, axisY);
        }

        if (activeDragHandle == DragHandle.None)
        {
            hoveredHandle = mouseLocal.HasValue ? DetectHandle(mouseLocal.Value, Settings.Playground) : DragHandle.None;
        }

        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            if (mouseLocal.HasValue && hoveredHandle != DragHandle.None)
            {
                activeDragHandle = hoveredHandle;
                dragStartMouseWorld = mouseLocal.Value;
                dragStartRect = Settings.Playground;

                // Cache plane basis + camera pose for the whole drag. Reusing live values
                // would feed back: moving the rect moves the camera (CameraFollowsRect),
                // which changes where the same mouse pixel raycasts to, producing a bigger
                // delta next frame, compounding without bound.
                dragOrigin = origin;
                dragNormal = normal;
                dragAxisX = axisX;
                dragAxisY = axisY;

                Camera cam = runtimeCamera != null ? runtimeCamera : Camera.main;
                if (cam != null)
                {
                    dragCamPosition = cam.transform.position;
                    dragCamRotation = cam.transform.rotation;
                }
            }
        }
        else if (Mouse.current != null && Mouse.current.leftButton.isPressed && activeDragHandle != DragHandle.None)
        {
            if (TryRaycastPlaneAtPose(dragOrigin, dragNormal, dragCamPosition, dragCamRotation, out Vector3 dragWorldPoint))
            {
                Vector2 mouseLocalDrag = WorldToLocal(dragWorldPoint, dragOrigin, dragAxisX, dragAxisY);
                Vector2 delta = mouseLocalDrag - dragStartMouseWorld;
                Settings.SetPlaygroundManual(ApplyDrag(dragStartRect, activeDragHandle, delta));
                FitCameraToPlayground(); // keep handles in frame as the rect resizes

                #if UNITY_EDITOR
                  EditorUtility.SetDirty(Settings);
                #endif
            }
        }
        else if (Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame)
        {
            activeDragHandle = DragHandle.None;
            SaveSettingsToAsset();
        }
    }

    private bool TryRaycastPlane(Vector3 origin, Vector3 normal, out Vector3 worldPoint)
    {
        Camera cam = runtimeCamera != null ? runtimeCamera : Camera.main;
        worldPoint = default;

        if (cam == null || Mouse.current == null)
        {
            return false;
        }

        Vector2 mousePos = Mouse.current.position.ReadValue();
        Ray ray = cam.ScreenPointToRay(mousePos);
        Plane plane = new Plane(normal, origin);

        if (plane.Raycast(ray, out float enter))
        {
            worldPoint = ray.GetPoint(enter);
            return true;
        }

        return false;
    }

    private bool TryRaycastPlaneAtPose(Vector3 origin, Vector3 normal, Vector3 camPosition, Quaternion camRotation, out Vector3 worldPoint)
    {
        Camera cam = runtimeCamera != null ? runtimeCamera : Camera.main;
        worldPoint = default;

        if (cam == null || Mouse.current == null)
        {
            return false;
        }

        Vector3 originalPosition = cam.transform.position;
        Quaternion originalRotation = cam.transform.rotation;

        cam.transform.SetPositionAndRotation(camPosition, camRotation);

        Vector2 mousePos = Mouse.current.position.ReadValue();
        Ray ray = cam.ScreenPointToRay(mousePos);
        Plane plane = new Plane(normal, origin);
        bool hit = plane.Raycast(ray, out float enter);
        worldPoint = hit ? ray.GetPoint(enter) : default;

        cam.transform.SetPositionAndRotation(originalPosition, originalRotation);

        return hit;
    }

    private DragHandle DetectHandle(Vector2 point, Rect rect)
    {
        float radius = GetHandleRadius(rect);

        if (Vector2.Distance(point, new Vector2(rect.xMin, rect.center.y)) <= radius) return DragHandle.Left;
        if (Vector2.Distance(point, new Vector2(rect.xMax, rect.center.y)) <= radius) return DragHandle.Right;
        if (Vector2.Distance(point, new Vector2(rect.center.x, rect.yMax)) <= radius) return DragHandle.Top;
        if (Vector2.Distance(point, new Vector2(rect.center.x, rect.yMin)) <= radius) return DragHandle.Bottom;
        if (rect.Contains(point)) return DragHandle.Move;

        return DragHandle.None;
    }

    private float GetHandleRadius(Rect rect)
    {
        return Mathf.Max(rect.width, rect.height) * handleSizeRatio;
    }

    private static Rect ApplyDrag(Rect start, DragHandle handle, Vector2 delta)
    {
        const float minSize = 0.01f;

        switch (handle)
        {
            case DragHandle.Move:
                return new Rect(start.x + delta.x, start.y + delta.y, start.width, start.height);

            case DragHandle.Left:
                {
                    float newXMin = Mathf.Min(start.xMin + delta.x, start.xMax - minSize);
                    return Rect.MinMaxRect(newXMin, start.yMin, start.xMax, start.yMax);
                }
            case DragHandle.Right:
                {
                    float newXMax = Mathf.Max(start.xMax + delta.x, start.xMin + minSize);
                    return Rect.MinMaxRect(start.xMin, start.yMin, newXMax, start.yMax);
                }
            case DragHandle.Bottom:
                {
                    float newYMin = Mathf.Min(start.yMin + delta.y, start.yMax - minSize);
                    return Rect.MinMaxRect(start.xMin, newYMin, start.xMax, start.yMax);
                }
            case DragHandle.Top:
                {
                    float newYMax = Mathf.Max(start.yMax + delta.y, start.yMin + minSize);
                    return Rect.MinMaxRect(start.xMin, start.yMin, start.xMax, newYMax);
                }
            default:
                return start;
        }
    }

    private void OnGUI()
    {
        if (!isEditModeActive)
        {
            return;
        }

        var style = new GUIStyle(GUI.skin.box)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.yellow }
        };

        GUI.Box(new Rect(10, 10, 300, 52), $"PLAYGROUND EDIT MODE (F1 to exit)\nOrientation: {currentOrientation}  (C to cycle)", style);
    }

    // ---------------------------------------------------------------------
    // Editor Scene view dragging
    // ---------------------------------------------------------------------

#if UNITY_EDITOR
    private void OnSceneGUI(SceneView sceneView)
    {
        if (!isEditModeActive || Settings == null)
        {
            return;
        }

        Rect rect = Settings.Playground;
        GetPlaneBasis(out Vector3 origin, out Vector3 normal, out Vector3 axisX, out Vector3 axisY);

        Vector3 center = LocalToWorld(new Vector2(rect.center.x, rect.center.y), origin, axisX, axisY);
        Vector3 leftMid = LocalToWorld(new Vector2(rect.xMin, rect.center.y), origin, axisX, axisY);
        Vector3 rightMid = LocalToWorld(new Vector2(rect.xMax, rect.center.y), origin, axisX, axisY);
        Vector3 topMid = LocalToWorld(new Vector2(rect.center.x, rect.yMax), origin, axisX, axisY);
        Vector3 bottomMid = LocalToWorld(new Vector2(rect.center.x, rect.yMin), origin, axisX, axisY);

        Handles.color = Color.magenta;
        EditorGUI.BeginChangeCheck();
        Vector3 newCenter = Handles.FreeMoveHandle(center, HandleUtility.GetHandleSize(center) * 0.15f, Vector3.zero, Handles.SphereHandleCap);
        if (EditorGUI.EndChangeCheck())
        {
            newCenter = ProjectOntoPlane(newCenter, origin, normal);
            Vector2 newCenterLocal = WorldToLocal(newCenter, origin, axisX, axisY);
            Vector2 delta = newCenterLocal - rect.center;
            Settings.SetPlaygroundManual(new Rect(rect.x + delta.x, rect.y + delta.y, rect.width, rect.height));
            sceneView.Repaint();
            return;
        }

        Handles.color = Color.yellow;

        EditorGUI.BeginChangeCheck();
        Vector3 newLeft = Handles.FreeMoveHandle(leftMid, HandleUtility.GetHandleSize(leftMid) * 0.1f, Vector3.zero, Handles.CubeHandleCap);
        if (EditorGUI.EndChangeCheck())
        {
            newLeft = ProjectOntoPlane(newLeft, origin, normal);
            float newXMin = Mathf.Min(WorldToLocal(newLeft, origin, axisX, axisY).x, rect.xMax - 0.01f);
            Settings.SetPlaygroundManual(Rect.MinMaxRect(newXMin, rect.yMin, rect.xMax, rect.yMax));
            sceneView.Repaint();
            return;
        }

        EditorGUI.BeginChangeCheck();
        Vector3 newRight = Handles.FreeMoveHandle(rightMid, HandleUtility.GetHandleSize(rightMid) * 0.1f, Vector3.zero, Handles.CubeHandleCap);
        if (EditorGUI.EndChangeCheck())
        {
            newRight = ProjectOntoPlane(newRight, origin, normal);
            float newXMax = Mathf.Max(WorldToLocal(newRight, origin, axisX, axisY).x, rect.xMin + 0.01f);
            Settings.SetPlaygroundManual(Rect.MinMaxRect(rect.xMin, rect.yMin, newXMax, rect.yMax));
            sceneView.Repaint();
            return;
        }

        EditorGUI.BeginChangeCheck();
        Vector3 newTop = Handles.FreeMoveHandle(topMid, HandleUtility.GetHandleSize(topMid) * 0.1f, Vector3.zero, Handles.CubeHandleCap);
        if (EditorGUI.EndChangeCheck())
        {
            newTop = ProjectOntoPlane(newTop, origin, normal);
            float newYMax = Mathf.Max(WorldToLocal(newTop, origin, axisX, axisY).y, rect.yMin + 0.01f);
            Settings.SetPlaygroundManual(Rect.MinMaxRect(rect.xMin, rect.yMin, rect.xMax, newYMax));
            sceneView.Repaint();
            return;
        }

        EditorGUI.BeginChangeCheck();
        Vector3 newBottom = Handles.FreeMoveHandle(bottomMid, HandleUtility.GetHandleSize(bottomMid) * 0.1f, Vector3.zero, Handles.CubeHandleCap);
        if (EditorGUI.EndChangeCheck())
        {
            newBottom = ProjectOntoPlane(newBottom, origin, normal);
            float newYMin = Mathf.Min(WorldToLocal(newBottom, origin, axisX, axisY).y, rect.yMax - 0.01f);
            Settings.SetPlaygroundManual(Rect.MinMaxRect(rect.xMin, newYMin, rect.xMax, rect.yMax));
            sceneView.Repaint();
            return;
        }

        sceneView.Repaint();
    }
#endif

    private void OnDrawGizmos()
    {
        if (Settings == null)
        {
            return;
        }

        int width = playgroundPlane != null ? playgroundPlane.Width : 512;
        int height = playgroundPlane != null ? playgroundPlane.Height : 424;

        GetPlaneBasis(out Vector3 origin, out Vector3 normal, out Vector3 axisX, out Vector3 axisY);

        // kinect detection
        Gizmos.color = Color.red;
        DrawRectGizmo(new Rect(0, 0, width, height), origin, axisX, axisY);

        // projection area
        Gizmos.color = Color.yellow;
        DrawRectGizmo(Settings.Projection, origin, axisX, axisY);

        // playground area
        Rect playground = Settings.Playground;
        Gizmos.color = isEditModeActive ? Color.magenta : Color.green;
        DrawRectGizmo(playground, origin, axisX, axisY);

        Vector3 center = LocalToWorld(new Vector2(playground.center.x, playground.center.y), origin, axisX, axisY);

        // normal indicator - makes Floor vs Wall obvious at a glance
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(center, center + normal * Mathf.Max(playground.width, playground.height) * 0.2f);

        if (isEditModeActive)
        {
            float handleSize = GetHandleRadius(playground);

            DrawHandleGizmo(DragHandle.Move, center, handleSize);
            DrawHandleGizmo(DragHandle.Left, LocalToWorld(new Vector2(playground.xMin, playground.center.y), origin, axisX, axisY), handleSize);
            DrawHandleGizmo(DragHandle.Right, LocalToWorld(new Vector2(playground.xMax, playground.center.y), origin, axisX, axisY), handleSize);
            DrawHandleGizmo(DragHandle.Bottom, LocalToWorld(new Vector2(playground.center.x, playground.yMin), origin, axisX, axisY), handleSize);
            DrawHandleGizmo(DragHandle.Top, LocalToWorld(new Vector2(playground.center.x, playground.yMax), origin, axisX, axisY), handleSize);
        }
    }

    private void DrawHandleGizmo(DragHandle handle, Vector3 position, float size)
    {
        bool isActive = activeDragHandle == handle;
        bool isHovered = hoveredHandle == handle;

        Gizmos.color = isActive ? Color.white : (isHovered ? Color.cyan : Color.magenta);
        Gizmos.DrawSphere(position, size);
    }

    private void DrawRectGizmo(Rect rect, Vector3 origin, Vector3 axisX, Vector3 axisY)
    {
        Vector3 p00 = LocalToWorld(new Vector2(rect.xMin, rect.yMin), origin, axisX, axisY);
        Vector3 p10 = LocalToWorld(new Vector2(rect.xMax, rect.yMin), origin, axisX, axisY);
        Vector3 p11 = LocalToWorld(new Vector2(rect.xMax, rect.yMax), origin, axisX, axisY);
        Vector3 p01 = LocalToWorld(new Vector2(rect.xMin, rect.yMax), origin, axisX, axisY);

        Gizmos.DrawLine(p00, p10);
        Gizmos.DrawLine(p10, p11);
        Gizmos.DrawLine(p11, p01);
        Gizmos.DrawLine(p01, p00);
    }
}