using UnityEngine;
using UnityEngine.InputSystem;
using static ArucoUnity.Plugin.Aruco;

public class FPSDisplay : MonoBehaviour
{
    [Header("Display Settings")]
    [SerializeField] private Key toggleKey = Key.Tab;
    [SerializeField] private int fontSize = 24;
    [SerializeField] private Color textColor = Color.white;
    [SerializeField] private Vector2 margin = new Vector2(20f, 20f);

    [Header("Quit")]
    [SerializeField] private Key quitKey = Key.Escape;

    [Header("VSync")]
    [SerializeField] private Key vsyncToggleKey = Key.V;

    [Header("Smoothing")]
    [SerializeField, Range(0.01f, 1f)] private float updateInterval = 0.25f;

    private float accumFps;
    private int accumFrames;
    private float timeLeft;
    private float displayFps;

    private GUIStyle style;

    private void Update()
    {
        if (Keyboard.current[quitKey].wasPressedThisFrame)
        {
            QuitGame();
        }

        if (Keyboard.current[vsyncToggleKey].wasPressedThisFrame)
        {
            QualitySettings.vSyncCount = QualitySettings.vSyncCount == 0 ? 1 : 0;
        }

        timeLeft -= Time.unscaledDeltaTime;
        accumFps += 1f / Time.unscaledDeltaTime;
        accumFrames++;

        if (timeLeft <= 0f)
        {
            displayFps = accumFps / accumFrames;
            timeLeft = updateInterval;
            accumFps = 0f;
            accumFrames = 0;
        }
    }

    private void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void OnGUI()
    {
        if (Keyboard.current == null || !Keyboard.current[toggleKey].wasPressedThisFrame) return;

        style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            alignment = TextAnchor.UpperLeft,
            normal = { textColor = textColor }
        };

        float width = 150f;
        Rect rect = new Rect(0 - width - margin.x, margin.y, width, (fontSize + 10f) * 2f);
        string vsyncLabel = QualitySettings.vSyncCount == 0 ? "VSync Off" : "VSync On";
        GUI.Label(rect, $"{displayFps:F1} FPS\n{vsyncLabel}", style);
    }
}