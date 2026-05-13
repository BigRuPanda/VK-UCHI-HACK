using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReadingModule
{
    /// <summary>
    /// Displays real-time microphone volume as a UI fill bar and shows diagnostic warnings.
    ///
    /// Volume data comes from MicrophonePlugin.jslib AudioContext analyser,
    /// polled by SpeechBridge.Update() and broadcast via SpeechBridge.OnMicLevel.
    /// No JS calls are made from this component.
    ///
    /// Scene setup:
    ///   1. Create a small UI panel (e.g. bottom corner).
    ///   2. Add a background Image (bar track).
    ///   3. Add a child Image: Image Type=Filled, Fill Method=Horizontal → levelBarFill.
    ///   4. Optionally add TextMeshProUGUI for status text → statusLabel.
    ///   5. Optionally add a "silent warning" panel (starts inactive) → silentWarningPanel.
    ///   6. Attach this component to any GameObject.
    ///
    /// Wiring:
    ///   MicrophonePermissionHandler.onPermissionGranted → MicLevelIndicator.StartMonitoring()
    /// </summary>
    public class MicLevelIndicator : MonoBehaviour
    {
        [Header("Level Bar")]
        [Tooltip("Image with Fill Method=Horizontal. Fill Amount driven by mic amplitude.")]
        [SerializeField] private Image levelBarFill;

        [Tooltip("How quickly the bar rises/falls. Higher = snappier.")]
        [SerializeField] private float smoothSpeed = 20f;

        [Tooltip("Multiply raw amplitude to make bar more visible (raw values are 0-0.3).")]
        [SerializeField] private float amplification = 5f;

        [Header("Status Text (optional)")]
        [SerializeField] private TextMeshProUGUI statusLabel;

        [Header("Silent Warning (optional)")]
        [Tooltip("Panel shown when mic is silent for silentWarningDelay seconds.")]
        [SerializeField] private GameObject silentWarningPanel;

        [SerializeField] private TextMeshProUGUI silentWarningLabel;

        [SerializeField] private string silentWarningText =
            "Микрофон не слышит тебя!\nПроверь, что микрофон подключён и не заглушён.";

        [Tooltip("Seconds of silence before showing the warning panel.")]
        [SerializeField] private float silentWarningDelay = 3f;

        [Header("Colors")]
        [SerializeField] private Color colorNormal  = new Color(0.298f, 0.686f, 0.314f);
        [SerializeField] private Color colorWarning = new Color(1f, 0.596f, 0f);
        [SerializeField] private Color colorError   = new Color(0.957f, 0.263f, 0.212f);

        [Header("Thresholds")]
        [Tooltip("Amplitude below this is considered silence.")]
        [SerializeField] private float silentThreshold = 0.005f;

        // ── Private ───────────────────────────────────────────────────────────────
        private float _displayLevel;
        private float _rawLevel;
        private bool  _isMonitoring;
        private float _silentTimer;
        private bool  _silentWarningShown;

        // ── Unity Lifecycle ───────────────────────────────────────────────────────

        private void Start()
        {
            SetBarFill(0f);
            HideSilentWarning();
            SetStatus("Ожидание микрофона...", colorWarning);

            // Fallback: if SpeechBridge.Instance was null during OnEnable()
            // (e.g. Script Execution Order puts MicLevelIndicator before SpeechBridge),
            // subscribe here in Start() when all Awake() calls are guaranteed complete.
            if (!_subscribed)
                Subscribe();
        }

        private bool _subscribed;

        private void Subscribe()
        {
            if (SpeechBridge.Instance == null) return;
            SpeechBridge.Instance.OnMicLevel          += HandleMicLevel;
            SpeechBridge.Instance.OnPermissionGranted += HandlePermissionGranted;
            SpeechBridge.Instance.OnPermissionDenied  += HandlePermissionDenied;
            SpeechBridge.Instance.OnSpeechError       += HandleSpeechError;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (SpeechBridge.Instance == null || !_subscribed) return;
            SpeechBridge.Instance.OnMicLevel          -= HandleMicLevel;
            SpeechBridge.Instance.OnPermissionGranted -= HandlePermissionGranted;
            SpeechBridge.Instance.OnPermissionDenied  -= HandlePermissionDenied;
            SpeechBridge.Instance.OnSpeechError       -= HandleSpeechError;
            _subscribed = false;
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Update()
        {
            if (!_isMonitoring) return;

            _displayLevel = Mathf.Lerp(_displayLevel, _rawLevel, Time.deltaTime * smoothSpeed);
            SetBarFill(Mathf.Clamp01(_displayLevel * amplification));

            if (_rawLevel < silentThreshold)
            {
                _silentTimer += Time.deltaTime;
                if (_silentTimer >= silentWarningDelay && !_silentWarningShown)
                {
                    _silentWarningShown = true;
                    ShowSilentWarning();
                    SetStatus("Микрофон молчит", colorWarning);
                    SetBarColor(colorWarning);
                }
            }
            else
            {
                _silentTimer = 0f;
                if (_silentWarningShown)
                {
                    _silentWarningShown = false;
                    HideSilentWarning();
                    SetStatus("Микрофон активен", colorNormal);
                    SetBarColor(colorNormal);
                }
            }
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Start showing mic level. Wire to MicrophonePermissionHandler.onPermissionGranted.
        /// </summary>
        public void StartMonitoring()
        {
            _isMonitoring       = true;
            _silentTimer        = 0f;
            _silentWarningShown = false;
            HideSilentWarning();
            SetStatus("Микрофон активен", colorNormal);
            SetBarColor(colorNormal);
        }

        /// <summary>Stop showing mic level.</summary>
        public void StopMonitoring()
        {
            _isMonitoring = false;
            SetBarFill(0f);
            SetStatus("", colorNormal);
        }

        // ── Event Handlers ────────────────────────────────────────────────────────

        private void HandleMicLevel(float level)
        {
            _rawLevel = level;
        }

        private void HandlePermissionGranted()
        {
            SetStatus("Микрофон разрешён", colorNormal);
        }

        private void HandlePermissionDenied(string reason)
        {
            SetStatus($"Микрофон запрещён: {reason}", colorError);
            SetBarColor(colorError);
        }

        private void HandleSpeechError(string error)
        {
            if (error == "no-speech-timeout")
            {
                string msg = _rawLevel < silentThreshold
                    ? "Речь не распознаётся — говори громче"
                    : "Говори чётче или ближе к микрофону";
                SetStatus(msg, colorWarning);
            }
        }

        // ── UI Helpers ────────────────────────────────────────────────────────────

        private void SetBarFill(float amount)
        {
            if (levelBarFill != null)
                levelBarFill.fillAmount = amount;
        }

        private void SetBarColor(Color color)
        {
            if (levelBarFill != null)
                levelBarFill.color = color;
        }

        private void SetStatus(string text, Color color)
        {
            if (statusLabel == null) return;
            statusLabel.text  = text;
            statusLabel.color = color;
        }

        private void ShowSilentWarning()
        {
            if (silentWarningPanel != null)
                silentWarningPanel.SetActive(true);
            if (silentWarningLabel != null)
                silentWarningLabel.text = silentWarningText;
        }

        private void HideSilentWarning()
        {
            if (silentWarningPanel != null)
                silentWarningPanel.SetActive(false);
        }
    }
}
