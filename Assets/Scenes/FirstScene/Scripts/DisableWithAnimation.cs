using System.Collections;
using UnityEngine;

public class StartSequence : MonoBehaviour
{
    [Header("Panel Hide")]
    public GameObject panelToDisable;
    public Animator panelAnimator;
    public string panelHideTrigger = "Hide";
    public float delayBeforePanelHide = 1.5f;
    public float panelHideAnimationDuration = 1f;

    [Header("Hero")]
    public Animator heroAnimator;
    public string heroTrigger = "StartWalk";
    public float heroAnimationDuration = 13f;

    [Header("After Hero Animation")]
    public GameObject swampFX;
    public Animator bookAnimator;
    public string bookTrigger = "BookClose";

    private bool isPlaying;

    public void PlaySequence()
    {
        if (isPlaying) return;
        StartCoroutine(Sequence());
    }

    private IEnumerator Sequence()
    {
        isPlaying = true;

        // 1. Ждём перед исчезновением панели
        yield return new WaitForSeconds(delayBeforePanelHide);

        // 2. Запускаем анимацию исчезновения панели
        if (panelAnimator != null)
            panelAnimator.SetTrigger(panelHideTrigger);

        // 3. Ждём конец анимации панели
        yield return new WaitForSeconds(panelHideAnimationDuration);

        // 4. Выключаем панель
        if (panelToDisable != null)
            panelToDisable.SetActive(false);

        // 5. Запускаем анимацию героя
        if (heroAnimator != null)
            heroAnimator.SetTrigger(heroTrigger);

        // 6. Ждём, пока герой закончит анимацию
        yield return new WaitForSeconds(heroAnimationDuration);

        // 7. Только теперь выключаем светлячков
        if (swampFX != null)
            swampFX.SetActive(false);

        // 8. И только теперь закрываем книгу
        if (bookAnimator != null)
            bookAnimator.SetTrigger(bookTrigger);

        isPlaying = false;
    }
}