using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace HandTracking.Gameplay
{
    /// <summary>
    /// Animated warning marker that appears at a landing zone before a projectile arrives.
    ///
    /// Behaviour:
    ///   • Spawned / activated at the target canvas position.
    ///   • Plays a shrinking-ring animation over <warningDuration> seconds.
    ///   • The ring starts large and shrinks to zero exactly when the projectile lands.
    ///   • Automatically deactivates itself when the animation finishes or when
    ///     Dismiss() is called (e.g. on a successful catch).
    ///
    /// Requires: a RectTransform with an Image child for the ring graphic.
    /// </summary>
    public class ReticleMarker : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Header("Animation")]
        [Tooltip("Total duration of the shrink animation (should match projectile travel time).")]
        public float warningDuration = 1.5f;

        [Tooltip("Starting scale multiplier of the ring (e.g. 3 = starts 3× its normal size).")]
        public float ringStartScale = 3f;

        [Tooltip("Ending scale multiplier (0 = shrinks to nothing).")]
        public float ringEndScale = 0.8f;

        [Tooltip("Animation curve controlling the shrink easing.")]
        public AnimationCurve shrinkCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Tooltip("How long it takes for the marker to smoothly fade in when it appears.")]
        public float fadeInDuration = 0.3f;

        [Header("Appearance")]
        [Tooltip("Image component used as the ring graphic.")]
        public Image ringImage;

        [Tooltip("Starting colour of the ring.")]
        public Color startColor = new Color(1f, 0.9f, 0.2f, 0.9f);

        [Tooltip("Ending colour of the ring (typically more transparent).")]
        public Color endColor = new Color(1f, 0.2f, 0.2f, 0.4f);

        [Header("Pulse")]
        [Tooltip("Add a secondary pulsing scale on top of the shrink for extra ADHD flair.")]
        public bool enablePulse = true;

        [Tooltip("Pulse frequency in Hz.")]
        public float pulseFrequency = 3f;

        [Tooltip("Pulse amplitude as a fraction of current scale.")]
        [Range(0f, 0.3f)]
        public float pulseAmplitude = 0.08f;

        // ── Private ───────────────────────────────────────────────────────────────

        private RectTransform _rect;
        private Coroutine     _animCoroutine;
        private Vector3       _baseScale;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            _rect = GetComponent<RectTransform>();
            _baseScale = _rect != null ? _rect.localScale : Vector3.one;
        }

        private void OnEnable()
        {
            Play(warningDuration);
        }

        private void OnDisable()
        {
            StopAnimation();
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Place the marker at a canvas-space position and start the animation.
        /// </summary>
        public void Activate(Vector2 canvasPosition, float duration = -1f)
        {
            // Ensure _rect is initialized if Activate is called before Awake
            if (_rect == null)
            {
                _rect = GetComponent<RectTransform>();
                _baseScale = _rect != null ? _rect.localScale : Vector3.one;
            }

            if (_rect != null)
                _rect.anchoredPosition = canvasPosition;

            gameObject.SetActive(true);
            Play(duration > 0f ? duration : warningDuration);
        }

        /// <summary>
        /// Immediately dismiss the marker (e.g. projectile was caught).
        /// Plays a quick fade-out before deactivating.
        /// </summary>
        public void Dismiss()
        {
            StopAnimation();
            _animCoroutine = StartCoroutine(FadeOutAndDeactivate(0.15f));
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private void Play(float duration)
        {
            StopAnimation();
            _animCoroutine = StartCoroutine(AnimateRoutine(duration));
        }

        private void StopAnimation()
        {
            if (_animCoroutine != null)
            {
                StopCoroutine(_animCoroutine);
                _animCoroutine = null;
            }
        }

        private IEnumerator AnimateRoutine(float duration)
        {
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float curveT = shrinkCurve.Evaluate(t);

                // Scale: lerp from ringStartScale → ringEndScale
                float scale = Mathf.Lerp(ringStartScale, ringEndScale, curveT);

                // Smooth scale-in and fade-in during the first fadeInDuration seconds
                float fadeT = 1f;
                if (elapsed < fadeInDuration && fadeInDuration > 0f)
                {
                    fadeT = elapsed / fadeInDuration;
                    // Scale from 0 to the current target scale
                    scale = Mathf.Lerp(0f, scale, fadeT);
                }

                // Optional pulse on top
                if (enablePulse)
                    scale += Mathf.Sin(elapsed * pulseFrequency * Mathf.PI * 2f) * pulseAmplitude * scale;

                if (_rect != null)
                    _rect.localScale = _baseScale * scale;

                // Colour & Fade In
                if (ringImage != null)
                {
                    Color targetColor = Color.Lerp(startColor, endColor, curveT);
                    
                    if (fadeT < 1f)
                    {
                        targetColor.a = Mathf.Lerp(0f, targetColor.a, fadeT);
                    }
                    
                    ringImage.color = targetColor;
                }

                yield return null;
            }

            // Animation finished — projectile has landed
            gameObject.SetActive(false);
        }

        private IEnumerator FadeOutAndDeactivate(float fadeDuration)
        {
            if (ringImage == null) { gameObject.SetActive(false); yield break; }

            Color startC = ringImage.color;
            float elapsed = 0f;

            while (elapsed < fadeDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / fadeDuration);
                var c = startC;
                c.a = Mathf.Lerp(startC.a, 0f, t);
                ringImage.color = c;
                yield return null;
            }

            gameObject.SetActive(false);
        }
    }
}
