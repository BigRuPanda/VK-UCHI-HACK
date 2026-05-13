using UnityEngine;
using HandTracking.Core;
using HandTracking.Gameplay;

namespace HandTracking.Visualization
{
    /// <summary>
    /// Optional particle effects for catch and miss events.
    ///
    /// Attach to the HandTrackingRoot GameObject alongside CatchZoneController.
    /// Assign particle system prefabs in the Inspector; leave them null to skip
    /// that effect entirely.
    ///
    /// All particle systems are spawned in world space at the position that
    /// corresponds to the hand / landing zone on screen.
    /// </summary>
    public class HandCatchParticles : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Header("Toggle")]
        [Tooltip("Master switch. Disable to turn off all particle effects without removing the component.")]
        public bool enableParticles = true;

        [Header("References")]
        [Tooltip("CatchZoneController whose events we subscribe to.")]
        public CatchZoneController catchZone;

        [Tooltip("HandTrackingBridge used to get hand screen positions.")]
        public HandTrackingBridge bridge;

        [Tooltip("Camera used to convert screen positions to world positions.")]
        public Camera renderCamera;

        [Header("Catch Particles")]
        [Tooltip("Particle system prefab played at the catching hand's position on a successful catch.")]
        public ParticleSystem catchParticlePrefab;

        [Tooltip("Scale multiplier applied to the catch particle system.")]
        public float catchParticleScale = 1f;

        [Header("Miss Particles")]
        [Tooltip("Particle system prefab played at the projectile's landing position on a miss.")]
        public ParticleSystem missParticlePrefab;

        [Tooltip("Scale multiplier applied to the miss particle system.")]
        public float missParticleScale = 0.7f;

        [Header("Pool")]
        [Tooltip("How many particle instances to pre-warm in the pool at Start.")]
        [Range(0, 10)]
        public int catchPoolSize = 4;

        [Tooltip("How many miss particle instances to pre-warm.")]
        [Range(0, 6)]
        public int missPoolSize  = 3;

        // ── Private ───────────────────────────────────────────────────────────────

        private ParticleSystem[] _catchPool;
        private ParticleSystem[] _missPool;
        private int _catchPoolIdx;
        private int _missPoolIdx;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (catchZone == null) catchZone = FindObjectOfType<CatchZoneController>();
            if (bridge    == null) bridge    = FindObjectOfType<HandTrackingBridge>();
            if (renderCamera == null) renderCamera = Camera.main;
        }

        private void Start()
        {
            // Subscribe to events
            if (catchZone != null)
            {
                catchZone.OnCatchCS += HandleCatch;
                catchZone.OnMissCS  += HandleMiss;
            }

            // Pre-warm pools
            _catchPool = BuildPool(catchParticlePrefab, catchPoolSize, "CatchFX");
            _missPool  = BuildPool(missParticlePrefab,  missPoolSize,  "MissFX");
        }

        private void OnDestroy()
        {
            if (catchZone != null)
            {
                catchZone.OnCatchCS -= HandleCatch;
                catchZone.OnMissCS  -= HandleMiss;
            }
        }

        // ── Event handlers ────────────────────────────────────────────────────────

        private void HandleCatch(HandSide side, ProjectileInstance proj)
        {
            if (!enableParticles || _catchPool == null) return;

            Vector2 screenPos = side == HandSide.Left
                ? bridge.LeftHandScreenPos
                : bridge.RightHandScreenPos;

            Vector3 worldPos = ScreenToWorld(screenPos);
            PlayFromPool(_catchPool, ref _catchPoolIdx, worldPos, catchParticleScale);
        }

        private void HandleMiss(ProjectileInstance proj)
        {
            if (!enableParticles || _missPool == null || proj?.Rect == null) return;

            // Convert canvas anchoredPosition → screen position → world position
            // We approximate by using the projectile's last known screen position
            Vector2 screenPos = CanvasToScreen(proj.Rect);
            Vector3 worldPos  = ScreenToWorld(screenPos);
            PlayFromPool(_missPool, ref _missPoolIdx, worldPos, missParticleScale);
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private ParticleSystem[] BuildPool(ParticleSystem prefab, int size, string label)
        {
            if (prefab == null || size <= 0) return null;

            var pool = new ParticleSystem[size];
            for (int i = 0; i < size; i++)
            {
                var ps = Instantiate(prefab, transform);
                ps.gameObject.name = $"{label}_{i}";
                ps.Stop();
                pool[i] = ps;
            }
            return pool;
        }

        private static void PlayFromPool(ParticleSystem[] pool, ref int idx, Vector3 worldPos, float scale)
        {
            if (pool == null || pool.Length == 0) return;

            var ps = pool[idx % pool.Length];
            idx = (idx + 1) % pool.Length;

            if (ps == null) return;

            ps.transform.position   = worldPos;
            ps.transform.localScale = Vector3.one * scale;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Play();
        }

        private Vector3 ScreenToWorld(Vector2 screenPos)
        {
            if (renderCamera == null) return Vector3.zero;
            // Place particles slightly in front of the camera
            float depth = renderCamera.nearClipPlane + 0.5f;
            return renderCamera.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, depth));
        }

        private Vector2 CanvasToScreen(RectTransform rect)
        {
            if (rect == null) return Vector2.zero;
            // RectTransform.position is already in screen space for Overlay canvases
            return RectTransformUtility.WorldToScreenPoint(null, rect.position);
        }
    }
}
