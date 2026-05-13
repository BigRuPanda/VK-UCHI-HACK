using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LoadingManager : MonoBehaviour
{
    [SerializeField] private string gameSceneName = "GameScene";
    [SerializeField] private float loadingTime = 2f;

    private IEnumerator Start()
    {
        yield return new WaitForSeconds(loadingTime);
        SceneManager.LoadScene(gameSceneName);
    }
}