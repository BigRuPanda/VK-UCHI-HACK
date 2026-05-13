using UnityEngine;

public class GameTimeUpdater : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void CreateInstance()
    {
        var obj = new GameObject("GameTimeUpdater");
        obj.AddComponent<GameTimeUpdater>();
        DontDestroyOnLoad(obj);
    }

    private void Update()
    {
        GameTimeManager.Update();
    }
}