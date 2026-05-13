using System.Collections;
using UnityEngine;

/// <summary>
/// Pure animation controller for the pirate scene.
/// No buttons, no panel sequence, no hero/book logic.
/// Put this script on PirateSceneRoot, drag scene parts into the fields,
/// and the animation starts automatically when the object becomes active.
/// </summary>
public class PirateSceneAutoAnimator : MonoBehaviour
{
    [Header("Scene Parts")]
    [Tooltip("Static sun/sky background. This does NOT move.")]
    public Transform staticBackground;

    [Tooltip("Optional moving background layer that should move together with the waves. Do NOT put the sun here.")]
    public Transform movingBackground;

    public Transform ship;
    public Transform waves;
    public Transform sky;
    public Transform birds;

    [Header("Optional Fade Groups")]
    public CanvasGroup shipCanvasGroup;
    public CanvasGroup wavesCanvasGroup;
    public CanvasGroup skyCanvasGroup;
    public CanvasGroup birdsCanvasGroup;

    [Header("Intro")]
    public bool playIntro = true;
    public float introDuration = 1.2f;
    public float shipStartOffsetY = -80f;
    public float wavesStartOffsetY = -40f;
    public float skyStartOffsetY = 20f;
    public float birdsStartOffsetX = -120f;

    [Header("Ship Idle Motion")]
    public float shipBobHeight = 14f;
    public float shipBobSpeed = 1.15f;
    public float shipRockAngle = 3.2f;

    [Header("Waves Motion")]
    public float wavesMoveX = 70f;
    public float wavesBobHeight = 8f;
    public float wavesSpeed = 0.7f;

    [Header("Sky / Clouds Motion")]
    public float skyMoveX = 45f;
    public float skySpeed = 0.18f;

    [Header("Birds Motion")]
    public float birdsMoveX = 120f;
    public float birdsMoveY = 18f;
    public float birdsSpeed = 0.45f;

    private Vector3 staticBackgroundStartPos;
    private Vector3 movingBackgroundStartPos;
    private Vector3 shipStartPos;
    private Vector3 wavesStartPos;
    private Vector3 skyStartPos;
    private Vector3 birdsStartPos;

    private Coroutine routine;

    private void Awake()
    {
        CacheStartPositions();
    }

    private void OnEnable()
    {
        Play();
    }

    private void OnDisable()
    {
        if (routine != null)
        {
            StopCoroutine(routine);
            routine = null;
        }
    }

    public void Play()
    {
        if (routine != null)
            StopCoroutine(routine);

        CacheStartPositions();
        routine = StartCoroutine(AnimationRoutine());
    }

    public void StopAndReset()
    {
        if (routine != null)
            StopCoroutine(routine);

        routine = null;
        ResetTransforms();
        SetAlpha(shipCanvasGroup, 1f);
        SetAlpha(wavesCanvasGroup, 1f);
        SetAlpha(skyCanvasGroup, 1f);
        SetAlpha(birdsCanvasGroup, 1f);
    }

    private IEnumerator AnimationRoutine()
    {
        if (playIntro)
            yield return StartCoroutine(IntroRoutine());

        yield return StartCoroutine(IdleRoutine());
    }

