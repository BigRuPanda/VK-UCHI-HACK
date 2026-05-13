using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using HandTracking.Core;

namespace HandTracking.Visualization
{
    /// <summary>
    /// Feeds a live webcam stream into a RawImage.
    ///
    /// Setup:
    ///   1. Add this component to the HandTrackingBridge root GameObject.
    ///   2. Create a RawImage anywhere in your Canvas (size and position it however you like).
    ///   3. Assign that RawImage to the <see cref="previewImage"/> field.
    ///   4. Assign the HandTrackingBridge reference (auto-found if on the same GameObject).
    ///
    /// The component starts the webcam automatically when tracking starts and
    /// stops it when tracking stops.
    /// You can also call <see cref="StartPreview"/> / <see cref="StopPreview"/> manually.
    /// </summary>
    public class WebcamPreview : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Header("References")]
        [Tooltip("RawImage that will display the webcam feed. Position and size it in the Canvas however you like.")]
        public RawImage previewImage;

        [Tooltip("HandTrackingBridge that controls tracking state. Auto-found on the same GameObject if left empty.")]
        public HandTrackingBridge bridge;

        [Header("Settings")]
        [Tooltip("Mirror the image horizontally (selfie mode — matches the hand positions on screen).")]
        public bool mirrorHorizontal = true;

        [Tooltip("Start the preview automatically when tracking starts.")]
        public bool autoStartWithTracking = true;

        // ── Private ───────────────────────────────────────────────────────────────

        private WebCamTexture _webcamTexture;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (bridge == null)
                bridge = GetComponent<HandTrackingBridge>();
        }

        private void Start()
        {
            if (bridge != null && autoStartWithTracking)
            {
                bridge.OnTrackingStartedEvent += HandleTrackingStarted;
                bridge.OnTrackingStoppedEvent += HandleTrackingStopped;
            }
        }

        private void OnDestroy()
        {
            if (bridge != null)
            {
                bridge.OnTrackingStartedEvent -= HandleTrackingStarted;
                bridge.OnTrackingStoppedEvent -= HandleTrackingStopped;
            }
            StopPreview();
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>Start the webcam and display it in the assigned RawImage.</summary>
        public void StartPreview()
        {
            if (_webcamTexture != null && _webcamTexture.isPlaying) return;
            StartCoroutine(StartWebcamCoroutine());
        }

        /// <summary>Stop the webcam and clear the RawImage texture.</summary>
        public void StopPreview()
        {
            if (_webcamTexture != null)
            {
                if (_webcamTexture.isPlaying)
                    _webcamTexture.Stop();
                Destroy(_webcamTexture);
                _webcamTexture = null;
            }

            if (previewImage != null)
                previewImage.texture = null;
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private IEnumerator StartWebcamCoroutine()
        {
            // Permission is already handled by CameraPermissionManager before this is called.
            if (WebCamTexture.devices.Length == 0)
            {
                Debug.LogWarning("[WebcamPreview] No webcam devices found.");
                yield break;
            }

            _webcamTexture = new WebCamTexture(WebCamTexture.devices[0].name, 640, 480, 30);
            _webcamTexture.Play();

            // Wait for the first frame
            float timeout = 5f;
            while (!_webcamTexture.didUpdateThisFrame && timeout > 0f)
            {
                timeout -= Time.deltaTime;
                yield return null;
            }

            if (previewImage != null)
            {
                previewImage.texture = _webcamTexture;
                // Mirror: flip UV horizontally
                previewImage.uvRect = mirrorHorizontal
                    ? new Rect(1f, 0f, -1f, 1f)
                    : new Rect(0f, 0f,  1f, 1f);
            }
        }

        // ── Bridge event handlers ─────────────────────────────────────────────────

        private void HandleTrackingStarted() => StartPreview();
        private void HandleTrackingStopped() => StopPreview();
    }
}
