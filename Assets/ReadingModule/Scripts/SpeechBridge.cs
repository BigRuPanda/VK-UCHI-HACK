using System;
using System.Runtime.InteropServices;
using UnityEngine;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

namespace ReadingModule
{
    /// <summary>
    /// Singleton MonoBehaviour that:
    ///   1. Initializes the WebGL microphone (MicrophonePlugin.jslib) on permission request
    ///   2. Polls mic volume every frame and fires OnMicLevel
    ///   3. Bridges C# ↔ SpeechRecognizer.jslib for word recognition
    ///   4. (v3) Exposes UpdateExpectedWords() to push a JSGF grammar hint to JS
    ///      so Chrome/Edge narrow recognition to the current sentence vocabulary.
    ///
    /// Scene setup:
    ///   • Create an empty GameObject named exactly "SpeechBridge" and attach this component.
    ///   • The name must match the SendMessage target in both JS plugins.
    ///
    /// Editor testing (no browser build needed):
    ///   M     → simulate microphone permission granted
    ///   Space → simulate correct word recognized
    ///   X     → simulate wrong word recognized
    /// </summary>
    public class SpeechBridge : MonoBehaviour
    {
        // ── Singleton ────────────────────────────────────────────────────────────
        public static SpeechBridge Instance { get; private set; }

        // ── Events ───────────────────────────────────────────────────────────────

        /// <summary>Fired every time a word is recognized by the Speech API.</summary>
        public event Action<string> OnWordRecognized;

        /// <summary>Fired when microphone permission is granted (getUserMedia succeeded).</summary>
        public event Action OnPermissionGranted;

        /// <summary>Fired when microphone permission is denied. Parameter = reason.</summary>
        public event Action<string> OnPermissionDenied;

        /// <summary>Fired on fatal speech recognition errors.</summary>
        public event Action<string> OnSpeechError;

        /// <summary>
        /// Fired every frame with the current peak mic amplitude [0..1].
        /// Sourced from MicrophonePlugin.jslib's AudioContext analyser.
        /// Subscribe in MicLevelIndicator to drive the level bar.
        /// </summary>
        public event Action<float> OnMicLevel;

        /// <summary>
        /// Fired during Vosk model loading with progress in [0..100].
        /// Use this to drive the loading-screen progress bar on the book cover.
        /// Sourced from VoskBridge.jslib → SendMessage("SpeechBridge","ReceiveModelProgress","42").
        /// Not fired when using the Web Speech API fallback.
        /// </summary>
        public event Action<int> OnModelLoadProgress;

        // ── Inspector — Vosk / recognition tuning ────────────────────────────────

        [Header("── Dedup Guard ──────────────────────────────────────")]
        [Tooltip("Suppress the same word if it arrives again within this window (seconds).\n" +
                 "Protects against partialresult+result overlap and other JS double-fires.\n" +
                 "0 = disabled.")]
        [SerializeField] private float dedupWindowSec = 1.5f;

        [Header("── Syllabic Reading — JS Debounce ────────────────────")]
        [Tooltip("Milliseconds to wait after the last Vosk 'result' event before forwarding\n" +
                 "words to Unity. Merges rapid successive results so syllables of the same\n" +
                 "word (e.g. 'мо' + 'ст') arrive together and can be concatenated.\n" +
                 "0 = disabled (words sent immediately).")]
        [SerializeField] private int resultDebounceMsec = 600;

        // ── JS Imports — VoskBridge.jslib ─────────────────────────────────────────
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void StartRecognition(string lang);
        [DllImport("__Internal")] private static extern void StopRecognition();

        /// <summary>
        /// Push a JSON array of expected words to the JS recognizer so it can
        /// build a JSGF SpeechGrammarList. Chrome/Edge use this to narrow the
        /// recognition search space to the current sentence vocabulary.
        /// Example JSON: ["жил","был","маленький","волшебник"]
        /// </summary>
        [DllImport("__Internal")] private static extern void SetExpectedWords(string wordsJson);

        /// <summary>
        /// Apply runtime recognition config (debounce + endpointer rules) to VoskBridge.jslib.
        /// JSON format: { "resultDebounceMsec": 600, "endpointer": { "rule1": 0.8, ... } }
        /// </summary>
        [DllImport("__Internal")] private static extern void SetRecognitionConfig(string configJson);
#endif

        // ── State ────────────────────────────────────────────────────────────────
        private bool _isListening;
        private bool _permissionGranted;
        private bool _micInitialized;

        // ── Dedup guard — suppresses the same word arriving twice within the window
        // (covers the partialresult+result overlap from Vosk and future regressions)
        private string _lastWord     = "";
        private float  _lastWordTime = -999f;

