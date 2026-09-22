using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;

public class FPSDisplay : MonoBehaviour
{
    [Header("UI Reference")]
    [SerializeField] private TextMeshProUGUI fpsText;

    [Header("Controls")]
    [SerializeField] private Key toggleKey = Key.Tab;
    [SerializeField] private Key quitKey = Key.Escape;
    [SerializeField] private Key vsyncToggleKey = Key.V;

    [Header("Smoothing")]
    [SerializeField, Range(0.01f, 1f)] private float updateInterval = 0.25f;

    private float accumFps;
    private int accumFrames;
    private float timeLeft;
    private float displayFps;
    private bool visible = false;

    private void Start()
    {
        if (fpsText != null)
        {
            fpsText.gameObject.SetActive(visible);
        }
    }

    private void Update()
    {
        if (Keyboard.current != null)
        {
            if (Keyboard.current[toggleKey].wasPressedThisFrame)
            {
                visible = !visible;
                if (fpsText != null)
                {
                    fpsText.gameObject.SetActive(visible);
                }
            }

            if (Keyboard.current[quitKey].wasPressedThisFrame)
            {
                QuitGame();
            }

            if (Keyboard.current[vsyncToggleKey].wasPressedThisFrame)
            {
                QualitySettings.vSyncCount = QualitySettings.vSyncCount == 0 ? 1 : 0;
                UpdateUIText(); // Refresh immediately on keypress
            }
        }

        // FPS Calculation
        timeLeft -= Time.unscaledDeltaTime;
        accumFps += 1f / Time.unscaledDeltaTime;
        accumFrames++;

        if (timeLeft <= 0f)
        {
            displayFps = accumFps / accumFrames;
            timeLeft = updateInterval;
            accumFps = 0f;
            accumFrames = 0;

            if (visible)
            {
                UpdateUIText();
            }
        }
    }

    private void UpdateUIText()
    {
        if (fpsText == null) return;

        string vsyncLabel = QualitySettings.vSyncCount == 0 ? "VSync Off" : "VSync On";
        string modeLabel = Settings.instance.Orientation.ToString();
        fpsText.text = $"{displayFps:F1} FPS\n{vsyncLabel}\nMode: {modeLabel}";
    }

    private void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}