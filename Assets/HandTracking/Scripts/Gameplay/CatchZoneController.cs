using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using HandTracking.Core;

namespace HandTracking.Gameplay
{
    // ─── UnityEvent wrappers (serialisable) ──────────────────────────────────────

    [System.Serializable]
    public class CatchEvent : UnityEvent<HandSide> { }

    [System.Serializable]
    public class MissEvent : UnityEvent { }

    // ─── Controller ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Each frame checks whether either hand's screen position overlaps any
    /// active projectile. Fires OnCatch / OnMiss events accordingly.
    ///
    /// Works entirely in canvas-space (anchoredPosition pixels) — no physics
    /// rigidbodies required.
    /// </summary>
    public class CatchZoneController : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Header("References")]
        [Tooltip("Supplies hand screen positions.")]
        public HandTrackingBridge bridge;

        [Tooltip("Supplies the list of active projectiles.")]
        public ProjectileLauncher launcher;

        [Tooltip("Canvas used for coordinate conversion.")]
        public Canvas targetCanvas;

        [Header("Detection")]
        [Tooltip("Catch radius in canvas pixels. A projectile is caught when a hand " +
                 "centre is within this distance of the projectile centre.")]
        [Range(20f, 300f)]
        public float catchRadius = 90f;

        [Tooltip("Require the hand to be visible (confidence > 0) to register a catch.")]
        public bool requireHandVisible = true;

        [Tooltip("Maximum distance from the projectile's target destination to allow catching. " +
                 "Ensures projectiles are only caught when they arrive at the reticle.")]
        [Range(50f, 500f)]
        public float catchTargetProximity = 150f;

        [Header("Feedback")]
        [Tooltip("Duration of the screen-flash effect on a miss (seconds). 0 = disabled.")]
        [Range(0f, 1f)]
        public float missFlashDuration = 0.25f;

        [Tooltip("Colour of the miss flash overlay.")]
        public Color missFlashColor = new Color(1f, 0.1f, 0.1f, 0.35f);

        [Tooltip("Optional CanvasGroup or Image used as the miss-flash overlay.")]
        public UnityEngine.UI.Image missFlashImage;

        [Header("Events")]
        [Tooltip("Fired when a projectile is caught. Passes the catching hand side.")]
        public CatchEvent OnCatch;

        [Tooltip("Fired when a projectile reaches its target without being caught.")]
        public MissEvent OnMiss;

        // ── C# events (for non-Inspector subscribers) ─────────────────────────────

        public event System.Action<HandSide, ProjectileInstance> OnCatchCS;
        public event System.Action<ProjectileInstance>           OnMissCS;

        // ── Private ───────────────────────────────────────────────────────────────

        private float _flashTimer;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (bridge   == null) bridge   = FindObjectOfType<HandTrackingBridge>();
            if (launcher == null) launcher = FindObjectOfType<ProjectileLauncher>();
            if (targetCanvas == null) targetCanvas = GetComponentInParent<Canvas>();

            // Subscribe to launcher's miss event
            if (launcher != null)
                launcher.OnProjectileMissed += HandleMiss;

            // Hide flash overlay
            if (missFlashImage != null)
            {
                var c = missFlashImage.color;
                c.a = 0f;
                missFlashImage.color = c;
            }
        }

        private void OnDestroy()
        {
            if (launcher != null)
                launcher.OnProjectileMissed -= HandleMiss;
        }

        private void Update()
        {
            CheckCatches();
            UpdateFlash();
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private void CheckCatches()
        {
            if (bridge == null || launcher == null) return;

            var projectiles = launcher.ActiveProjectiles;
            if (projectiles == null || projectiles.Count == 0) return;

            // Build hand positions in canvas space
            // We need to convert screen-space positions to canvas anchoredPositions
            Vector2 leftCanvas  = ScreenToCanvas(bridge.LeftHandScreenPos);
            Vector2 rightCanvas = ScreenToCanvas(bridge.RightHandScreenPos);

            bool leftVisible  = !requireHandVisible || bridge.LeftHandVisible;
            bool rightVisible = !requireHandVisible || bridge.RightHandVisible;

            // Iterate backwards so we can safely remove while iterating
            for (int i = projectiles.Count - 1; i >= 0; i--)
            {
                var proj = projectiles[i];
                if (proj == null || !proj.Active) continue;

                Vector2 projPos = proj.Rect.anchoredPosition;

                // Only allow catching if the projectile is close to its final destination
                if (Vector2.Distance(projPos, proj.TargetCanvasPos) > catchTargetProximity)
                    continue;

                if (leftVisible && Vector2.Distance(leftCanvas, projPos) <= catchRadius)
                {
                    RegisterCatch(proj, HandSide.Left);
                    continue;
                }

                if (rightVisible && Vector2.Distance(rightCanvas, projPos) <= catchRadius)
                {
                    RegisterCatch(proj, HandSide.Right);
                }
            }
        }

        private void RegisterCatch(ProjectileInstance proj, HandSide side)
        {
            launcher.NotifyCaught(proj);
            OnCatch?.Invoke(side);
            OnCatchCS?.Invoke(side, proj);
        }

        private void HandleMiss(ProjectileInstance proj)
        {
            OnMiss?.Invoke();
            OnMissCS?.Invoke(proj);
            TriggerMissFlash();
        }

        private void TriggerMissFlash()
        {
            if (missFlashDuration <= 0f || missFlashImage == null) return;
            _flashTimer = missFlashDuration;
            missFlashImage.color = missFlashColor;
        }

        private void UpdateFlash()
        {
            if (_flashTimer <= 0f || missFlashImage == null) return;

            _flashTimer -= Time.deltaTime;
            float alpha = Mathf.Clamp01(_flashTimer / missFlashDuration) * missFlashColor.a;
            var c = missFlashImage.color;
            c.a = alpha;
            missFlashImage.color = c;
        }

        /// <summary>
        /// Converts Unity screen-space position (pixels, origin bottom-left) to
        /// canvas anchoredPosition (origin at canvas pivot).
        /// </summary>
        private Vector2 ScreenToCanvas(Vector2 screenPos)
        {
            if (targetCanvas == null) return screenPos;

            RectTransform canvasRect = targetCanvas.GetComponent<RectTransform>();
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRect,
                screenPos,
                targetCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null
                    : targetCanvas.worldCamera,
                out Vector2 localPoint);

            return localPoint;
        }

#if UNITY_EDITOR
        // Draw catch radius gizmos in Scene view for easier tuning
        private void OnDrawGizmosSelected()
        {
            if (bridge == null) return;
            Gizmos.color = Color.cyan;
            // Approximate: draw in world space at z=0 using screen→world
            DrawScreenCircleGizmo(bridge.LeftHandScreenPos,  catchRadius);
            DrawScreenCircleGizmo(bridge.RightHandScreenPos, catchRadius);
        }

        private static void DrawScreenCircleGizmo(Vector2 screenPos, float radiusPx)
        {
            if (Camera.main == null) return;
            Vector3 world = Camera.main.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, 1f));
            Gizmos.DrawWireSphere(world, radiusPx * 0.01f);
        }
#endif
    }
}
