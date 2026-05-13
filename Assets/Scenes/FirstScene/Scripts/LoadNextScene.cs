using UnityEngine;
using UnityEngine.SceneManagement;

public class LoadNextScene : MonoBehaviour
{
    public string nextScene;
    public Animator animator;

    public void PlayAnimation()
    {
        animator.SetTrigger("BookClose");
    }

    // Вызовется из анимации!
    public void LoadScene()
    {
        SceneManager.LoadScene(nextScene);
    }
}