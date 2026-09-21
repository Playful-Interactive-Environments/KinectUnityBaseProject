using UnityEngine;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Single source of truth for "which marker ID represents which object type." Both
/// ArucoDetector (to set Marker.Assign) and MarkerWorldOverlay (to pick the right prefab)
/// read from this same asset, so the two can never disagree about which IDs are bound.
///
/// Create via Assets > Create > Sandbox > Marker Object Registry, fill in the list in the
/// Inspector, and drag the asset into both ArucoDetector and MarkerWorldOverlay.
/// </summary>
[CreateAssetMenu(menuName = "Sandbox/Marker Object Registry", fileName = "MarkerObjectRegistry")]
public class MarkerObjectRegistry : ScriptableObject
{
    [System.Serializable]
    public struct MarkerBinding
    {
        public byte MarkerId;
        public GameObject Prefab;

        [Min(0.000001f), Tooltip("Multiplies the marker's measured real-world size before applying it as scale. 1 = sized to match the marker exactly. Can't go below 1.")]
        public float ScaleMultiplier;

        [Tooltip("If true, scales X/Y independently to exactly match the marker's measured footprint � correct for a flat overlay quad, but will squish/stretch a real 3D model. If false (default), applies one uniform scale factor so 3D prefabs keep their natural proportions.")]
        public bool MatchFootprintExactly;
    }

    [SerializeField] private List<MarkerBinding> bindings = new List<MarkerBinding>();

    private Dictionary<byte, GameObject> lookup;
    private Dictionary<byte, MarkerBinding> bindingLookup;

    private void EnsureLookup()
    {
        if (lookup != null) return;

        lookup = new Dictionary<byte, GameObject>(bindings.Count);
        bindingLookup = new Dictionary<byte, MarkerBinding>(bindings.Count);
        foreach (var b in bindings)
        {
            if (b.Prefab == null) continue;

            if (lookup.ContainsKey(b.MarkerId))
            {
                Debug.LogWarning($"MarkerObjectRegistry: duplicate binding for marker ID {b.MarkerId} � keeping the first one.");
                continue;
            }
            lookup.Add(b.MarkerId, b.Prefab);
            bindingLookup.Add(b.MarkerId, b);
        }
    }

    /// <summary>True if this marker ID has a registered object type bound to it.</summary>
    public bool IsAssigned(byte markerId)
    {
        EnsureLookup();
        return lookup.ContainsKey(markerId);
    }

    /// <summary>Gets the prefab bound to this marker ID, if any.</summary>
    public bool TryGetPrefab(byte markerId, out GameObject prefab)
    {
        EnsureLookup();
        return lookup.TryGetValue(markerId, out prefab);
    }

    /// <summary>Gets the full binding (prefab + scale settings) for this marker ID, if any.</summary>
    public bool TryGetBinding(byte markerId, out MarkerBinding binding)
    {
        EnsureLookup();
        return bindingLookup.TryGetValue(markerId, out binding);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // Force a rebuild next time it's queried, since bindings may have changed in the Inspector.
        lookup = null;
        bindingLookup = null;
    }
#endif
}

#if UNITY_EDITOR
[CustomEditor(typeof(MarkerObjectRegistry))]
public class MarkerObjectRegistryEditor : Editor
{
    private UnityEditorInternal.ReorderableList list;

    private void OnEnable()
    {
        var bindingsProp = serializedObject.FindProperty("bindings");

        list = new UnityEditorInternal.ReorderableList(serializedObject, bindingsProp, true, true, true, true)
        {
            drawHeaderCallback = rect => EditorGUI.LabelField(rect, "Marker Bindings"),

            elementHeightCallback = index => EditorGUIUtility.singleLineHeight * 2 + 8,

            drawElementCallback = (rect, index, active, focused) =>
            {
                var element = bindingsProp.GetArrayElementAtIndex(index);
                var idProp = element.FindPropertyRelative("MarkerId");
                var prefabProp = element.FindPropertyRelative("Prefab");
                var scaleProp = element.FindPropertyRelative("ScaleMultiplier");
                var footprintProp = element.FindPropertyRelative("MatchFootprintExactly");

                float rowHeight = EditorGUIUtility.singleLineHeight;
                var idRect = new Rect(rect.x, rect.y + 2, 50f, rowHeight);
                var prefabRect = new Rect(idRect.xMax + 6, rect.y + 2, rect.width - idRect.width - 6, rowHeight);

                int newId = EditorGUI.IntField(idRect, idProp.intValue);
                idProp.intValue = Mathf.Clamp(newId, 0, 255);
                EditorGUI.PropertyField(prefabRect, prefabProp, GUIContent.none);

                float secondRowY = rect.y + rowHeight + 4;
                var scaleRect = new Rect(rect.x, secondRowY, rect.width * 0.5f - 4, rowHeight);
                var footprintRect = new Rect(scaleRect.xMax + 8, secondRowY, rect.width * 0.5f - 4, rowHeight);

                EditorGUI.PropertyField(scaleRect, scaleProp, new GUIContent("Scale �"));
                EditorGUI.PropertyField(footprintRect, footprintProp, new GUIContent("Exact footprint"));
            },

            // Auto-increment: fills in the next unused marker ID instead of defaulting to 0
            // every time, so you don't have to manually renumber each new row.
            onAddCallback = l =>
            {
                int index = l.serializedProperty.arraySize;
                l.serializedProperty.arraySize++;
                l.index = index;

                var element = l.serializedProperty.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("MarkerId").intValue = ComputeNextMarkerId(l.serializedProperty, index);
                element.FindPropertyRelative("Prefab").objectReferenceValue = null;
                element.FindPropertyRelative("ScaleMultiplier").floatValue = 1f;
                element.FindPropertyRelative("MatchFootprintExactly").boolValue = false;
            }
        };
    }

    /// <summary>
    /// Next ID after the current highest, or the first unused gap (0-255) if IDs were deleted
    /// out of order or 255 is already taken. Returns 0 if the registry is completely full.
    /// </summary>
    private static int ComputeNextMarkerId(SerializedProperty arrayProp, int excludeIndex)
    {
        var used = new HashSet<int>();
        for (int i = 0; i < arrayProp.arraySize; i++)
        {
            if (i == excludeIndex) continue;
            used.Add(arrayProp.GetArrayElementAtIndex(i).FindPropertyRelative("MarkerId").intValue);
        }

        if (used.Count == 0) return 0;

        int max = 0;
        foreach (var id in used) if (id > max) max = id;
        if (max < 255) return max + 1;

        for (int candidate = 0; candidate <= 255; candidate++)
            if (!used.Contains(candidate)) return candidate;

        return 0; // registry full � all 256 possible IDs are already bound
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        list.DoLayoutList();
        serializedObject.ApplyModifiedProperties();
    }
}
#endif