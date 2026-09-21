using UnityEngine;

// ---------------------------------------------------------------------------------------
// Moves this object smoothly back and forth along a single axis. Useful for visually
// testing frame smoothness / stutter (e.g. comparing against Settings.FrameSkipInterval,
// blur settings, or general pipeline latency).
// ---------------------------------------------------------------------------------------
public class SmoothMover : MonoBehaviour
{
    private enum Axis { X, Y, Z }

    [Header("Motion")]
    [SerializeField] private Axis moveAxis = Axis.X;
    [SerializeField] private float distance = 5f;
    [SerializeField] private float speed = 1f;

    [Header("Easing")]
    [Tooltip("If enabled, uses smoothstep easing at the turnaround points instead of a linear ping-pong (removes the constant-velocity 'bounce').")]
    [SerializeField] private bool smoothEasing = true;

    private Vector3 startPosition;

    private void Start()
    {
        startPosition = transform.position;
    }

    private void Update()
    {
        float t = Mathf.PingPong(Time.time * speed, 1f);

        if (smoothEasing)
        {
            t = t * t * (3f - 2f * t); // smoothstep
        }

        float offset = Mathf.Lerp(-distance / 2f, distance / 2f, t);

        Vector3 delta = Vector3.zero;
        switch (moveAxis)
        {
            case Axis.X: delta = new Vector3(offset, 0f, 0f); break;
            case Axis.Y: delta = new Vector3(0f, offset, 0f); break;
            case Axis.Z: delta = new Vector3(0f, 0f, offset); break;
        }

        transform.position = startPosition + delta;
    }
}