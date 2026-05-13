using UnityEngine;

public class RotateObject : MonoBehaviour
{
    [Tooltip("Скорость вращения по осям X, Y, Z (в градусах в секунду)")]
    public Vector3 rotationSpeed = new Vector3(0, 100, 0);

    void Update()
    {
        transform.Rotate(rotationSpeed * Time.deltaTime);
    }
}