using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using TMPro;

namespace HandTracking.Core
{
    /// <summary>
    /// Manages the browser camera-permission UI flow.
    ///
    /// Panel states:
    ///   Requesting  → shows spinner + "We need your camera" text + Allow button
    ///   Denied      → shows error text + Retry button
    ///   Granted     → hides the panel, fires OnGranted (C# event + UnityEvent)
    ///
    /// Uses Unity's native Application.RequestUserAuthorization to trigger the browser prompt.
    /// </summary>
    public class CameraPermissionManager : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Header("References")]
        [Tooltip("Root panel that contains all permission UI. Will be shown/hidden.")]
        public GameObject permissionPanel;

        [Tooltip("The HandTrackingBridge component on the same GameObject.")]
        public HandTrackingBridge bridge;

        [Header("Requesting State UI")]
        [Tooltip("Container shown while waiting for the user to click Allow.")]
        public GameObject requestingContainer;

        [Tooltip("Button the user clicks to trigger the browser permission dialog.")]
        public Button allowButton;

        [Tooltip("Optional spinner / loading indicator shown while waiting.")]
        public GameObject spinnerObject;

        [Header("Denied State UI")]
        [Tooltip("Container shown when permission was denied.")]
        public GameObject deniedContainer;

        [Tooltip("Text element that shows the denial reason.")]
        public TMP_Text deniedReasonText;

        [Tooltip("Button to retry the permission request.")]
        public Button retryButton;

        [Header("Settings")]
        [Tooltip("Automatically call RequestPermission when this component starts.")]
        public bool autoRequestOnStart = true;

        [Tooltip("How many times to allow automatic retries before showing the denied UI.")]
        [Range(0, 5)]
        public int maxAutoRetries = 0;

        // ── Inspector-visible UnityEvents ─────────────────────────────────────────

        [Header("Events")]
        [Tooltip("Fired when the browser grants camera access. Hook up your next step here.")]
        public UnityEvent OnGrantedEvent;

        [Tooltip("Fired when the browser denies camera access. Passes the reason string.")]
        public UnityEvent<string> OnDeniedEvent;

        // ── C# events (for code subscribers) ─────────────────────────────────────

        /// <summary>Fired when the browser grants camera access.</summary>
        public event Action OnGranted;

        /// <summary>Fired when the browser denies camera access (passes the reason string).</summary>
        public event Action<string> OnDenied;

        // ── Private ───────────────────────────────────────────────────────────────

        private int  _retryCount;
        private bool _granted;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (bridge == null)
                bridge = GetComponent<HandTrackingBridge>();

            if (bridge == null)
                Debug.LogError("[CameraPermissionManager] No HandTrackingBridge found on this GameObject!");

            if (allowButton != null)
                allowButton.onClick.AddListener(RequestPermission);

            if (retryButton != null)
                retryButton.onClick.AddListener(RequestPermission);
        }

        private void Start()
        {
            ShowRequestingState();

            if (autoRequestOnStart)
                RequestPermission();
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>Trigger the browser camera permission dialog.</summary>
        public void RequestPermission()
        {
            if (_granted) return;
            StartCoroutine(RequestRoutine());
        }

        private System.Collections.IEnumerator RequestRoutine()
        {
            ShowRequestingState();

            // Trigger browser prompt natively in Unity
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);

            // Wait a bit for devices to populate (WebGL quirk)
            float timeout = 2f;
            while (WebCamTexture.devices.Length == 0 && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (WebCamTexture.devices.Length > 0)
            {
                _granted = true;
                HidePanel();
                Debug.Log("[CameraPermissionManager] Camera permission granted.");
                OnGranted?.Invoke();
                OnGrantedEvent?.Invoke();
            }
            else
            {
                _granted = false;
                string reason = "Камера не найдена или доступ запрещен.";
                Debug.LogWarning($"[CameraPermissionManager] Camera permission denied: {reason}");

                if (_retryCount < maxAutoRetries)
                {
                    _retryCount++;
                    RequestPermission();
                    yield break;
                }

                ShowDeniedState(reason);
                OnDenied?.Invoke(reason);
                OnDeniedEvent?.Invoke(reason);
            }
        }

        // ── JS → Unity callbacks (forwarded from HandTrackingBridge) ──────────────

        /// <summary>Called by HandTrackingBridge.OnPermissionGranted when JS reports success.</summary>
        public void OnPermissionGranted(string _)
        {
            // No longer used, handled natively in C#
        }

        /// <summary>Called by HandTrackingBridge.OnPermissionDenied when JS reports failure.</summary>
        public void OnPermissionDenied(string reason)
        {
            // No longer used, handled natively in C#
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private void ShowRequestingState()
        {
            SetPanelVisible(true);
            if (requestingContainer != null) requestingContainer.SetActive(true);
            if (deniedContainer     != null) deniedContainer.SetActive(false);
            if (spinnerObject       != null) spinnerObject.SetActive(true);
        }

        private void ShowDeniedState(string reason)
        {
            SetPanelVisible(true);
            if (requestingContainer != null) requestingContainer.SetActive(false);
            if (deniedContainer     != null) deniedContainer.SetActive(true);
            if (spinnerObject       != null) spinnerObject.SetActive(false);

            if (deniedReasonText != null)
            {
                deniedReasonText.text = string.IsNullOrEmpty(reason)
                    ? "Camera access was denied. Please allow camera access in your browser settings and try again."
                    : $"Error: {reason}\n\nPlease allow camera access in your browser settings and try again.";
            }
        }

        private void HidePanel()   => SetPanelVisible(false);

        private void SetPanelVisible(bool visible)
        {
            if (permissionPanel != null)
                permissionPanel.SetActive(visible);
        }
    }
}
