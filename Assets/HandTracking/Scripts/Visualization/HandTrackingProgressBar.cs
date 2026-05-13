using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace HandTracking.Visualization
{
    /// <summary>
    /// HandTrackingProgressBar — UI helper for the HandTracking module.
    ///
    /// Attach to a GameObject that has a <see cref="Slider"/> and wire it to
    /// <see cref="HandTrackingGameManager"/> via the Inspector.
    ///
    /// Features (no external dependencies — pure Unity coroutines):
    ///   • Smooth animated fill
    ///   • Colour gradient: red → yellow → green
    ///   • Flash + scale-pop animation on acceptance
    ///   • "Accepted" indicator reveal
    /// </summary>
    public class HandTrackingProgressBar : MonoBehaviour
    {
        // ─────────────────────────────────────────────────────────────────────
        // Inspector fields
        // ─────────────────────────────────────────────────────────────────────

        [Header("References")]
        [Tooltip("The Slider whose value (0–1) represents the progress.")]
        public Slider progressSlider;

        [Tooltip("Image component whose color is tinted by the score. " +
                 "Typically the fill area of the Slider.")]
        public Image fillImage;

        [Tooltip("Optional: GameObject shown when the game is won (e.g. a star/checkmark).")]
        public GameObject acceptedIndicator;

        [Tooltip("Optional: TextMeshPro label showing the numeric score (e.g. '72%').")]
        public TextMeshProUGUI scoreLabel;

        [Header("Colours")]
        public Color colorLow  = new Color(0.90f, 0.20f, 0.20f); // red
        public Color colorMid  = new Color(0.95f, 0.75f, 0.10f); // yellow
        public Color colorHigh = new Color(0.20f, 0.80f, 0.30f); // green

        [Header("Thresholds for colour bands")]
        [Tooltip("Score (0-1) where the bar turns yellow.")]
        [Range(0f, 1f)] public float midThreshold  = 0.70f;
        
        [Tooltip("Score (0-1) where the bar turns green.")]
        [Range(0f, 1f)] public float highThreshold = 1.0f;

        [Header("Animation")]
        [Tooltip("Duration (seconds) for the smooth fill tween.")]
        public float fillTweenDuration = 0.15f;

        [Tooltip("Duration (seconds) for the win flash animation.")]
        public float acceptFlashDuration = 0.4f;

        // ─────────────────────────────────────────────────────────────────────
        // Private state
        // ─────────────────────────────────────────────────────────────────────

        private bool _accepted;
        private Coroutine _fillCoroutine;
        private Coroutine _colorCoroutine;
        private Coroutine _acceptCoroutine;

        // ─────────────────────────────────────────────────────────────────────
        // Unity lifecycle
        // ─────────────────────────────────────────────────────────────────────

        private void Awake()
        {
            if (progressSlider == null)
                progressSlider = GetComponent<Slider>();

            ValidateReferences();

            if (acceptedIndicator != null)
                acceptedIndicator.SetActive(false);

            SetVisualScore(0f, instant: true);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Update the progress bar with the latest score (0 to 1).
        /// </summary>
        public void OnScoreUpdate(float score)
        {
            if (!isActiveAndEnabled) return;
            if (_accepted) return;
            SetVisualScore(score, instant: false);
        }

        /// <summary>
        /// Play the win animation.
        /// </summary>
        public void OnWinAnimation()
        {
            _accepted = true;
            if (_acceptCoroutine != null) StopCoroutine(_acceptCoroutine);
            _acceptCoroutine = StartCoroutine(PlayAcceptAnimation());
        }

        /// <summary>Reset the bar to zero (e.g. when the player retries).</summary>
        public void ResetBar()
        {
            _accepted = false;
            StopAllCoroutines();
            _fillCoroutine  = null;
            _colorCoroutine = null;
            _acceptCoroutine = null;

            if (acceptedIndicator != null)
                acceptedIndicator.SetActive(false);

            SetVisualScore(0f, instant: false);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Visual helpers
        // ─────────────────────────────────────────────────────────────────────

        private void ValidateReferences()
        {
            if (progressSlider == null)
            {
                Debug.LogWarning(
                    "[HandTrackingProgressBar] progressSlider is not assigned and no Slider was found on this GameObject. " +
                    "Assign Progress Slider in the Inspector.", this);
            }
            else
            {
                progressSlider.minValue = 0f;
                progressSlider.maxValue = 1f;
                progressSlider.wholeNumbers = false;
                progressSlider.interactable = false;
            }

            if (fillImage == null && progressSlider != null && progressSlider.fillRect != null)
                fillImage = progressSlider.fillRect.GetComponent<Image>();

            if (fillImage == null)
            {
                Debug.LogWarning(
                    "[HandTrackingProgressBar] fillImage is not assigned. " +
                    "The score value will still update, but the fill colour will not change.", this);
            }
        }

        private void SetVisualScore(float score, bool instant)
        {
            score = Mathf.Clamp01(score);

            // Update label
            if (scoreLabel != null)
                scoreLabel.text = Mathf.RoundToInt(score * 100f) + "%";

            // Slider fill
            if (progressSlider != null)
            {
                if (instant)
                {
                    progressSlider.value = score;
                }
                else
                {
                    if (_fillCoroutine != null) StopCoroutine(_fillCoroutine);
                    _fillCoroutine = StartCoroutine(
                        TweenFloat(progressSlider.value, score, fillTweenDuration,
                                   v => progressSlider.value = v));
                }
            }

            // Colour tint
            if (fillImage != null)
            {
                Color targetColor = ScoreToColor(score);
                if (instant)
                {
                    fillImage.color = targetColor;
                }
                else
                {
                    if (_colorCoroutine != null) StopCoroutine(_colorCoroutine);
                    _colorCoroutine = StartCoroutine(
                        TweenColor(fillImage.color, targetColor, fillTweenDuration,
                                   c => fillImage.color = c));
                }
            }
        }

        private Color ScoreToColor(float score)
        {
            if (score < midThreshold)
            {
                float t = score / Mathf.Max(midThreshold, 0.001f);
                return Color.Lerp(colorLow, colorMid, t);
            }
            else
            {
                float range = Mathf.Max(highThreshold - midThreshold, 0.001f);
                float t = (score - midThreshold) / range;
                return Color.Lerp(colorMid, colorHigh, Mathf.Clamp01(t));
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Acceptance animation (coroutine, no DOTween)
        // ─────────────────────────────────────────────────────────────────────

        private IEnumerator PlayAcceptAnimation()
        {
            // 1. Fill to 100%
            if (progressSlider != null)
            {
                yield return StartCoroutine(
                    TweenFloat(progressSlider.value, 1f, fillTweenDuration * 2f,
                               v => progressSlider.value = v));
            }

            // 2. Flash fill image white → green
            if (fillImage != null)
            {
                float half = acceptFlashDuration * 0.35f;
                yield return StartCoroutine(
                    TweenColor(fillImage.color, Color.white, half,
                               c => fillImage.color = c));
                yield return StartCoroutine(
                    TweenColor(Color.white, colorHigh, acceptFlashDuration * 0.65f,
                               c => fillImage.color = c));
            }

            // 3. Scale pop on the whole bar: 1 → 1.15 → 1
            Vector3 originalScale = transform.localScale;
            Vector3 bigScale = originalScale * 1.15f;
            float halfPop = acceptFlashDuration * 0.5f;

            yield return StartCoroutine(
                TweenVector3(originalScale, bigScale, halfPop,
                             s => transform.localScale = s, EaseOutBack));
            yield return StartCoroutine(
                TweenVector3(bigScale, originalScale, halfPop,
                             s => transform.localScale = s, EaseInBack));

            // 4. Show accepted indicator with scale-in
            if (acceptedIndicator != null)
            {
                acceptedIndicator.SetActive(true);
                acceptedIndicator.transform.localScale = Vector3.zero;
                yield return StartCoroutine(
                    TweenVector3(Vector3.zero, Vector3.one, acceptFlashDuration,
                                 s => acceptedIndicator.transform.localScale = s,
                                 EaseOutElastic));
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Generic coroutine tweeners
        // ─────────────────────────────────────────────────────────────────────

        private static IEnumerator TweenFloat(float from, float to, float duration,
                                              System.Action<float> setter)
        {
            if (duration <= 0f)
            {
                setter(to);
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                setter(Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration)));
                yield return null;
            }
            setter(to);
        }

        private static IEnumerator TweenColor(Color from, Color to, float duration,
                                              System.Action<Color> setter)
        {
            if (duration <= 0f)
            {
                setter(to);
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                setter(Color.Lerp(from, to, Mathf.Clamp01(elapsed / duration)));
                yield return null;
            }
            setter(to);
        }

        private static IEnumerator TweenVector3(Vector3 from, Vector3 to, float duration,
                                                System.Action<Vector3> setter,
                                                System.Func<float, float> easing = null)
        {
            if (duration <= 0f)
            {
                setter(to);
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float easedT = easing != null ? easing(t) : t;
                setter(Vector3.LerpUnclamped(from, to, easedT));
                yield return null;
            }
            setter(to);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Easing functions (no external library needed)
        // ─────────────────────────────────────────────────────────────────────

        private static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
        }

        private static float EaseInBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            return c3 * t * t * t - c1 * t * t;
        }

        private static float EaseOutElastic(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            const float c4 = (2f * Mathf.PI) / 3f;
            return Mathf.Pow(2f, -10f * t) * Mathf.Sin((t * 10f - 0.75f) * c4) + 1f;
        }
    }
}
