using UnityEngine;
using UnityEngine.UI;
using HandTracking.Core;

namespace HandTracking.Visualization
{
    /// <summary>
    /// Moves two hand Image UI elements to match the player's real hand positions
    /// reported by HandTrackingBridge (first-person / selfie view).
    ///
    /// Attach this to a GameObject that is a child of a Screen Space – Overlay Canvas.
    /// The two hand RectTransforms must also live inside that Canvas.
    /// </summary>
    public class HandVisualizer : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Header("References")]
        [Tooltip("The HandTrackingBridge that supplies hand positions.")]
        public HandTrackingBridge bridge;

        [Header("Hand Images")]
        [Tooltip("RectTransform for the LEFT hand sprite (player's left).")]
        public RectTransform leftHandRect;

        [Tooltip("RectTransform for the RIGHT hand sprite (player's right).")]
        public RectTransform rightHandRect;

        [Tooltip("Image component on the left hand (for show/hide and sprite swap).")]
        public Image leftHandImage;

        [Tooltip("Image component on the right hand.")]
        public Image rightHandImage;

        [Header("Sprites")]
        [Tooltip("Sprite shown for the left hand.")]
        public Sprite leftHandSprite;

        [Tooltip("Sprite shown for the right hand.")]
        public Sprite rightHandSprite;

        [Header("Appearance")]
        [Tooltip("Uniform scale applied to both hand images.")]
        public float handScale = 1f;

        [Tooltip("Alpha when the hand is visible.")]
        [Range(0f, 1f)]
        public float visibleAlpha = 1f;

        [Tooltip("Alpha when the hand is NOT detected (0 = fully hidden).")]
        [Range(0f, 1f)]
        public float hiddenAlpha = 0f;

        [Tooltip("How quickly the alpha fades in/out (units per second).")]
        public float alphaFadeSpeed = 8f;

        [Header("Canvas Reference")]
        [Tooltip("The Canvas this visualizer lives in. Used for coordinate conversion.")]
        public Canvas parentCanvas;

        // ── Private ───────────────────────────────────────────────────────────────

        private float _leftAlpha;
        private float _rightAlpha;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (bridge == null)
                bridge = FindObjectOfType<HandTrackingBridge>();

            if (parentCanvas == null)
                parentCanvas = GetComponentInParent<Canvas>();

            // Apply sprites
            if (leftHandImage  != null && leftHandSprite  != null) leftHandImage.sprite  = leftHandSprite;
            if (rightHandImage != null && rightHandSprite != null) rightHandImage.sprite = rightHandSprite;

            // Apply scale
            ApplyScale();
        }

        private void Update()
        {
            if (bridge == null) return;

            UpdateHand(
                bridge.LeftHandScreenPos,
                bridge.LeftHandVisible,
                leftHandRect,
                leftHandImage,
                ref _leftAlpha);

            UpdateHand(
                bridge.RightHandScreenPos,
                bridge.RightHandVisible,
                rightHandRect,
                rightHandImage,
                ref _rightAlpha);
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>Change the hand scale at runtime.</summary>
        public void SetHandScale(float scale)
        {
            handScale = scale;
            ApplyScale();
        }

        /// <summary>Swap the sprite for one hand at runtime (e.g. for a "catching" animation).</summary>
        public void SetHandSprite(HandSide side, Sprite sprite)
        {
            if (side == HandSide.Left  && leftHandImage  != null) leftHandImage.sprite  = sprite;
            if (side == HandSide.Right && rightHandImage != null) rightHandImage.sprite = sprite;
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private void UpdateHand(
            Vector2 screenPos,
            bool    visible,
            RectTransform rect,
            Image   image,
            ref float currentAlpha)
        {
            if (rect == null) return;

            // Move
            if (visible)
            {
                rect.anchoredPosition = ScreenToCanvasPos(screenPos);
            }

            // Fade alpha
            float targetAlpha = visible ? visibleAlpha : hiddenAlpha;
            currentAlpha = Mathf.MoveTowards(currentAlpha, targetAlpha, alphaFadeSpeed * Time.deltaTime);

            if (image != null)
            {
                var c = image.color;
                c.a = currentAlpha;
                image.color = c;
            }
        }

        /// <summary>
        /// Converts Unity screen-space coords (pixels, origin bottom-left) to
        /// Canvas anchoredPosition (origin at Canvas pivot, typically centre).
        /// Works for Screen Space – Overlay and Screen Space – Camera canvases.
        /// </summary>
        private Vector2 ScreenToCanvasPos(Vector2 screenPos)
        {
            if (parentCanvas == null) return screenPos;

            RectTransform canvasRect = parentCanvas.GetComponent<RectTransform>();

            // RectTransformUtility handles both Overlay and Camera modes
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRect,
                screenPos,
                parentCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : parentCanvas.worldCamera,
                out Vector2 localPoint);

            return localPoint;
        }

        private void ApplyScale()
        {
            Vector3 s = Vector3.one * handScale;
            if (leftHandRect  != null) leftHandRect.localScale  = s;
            if (rightHandRect != null) rightHandRect.localScale = s;
        }
    }
}
