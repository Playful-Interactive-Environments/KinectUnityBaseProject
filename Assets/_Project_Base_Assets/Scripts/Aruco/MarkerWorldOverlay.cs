using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Syncs a GameObject per detected ArUco marker (ArucoDetector.Markers) onto the game world,
/// matching real-world position, rotation, and scale every frame.
///
/// Subscribes to ArucoDetector.OnMarkersUpdated rather than SourceManager.OnFrameUpdated
/// directly, so it always reads a fully-rebuilt Markers list regardless of Unity's script
/// execution order.
/// </summary>
public class MarkerWorldOverlay : MonoBehaviour
{
    [Header("Marker Assignment")]
    [Tooltip("Same registry asset assigned on ArucoDetector. Markers not registered here (Assign == false) are ignored entirely � no overlay object is spawned for them.")]
    [SerializeField] private MarkerObjectRegistry objectRegistry;

    [Tooltip("Fallback prefab for assigned markers whose ID somehow isn't in the registry lookup (shouldn't normally happen if the same registry is wired everywhere). Leave null to just skip them.")]
    [SerializeField] private GameObject fallbackPrefab;

    [Header("Marker miss tolerance")]
    [Tooltip("How many consecutive frames a marker can be missing before its overlay is hidden/destroyed. Prevents flicker from single dropped detections.")]
    [SerializeField] private int missFrameTolerance = 5;

    [Tooltip("If true, hides the overlay object when a marker goes stale instead of destroying it. Re-enables instantly if the marker reappears.")]
    [SerializeField] private bool hideInsteadOfDestroy = true;

    private class TrackedMarker
    {
        public GameObject Instance;
        public int MissedFrames;
        public MarkerObjectRegistry.MarkerBinding Binding;
    }

    private readonly Dictionary<int, TrackedMarker> tracked = new Dictionary<int, TrackedMarker>();
    private readonly HashSet<int> seenThisFrame = new HashSet<int>();

    private void OnEnable()
    {
        ArucoDetector.OnMarkersUpdated += SyncOverlays;
    }

    private void OnDisable()
    {
        ArucoDetector.OnMarkersUpdated -= SyncOverlays;
    }

    private void SyncOverlays()
    {
        seenThisFrame.Clear();

        // Marker is a struct - iterating ArucoDetector.Markers copies each value, which is fine
        // here since we only read from it.
        foreach (Marker marker in ArucoDetector.Markers)
        {
            // Unassigned markers (no registered object type for this ID) are ignored entirely �
            // they might be calibration markers, hand markers, or IDs not yet bound to anything.
            if (!marker.Assign) continue;

            seenThisFrame.Add(marker.Id);

            if (!tracked.TryGetValue(marker.Id, out var t))
            {
                if (objectRegistry == null || !objectRegistry.TryGetBinding(marker.Id, out var binding))
                {
                    // Assign was true but this ID isn't actually in the registry (registries out
                    // of sync between ArucoDetector and this component, most likely). Fall back
                    // to a default binding around fallbackPrefab if one's set, else skip.
                    if (fallbackPrefab == null) continue;
                    binding = new MarkerObjectRegistry.MarkerBinding
                    {
                        MarkerId = marker.Id,
                        Prefab = fallbackPrefab,
                        ScaleMultiplier = 1f,
                        MatchFootprintExactly = false
                    };
                }

                GameObject instance = CreateInstance(binding);
                if (instance == null) continue;

                t = new TrackedMarker { Instance = instance, Binding = binding };
                tracked[marker.Id] = t;
            }

            t.MissedFrames = 0;
            if (!t.Instance.activeSelf) t.Instance.SetActive(true);

            ApplyPose(t.Instance.transform, marker, t.Binding);
        }

        // Age out anything not seen this frame
        List<int> toRemove = null;
        foreach (var kvp in tracked)
        {
            if (seenThisFrame.Contains(kvp.Key)) continue;

            kvp.Value.MissedFrames++;
            if (kvp.Value.MissedFrames < missFrameTolerance) continue;

            if (hideInsteadOfDestroy)
            {
                if (kvp.Value.Instance.activeSelf) kvp.Value.Instance.SetActive(false);
            }
            else
            {
                Destroy(kvp.Value.Instance);
                (toRemove ??= new List<int>()).Add(kvp.Key);
            }
        }
        if (toRemove != null)
            foreach (var id in toRemove) tracked.Remove(id);
    }

    private GameObject CreateInstance(MarkerObjectRegistry.MarkerBinding binding)
    {
        if (binding.Prefab == null) return null;

        GameObject go = Instantiate(binding.Prefab);
        go.name = $"ArucoMarker_{binding.MarkerId}";
        go.transform.SetParent(transform, worldPositionStays: true);
        return go;
    }

    /// <summary>
    /// Sets position, rotation, and scale to match the physical marker. Rotation comes straight
    /// from ArucoDetector (computed once, at detection time, from the same corner data).
    ///
    /// Scale has two modes, per binding:
    /// - MatchFootprintExactly: sets non-uniform X/Y to the marker's exact measured edge lengths
    ///   and Z=1. Correct for a flat debug quad matching the marker's outline; will squish/stretch
    ///   any prefab that actually has depth, since Z never reflects the marker's real size.
    /// - Default (uniform): derives one scale factor from the marker's measured size and applies
    ///   it to all three axes, so a 3D prefab keeps its natural proportions and just grows/shrinks
    ///   as a whole with the marker.
    /// </summary>
    private void ApplyPose(Transform t, Marker marker, MarkerObjectRegistry.MarkerBinding binding)
    {
        Vector3[] p = marker.Points; // [0]=TL, [1]=TR, [2]=BR, [3]=BL

        float rightLen = Vector3.Distance(p[0], p[1]);
        float downLen = Vector3.Distance(p[0], p[3]);
        if (rightLen < 1e-5f || downLen < 1e-5f) return; // degenerate detection this frame, skip

        t.position = marker.Center;
        t.rotation = marker.Rotation;

        float multiplier = Mathf.Max(binding.ScaleMultiplier, 1f);

        if (binding.MatchFootprintExactly)
        {
            t.localScale = new Vector3(rightLen * multiplier, downLen * multiplier, multiplier);
        }
        else
        {
            float uniform = (rightLen + downLen) * 0.5f * multiplier;
            t.localScale = Vector3.one * uniform;
        }
    }
}