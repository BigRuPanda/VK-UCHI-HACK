using UnityEngine;

namespace ReadingModule
{
    /// <summary>
    /// Holds all data for a single sentence in the reading module.
    /// Configure entirely in the Unity Inspector — no code changes needed.
    /// </summary>
    [System.Serializable]
    public class SentenceData
    {
        [TextArea(2, 5)]
        [Tooltip("The full sentence the child must read aloud. Words are split by spaces.")]
        public string sentenceText;

        [Tooltip("Optional audio clip that plays when this sentence appears (voice-over hint). Leave empty to skip.")]
        public AudioClip voiceOver;

        [Tooltip("Scene GameObjects to SetActive(true) when this sentence is completed. Pre-place them in the scene with SetActive(false).")]
        public GameObject[] objectsToUnlock;

        [Tooltip("Scene GameObjects to SetActive(false) when this sentence is completed. Pre-place them in the scene with SetActive(true).")]
        public GameObject[] objectsToDeactivate;
    }
}
