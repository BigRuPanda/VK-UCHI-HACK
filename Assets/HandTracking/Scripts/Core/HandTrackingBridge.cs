using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HandTracking.Core
{
    // ─── Data structures ────────────────────────────────────────────────────────

    [Serializable]
    public struct LandmarkPoint
    {
        public float x; // normalised [0,1], origin top-left
        public float y;
    }

    [Serializable]
    public class HandLandmarkData
    {
        public string      label;      // "Left" or "Right" (in selfie/mirror space)
        public float       score;
        public LandmarkPoint wrist;
        public LandmarkPoint thumbTip;
        public LandmarkPoint indexTip;
        public LandmarkPoint middleTip;
        public LandmarkPoint ringTip;
        public LandmarkPoint pinkyTip;
    }

    [Serializable]
    internal class HandPayload
    {
        public List<HandLandmarkData> hands = new List<HandLandmarkData>();
    }

    public enum HandSide { Left, Right }

    // ─── Bridge ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sits between the JS MediaPipe plugin and the rest of the Unity code.
    /// • Receives raw JSON from JS via SendMessage.
    /// • Exposes clean per-hand screen positions and visibility flags.
    /// • In the Unity Editor (non-WebGL) it falls back to mouse position so the
    ///   team can iterate without a physical camera.
    /// </summary>
    public class HandTrackingBridge : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Header("MediaPipe Settings")]
        [Tooltip("Max number of hands to detect (1 or 2).")]
        [Range(1, 2)]
        public int maxHands = 2;

        [Tooltip("Minimum confidence to consider a detection valid.")]
        [Range(0f, 1f)]
        public float minDetectionConfidence = 0.7f;

        [Tooltip("Minimum confidence to keep tracking a hand between frames.")]
        [Range(0f, 1f)]
        public float minTrackingConfidence = 0.5f;

        [Header("Smoothing")]
        [Tooltip("Lerp factor applied to hand screen positions each frame (0=no movement, 1=instant).")]
        [Range(0.01f, 1f)]
        public float smoothingFactor = 0.2f;

        [Header("Editor Fallback")]
        [Tooltip("In the Unity Editor, drive the RIGHT hand with the mouse cursor.")]
        public bool editorMouseFallback = true;

        // ── Events ───────────────────────────────────────────────────────────────

        /// <summary>Fired when JS reports tracking has been initialised.</summary>
        public event Action OnTrackingInitialisedEvent;

        /// <summary>Fired when JS reports tracking has started (camera stream open).</summary>
        public event Action OnTrackingStartedEvent;

        /// <summary>Fired when JS reports tracking has stopped.</summary>
        public event Action OnTrackingStoppedEvent;

        /// <summary>Fired when JS reports an error string.</summary>
        public event Action<string> OnTrackingErrorEvent;

        // ── Public read-only state ────────────────────────────────────────────────

        /// <summary>Screen-space position of the left hand wrist (pixels, origin bottom-left).</summary>
        public Vector2 LeftHandScreenPos  { get; private set; }

        /// <summary>Screen-space position of the right hand wrist (pixels, origin bottom-left).</summary>
        public Vector2 RightHandScreenPos { get; private set; }

        /// <summary>Whether the left hand is currently visible.</summary>
        public bool LeftHandVisible  { get; private set; }

        /// <summary>Whether the right hand is currently visible.</summary>
        public bool RightHandVisible { get; private set; }

        /// <summary>Full landmark data for the last frame (may be null if no hands detected).</summary>
        public IReadOnlyList<HandLandmarkData> LastLandmarks => _lastLandmarks;

        // ── Private ───────────────────────────────────────────────────────────────

        private List<HandLandmarkData> _lastLandmarks = new List<HandLandmarkData>();
        private Vector2 _leftRaw;
        private Vector2 _rightRaw;
        private bool    _isTracking;

        // ── JS imports ────────────────────────────────────────────────────────────

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void HT_InitHandTracking(string gameObjectName, int maxHands, float minDetConf, float minTrkConf);
        [DllImport("__Internal")] private static extern void HT_StartHandTracking();
        [DllImport("__Internal")] private static extern void HT_StopHandTracking();
        [DllImport("__Internal")] private static extern int  HT_IsTracking();
#else
        // Stubs for non-WebGL builds
        private static void HT_InitHandTracking(string n, int m, float d, float t) { }
        private static void HT_StartHandTracking() { }
        private static void HT_StopHandTracking() { }
        private static int  HT_IsTracking() => 0;
