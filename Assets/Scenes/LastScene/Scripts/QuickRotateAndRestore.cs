using UnityEngine;
using System.Collections;

public class FlipRotation : MonoBehaviour
{
    [Tooltip("Угол поворота (в градусах) относительно исходного положения")]
    public Vector3 flipAngle = new Vector3(0, 180, 0);

    [Tooltip("Время ожидания после поворота (в секундах)")]
    public float holdTime = 1f;

    [Tooltip("Время ожидания после возврата (в секундах)")]
    public float resetHoldTime = 1f;

    private Quaternion originalRotation;

    void Start()
    {
        originalRotation = transform.rotation;
        StartCoroutine(FlipCycle());
    }

    IEnumerator FlipCycle()
    {
        while (true)
        {
            // Мгновенно поворачиваем на flipAngle относительно исходного поворота
            transform.rotation = originalRotation * Quaternion.Euler(flipAngle);
            yield return new WaitForSeconds(holdTime);

            // Мгновенно возвращаем в исходное положение
            transform.rotation = originalRotation;
            yield return new WaitForSeconds(resetHoldTime);
        }
    }
}