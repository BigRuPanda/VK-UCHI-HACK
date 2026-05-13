using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using TMPro;

namespace ReadingModule
{
    /// <summary>
    /// Manages the microphone permission flow before the reading module starts.
    ///
    /// Scene setup:
    ///   1. Create a UI Panel (PermissionPanel) with:
    ///      - A "Allow Microphone" Button  → assign to allowButton
    ///      - Optional: a TextMeshProUGUI label for instructions
    ///   2. Create a second UI Panel (DeniedPanel) shown when permission is refused:
    ///      - Explain to the parent/child how to re-enable mic in browser settings
    ///   3. Attach this component to any GameObject.
    ///   4. Assign references in the Inspector.
    ///   5. Wire ReadingController to onPermissionGranted so reading starts automatically.
    ///
    /// Flow:
    ///   Scene loads → PermissionPanel shown → child/parent clicks Allow →
    ///   browser mic prompt → granted → PermissionPanel hidden → ReadingController.StartReading()
    ///                                → denied → DeniedPanel shown
    /// </summary>
    public class MicrophonePermissionHandler : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────────

        [Header("UI Panels")]
        [Tooltip("Panel shown at startup asking the user to allow the microphone.")]
        [SerializeField] private GameObject permissionPanel;

        [Tooltip("Panel shown when the user denies microphone access.")]
        [SerializeField] private GameObject deniedPanel;

        [Header("Buttons")]
        [Tooltip("Button the user clicks to trigger the browser mic permission prompt.")]
        [SerializeField] private Button allowButton;

        [Tooltip("Optional retry button on the denied panel — calls RequestPermission again.")]
        [SerializeField] private Button retryButton;

        [Header("Optional Labels")]
        [Tooltip("Label on the permission panel. Leave empty to skip.")]
        [SerializeField] private TextMeshProUGUI permissionMessageLabel;

        [Tooltip("Label on the denied panel. Leave empty to skip.")]
        [SerializeField] private TextMeshProUGUI deniedMessageLabel;

        [Header("Messages")]
        [SerializeField] private string permissionMessage =
            "Для игры нужен микрофон.\nНажми кнопку и разреши доступ!";

        [SerializeField] private string deniedMessage =
            "Доступ к микрофону запрещён.\nОткрой настройки браузера и разреши микрофон, затем обнови страницу.";

        [Header("Events")]
        [Tooltip("Fires when permission is granted. Wire ReadingController.StartReading() here.")]
        public UnityEvent onPermissionGranted;

        [Tooltip("Fires when permission is denied. Wire any fallback UI here.")]
        public UnityEvent<string> onPermissionDenied;

        // ── Unity Lifecycle ───────────────────────────────────────────────────────

        private void Start()
        {
            // Set label texts
            if (permissionMessageLabel != null)
                permissionMessageLabel.text = permissionMessage;

            if (deniedMessageLabel != null)
                deniedMessageLabel.text = deniedMessage;

            // Wire buttons
            if (allowButton != null)
                allowButton.onClick.AddListener(OnAllowClicked);

            if (retryButton != null)
                retryButton.onClick.AddListener(OnAllowClicked);

            // Subscribe to SpeechBridge events
            if (SpeechBridge.Instance != null)
            {
                SpeechBridge.Instance.OnPermissionGranted += HandleGranted;
                SpeechBridge.Instance.OnPermissionDenied  += HandleDenied;
            }
            else
            {
                Debug.LogError("[MicrophonePermissionHandler] SpeechBridge.Instance is null. " +
                               "Make sure a GameObject named 'SpeechBridge' with the SpeechBridge " +
                               "component exists in the scene and is initialized before this component.", this);
            }

            // Show permission panel, hide denied panel
            ShowPermissionPanel();
        }

        private void OnDestroy()
        {
            if (SpeechBridge.Instance != null)
            {
                SpeechBridge.Instance.OnPermissionGranted -= HandleGranted;
                SpeechBridge.Instance.OnPermissionDenied  -= HandleDenied;
            }

            if (allowButton != null)
                allowButton.onClick.RemoveListener(OnAllowClicked);

            if (retryButton != null)
                retryButton.onClick.RemoveListener(OnAllowClicked);
        }

        // ── Button Handlers ───────────────────────────────────────────────────────

        private void OnAllowClicked()
        {
            // Disable button to prevent double-clicks while browser prompt is open
            if (allowButton != null) allowButton.interactable = false;
            if (retryButton != null) retryButton.interactable = false;

            if (SpeechBridge.Instance != null)
                SpeechBridge.Instance.RequestPermission();
            else
                Debug.LogError("[MicrophonePermissionHandler] SpeechBridge.Instance is null.", this);
        }

        // ── Permission Result Handlers ────────────────────────────────────────────

        private void HandleGranted()
        {
            Debug.Log("[MicrophonePermissionHandler] Permission granted — starting reading.");

            HideAllPanels();
            onPermissionGranted?.Invoke();
        }

        private void HandleDenied(string reason)
        {
            Debug.LogWarning($"[MicrophonePermissionHandler] Permission denied: {reason}");

            // Re-enable buttons so user can retry
            if (allowButton != null) allowButton.interactable = true;
            if (retryButton != null) retryButton.interactable = true;

            ShowDeniedPanel();
            onPermissionDenied?.Invoke(reason);
        }

        // ── UI Helpers ────────────────────────────────────────────────────────────

        private void ShowPermissionPanel()
        {
            if (permissionPanel != null) permissionPanel.SetActive(true);
            if (deniedPanel     != null) deniedPanel.SetActive(false);
        }

        private void ShowDeniedPanel()
        {
            if (permissionPanel != null) permissionPanel.SetActive(false);
            if (deniedPanel     != null) deniedPanel.SetActive(true);
        }

        private void HideAllPanels()
        {
            if (permissionPanel != null) permissionPanel.SetActive(false);
            if (deniedPanel     != null) deniedPanel.SetActive(false);
        }
    }
}