#endif

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>Ask the browser for camera permission. Listen to CameraPermissionManager events for the result.</summary>
        public void RequestCameraPermission()
        {
            // Handled natively by CameraPermissionManager now.
        }

        /// <summary>Initialise MediaPipe Hands (loads WASM). Call after permission is granted.</summary>
        public void InitTracking()
        {
            HT_InitHandTracking(gameObject.name, maxHands, minDetectionConfidence, minTrackingConfidence);
        }

        /// <summary>Open the camera stream and start sending frames to MediaPipe.</summary>
        public void StartTracking()
        {
            _isTracking = true;
            HT_StartHandTracking();
        }

        /// <summary>Stop the camera stream.</summary>
        public void StopTracking()
        {
            _isTracking = false;
            HT_StopHandTracking();
        }

        /// <summary>Returns the screen position for the requested hand side.</summary>
        public Vector2 GetHandScreenPos(HandSide side) =>
            side == HandSide.Left ? LeftHandScreenPos : RightHandScreenPos;

        /// <summary>Returns whether the requested hand is currently visible.</summary>
        public bool IsHandVisible(HandSide side) =>
            side == HandSide.Left ? LeftHandVisible : RightHandVisible;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Update()
        {
#if UNITY_EDITOR
            if (editorMouseFallback)
            {
#if ENABLE_INPUT_SYSTEM
                _rightRaw = Mouse.current != null
                    ? Mouse.current.position.ReadValue()
                    : Vector2.zero;
#else
                _rightRaw = Input.mousePosition;
#endif
                RightHandVisible = true;
                LeftHandVisible  = false;
            }
#endif
            // Smooth positions
            LeftHandScreenPos  = Vector2.Lerp(LeftHandScreenPos,  _leftRaw,  smoothingFactor);
            RightHandScreenPos = Vector2.Lerp(RightHandScreenPos, _rightRaw, smoothingFactor);
        }

        private void OnDestroy()
        {
            if (_isTracking) StopTracking();
        }

        // ── JS → Unity callbacks (called via SendMessage) ─────────────────────────

        /// <summary>Called by JS plugin every frame with serialised landmark JSON.</summary>
        public void OnHandData(string json)
        {
            if (string.IsNullOrEmpty(json)) return;

            HandPayload payload;
            try { payload = JsonUtility.FromJson<HandPayload>(json); }
            catch (Exception e)
            {
                Debug.LogWarning($"[HandTrackingBridge] JSON parse error: {e.Message}");
                return;
            }

            _lastLandmarks = payload.hands ?? new List<HandLandmarkData>();

            bool leftSeen  = false;
            bool rightSeen = false;

            if (_lastLandmarks.Count == 2)
            {
                // Sort by X coordinate to prevent hands swapping when MediaPipe gets confused
                Vector2 pos0 = NormalisedToScreen(_lastLandmarks[0].wrist.x, _lastLandmarks[0].wrist.y);
                Vector2 pos1 = NormalisedToScreen(_lastLandmarks[1].wrist.x, _lastLandmarks[1].wrist.y);

                if (pos0.x < pos1.x)
                {
                    _leftRaw = pos0;
                    _rightRaw = pos1;
                }
                else
                {
                    _leftRaw = pos1;
                    _rightRaw = pos0;
                }
                leftSeen = true;
                rightSeen = true;
            }
            else if (_lastLandmarks.Count == 1)
            {
                Vector2 pos = NormalisedToScreen(_lastLandmarks[0].wrist.x, _lastLandmarks[0].wrist.y);
                
                // If only one hand is visible, determine side by screen half
                if (pos.x < Screen.width * 0.5f)
                {
                    _leftRaw = pos;
                    leftSeen = true;
                }
                else
                {
                    _rightRaw = pos;
                    rightSeen = true;
                }
            }

            LeftHandVisible  = leftSeen;
            RightHandVisible = rightSeen;
        }

        public void OnPermissionGranted(string _)
        {
            // No longer used, handled natively in C#
        }

        public void OnPermissionDenied(string msg)
        {
            // No longer used, handled natively in C#
        }

        // Called by JS via SendMessage — names must match exactly what the jslib sends.
        public void OnTrackingInitialised(string _)
        {
            Debug.Log("[HandTrackingBridge] MediaPipe initialised.");
            OnTrackingInitialisedEvent?.Invoke();
        }

        public void OnTrackingStarted(string _)
        {
            Debug.Log("[HandTrackingBridge] Tracking started.");
            OnTrackingStartedEvent?.Invoke();
        }

        public void OnTrackingStopped(string _)
        {
            Debug.Log("[HandTrackingBridge] Tracking stopped.");
            OnTrackingStoppedEvent?.Invoke();
        }

        public void OnTrackingError(string msg)
        {
            Debug.LogError($"[HandTrackingBridge] Error: {msg}");
            OnTrackingErrorEvent?.Invoke(msg);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Converts normalised MediaPipe coords (origin top-left, [0,1]) to
        /// Unity screen coords (origin bottom-left, pixels).
        /// </summary>
        private static Vector2 NormalisedToScreen(float nx, float ny)
        {
            return new Vector2(
                nx * Screen.width,
                (1f - ny) * Screen.height   // flip Y axis
            );
        }
    }
}
