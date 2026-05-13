using UnityEngine;
using UnityEngine.Events;
using HandTracking.Gameplay;
using HandTracking.Visualization;

namespace HandTracking.Core
{
    // ─── State machine ────────────────────────────────────────────────────────────

    public enum HandTrackingState
    {
        Idle,
        WaitingForPermission,
        InitialisingTracking,
        ShowingRules,
        Countdown,
        Playing,
        Paused,
        Finished,
    }

    // ─── Manager ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Top-level coordinator for the hand-tracking mini-game.
    ///
    /// Responsibilities:
    ///   • Drives the state machine: Idle → Permission → Init → Playing → Finished
    ///   • Wires CameraPermissionManager → HandTrackingBridge → gameplay systems
    ///   • Exposes UnityEvents so the parent game (e.g. the pirate screen) can
    ///     react to catches, misses, and game-over without touching internals.
    ///   • Exposes StartMinigame() / StopMinigame() / PauseMinigame() as a clean API.
    ///
    /// Place this on the HandTrackingRoot GameObject and assign all references
    /// in the Inspector (see README_HandTracking.md for the full hierarchy).
    /// </summary>
    public class HandTrackingGameManager : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Header("Sub-systems (assign in Inspector)")]
        public CameraPermissionManager permissionManager;
        public HandTrackingBridge      bridge;
        public HandVisualizer          handVisualizer;
        public ProjectileLauncher      launcher;
        public CatchZoneController     catchZone;
        public HandCatchParticles      particles;   // optional

        [Header("UI Flow (assign in Inspector)")]
        [Tooltip("Panel showing the game rules. Shown after camera permission is granted.")]
        public GameObject rulesPanel;
        
        [Tooltip("Button to click when ready to start. Should be inside the rules panel.")]
        public UnityEngine.UI.Button readyButton;
        
        [Tooltip("Panel showing the countdown.")]
        public GameObject countdownPanel;
        
        [Tooltip("Text element displaying the countdown numbers.")]
        public TMPro.TMP_Text countdownText;

        [Tooltip("Optional progress bar to show how many projectiles have been caught.")]
        public HandTrackingProgressBar progressBar;

        [Header("Game Rules")]
        [Tooltip("Total number of projectiles the player must catch to win. 0 = infinite / no win condition.")]
        public int catchesToWin = 10;

        [Tooltip("Maximum misses allowed before the game ends. 0 = infinite.")]
        public int maxMisses = 3;

        [Header("Auto-start")]
        [Tooltip("Automatically call StartMinigame() on Start.")]
        public bool autoStart = true;

        [Tooltip("Number of seconds for the countdown before the game starts.")]
        public int countdownSeconds = 3;

        // ── UnityEvents (Inspector-hookable) ──────────────────────────────────────

        [Header("Events")]
        [Tooltip("Fired each time a projectile is caught.")]
        public UnityEvent OnCatch;

        [Tooltip("Fired each time a projectile is missed.")]
        public UnityEvent OnMiss;

        [Tooltip("Fired when the player catches enough projectiles to win.")]
        public UnityEvent OnWin;

        [Tooltip("Fired when the player exceeds the miss limit.")]
        public UnityEvent OnLose;

        [Tooltip("Fired whenever the state changes. Passes the new state as an int (cast to HandTrackingState).")]
        public UnityEvent<int> OnStateChanged;

        // ── Public read-only state ────────────────────────────────────────────────

        public HandTrackingState CurrentState { get; private set; } = HandTrackingState.Idle;
        public int CatchCount { get; private set; }
        public int MissCount  { get; private set; }

        // ── Private ───────────────────────────────────────────────────────────────

        private bool _trackingInitialised;
        private bool _trackingStarted;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            // Auto-find components if not assigned
            if (permissionManager == null) permissionManager = FindObjectOfType<CameraPermissionManager>();
            if (bridge            == null) bridge            = FindObjectOfType<HandTrackingBridge>();
            if (handVisualizer    == null) handVisualizer    = FindObjectOfType<HandVisualizer>();
            if (launcher          == null) launcher          = FindObjectOfType<ProjectileLauncher>();
            if (catchZone         == null) catchZone         = FindObjectOfType<CatchZoneController>();
            if (particles         == null) particles         = FindObjectOfType<HandCatchParticles>();

            if (readyButton != null)
                readyButton.onClick.AddListener(OnReadyClicked);

            if (rulesPanel != null) rulesPanel.SetActive(false);
            if (countdownPanel != null) countdownPanel.SetActive(false);

            ValidateReferences();
            WireEvents();
        }

        private void Start()
        {
            if (autoStart)
                StartMinigame();
        }

        private void OnDestroy()
        {
            UnwireEvents();
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Begin the mini-game flow: request camera permission, initialise
        /// MediaPipe, then start spawning projectiles.
        /// </summary>
        public void StartMinigame()
        {
            if (CurrentState != HandTrackingState.Idle &&
                CurrentState != HandTrackingState.Finished)
            {
                Debug.LogWarning("[HandTrackingGameManager] StartMinigame called in invalid state: " + CurrentState);
                return;
            }

            CatchCount = 0;
            MissCount  = 0;
            _trackingInitialised = false;
            _trackingStarted     = false;

            progressBar?.ResetBar();

            SetState(HandTrackingState.WaitingForPermission);
            permissionManager?.RequestPermission();
        }

        /// <summary>Pause projectile spawning and hand tracking updates.</summary>
        public void PauseMinigame()
        {
            if (CurrentState != HandTrackingState.Playing) return;
            launcher?.StopLaunching();
            SetState(HandTrackingState.Paused);
        }

        /// <summary>Resume from pause.</summary>
        public void ResumeMinigame()
        {
            if (CurrentState != HandTrackingState.Paused) return;
            launcher?.StartLaunching();
            SetState(HandTrackingState.Playing);
        }

        /// <summary>Stop the mini-game immediately and clean up.</summary>
        public void StopMinigame()
        {
            launcher?.StopLaunching();
            bridge?.StopTracking();
            SetState(HandTrackingState.Finished);
        }

        // ── Event wiring ──────────────────────────────────────────────────────────

        private void WireEvents()
        {
            // Permission
            if (permissionManager != null)
            {
                permissionManager.OnGranted += HandlePermissionGranted;
                permissionManager.OnDenied  += HandlePermissionDenied;
            }

            // Bridge
            if (bridge != null)
            {
                bridge.OnTrackingInitialisedEvent += HandleTrackingInitialised;
                bridge.OnTrackingStartedEvent     += HandleTrackingStarted;
                bridge.OnTrackingErrorEvent       += HandleTrackingError;
            }

            // Catch / Miss
            if (catchZone != null)
            {
                catchZone.OnCatchCS += HandleCatch;
                catchZone.OnMissCS  += HandleMiss;
            }
        }

        private void UnwireEvents()
        {
            if (permissionManager != null)
            {
                permissionManager.OnGranted -= HandlePermissionGranted;
                permissionManager.OnDenied  -= HandlePermissionDenied;
            }

            if (bridge != null)
            {
                bridge.OnTrackingInitialisedEvent -= HandleTrackingInitialised;
                bridge.OnTrackingStartedEvent     -= HandleTrackingStarted;
                bridge.OnTrackingErrorEvent       -= HandleTrackingError;
            }

            if (catchZone != null)
            {
                catchZone.OnCatchCS -= HandleCatch;
                catchZone.OnMissCS  -= HandleMiss;
            }
        }

        // ── Handlers ──────────────────────────────────────────────────────────────

        private void HandlePermissionGranted()
        {
            if (CurrentState != HandTrackingState.WaitingForPermission) return;
            SetState(HandTrackingState.InitialisingTracking);
            bridge?.InitTracking();
        }

        private void HandlePermissionDenied(string reason)
        {
            Debug.LogWarning($"[HandTrackingGameManager] Camera permission denied: {reason}");
            // Stay in WaitingForPermission — the UI panel shows the retry button.
        }

        private void HandleTrackingInitialised()
        {
            _trackingInitialised = true;
            bridge?.StartTracking();
        }

        private void HandleTrackingStarted()
        {
            _trackingStarted = true;
            if (CurrentState == HandTrackingState.InitialisingTracking)
            {
                SetState(HandTrackingState.ShowingRules);
                if (rulesPanel != null)
                {
                    rulesPanel.SetActive(true);
                }
                else
                {
                    // Fallback if no rules panel is assigned
                    StartCountdown();
                }
            }
        }

        private void OnReadyClicked()
        {
            if (CurrentState != HandTrackingState.ShowingRules) return;
            
            if (rulesPanel != null) rulesPanel.SetActive(false);
            StartCountdown();
        }

        private void StartCountdown()
        {
            SetState(HandTrackingState.Countdown);
            if (countdownPanel != null) countdownPanel.SetActive(true);
            StartCoroutine(CountdownRoutine());
        }

        private System.Collections.IEnumerator CountdownRoutine()
        {
            for (int i = countdownSeconds; i > 0; i--)
            {
                StartCoroutine(AnimateCountdownText(i.ToString()));
                yield return new WaitForSeconds(1f);
            }

            StartCoroutine(AnimateCountdownText("Старт!"));
            yield return new WaitForSeconds(1f);

            if (countdownPanel != null) countdownPanel.SetActive(false);

            SetState(HandTrackingState.Playing);
            launcher?.StartLaunching();
        }

        private System.Collections.IEnumerator AnimateCountdownText(string text)
        {
            if (countdownText == null) yield break;

            countdownText.text = text;
            RectTransform rt = countdownText.GetComponent<RectTransform>();
            if (rt == null) yield break;

            float duration = 0.6f;
            float elapsed = 0f;
            
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;
                
                // Scale pop: fast up to 1.3x, then settle to 1x
                float scale = 1f;
                if (t < 0.3f) {
                    scale = Mathf.Lerp(0.2f, 1.3f, t / 0.3f);
                } else {
                    scale = Mathf.Lerp(1.3f, 1f, (t - 0.3f) / 0.7f);
                }
                
                // Rotation shake: dampened sine wave
                float angle = Mathf.Sin(t * Mathf.PI * 4f) * 10f * (1f - t);
                
                rt.localScale = Vector3.one * scale;
                rt.localRotation = Quaternion.Euler(0, 0, angle);
                
                yield return null;
            }
            
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
        }

        private void HandleTrackingError(string error)
        {
            Debug.LogError($"[HandTrackingGameManager] Tracking error: {error}");
            // Optionally surface this to the UI
        }

        private void HandleCatch(HandSide side, ProjectileInstance proj)
        {
            if (CurrentState != HandTrackingState.Playing) return;

            CatchCount++;
            OnCatch?.Invoke();

            if (catchesToWin > 0)
            {
                float score = Mathf.Clamp01((float)CatchCount / catchesToWin);
                progressBar?.OnScoreUpdate(score);

                if (CatchCount >= catchesToWin)
                {
                    launcher?.StopLaunching();
                    SetState(HandTrackingState.Finished);
                    progressBar?.OnWinAnimation();
                    OnWin?.Invoke();
                }
            }
        }

        private void HandleMiss(ProjectileInstance proj)
        {
            if (CurrentState != HandTrackingState.Playing) return;

            MissCount++;
            OnMiss?.Invoke();

            if (maxMisses > 0 && MissCount >= maxMisses)
            {
                launcher?.StopLaunching();
                SetState(HandTrackingState.Finished);
                OnLose?.Invoke();
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private void SetState(HandTrackingState newState)
        {
            if (CurrentState == newState) return;
            CurrentState = newState;
            Debug.Log($"[HandTrackingGameManager] State → {newState}");
            OnStateChanged?.Invoke((int)newState);
        }

        private void ValidateReferences()
        {
            if (permissionManager == null)
                Debug.LogError("[HandTrackingGameManager] CameraPermissionManager not found!");
            if (bridge == null)
                Debug.LogError("[HandTrackingGameManager] HandTrackingBridge not found!");
            if (launcher == null)
                Debug.LogError("[HandTrackingGameManager] ProjectileLauncher not found!");
            if (catchZone == null)
                Debug.LogError("[HandTrackingGameManager] CatchZoneController not found!");
        }

#if UNITY_EDITOR
        [ContextMenu("Debug: Force Start Playing")]
        private void DebugForcePlay()
        {
            SetState(HandTrackingState.Playing);
            launcher?.StartLaunching();
        }

        [ContextMenu("Debug: Force Stop")]
        private void DebugForceStop()
        {
            StopMinigame();
        }
#endif
    }
}
