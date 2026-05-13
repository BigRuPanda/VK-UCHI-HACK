using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// DrawScanningCameraPermissionUI — in-game camera permission panel.
///
/// Shows a child-friendly panel asking for camera access before the drawing
/// recognition starts. The "Allow" button triggers the browser's native
/// permission dialog via <see cref="DrawScanningChecker.RequestCameraPermission"/>.
///
/// States:
///   • Idle      — panel hidden (before StartScanning is called)
///   • Asking    — panel visible, "Allow Camera" button shown
///   • Granted   — panel hides automatically (JS_OnCameraGranted fires)
///   • Denied    — error state shown with retry button
///
/// Setup:
///   1. Create a Panel GameObject as a child of your Canvas.
///   2. Add this component to the Panel.
///   3. Assign the Panel to <see cref="DrawScanningChecker.cameraPermissionPanel"/>.
///   4. Wire <see cref="DrawScanningChecker.OnCameraError"/> → <see cref="ShowDeniedState"/>.
///   5. Wire the "Allow" button's OnClick → <see cref="DrawScanningChecker.RequestCameraPermission"/>.
///      (Or leave allowButton unassigned and wire it manually in the Inspector.)
/// </summary>
public class DrawScanningCameraPermissionUI : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────
    // Inspector fields
    // ─────────────────────────────────────────────────────────────────────

    [Header("References")]
    [Tooltip("The DrawScanningChecker in the scene. " +
             "If null, the component searches for one automatically.")]
    public DrawScanningChecker checker;

    [Tooltip("Button that triggers the browser camera permission dialog. " +
             "Its OnClick is wired automatically to checker.RequestCameraPermission().")]
    public Button allowButton;

    [Tooltip("Button shown in the denied state to let the player retry. " +
             "Its OnClick is wired automatically to checker.RequestCameraPermission().")]
    public Button retryButton;

    [Tooltip("Text shown in the 'asking' state. " +
             "Default: 'Нам нужна камера, чтобы проверить твой рисунок 📷'")]
    public TextMeshProUGUI askingLabel;

    [Tooltip("Text shown in the 'denied' state. " +
             "Default: 'Камера недоступна. Разреши доступ в настройках браузера.'")]
    public TextMeshProUGUI deniedLabel;

    [Tooltip("Icon shown in the 'asking' state (e.g. camera emoji sprite). Optional.")]
    public Image askingIcon;

    [Tooltip("Icon shown in the 'denied' state (e.g. warning sprite). Optional.")]
    public Image deniedIcon;

    [Header("Text Content")]
    [Tooltip("Message shown when asking for permission.")]
    [TextArea(2, 4)]
    public string askingText = "Нам нужна камера,\nчтобы проверить твой рисунок 📷";

    [Tooltip("Label on the Allow button.")]
    public string allowButtonText = "Разрешить";

    [Tooltip("Message shown when permission was denied.")]
    [TextArea(2, 4)]
    public string deniedText = "Камера недоступна.\nРазреши доступ в настройках браузера и попробуй снова.";

    [Tooltip("Label on the Retry button.")]
    public string retryButtonText = "Попробовать снова";

    [Header("Animation")]
    [Tooltip("Duration of the panel fade-in animation (seconds). 0 = instant.")]
    public float fadeInDuration = 0.25f;

    [Tooltip("Duration of the panel fade-out animation (seconds). 0 = instant.")]
    public float fadeOutDuration = 0.2f;

    // ─────────────────────────────────────────────────────────────────────
    // Private state
    // ─────────────────────────────────────────────────────────────────────

    private CanvasGroup _canvasGroup;
    private bool _isDenied;

    // ─────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ─────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        // Get or add CanvasGroup for fade animation
        _canvasGroup = GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
            _canvasGroup = gameObject.AddComponent<CanvasGroup>();

        // Auto-find checker if not assigned
        if (checker == null)
            checker = FindObjectOfType<DrawScanningChecker>();

        // Wire buttons
        if (allowButton != null)
        {
            allowButton.onClick.RemoveAllListeners();
            allowButton.onClick.AddListener(OnAllowClicked);
        }

        if (retryButton != null)
        {
            retryButton.onClick.RemoveAllListeners();
            retryButton.onClick.AddListener(OnAllowClicked);
        }

        // Apply text content
        ApplyTextContent();

        // Start in asking state (panel is shown by DrawScanningChecker.StartScanning)
        ShowAskingState();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Switch to the "denied" state — shows the error message and retry button.
    /// Wire to <see cref="DrawScanningChecker.OnCameraError"/>.
    /// </summary>
    public void ShowDeniedState()
    {
        _isDenied = true;

        if (askingLabel != null) askingLabel.gameObject.SetActive(false);
        if (askingIcon  != null) askingIcon.gameObject.SetActive(false);
        if (allowButton != null) allowButton.gameObject.SetActive(false);

        if (deniedLabel != null) deniedLabel.gameObject.SetActive(true);
        if (deniedIcon  != null) deniedIcon.gameObject.SetActive(true);
        if (retryButton != null) retryButton.gameObject.SetActive(true);
    }

    /// <summary>
    /// Switch back to the "asking" state (e.g. after the player clicks Retry).
    /// </summary>
    public void ShowAskingState()
    {
        _isDenied = false;

        if (askingLabel != null) askingLabel.gameObject.SetActive(true);
        if (askingIcon  != null) askingIcon.gameObject.SetActive(true);
        if (allowButton != null) allowButton.gameObject.SetActive(true);

        if (deniedLabel != null) deniedLabel.gameObject.SetActive(false);
        if (deniedIcon  != null) deniedIcon.gameObject.SetActive(false);
        if (retryButton != null) retryButton.gameObject.SetActive(false);
    }

    /// <summary>
    /// Fade the panel in. Called automatically when the panel is activated by
    /// <see cref="DrawScanningChecker.StartScanning"/>.
    /// </summary>
    private void OnEnable()
    {
        if (_isDenied)
            ShowDeniedState();
        else
            ShowAskingState();

        if (fadeInDuration > 0f)
        {
            _canvasGroup.alpha = 0f;
            StartCoroutine(FadeCanvasGroup(_canvasGroup, 0f, 1f, fadeInDuration));
        }
        else
        {
            _canvasGroup.alpha = 1f;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Button handlers
    // ─────────────────────────────────────────────────────────────────────

    private void OnAllowClicked()
    {
        if (checker == null)
        {
            Debug.LogWarning("[DrawScanningCameraPermissionUI] checker is null — cannot request permission.");
            return;
        }

        // Reset to asking state in case we're retrying after a denial
        ShowAskingState();

        // This triggers getUserMedia — MUST be called from a user gesture (button click)
        checker.RequestCameraPermission();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private void ApplyTextContent()
    {
        if (askingLabel != null)
            askingLabel.text = askingText;

        if (deniedLabel != null)
            deniedLabel.text = deniedText;

        if (allowButton != null)
        {
            var lbl = allowButton.GetComponentInChildren<TextMeshProUGUI>();
            if (lbl != null) lbl.text = allowButtonText;
        }

        if (retryButton != null)
        {
            var lbl = retryButton.GetComponentInChildren<TextMeshProUGUI>();
            if (lbl != null) lbl.text = retryButtonText;
        }
    }

    private static IEnumerator FadeCanvasGroup(CanvasGroup cg, float from, float to, float duration)
    {
        float elapsed = 0f;
        cg.alpha = from;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            cg.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
            yield return null;
        }
        cg.alpha = to;
    }
}
