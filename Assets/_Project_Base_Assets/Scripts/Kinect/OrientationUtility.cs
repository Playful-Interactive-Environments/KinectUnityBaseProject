using UnityEngine;

public static class OrientationUtility
{
    public static void GetPlaneBasis(Settings.PlaneOrientation orientation, Transform pivot, out Vector3 origin, out Vector3 normal, out Vector3 axisX, out Vector3 axisY)
    {
        origin = pivot != null ? pivot.position : Vector3.zero;

        if (orientation == Settings.PlaneOrientation.Wall)
        {
            normal = Vector3.back;
            axisX = Vector3.right;
            axisY = Vector3.up;
        }
        else
        {
            normal = Vector3.up;
            axisX = Vector3.right;
            axisY = Vector3.forward;
        }
    }

    public static Vector3 LocalToWorld(Vector2 local, Vector3 origin, Vector3 axisX, Vector3 axisY)
    {
        return origin + axisX * local.x + axisY * local.y;
    }

}