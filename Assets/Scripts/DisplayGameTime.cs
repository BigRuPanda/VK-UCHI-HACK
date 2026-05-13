using TMPro;
using UnityEngine;

public class ShowFinalTime : MonoBehaviour
{
    [SerializeField] private TMP_Text timeText;

    private void OnEnable()
    {
        if (timeText != null)
        {
            timeText.text = GameTimeManager.GetFormattedTime();
        }
    }
}