        // ── Unity Lifecycle ──────────────────────────────────────────────────────
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                StopListening();
                Instance = null;
            }
        }

        private void Update()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // ── Poll mic volume from MicrophonePlugin.jslib ───────────────────────
            // Use GetMicVolumeDirect() — reads document.volume directly from JS.
            // This does NOT depend on device enumeration (GetNumberOfMicrophones),
            // which is async and may return 0 for several frames after Init().
            if (_micInitialized)
            {
                UnityEngine.Microphone.Update(); // dispatch JS action queue
                float level = UnityEngine.Microphone.GetMicVolumeDirect();
                OnMicLevel?.Invoke(level);
            }
#else
            // ── Editor keyboard simulation ────────────────────────────────────────
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.mKey.wasPressedThisFrame)
            {
                Debug.Log("[SpeechBridge] EDITOR: simulating permission GRANTED");
                ReceivePermissionResult("granted");
            }

            if (!_isListening) return;

            if (keyboard.spaceKey.wasPressedThisFrame)
            {
                Debug.Log("[SpeechBridge] EDITOR: simulating CORRECT word");
                OnWordRecognized?.Invoke("__EDITOR_CORRECT__");
            }
            else if (keyboard.xKey.wasPressedThisFrame)
            {
                Debug.Log("[SpeechBridge] EDITOR: simulating WRONG word");
                OnWordRecognized?.Invoke("__EDITOR_WRONG__");
            }
#endif
        }

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Request microphone permission from the browser.
        /// MUST be called from a user gesture handler (Button.onClick).
        /// On WebGL: calls Microphone.Init() which triggers getUserMedia.
        /// On success/failure, MicrophonePlugin.jslib calls ReceivePermissionResult via SendMessage.
        /// </summary>
        public void RequestPermission()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            UnityEngine.Microphone.Init();
            UnityEngine.Microphone.QueryAudioInput();
            _micInitialized = true;
            Debug.Log("[SpeechBridge] Microphone.Init() called — waiting for browser response.");
#else
            Debug.Log("[SpeechBridge] RequestPermission — Editor mode. Press M to simulate grant.");
#endif
        }

        /// <summary>
        /// Start continuous speech recognition.
        /// Call only after OnPermissionGranted has fired.
        /// </summary>
        public void StartListening(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                language = "ru-RU";
                Debug.LogWarning("[SpeechBridge] Empty speech language was passed. Falling back to ru-RU.");
            }

            if (!_permissionGranted)
            {
                Debug.LogWarning($"[SpeechBridge] StartListening({language}) called before microphone permission was confirmed. " +
                                 "Recognition may fail in the browser.");
            }

            if (_isListening)
            {
                Debug.Log($"[SpeechBridge] StartListening({language}) called while already listening. Restarting cleanly.");
                StopListening();
            }

            _isListening = true;
#if UNITY_WEBGL && !UNITY_EDITOR
            // Push Inspector-configured recognition parameters to JS before starting.
            ApplyRecognitionConfig();
            Debug.Log($"[SpeechBridge] Calling JS StartRecognition({language}).");
            StartRecognition(language);
#else
            Debug.Log($"[SpeechBridge] StartListening({language}) — Editor mode. Use Space/X to simulate words.");
#endif
        }

        /// <summary>
        /// Serialize Inspector fields into JSON and push them to VoskBridge.jslib.
        /// Called automatically by StartListening(); can also be called at runtime
        /// to hot-reload parameters without restarting recognition.
        /// </summary>
        public void ApplyRecognitionConfig()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            // NOTE: Vosk endpointer rules cannot be set via the vosk-browser JS API —
            // KaldiRecognizer only accepts a JSGF grammar string as its second argument.
            // Only resultDebounceMsec is forwarded to JS.
            string json = $"{{\"resultDebounceMsec\":{resultDebounceMsec}}}";
            Debug.Log($"[SpeechBridge] ApplyRecognitionConfig: {json}");
            SetRecognitionConfig(json);
#else
            Debug.Log($"[SpeechBridge] ApplyRecognitionConfig — Editor mode (no-op). debounce={resultDebounceMsec}ms");