    private IEnumerator IntroRoutine()
    {
        SetAlpha(shipCanvasGroup, 0f);
        SetAlpha(wavesCanvasGroup, 0f);
        SetAlpha(skyCanvasGroup, 0f);
        SetAlpha(birdsCanvasGroup, 0f);

        Vector3 shipFrom = shipStartPos + new Vector3(0f, shipStartOffsetY, 0f);
        Vector3 wavesFrom = wavesStartPos + new Vector3(0f, wavesStartOffsetY, 0f);
        Vector3 skyFrom = skyStartPos + new Vector3(0f, skyStartOffsetY, 0f);
        Vector3 birdsFrom = birdsStartPos + new Vector3(birdsStartOffsetX, 0f, 0f);

        if (ship != null) ship.localPosition = shipFrom;
        if (waves != null) waves.localPosition = wavesFrom;
        if (sky != null) sky.localPosition = skyFrom;
        if (birds != null) birds.localPosition = birdsFrom;

        float time = 0f;

        while (time < introDuration)
        {
            time += Time.deltaTime;
            float t = Mathf.Clamp01(time / introDuration);
            float smooth = Mathf.SmoothStep(0f, 1f, t);
            float back = EaseOutBack(t);

            if (ship != null) ship.localPosition = Vector3.LerpUnclamped(shipFrom, shipStartPos, back);
            if (waves != null) waves.localPosition = Vector3.Lerp(wavesFrom, wavesStartPos, smooth);
            if (sky != null) sky.localPosition = Vector3.Lerp(skyFrom, skyStartPos, smooth);
            if (birds != null) birds.localPosition = Vector3.Lerp(birdsFrom, birdsStartPos, smooth);

            SetAlpha(shipCanvasGroup, smooth);
            SetAlpha(wavesCanvasGroup, smooth);
            SetAlpha(skyCanvasGroup, smooth);
            SetAlpha(birdsCanvasGroup, smooth);

            yield return null;
        }

        ResetTransforms();
        SetAlpha(shipCanvasGroup, 1f);
        SetAlpha(wavesCanvasGroup, 1f);
        SetAlpha(skyCanvasGroup, 1f);
        SetAlpha(birdsCanvasGroup, 1f);
    }

    private IEnumerator IdleRoutine()
    {
        float time = 0f;

        while (true)
        {
            time += Time.deltaTime;

            if (ship != null)
            {
                float y = Mathf.Sin(time * shipBobSpeed) * shipBobHeight;
                float angle = Mathf.Sin(time * shipBobSpeed * 0.8f) * shipRockAngle;
                ship.localPosition = shipStartPos + new Vector3(0f, y, 0f);
                ship.localRotation = Quaternion.Euler(0f, 0f, angle);
            }

            Vector3 waveOffset = Vector3.zero;

            if (waves != null)
            {
                float x = Mathf.Sin(time * wavesSpeed) * wavesMoveX;
                float y = Mathf.Sin(time * wavesSpeed * 2f) * wavesBobHeight;
                waveOffset = new Vector3(x, y, 0f);
                waves.localPosition = wavesStartPos + waveOffset;
            }

            // Moving background follows the exact same motion as the waves.
            // Static background / sun stays still.
            if (movingBackground != null)
                movingBackground.localPosition = movingBackgroundStartPos + waveOffset;

            if (sky != null)
            {
                float x = Mathf.Sin(time * skySpeed) * skyMoveX;
                sky.localPosition = skyStartPos + new Vector3(x, 0f, 0f);
            }

            if (birds != null)
            {
                float x = Mathf.Sin(time * birdsSpeed) * birdsMoveX;
                float y = Mathf.Sin(time * birdsSpeed * 2f) * birdsMoveY;
                birds.localPosition = birdsStartPos + new Vector3(x, y, 0f);
            }

            yield return null;
        }
    }

    private void CacheStartPositions()
    {
        if (staticBackground != null) staticBackgroundStartPos = staticBackground.localPosition;
        if (movingBackground != null) movingBackgroundStartPos = movingBackground.localPosition;
        if (ship != null) shipStartPos = ship.localPosition;
        if (waves != null) wavesStartPos = waves.localPosition;
        if (sky != null) skyStartPos = sky.localPosition;
        if (birds != null) birdsStartPos = birds.localPosition;
    }

    private void ResetTransforms()
    {
        if (staticBackground != null) staticBackground.localPosition = staticBackgroundStartPos;
        if (movingBackground != null) movingBackground.localPosition = movingBackgroundStartPos;

        if (ship != null)
        {
            ship.localPosition = shipStartPos;
            ship.localRotation = Quaternion.identity;
        }

        if (waves != null) waves.localPosition = wavesStartPos;
        if (sky != null) sky.localPosition = skyStartPos;
        if (birds != null) birds.localPosition = birdsStartPos;
    }

    private void SetAlpha(CanvasGroup group, float value)
    {
        if (group != null)
            group.alpha = value;
    }

    private float EaseOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        return 1f + c3 * Mathf.Pow(t - 1f, 3f) + c1 * Mathf.Pow(t - 1f, 2f);
    }
}
