using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReadingModule
{
    /// <summary>
    /// Visual state of a single word in the reading module.
    /// </summary>
    public enum WordState
    {
        /// <summary>Not yet reached — neutral color, gentle float.</summary>
        Idle,
        /// <summary>Currently being read — FULLY STATIC so the child can focus.</summary>
        Active,
        /// <summary>Read correctly — green + scale punch + particles.</summary>
        Correct,
        /// <summary>Read incorrectly — red + horizontal shake, then reverts to Active.</summary>
        Error
    }

    /// <summary>
    /// Per-word animated token for the reading module.
    ///
    /// ┌─────────────────────────────────────────────────────────────────┐
    /// │  IMPORTANT — Two-layer prefab structure                         │
    /// │                                                                 │
    /// │  Root (this GameObject)                                         │
    /// │    • RectTransform  — managed by HorizontalLayoutGroup          │
    /// │    • LayoutElement  — preferredWidth set per-word by code       │
    /// │    • WordToken.cs   — this script                               │
    /// │    • NO position/scale DOTween here (Layout Group owns it)      │
    /// │                                                                 │
    /// │  Child "WordVisual"                                             │
    /// │    • RectTransform  — anchors stretch to fill parent            │
    /// │    • TextMeshProUGUI — the word text                            │
    /// │    • ALL DOTween animations run on this child's RectTransform   │
    /// │      and on the TMP component                                   │
    /// └─────────────────────────────────────────────────────────────────┘
    ///
    /// HorizontalLayoutGroup on the container MUST have:
    ///   Control Child Size Width  = OFF  ← critical, otherwise all slots equal width
    ///   Control Child Size Height = OFF  (or ON — doesn't affect width)
    ///   Child Force Expand Width  = OFF  ← critical
    ///
    /// Each word's slot width is driven by LayoutElement.preferredWidth,
    /// which is set from TMP.GetPreferredValues() in Initialize().
    ///
    /// Animation states:
    ///   Idle    → gentle vertical float on WordVisual (different phase per word)
    ///   Active  → FULLY STATIC — kill tweens, reset WordVisual transform
    ///   Correct → DOPunchScale + color flash to green + optional particles
    ///   Error   → instant red + DOShakePosition on WordVisual → reverts to Active
    ///
    /// Prefab requirements:
    ///   • This script on the root GameObject
    ///   • A child named "WordVisual" with TextMeshProUGUI (assign in Inspector)
    ///   • LayoutElement on the root (auto-added if missing)
    ///   • DoTween imported in the project
    ///   • (Optional) ParticleSystem child of WordVisual for sparkle effect
    /// </summary>
    public class WordToken : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────────

        [Header("References")]
        [Tooltip("The child RectTransform that holds the TMP label. " +
                 "All animations run on this transform, NOT on the root. " +
                 "Auto-found as first child if null.")]
        [SerializeField] private RectTransform visualTransform;

        [Tooltip("TextMeshProUGUI that displays the word. Auto-found inside visualTransform if null.")]
        [SerializeField] private TextMeshProUGUI label;

        [Tooltip("Optional ParticleSystem that plays on correct read. Leave null to skip.")]
        [SerializeField] private ParticleSystem correctParticles;

        [Header("Colors")]
        [SerializeField] private Color colorIdle    = new Color(0.72f, 0.72f, 0.72f, 1f);
        [SerializeField] private Color colorActive  = Color.white;
        [SerializeField] private Color colorCorrect = new Color(0.298f, 0.686f, 0.314f, 1f); // #4CAF50
        [SerializeField] private Color colorError   = new Color(0.957f, 0.263f, 0.212f, 1f); // #F44336

        [Header("Entry Animation")]
        [Tooltip("Duration of the fly-in animation per word.")]
        [SerializeField] private float entryDuration = 0.30f;
        [Tooltip("Vertical offset (pixels) words start from below their final position.")]
        [SerializeField] private float entryYOffset  = -24f;

        [Header("Idle Float")]
        [Tooltip("Vertical amplitude of the idle float in pixels.")]
        [SerializeField] private float floatAmplitude = 4f;
        [Tooltip("Base duration of one float half-cycle. Per-word phase offset is added on top.")]
        [SerializeField] private float floatDuration  = 1.15f;

        [Header("Correct Animation")]
        [Tooltip("Scale punch magnitude on correct read.")]
        [SerializeField] private float correctPunch    = 0.35f;
        [Tooltip("Duration of the scale punch.")]
        [SerializeField] private float correctDuration = 0.30f;
        [Tooltip("Vibrato of the scale punch.")]
        [SerializeField] private int   correctVibrato  = 6;

        [Header("Error Animation")]
        [Tooltip("Horizontal shake amplitude in pixels on error.")]
        [SerializeField] private float shakeAmplitude = 18f;
        [Tooltip("Duration of the error shake.")]
        [SerializeField] private float shakeDuration  = 0.40f;

        [Header("Sentence Complete Wave")]
        [Tooltip("Scale punch magnitude for the sentence-complete cascade wave.")]
        [SerializeField] private float wavePunch    = 0.20f;
        [Tooltip("Duration of the wave punch per word.")]
        [SerializeField] private float waveDuration = 0.25f;

        // ── Events ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired when the error shake animation completes.
        /// ReadingController subscribes to this to resume the word queue.
        /// </summary>
        public event Action OnErrorShakeComplete;

        // ── Private ───────────────────────────────────────────────────────────────

        private WordState    _currentState = WordState.Idle;
        private Tween        _activeTween;
        private int          _wordIndex;
        private LayoutElement _layoutElement;

        // ── Unity Lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            // Auto-find visualTransform as first child if not assigned
            if (visualTransform == null && transform.childCount > 0)
                visualTransform = transform.GetChild(0) as RectTransform;

            if (visualTransform == null)
                Debug.LogError("[WordToken] 'visualTransform' not found. " +
                               "Create a child GameObject named 'WordVisual' and assign it.", this);

            // Auto-find label inside visualTransform
            if (label == null && visualTransform != null)
                label = visualTransform.GetComponentInChildren<TextMeshProUGUI>();

            if (label == null)
                Debug.LogError("[WordToken] No TextMeshProUGUI found. " +
                               "Add TextMeshProUGUI to the WordVisual child.", this);

            // Ensure a LayoutElement exists on the root.
            // HorizontalLayoutGroup must have Control Child Size Width = OFF
            // so that this LayoutElement.preferredWidth drives the slot width.
            _layoutElement = GetComponent<LayoutElement>();
            if (_layoutElement == null)
                _layoutElement = gameObject.AddComponent<LayoutElement>();
        }

        private void OnDestroy()
        {
            _activeTween?.Kill();
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Set the word text and word index (used for stagger phase offset).
        /// Call once immediately after Instantiate, before PlayEntry.
        /// </summary>
        public void Initialize(string word, int index)
        {
            _wordIndex = index;

            if (label != null)
                label.text = word;

            // ── IMPORTANT: measure layout size BEFORE collapsing the visual ──
            // GetPreferredValues() returns 0 when the object's scale is 0,
            // so UpdateLayoutSize() MUST be called while visualTransform is
            // still at scale=1 (its default state in the prefab).
            UpdateLayoutSize();

            // Now hide the visual for the entry animation.
            // Only touch the VISUAL child, never the root RectTransform.
            if (label != null)
                label.color = new Color(colorIdle.r, colorIdle.g, colorIdle.b, 0f);

            if (visualTransform != null)
            {
                visualTransform.localScale    = Vector3.zero;
                visualTransform.localPosition = new Vector3(0f, entryYOffset, 0f);
            }
        }

        /// <summary>
        /// Play the fly-in entry animation with a stagger delay.
        /// After the animation completes the word transitions to Idle state.
        /// Animations run on visualTransform — root is untouched (Layout Group owns it).
        /// </summary>
        public void PlayEntry(float delay)
        {
            if (visualTransform == null) return;

            _activeTween?.Kill();

            Sequence seq = DOTween.Sequence();
            seq.SetDelay(delay);
            seq.Append(visualTransform.DOScale(Vector3.one, entryDuration).SetEase(Ease.OutBack));
            seq.Join(visualTransform.DOLocalMoveY(0f, entryDuration).SetEase(Ease.OutCubic));
            if (label != null)
                seq.Join(label.DOFade(1f, entryDuration * 0.7f).SetEase(Ease.InQuad));
            seq.OnComplete(() =>
            {
                visualTransform.localPosition = Vector3.zero;
                visualTransform.localScale    = Vector3.one;
                SetState(WordState.Idle);
            });

            _activeTween = seq;
        }

        /// <summary>
        /// Transition to a new visual state.
        /// Active state is guaranteed to be fully static (no tweens running).
        /// Error state automatically fires OnErrorShakeComplete when done.
        /// </summary>
        public void SetState(WordState state)
        {
            _currentState = state;

            switch (state)
            {
                case WordState.Idle:    PlayIdleFloat();    break;
                case WordState.Active:  PlayActiveStatic(); break;
                case WordState.Correct: PlayCorrect();      break;
                case WordState.Error:   PlayError();        break;
            }
        }

        /// <summary>
        /// Play the sentence-complete cascade wave with a per-word delay.
        /// Called by ReadingController after all words are read correctly.
        /// </summary>
        public void PlaySentenceCompleteWave(float delay)
        {
            if (visualTransform == null) return;

            _activeTween?.Kill();
            visualTransform.localScale = Vector3.one;

            Sequence seq = DOTween.Sequence();
            seq.SetDelay(delay);
            seq.Append(visualTransform.DOPunchScale(
                Vector3.one * wavePunch, waveDuration, vibrato: 4, elasticity: 0.6f));
            if (label != null)
                seq.Join(label.DOColor(colorCorrect, waveDuration * 0.4f));

            _activeTween = seq;
        }

        // ── Private Animation Helpers ─────────────────────────────────────────────

        /// <summary>
        /// Idle: gentle vertical float on visualTransform with per-word phase offset.
        /// Root RectTransform is never touched — Layout Group owns it.
        /// </summary>
        private void PlayIdleFloat()
        {
            if (visualTransform == null) return;

            _activeTween?.Kill();

            // Reset visual to baseline
            visualTransform.localScale    = Vector3.one;
            visualTransform.localPosition = Vector3.zero;
            if (label != null)
                label.color = colorIdle;

            // Per-word phase offset so words don't float in sync
            float phaseOffset = _wordIndex * 0.28f;
            float duration    = floatDuration + (_wordIndex % 3) * 0.12f;

            Sequence seq = DOTween.Sequence();
            if (phaseOffset > 0f)
                seq.AppendInterval(phaseOffset);
            seq.Append(
                visualTransform.DOLocalMoveY(floatAmplitude, duration)
                               .SetEase(Ease.InOutSine)
                               .SetLoops(-1, LoopType.Yoyo)
            );

            _activeTween = seq;
        }

        /// <summary>
        /// Active: FULLY STATIC. Kill all tweens, reset visualTransform to baseline.
        /// The child needs to read this word — no distractions.
        /// </summary>
        private void PlayActiveStatic()
        {
            _activeTween?.Kill();

            if (visualTransform != null)
            {
                visualTransform.localPosition = Vector3.zero;
                visualTransform.localScale    = Vector3.one;
            }

            if (label != null)
            {
                label.color     = colorActive;
                label.fontStyle = FontStyles.Bold;
            }
        }

        /// <summary>
        /// Correct: scale punch + color flash to green + optional particles.
        /// Word stays green and static after the punch completes.
        /// </summary>
        private void PlayCorrect()
        {
            if (visualTransform == null) return;

            _activeTween?.Kill();

            if (label != null)
                label.fontStyle = FontStyles.Normal;

            visualTransform.localScale    = Vector3.one;
            visualTransform.localPosition = Vector3.zero;

            Sequence seq = DOTween.Sequence();
            seq.Append(
                visualTransform.DOPunchScale(
                    Vector3.one * correctPunch, correctDuration,
                    vibrato: correctVibrato, elasticity: 0.8f)
                .SetEase(Ease.OutElastic)
            );
            if (label != null)
                seq.Join(label.DOColor(colorCorrect, correctDuration * 0.5f).SetEase(Ease.OutQuad));
            seq.OnComplete(() =>
            {
                visualTransform.localScale    = Vector3.one;
                visualTransform.localPosition = Vector3.zero;
                if (correctParticles != null)
                    correctParticles.Play();
            });

            _activeTween = seq;
        }

        /// <summary>
        /// Error: instant red + horizontal shake on visualTransform (this word only) →
        /// reverts to Active and fires OnErrorShakeComplete.
        /// </summary>
        private void PlayError()
        {
            if (visualTransform == null) return;

            _activeTween?.Kill();

            if (label != null)
                label.color = colorError;

            _activeTween = visualTransform
                .DOShakePosition(
                    duration:   shakeDuration,
                    strength:   new Vector3(shakeAmplitude, 0f, 0f),
                    vibrato:    20,
                    randomness: 0f,
                    snapping:   false,
                    fadeOut:    true)
                .SetEase(Ease.Linear)
                .OnComplete(() =>
                {
                    visualTransform.localPosition = Vector3.zero;
                    SetState(WordState.Active);
                    OnErrorShakeComplete?.Invoke();
                });
        }

        // ── Layout Helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// Measure the TMP label's preferred width for the current text and
        /// write it into the root LayoutElement so HorizontalLayoutGroup
        /// allocates exactly the right slot width for this word.
        ///
        /// Requires HorizontalLayoutGroup on the container:
        ///   Control Child Size Width  = OFF  ← must be off
        ///   Child Force Expand Width  = OFF  ← must be off
        /// </summary>
        private void UpdateLayoutSize()
        {
            if (label == null || _layoutElement == null) return;

            // GetPreferredValues returns the size TMP needs to render the text
            // on a single line (no wrapping). Call this while visualTransform
            // is at scale=1 — if scale=0 TMP returns 0 and all slots collapse.
            Vector2 preferred = label.GetPreferredValues(
                label.text,
                float.PositiveInfinity,
                float.PositiveInfinity);

            float w = preferred.x + 8f;  // 8px left+right padding
            float h = preferred.y;

            // Set both preferredWidth AND minWidth so HorizontalLayoutGroup
            // respects the size even in edge-case layout configurations.
            _layoutElement.minWidth       = w;
            _layoutElement.preferredWidth = w;
            _layoutElement.preferredHeight = h;
        }
    }
}
