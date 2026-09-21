using UnityEngine;

/// <summary>
/// Continuously spawns bouncy balls every N seconds to test collisions against colliders
/// (e.g. HandDetector's segment mesh colliders). Each ball auto-despawns after its own
/// lifetime, independent of the spawn interval. Spawn point and direction follow this
/// GameObject's transform — move/rotate it in the scene to aim.
/// </summary>
public class BouncyBallSpawner : MonoBehaviour
{
    [Header("Spawn")]
    [SerializeField] private float sphereRadius = 0.1f;
    [SerializeField, Tooltip("Seconds between automatic spawns. Spawning starts immediately and repeats on this interval.")]
    private float spawnInterval = 1f;
    [SerializeField, Tooltip("Optional manual spawn key, in addition to the automatic interval. None to disable.")]
    private KeyCode manualSpawnKey = KeyCode.Space;

    [Header("Physics")]
    [SerializeField] private float mass = 0.2f;
    [SerializeField, Range(0f, 1f), Tooltip("0 = no bounce, 1 = fully elastic (bounciness of the PhysicMaterial).")]
    private float bounciness = 0.9f;
    [SerializeField] private PhysicMaterialCombine bounceCombine = PhysicMaterialCombine.Maximum;

    [Header("Launch Force")]
    [SerializeField, Tooltip("Direction is in local space, relative to this transform's rotation. Forward (blue axis) by default.")]
    private Vector3 localForceDirection = Vector3.forward;
    [SerializeField] private float forceMagnitude = 5f;
    [SerializeField] private ForceMode forceMode = ForceMode.Impulse;

    [Header("Lifetime")]
    [SerializeField, Tooltip("Seconds each spawned ball lives before being destroyed. 0 = never despawn.")]
    private float ballLifetime = 5f;

    [Header("Gizmos")]
    [SerializeField] private Color gizmoSphereColor = new Color(1f, 0.6f, 0f, 0.6f);
    [SerializeField] private Color gizmoArrowColor = Color.yellow;
    [SerializeField, Tooltip("Visual length of the direction arrow in the scene view. Purely cosmetic — does not affect force magnitude.")]
    private float gizmoArrowLength = 0.5f;

    private PhysicMaterial bouncyMaterial;
    private float spawnTimer;

    private void Awake()
    {
        bouncyMaterial = new PhysicMaterial("BouncyBallMaterial")
        {
            bounciness = bounciness,
            frictionCombine = PhysicMaterialCombine.Minimum,
            bounceCombine = bounceCombine,
            dynamicFriction = 0.2f,
            staticFriction = 0.2f
        };
    }

    private void Start()
    {
        SpawnBall();
    }

    private void Update()
    {
        spawnTimer += Time.deltaTime;
        if (spawnInterval > 0f && spawnTimer >= spawnInterval)
        {
            spawnTimer -= spawnInterval;
            SpawnBall();
        }

        if (manualSpawnKey != KeyCode.None && Input.GetKeyDown(manualSpawnKey))
        {
            SpawnBall();
        }
    }

    /// <summary>
    /// Creates a sphere with a Rigidbody + bouncy PhysicMaterial at this transform's position,
    /// applies (transform.rotation * localForceDirection).normalized * forceMagnitude via the
    /// configured ForceMode, and schedules its own destruction after ballLifetime seconds
    /// (independent of the spawn interval).
    /// </summary>
    public GameObject SpawnBall()
    {
        GameObject ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ball.transform.parent = transform;

        ball.name = "BouncyBall";
        ball.transform.position = transform.position;
        ball.transform.localScale = Vector3.one * (sphereRadius * 2f); // primitive sphere has radius 0.5 at scale 1

        SphereCollider col = ball.GetComponent<SphereCollider>();
        col.material = bouncyMaterial;

        Rigidbody rb = ball.AddComponent<Rigidbody>();
        rb.mass = mass;

        rb.AddForce(GetWorldForceDirection() * forceMagnitude, forceMode);

        if (ballLifetime > 0f)
            Destroy(ball, ballLifetime);

        return ball;
    }

    private Vector3 GetWorldForceDirection()
    {
        Vector3 dir = localForceDirection.sqrMagnitude > 0f ? localForceDirection.normalized : Vector3.forward;
        return transform.rotation * dir;
    }

    private void OnDrawGizmos()
    {
        Vector3 origin = transform.position;

        // Sphere at the spawn point, matching the actual ball size
        Gizmos.color = gizmoSphereColor;
        Gizmos.DrawSphere(origin, sphereRadius);
        Gizmos.color = new Color(gizmoSphereColor.r, gizmoSphereColor.g, gizmoSphereColor.b, 1f);
        Gizmos.DrawWireSphere(origin, sphereRadius);

        // Arrow showing launch direction (not scaled by forceMagnitude — purely directional)
        Vector3 dir = GetWorldForceDirection();
        Vector3 tip = origin + dir * gizmoArrowLength;

        Gizmos.color = gizmoArrowColor;
        Gizmos.DrawLine(origin, tip);
        DrawArrowHead(origin, tip, gizmoArrowLength * 0.2f);
    }

    private void DrawArrowHead(Vector3 from, Vector3 tip, float headSize)
    {
        Vector3 dir = (tip - from).normalized;
        if (dir.sqrMagnitude < 0.0001f) return;

        Vector3 up = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up;
        Vector3 right = Vector3.Cross(dir, up).normalized;
        Vector3 back = -dir;

        Vector3 headBase = tip + back * headSize;
        Vector3 p1 = headBase + right * headSize * 0.5f;
        Vector3 p2 = headBase - right * headSize * 0.5f;

        Gizmos.DrawLine(tip, p1);
        Gizmos.DrawLine(tip, p2);
        Gizmos.DrawLine(p1, p2);
    }
}