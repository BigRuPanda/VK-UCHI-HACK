using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HandTracking.Gameplay
{
    // ─── Projectile data ─────────────────────────────────────────────────────────

    /// <summary>Runtime state for a single in-flight projectile.</summary>
    public class ProjectileInstance
    {
        public GameObject   Root;
        public RectTransform Rect;
        public Image        Image;
        public Vector2      StartCanvasPos;
        public Vector2      TargetCanvasPos;
        public float        TravelDuration;
        public float        Elapsed;
        public bool         Active;
        public int          ZoneIndex;          // which landing zone this targets
        public ReticleMarker Reticle;           // associated warning marker
    }

    // ─── Launcher ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spawns projectiles from the screen centre, sends them toward one of the
    /// configurable landing zones, and fires events when they arrive.
    ///
    /// Movement uses a quadratic Bézier curve (start → arc peak → target) so
    /// projectiles travel in a natural arc rather than a straight line.
    ///
    /// No DOTween dependency — pure coroutine + lerp so the module stays
    /// self-contained.
    /// </summary>
    public class ProjectileLauncher : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Header("References")]
        [Tooltip("Canvas that contains all UI elements.")]
        public Canvas targetCanvas;

        [Tooltip("Prefab for a single projectile. Must have a RectTransform and Image.")]
        public GameObject projectilePrefab;

        [Tooltip("Prefab for the reticle warning marker.")]
        public GameObject reticlePrefab;

        [Tooltip("Parent RectTransform under which projectiles are instantiated.")]
        public RectTransform projectileContainer;

        [Tooltip("Optional moving source for projectiles. If null, spawns from canvas centre.")]
        public RectTransform spawnSource;

        [Header("Landing Zones — Procedural (alternates Left/Right each spawn)")]
        [Tooltip("Leftmost X boundary of the left zone (canvas pixels, negative = left of centre).")]
        public float leftZoneXMin = -800f;

        [Tooltip("Rightmost X boundary of the left zone (canvas pixels).")]
        public float leftZoneXMax = -150f;

        [Tooltip("Leftmost X boundary of the right zone (canvas pixels).")]
        public float rightZoneXMin = 150f;

        [Tooltip("Rightmost X boundary of the right zone (canvas pixels).")]
        public float rightZoneXMax = 800f;

        [Tooltip("Minimum Y of the landing zone band (canvas pixels, negative = below centre).")]
        public float zoneYMin = -400f;

        [Tooltip("Maximum Y of the landing zone band (canvas pixels).")]
        public float zoneYMax = -150f;

        [Header("Spawn Settings")]
        [Tooltip("Seconds between consecutive projectile spawns.")]
        [Range(0.5f, 10f)]
        public float spawnInterval = 2f;

        [Tooltip("Maximum number of projectiles in flight at the same time.")]
        [Range(1, 10)]
        public int maxActiveProjectiles = 3;

        [Tooltip("How long (seconds) a projectile takes to travel from spawn to target.")]
        [Range(0.5f, 5f)]
        public float travelDuration = 2f;

        [Tooltip("How many seconds before the projectile arrives that the reticle appears.")]
        [Range(0.1f, 4f)]
        public float warningLeadTime = 1.5f;

        [Header("Arc")]
        [Tooltip("Height of the Bézier arc peak above the straight line, in canvas pixels.")]
        public float arcHeight = 200f;

        [Tooltip("Horizontal spread of the arc control point (0 = straight up).")]
        [Range(-1f, 1f)]
        public float arcHorizontalBias = 0f;

        [Header("Projectile Appearance")]
        [Tooltip("Starting scale of the projectile (small, far away).")]
        public float startScale = 0.3f;

        [Tooltip("Ending scale of the projectile (large, close up).")]
        public float endScale = 1.4f;

        [Tooltip("Animation curve for the scale growth.")]
        public AnimationCurve scaleCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Tooltip("Sprites to randomly pick from for each projectile (leave empty to use prefab default).")]
        public List<Sprite> projectileSprites = new List<Sprite>();

        // ── Events ───────────────────────────────────────────────────────────────

        /// <summary>Fired when a projectile reaches its target without being caught.</summary>
        public event Action<ProjectileInstance> OnProjectileMissed;

        /// <summary>Fired when a projectile is successfully caught (called by CatchZoneController).</summary>
        public event Action<ProjectileInstance> OnProjectileCaught;

        // ── Public state ──────────────────────────────────────────────────────────

        public IReadOnlyList<ProjectileInstance> ActiveProjectiles => _active;

        // ── Private ───────────────────────────────────────────────────────────────

        private readonly List<ProjectileInstance> _active = new List<ProjectileInstance>();
        private readonly List<ProjectileInstance> _pool   = new List<ProjectileInstance>();
        private bool   _running;
        private float  _spawnTimer;
        private bool   _nextSideIsLeft = true;   // strict alternation flag

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (targetCanvas == null)
                targetCanvas = GetComponentInParent<Canvas>();

            if (projectileContainer == null && targetCanvas != null)
                projectileContainer = targetCanvas.GetComponent<RectTransform>();
        }

        private void Update()
        {
            if (!_running) return;

            _spawnTimer -= Time.deltaTime;
            if (_spawnTimer <= 0f && _active.Count < maxActiveProjectiles)
            {
                _spawnTimer = spawnInterval;
                SpawnProjectile();
            }

            AdvanceProjectiles();
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>Begin spawning projectiles.</summary>
        public void StartLaunching()
        {
            _running    = true;
            _spawnTimer = 0.5f;   // small initial delay
        }

        /// <summary>Stop spawning and clear all active projectiles.</summary>
        public void StopLaunching()
        {
            _running = false;
            foreach (var p in _active)
                ReturnToPool(p);
            _active.Clear();
        }

        /// <summary>
        /// Called by CatchZoneController when a hand catches a projectile.
        /// </summary>
        public void NotifyCaught(ProjectileInstance proj)
        {
            if (!_active.Contains(proj)) return;
            proj.Reticle?.Dismiss();
            _active.Remove(proj);
            ReturnToPool(proj);
            OnProjectileCaught?.Invoke(proj);
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private void SpawnProjectile()
        {
            // Capture side BEFORE PickNextLandingPos flips the flag
            bool isLeft = _nextSideIsLeft;
            Vector2 targetPos = PickNextLandingPos();
            Vector2 startPos = spawnSource != null ? spawnSource.anchoredPosition : GetCanvasCentre();

            ProjectileInstance proj = GetFromPool();
            proj.StartCanvasPos  = startPos;
            proj.TargetCanvasPos = targetPos;
            proj.TravelDuration  = travelDuration;
            proj.Elapsed         = 0f;
            proj.Active          = true;
            proj.ZoneIndex       = isLeft ? 0 : 1;   // 0 = left, 1 = right

            // Random sprite
            if (projectileSprites.Count > 0 && proj.Image != null)
                proj.Image.sprite = projectileSprites[UnityEngine.Random.Range(0, projectileSprites.Count)];

            proj.Root.SetActive(true);
            proj.Rect.anchoredPosition = startPos;
            proj.Rect.localScale       = Vector3.one * startScale;

            // Spawn reticle immediately (or after delay if warningLeadTime < travelDuration)
            float reticleDelay = Mathf.Max(0f, travelDuration - warningLeadTime);
            StartCoroutine(SpawnReticleDelayed(proj, targetPos, reticleDelay));

            _active.Add(proj);
        }

        private IEnumerator SpawnReticleDelayed(ProjectileInstance proj, Vector2 canvasPos, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (!proj.Active) yield break;

            if (reticlePrefab != null)
            {
                GameObject reticleGO = Instantiate(reticlePrefab, projectileContainer);
                var marker = reticleGO.GetComponent<ReticleMarker>();
                if (marker != null)
                {
                    marker.Activate(canvasPos, warningLeadTime);
                    proj.Reticle = marker;
                }
            }
        }

        private void AdvanceProjectiles()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var proj = _active[i];
                proj.Elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(proj.Elapsed / proj.TravelDuration);

                // Quadratic Bézier: P(t) = (1-t)²·P0 + 2(1-t)t·P1 + t²·P2
                Vector2 p0 = proj.StartCanvasPos;
                Vector2 p2 = proj.TargetCanvasPos;
                Vector2 mid = (p0 + p2) * 0.5f;
                Vector2 p1 = mid + new Vector2(arcHorizontalBias * 200f, arcHeight);

                float u = 1f - t;
                Vector2 pos = u * u * p0 + 2f * u * t * p1 + t * t * p2;
                proj.Rect.anchoredPosition = pos;

                // Scale
                float scaleT = scaleCurve.Evaluate(t);
                float scale  = Mathf.Lerp(startScale, endScale, scaleT);
                proj.Rect.localScale = Vector3.one * scale;

                // Arrived?
                if (t >= 1f)
                {
                    proj.Active = false;
                    proj.Reticle?.gameObject.SetActive(false);
                    _active.RemoveAt(i);
                    ReturnToPool(proj);
                    OnProjectileMissed?.Invoke(proj);
                }
            }
        }

        // ── Object pool ───────────────────────────────────────────────────────────

        private ProjectileInstance GetFromPool()
        {
            foreach (var p in _pool)
            {
                if (!p.Active)
                {
                    _pool.Remove(p);
                    return p;
                }
            }
            return CreateProjectileInstance();
        }

        private void ReturnToPool(ProjectileInstance proj)
        {
            proj.Active = false;
            proj.Root.SetActive(false);
            if (!_pool.Contains(proj))
                _pool.Add(proj);
        }

        private ProjectileInstance CreateProjectileInstance()
        {
            GameObject go = projectilePrefab != null
                ? Instantiate(projectilePrefab, projectileContainer)
                : CreateDefaultProjectileGO();

            var inst = new ProjectileInstance
            {
                Root  = go,
                Rect  = go.GetComponent<RectTransform>(),
                Image = go.GetComponent<Image>(),
            };
            go.SetActive(false);
            return inst;
        }

        private GameObject CreateDefaultProjectileGO()
        {
            var go = new GameObject("Projectile", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(projectileContainer, false);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(80f, 80f);
            return go;
        }

        private Vector2 GetCanvasCentre()
        {
            // anchoredPosition (0,0) = canvas centre when pivot is (0.5, 0.5)
            return Vector2.zero;
        }

        /// <summary>
        /// Returns a random canvas-space position in the current side's X band,
        /// then flips the side for the next call.
        /// </summary>
        private Vector2 PickNextLandingPos()
        {
            float x, y;
            y = UnityEngine.Random.Range(zoneYMin, zoneYMax);

            if (_nextSideIsLeft)
                x = UnityEngine.Random.Range(leftZoneXMin, leftZoneXMax);
            else
                x = UnityEngine.Random.Range(rightZoneXMin, rightZoneXMax);

            _nextSideIsLeft = !_nextSideIsLeft;   // strict alternation
            return new Vector2(x, y);
        }
    }
}