#endif
        }

        /// <summary>Stop speech recognition.</summary>
        public void StopListening()
        {
            if (!_isListening)
            {
                Debug.Log("[SpeechBridge] StopListening called while not listening — ignored.");
                return;
            }

            _isListening  = false;
            _lastWord     = "";
            _lastWordTime = -999f;
#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.Log("[SpeechBridge] Calling JS StopRecognition().");
            StopRecognition();
#else
            Debug.Log("[SpeechBridge] StopListening — Editor mode.");
#endif
        }

        /// <summary>
        /// Push the words of the current sentence to the JS recognizer as a
        /// JSGF grammar hint. Chrome/Edge use this to narrow the recognition
        /// search space, improving both speed and accuracy.
        ///
        /// Call this every time a new sentence is loaded (ReadingController does
        /// this automatically via LoadSentence → UpdateGrammarHint).
        ///
        /// Safe to call before StartListening — the grammar is stored in JS and
        /// applied to the next SpeechRecognition instance created by createAndStart().
        /// </summary>
        /// <param name="words">Clean (punctuation-stripped) words of the sentence.</param>
        public void UpdateExpectedWords(string[] words)
        {
            if (words == null || words.Length == 0)
            {
                Debug.Log("[SpeechBridge] UpdateExpectedWords: empty array — grammar hint cleared.");
#if UNITY_WEBGL && !UNITY_EDITOR
                SetExpectedWords("[]");
#endif
                return;
            }

            // Build a compact JSON array without JsonUtility dependency.
            // Words are already stripped of punctuation by ReadingController.ParseSentence.
            // Normalise to lowercase: Vosk vocabulary is lowercase-only.
            // "Жил" → "жил" — without this Vosk logs "Ignoring word missing in vocabulary".
            string json = "[\"" + string.Join("\",\"", System.Array.ConvertAll(words, w => w.ToLowerInvariant())) + "\"]";

            Debug.Log($"[SpeechBridge] UpdateExpectedWords: {json}");

#if UNITY_WEBGL && !UNITY_EDITOR
            SetExpectedWords(json);
#endif
        }

        /// <summary>Whether microphone permission has been granted this session.</summary>
        public bool IsPermissionGranted => _permissionGranted;

        // ── Called by JS via SendMessage ─────────────────────────────────────────

        /// <summary>
        /// Called by MicrophonePlugin.jslib:
        ///   SendMessage('SpeechBridge', 'ReceivePermissionResult', 'granted')
        ///   SendMessage('SpeechBridge', 'ReceivePermissionResult', 'denied:NotAllowedError')
        /// </summary>
        public void ReceivePermissionResult(string result)
        {
            Debug.Log($"[SpeechBridge] ReceivePermissionResult: '{result}'");
            if (result == "granted")
            {
                _permissionGranted = true;
                Debug.Log("[SpeechBridge] Microphone permission granted. Speech recognition can be started now.");
                OnPermissionGranted?.Invoke();
            }
            else
            {
                _permissionGranted = false;
                string reason = result.StartsWith("denied:") ? result.Substring(7) : result;
                Debug.LogWarning($"[SpeechBridge] Microphone permission denied: {reason}");
                OnPermissionDenied?.Invoke(reason);
            }
        }

        /// <summary>
        /// Called by SpeechRecognizer.jslib:
        ///   SendMessage('SpeechBridge', 'ReceiveWord', word)
        /// </summary>
        public void ReceiveWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return;

            string trimmed = word.Trim();
            if (!_isListening)
            {
                Debug.Log($"[SpeechBridge] Ignoring word while not listening: '{trimmed}'");
                return;
            }

            // Dedup: suppress the same word if it arrives again within the window.
            if (trimmed == _lastWord && (Time.unscaledTime - _lastWordTime) < dedupWindowSec)
            {
                Debug.Log($"[SpeechBridge] Dedup suppressed duplicate: '{trimmed}'");
                return;
            }
            _lastWord     = trimmed;
            _lastWordTime = Time.unscaledTime;

            Debug.Log($"[SpeechBridge] ReceiveWord: '{trimmed}'");
            OnWordRecognized?.Invoke(trimmed);
        }

        /// <summary>
        /// Called by VoskBridge.jslib during Vosk model loading:
        ///   SendMessage('SpeechBridge', 'ReceiveModelProgress', '42')
        /// Fires OnModelLoadProgress with an integer in [0..100].
        /// Use this to drive the loading-screen progress bar on the book cover.
        /// </summary>
        public void ReceiveModelProgress(string valueStr)
        {
            if (int.TryParse(valueStr, out int progress))
            {
                progress = Mathf.Clamp(progress, 0, 100);
                Debug.Log($"[SpeechBridge] Vosk model load progress: {progress}%");
                OnModelLoadProgress?.Invoke(progress);
            }
            else
            {
                Debug.LogWarning($"[SpeechBridge] ReceiveModelProgress: could not parse '{valueStr}'");
            }
        }

        /// <summary>
        /// Called by VoskBridge.jslib (and legacy SpeechRecognizer.jslib):
        ///   SendMessage('SpeechBridge', 'ReceiveError', errorCode)
        /// </summary>
        public void ReceiveError(string error)
        {
            Debug.LogWarning($"[SpeechBridge] Speech error: {error}");

            if (error == "not-allowed" || error == "service-not-allowed" ||
                (error != null && error.StartsWith("mic-denied:")))
            {
                _isListening = false;
            }

            OnSpeechError?.Invoke(error);
        }
    }
}
