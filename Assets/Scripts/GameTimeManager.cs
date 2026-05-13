using UnityEngine;

public static class GameTimeManager
{
    private static float _totalSeconds = 0f;
    private static bool _isPaused = false;
    private static bool _isEnabled = true; // чтобы не обновлять, если не нужно

    // Обновляем время каждый кадр (вызывать из любого MonoBehaviour.Update)
    public static void Update()
    {
        if (_isEnabled && !_isPaused)
        {
            _totalSeconds += Time.deltaTime;
        }
    }

    // Получить общее время в секундах
    public static float TotalSeconds => _totalSeconds;

    // Получить отформатированную строку ЧЧ:ММ:СС
    public static string GetFormattedTime()
    {
        int totalSeconds = Mathf.FloorToInt(_totalSeconds);
        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int seconds = totalSeconds % 60;

        return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
    }

    public static void Pause() => _isPaused = true;
    public static void Resume() => _isPaused = false;
    public static void Reset() => _totalSeconds = 0f;

    // Опционально: отключить обновление (если выйдешь в главное меню и не хочешь считать)
    public static void SetEnabled(bool enabled) => _isEnabled = enabled;
}