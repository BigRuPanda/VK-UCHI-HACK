using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.Events;

namespace ReadingModule
{
    /// <summary>
    /// Main controller for the reading module.  (v4 — per-word animations)
    ///
    /// Each word in the sentence is a separate WordToken prefab instance spawned
    /// inside a HorizontalLayoutGroup container. This gives full per-word control
    /// over animations:
    ///   Idle    → gentle vertical float (different phase per word)
    ///   Active  → FULLY STATIC (child needs to read this word)
    ///   Correct → scale punch + green flash + optional particles
    ///   Error   → horizontal shake (this word only) + red → reverts to Active
    ///
    /// CHANGES vs v3:
    ///   • sentenceLabel (single TMP) replaced by wordTokenPrefab + wordTokenContainer
    ///   • RebuildText() replaced by per-token SetState() calls
    ///   • StartActivePulse() / StopActivePulse() removed (logic lives in WordToken)
    ///   • _isShaking is now driven by WordToken.OnErrorShakeComplete event
    ///   • SpawnWordTokens() / ClearWordTokens() manage token lifecycle
    ///   • Sentence-complete cascade wave played before advancing
    ///
    /// UNCHANGED vs v3:
    ///   • _wordQueue / Update() drain logic
    ///   • FuzzyMatch() / LevenshteinDistance()
    ///   • SpeechBridge integration
    ///   • SentenceData structure
    ///   • UnlockBackgroundObjects()
    ///   • Grammar hint (UpdateGrammarHint / SpeechBridge.UpdateExpectedWords)
    ///
    /// Attach to any GameObject. Configure in Inspector.
    /// </summary>
    public class ReadingController : MonoBehaviour
    {
        // ═══════════════════════════════════════════════════════════════════════════
        // Inspector Fields
        // ═══════════════════════════════════════════════════════════════════════════

        [Header("── Sentences ──────────────────────────────────────")]
        [Tooltip("All sentences the child must read, in order.")]
        public SentenceData[] sentences;

        [Header("── Word Token UI ────────────────────────────────────")]
        [Tooltip("Prefab with a WordToken component. Each word gets its own instance.")]
        public WordToken wordTokenPrefab;

        [Tooltip("Parent transform for word tokens. Should have HorizontalLayoutGroup + ContentSizeFitter.")]
        public Transform wordTokenContainer;

        [Header("── Entry Animation ──────────────────────────────────")]
        [Tooltip("Stagger delay between each word's fly-in animation (seconds).")]
        public float entryStaggerDelay = 0.05f;

        [Header("── Sentence Complete Wave ────────────────────────────")]
        [Tooltip("Stagger delay between each word in the sentence-complete cascade wave.")]
        public float waveStaggerDelay = 0.06f;

        [Tooltip("Pause after the wave before unlocking background objects and loading next sentence.")]
        public float postWavePause = 0.5f;

        [Header("── Speech Settings ─────────────────────────────────")]
        [Tooltip("BCP-47 language tag. Examples: ru-RU, en-US, de-DE")]
        public string language = "ru-RU";

        [Range(0f, 1f)]
        [Tooltip("Word similarity threshold.\n1.0 = exact\n0.85 = 1-2 char tolerance\n0.70 = ~30% (1st grade)\n0.60 = very lenient")]
        public float fuzzyMatchThreshold = 0.70f;

        [Header("── Syllabic Reading ────────────────────────────────")]
        [Tooltip("Seconds to ignore new words after a word is accepted.\n" +
                 "Prevents tail-syllables of the just-accepted word from firing against the next word.\n" +
                 "Increase if you see false rejects on the word after a slow syllabic read.")]
        public float acceptCooldownSec = 0.8f;

        [Header("── Audio ─────────────────────────────────────────────")]
        [Tooltip("AudioSource for voice-over clips. Leave null to auto-create.")]
        public AudioSource audioSource;

        [Header("── Events ───────────────────────────────────────────")]
        public UnityEvent OnWordSuccess;
        public UnityEvent OnWordError;
        public UnityEvent<int> OnSentenceComplete;
        public UnityEvent OnAllSentencesComplete;
        public UnityEvent<string> OnSpeechPermissionError;

        // ═══════════════════════════════════════════════════════════════════════════
        // Private State
        // ═══════════════════════════════════════════════════════════════════════════

        private int      _currentSentenceIndex;
        private int      _currentWordIndex;
        private string[] _currentWords;   // clean words (no punctuation) — for matching
        private string[] _displayWords;   // original tokens (with punctuation) — for display

        private SpeechBridge _speechBridge;
        private bool _isShaking;          // true while error shake plays → pauses queue drain
        private bool _speechSessionStarted;
        private bool _acceptingSpeech;
        private string _syllableBuffer = ""; // accumulates unrecognized words for syllabic reading

        // Runtime cooldown state — configured from acceptCooldownSec Inspector field.
        private float _acceptCooldownUntil = 0f;

        // Spawned WordToken instances for the current sentence.
        // Index aligns with _currentWords (clean words only).
        private readonly List<WordToken> _tokens = new List<WordToken>();

        // Incoming recognized words are enqueued here and drained one-per-frame
        // in Update(). This prevents any word from being dropped even during
        // shake animations or rapid speech.
        private readonly Queue<string> _wordQueue = new Queue<string>();

        // ═══════════════════════════════════════════════════════════════════════════
        // Unity Lifecycle
        // ═══════════════════════════════════════════════════════════════════════════

        private void Start()
        {
            ValidateReferences();
            SetupAudioSource();
            ConnectSpeechBridge();
        }

        /// <summary>
        /// Drain the word queue one entry per frame.
        /// Processing is paused while the shake animation plays so the child
        /// sees the red feedback before the next word is evaluated.
        /// </summary>
        private void Update()
        {
            if (_wordQueue.Count == 0 || _isShaking || !_acceptingSpeech) return;
            if (Time.unscaledTime < _acceptCooldownUntil) return; // cooldown after AcceptWord
            ProcessNextWord(_wordQueue.Dequeue());
        }

        private void OnDestroy()
        {
            EndSpeechSession();
            DisconnectSpeechBridge();
            ClearWordTokens();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Initialization
        // ═══════════════════════════════════════════════════════════════════════════

        private void ValidateReferences()
        {
            if (sentences == null || sentences.Length == 0)
                Debug.LogError("[ReadingController] 'sentences' array is empty.", this);

            if (wordTokenPrefab == null)
                Debug.LogError("[ReadingController] 'wordTokenPrefab' is not assigned.", this);

            if (wordTokenContainer == null)
                Debug.LogError("[ReadingController] 'wordTokenContainer' is not assigned.", this);
        }

        private void SetupAudioSource()
        {
            if (audioSource == null)
                audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
        }

        private void ConnectSpeechBridge()
        {
            _speechBridge = SpeechBridge.Instance ?? FindObjectOfType<SpeechBridge>();

            if (_speechBridge == null)
            {
                Debug.LogError("[ReadingController] SpeechBridge not found. " +
                               "Create a GameObject named 'SpeechBridge' with SpeechBridge component.", this);
                return;
            }

            _speechBridge.OnWordRecognized += HandleWordRecognized;
            _speechBridge.OnSpeechError    += HandleSpeechError;
        }

        private void DisconnectSpeechBridge()
        {
            if (_speechBridge == null) return;
            _speechBridge.OnWordRecognized -= HandleWordRecognized;
            _speechBridge.OnSpeechError    -= HandleSpeechError;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Speech Session
        // ═══════════════════════════════════════════════════════════════════════════

        private void BeginSpeechSession()
        {
            if (_speechBridge == null)
            {
                Debug.LogError("[ReadingController] Cannot start speech session: SpeechBridge is missing.", this);
                return;
            }

            if (_speechSessionStarted)
            {
                Debug.Log("[ReadingController] Speech session is already running — not restarting recognition.", this);
                return;
            }

            _speechBridge.StartListening(language);
            _speechSessionStarted = true;
            Debug.Log($"[ReadingController] Speech session started, language={language}.", this);
        }

        private void EndSpeechSession()
        {
            if (!_speechSessionStarted) return;

            _acceptingSpeech = false;
            _wordQueue.Clear();

            if (_speechBridge != null)
                _speechBridge.StopListening();

            _speechSessionStarted = false;
            Debug.Log("[ReadingController] Speech session stopped.", this);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Sentence Loading
        // ═══════════════════════════════════════════════════════════════════════════

        private void LoadSentence(int index)
        {
            if (index < 0 || index >= sentences.Length)
            {
                Debug.LogError($"[ReadingController] Sentence index {index} out of range.", this);
                return;
            }

            _acceptingSpeech      = false;
            _currentSentenceIndex = index;
            _currentWordIndex     = 0;
            _isShaking            = false;
            _syllableBuffer       = "";

            // Discard any words buffered from the previous sentence
            _wordQueue.Clear();

            // Destroy previous word tokens
            ClearWordTokens();

            SentenceData data = sentences[index];

            // Parse: keep display tokens (with punctuation) and clean tokens (for matching)
            ParseSentence(data.sentenceText, out _displayWords, out _currentWords);

            if (_currentWords.Length == 0)
            {
                Debug.LogWarning($"[ReadingController] Sentence {index} has no words. Skipping.", this);
                AdvanceToNextSentence();
                return;
            }

            // Push grammar hint to JS so Chrome/Edge narrow recognition to
            // the words of this sentence.
            UpdateGrammarHint();

            // Spawn word tokens and play entry animation
            SpawnWordTokens();

            if (data.voiceOver != null)
            {
                audioSource.clip = data.voiceOver;
                audioSource.Play();
            }

            // Delay accepting speech until entry animations finish
            float totalEntryDuration = entryStaggerDelay * _currentWords.Length + 0.35f;
            StartCoroutine(EnableSpeechAfterDelay(totalEntryDuration));

            Debug.Log($"[ReadingController] Loaded sentence {index}: \"{data.sentenceText}\"");
        }

        private IEnumerator EnableSpeechAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (_currentWordIndex < _currentWords.Length)
            {
                _tokens[_currentWordIndex].SetState(WordState.Active);
                _acceptingSpeech = true;
            }
        }

        /// <summary>
        /// Send the clean words of the current sentence to SpeechBridge so it
        /// can push a JSGF grammar hint to the JS recognizer.
        /// </summary>
        private void UpdateGrammarHint()
        {
            if (_speechBridge == null || _currentWords == null || _currentWords.Length == 0)
                return;

            _speechBridge.UpdateExpectedWords(_currentWords);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Word Token Management
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Instantiate one WordToken per clean word, play staggered entry animations.
        /// All tokens start in Idle state after their entry animation completes.
        /// The first token is set to Active after all entries finish (via EnableSpeechAfterDelay).
        /// </summary>
        private void SpawnWordTokens()
        {
            if (wordTokenPrefab == null || wordTokenContainer == null) return;

            _tokens.Clear();

            // Build a mapping from clean-word index to display token
            // (display tokens may include punctuation-only tokens we skip)
            int cleanIdx = 0;
            for (int i = 0; i < _displayWords.Length; i++)
            {
                string display = _displayWords[i];
                string clean   = StripPunctuation(display);
                bool   isWord  = !string.IsNullOrEmpty(clean);

                if (!isWord) continue; // skip punctuation-only tokens

                WordToken token = Instantiate(wordTokenPrefab, wordTokenContainer);
                token.Initialize(display, cleanIdx);

                // Subscribe to error shake complete event
                int capturedIndex = cleanIdx; // capture for lambda
                token.OnErrorShakeComplete += () => OnTokenErrorShakeComplete(capturedIndex);

                _tokens.Add(token);
                cleanIdx++;
            }

            // Play staggered entry animations
            for (int i = 0; i < _tokens.Count; i++)
            {
                _tokens[i].PlayEntry(i * entryStaggerDelay);
            }
        }

        /// <summary>
        /// Destroy all current word token GameObjects and clear the list.
        /// </summary>
        private void ClearWordTokens()
        {
            foreach (var token in _tokens)
            {
                if (token != null)
                    Destroy(token.gameObject);
            }
            _tokens.Clear();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Speech Handling
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called by SpeechBridge when a word arrives from the JS recognizer.
        /// Words are enqueued here and processed one-per-frame in Update().
        /// </summary>
        private void HandleWordRecognized(string recognizedWord)
        {
            if (!_acceptingSpeech)
            {
                Debug.Log($"[ReadingController] Ignoring word while not accepting speech: '{recognizedWord}'", this);
                return;
            }

            if (_currentWords == null || _currentWords.Length == 0)
            {
                Debug.LogWarning($"[ReadingController] Ignoring word because no sentence is loaded: '{recognizedWord}'", this);
                return;
            }

            _wordQueue.Enqueue(recognizedWord);
        }

        /// <summary>
        /// Dequeued and called from Update(). Validates one word against the
        /// current expected word and triggers accept or reject feedback.
        /// </summary>
        private void ProcessNextWord(string recognizedWord)
        {
            if (_currentWordIndex < 0 || _currentWordIndex >= _currentWords.Length)
            {
                Debug.LogWarning($"[ReadingController] Ignoring word because current index is out of range: " +
                                 $"index={_currentWordIndex}, words={_currentWords.Length}, word='{recognizedWord}'", this);
                return;
            }

            string expected = _currentWords[_currentWordIndex];

#if UNITY_EDITOR
            if (recognizedWord == "__EDITOR_CORRECT__") { AcceptWord(); return; }
            if (recognizedWord == "__EDITOR_WRONG__")  { RejectWord();  return; }
#endif

            string normalizedRecognized = Normalize(recognizedWord);
            string normalizedExpected = Normalize(expected);

            // 1. Try matching the single word first
            float similarity = FuzzyMatch(normalizedRecognized, normalizedExpected, fuzzyMatchThreshold);
            Debug.Log($"[ReadingController] Heard: '{recognizedWord}' | Expected: '{expected}' | Sim: {similarity:F2}");

            if (similarity >= fuzzyMatchThreshold)
            {
                _syllableBuffer = ""; // Clear buffer on success
                AcceptWord();
                return;
            }

            // 2. If it didn't match, append to the syllable buffer and try matching the concatenated string
            _syllableBuffer += normalizedRecognized;
            float bufferSimilarity = FuzzyMatch(_syllableBuffer, normalizedExpected, fuzzyMatchThreshold);
            Debug.Log($"[ReadingController] Buffer: '{_syllableBuffer}' | Expected: '{expected}' | Sim: {bufferSimilarity:F2}");

            if (bufferSimilarity >= fuzzyMatchThreshold)
            {
                _syllableBuffer = ""; // Clear buffer on success
                AcceptWord();
            }
            else
            {
                // 3. Check if the buffer is a reasonable prefix of the expected word.
                // We allow a small number of errors for the prefix.
                bool isPrefix = false;
                if (_syllableBuffer.Length <= normalizedExpected.Length)
                {
                    string expectedPrefix = normalizedExpected.Substring(0, _syllableBuffer.Length);
                    float prefixSim = FuzzyMatch(_syllableBuffer, expectedPrefix, 0f);
                    if (prefixSim >= 0.7f) // fairly lenient for prefixes
                    {
                        isPrefix = true;
                    }
                }

                if (isPrefix)
                {
                    Debug.Log($"[ReadingController] Buffer '{_syllableBuffer}' is a valid prefix. Waiting for more syllables.");
                    // Do not reject, just wait for more words.
                }
                else
                {
                    Debug.Log($"[ReadingController] Buffer '{_syllableBuffer}' is NOT a valid prefix. Rejecting.");
                    _syllableBuffer = ""; // Clear buffer on error
                    RejectWord();
                }
            }
        }

        private void AcceptWord()
        {
            int acceptedIndex = _currentWordIndex;
            _currentWordIndex++;

            // Reset syllable buffer — the accepted word is done.
            _syllableBuffer = "";

            // Smart queue purge: discard words that cannot be the next expected word
            // (tail-syllables of the just-accepted word), but KEEP words that fuzzy-match
            // the next expected word (fast reader sent multiple words in one Vosk result).
            PurgeStaleQueueEntries();

            // Short cooldown so the JS debounce has time to flush its pending batch
            // before Update() starts draining the queue again.
            _acceptCooldownUntil = Time.unscaledTime + acceptCooldownSec;

            // Mark accepted word as correct
            if (acceptedIndex < _tokens.Count)
                _tokens[acceptedIndex].SetState(WordState.Correct);

            OnWordSuccess?.Invoke();

            if (_currentWordIndex >= _currentWords.Length)
            {
                // All words read — stop accepting speech, play completion wave
                _acceptingSpeech = false;
                _wordQueue.Clear();
                StartCoroutine(CompleteSentenceRoutine(_currentSentenceIndex));
            }
            else
            {
                // Activate next word (fully static so child can read it)
                if (_currentWordIndex < _tokens.Count)
                    _tokens[_currentWordIndex].SetState(WordState.Active);
            }
        }

        /// <summary>
        /// Filters <see cref="_wordQueue"/> after a word is accepted:
        /// <list type="bullet">
        ///   <item>KEEPS entries that fuzzy-match the next expected word
        ///         (fast reader sent several words in one Vosk result batch).</item>
        ///   <item>DISCARDS entries that do NOT match
        ///         (tail-syllables of the just-accepted word still in the pipeline).</item>
        /// </list>
        /// If there is no next word (sentence complete), the queue is cleared entirely.
        /// </summary>
        /// <summary>
        /// Filters <see cref="_wordQueue"/> after a word is accepted.
        ///
        /// A queued candidate is KEPT if it fuzzy-matches ANY of the remaining
        /// words in the sentence (_currentWords[_currentWordIndex..]).
        /// This allows a fast reader's batch ("жил был маленький") to survive
        /// multiple successive AcceptWord() calls:
        ///   After AcceptWord("жил"):  nextIdx=1, remaining=["был","маленький"]
        ///     "был"       → matches remaining[0] → KEPT
        ///     "маленький" → matches remaining[1] → KEPT
        ///   After AcceptWord("был"):  nextIdx=2, remaining=["маленький"]
        ///     "маленький" → matches remaining[0] → KEPT
        ///
        /// A candidate is DISCARDED only if it matches NONE of the remaining words
        /// (i.e. it is a tail-syllable of the just-accepted word).
        /// </summary>
        private void PurgeStaleQueueEntries()
        {
            if (_wordQueue.Count == 0) return;

            // Sentence complete — nothing left to match against.
            if (_currentWordIndex >= _currentWords.Length)
            {
                _wordQueue.Clear();
                return;
            }

            // Pre-normalize the remaining expected words once.
            int remaining = _currentWords.Length - _currentWordIndex;
            var normalizedRemaining = new string[remaining];
            for (int i = 0; i < remaining; i++)
                normalizedRemaining[i] = Normalize(_currentWords[_currentWordIndex + i]);

            var kept = new Queue<string>(_wordQueue.Count);
            while (_wordQueue.Count > 0)
            {
                string candidate      = _wordQueue.Dequeue();
                string normalizedCand = Normalize(candidate);

                // Keep if the candidate fuzzy-matches ANY remaining word.
                bool matchesAny = false;
                for (int i = 0; i < normalizedRemaining.Length; i++)
                {
                    float sim = FuzzyMatch(normalizedCand, normalizedRemaining[i], fuzzyMatchThreshold);
                    if (sim >= fuzzyMatchThreshold)
                    {
                        matchesAny = true;
                        Debug.Log($"[ReadingController] PurgeStale: kept '{candidate}' " +
                                  $"(matches remaining[{i}]='{_currentWords[_currentWordIndex + i]}', sim={sim:F2})");
                        break;
                    }
                }

                if (matchesAny)
                    kept.Enqueue(candidate);
                else
                    Debug.Log($"[ReadingController] PurgeStale: discarded '{candidate}' (no match in remaining words)");
            }

            // Restore kept entries to the queue.
            while (kept.Count > 0)
                _wordQueue.Enqueue(kept.Dequeue());
        }

        private void RejectWord()
        {
            // Pause queue drain while shake plays
            _isShaking = true;

            if (_currentWordIndex < _tokens.Count)
                _tokens[_currentWordIndex].SetState(WordState.Error);

            OnWordError?.Invoke();
            // _isShaking is reset in OnTokenErrorShakeComplete (via event)
        }

        /// <summary>
        /// Called by the active WordToken when its error shake animation completes.
        /// Resumes the word queue drain.
        /// </summary>
        private void OnTokenErrorShakeComplete(int tokenIndex)
        {
            // Only respond to the currently active token
            if (tokenIndex != _currentWordIndex) return;

            _isShaking = false;
            // WordToken already reverted itself to Active state in its OnComplete callback
        }

        private void HandleSpeechError(string error)
        {
            Debug.LogWarning($"[ReadingController] Speech error: {error}");
            OnSpeechPermissionError?.Invoke(error);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Sentence Completion
        // ═══════════════════════════════════════════════════════════════════════════

        private IEnumerator CompleteSentenceRoutine(int sentenceIndex)
        {
            // Play cascade wave across all tokens
            for (int i = 0; i < _tokens.Count; i++)
                _tokens[i].PlaySentenceCompleteWave(i * waveStaggerDelay);

            // Wait for wave to finish + post-wave pause
            float waveTotalDuration = _tokens.Count * waveStaggerDelay + 0.30f + postWavePause;
            yield return new WaitForSeconds(waveTotalDuration);

            UnlockBackgroundObjects(sentenceIndex);
            OnSentenceComplete?.Invoke(sentenceIndex);
            AdvanceToNextSentence();
        }

        private void AdvanceToNextSentence()
        {
            int next = _currentSentenceIndex + 1;
            if (next < sentences.Length)
            {
                LoadSentence(next);
            }
            else
            {
                EndSpeechSession();
                OnAllSentencesComplete?.Invoke();
                Debug.Log("[ReadingController] All sentences completed!");
            }
        }

        private void UnlockBackgroundObjects(int sentenceIndex)
        {
            var data = sentences[sentenceIndex];

            if (data.objectsToUnlock != null)
                foreach (var go in data.objectsToUnlock)
                    if (go != null) go.SetActive(true);

            if (data.objectsToDeactivate != null)
                foreach (var go in data.objectsToDeactivate)
                    if (go != null) go.SetActive(false);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Word Parsing
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Split sentence into display tokens (original, with punctuation) and
        /// clean tokens (stripped, for fuzzy matching).
        /// </summary>
        private static void ParseSentence(string sentence,
                                          out string[] displayWords,
                                          out string[] cleanWords)
        {
            if (string.IsNullOrWhiteSpace(sentence))
            {
                displayWords = System.Array.Empty<string>();
                cleanWords   = System.Array.Empty<string>();
                return;
            }

            var raw = sentence.Split(new[] { ' ', '\t', '\n', '\r' },
                                     System.StringSplitOptions.RemoveEmptyEntries);

            var display = new List<string>();
            var clean   = new List<string>();

            foreach (var token in raw)
            {
                display.Add(token);
                string stripped = StripPunctuation(token);
                if (!string.IsNullOrEmpty(stripped))
                    clean.Add(stripped);
            }

            displayWords = display.ToArray();
            cleanWords   = clean.ToArray();
        }

        private static string StripPunctuation(string word)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                word, @"^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$", "");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Fuzzy Matching
        // ═══════════════════════════════════════════════════════════════════════════

        private static string Normalize(string word)
        {
            if (string.IsNullOrEmpty(word)) return string.Empty;
            word = word.ToLowerInvariant().Trim();
            word = System.Text.RegularExpressions.Regex.Replace(word, @"[^\p{L}\p{N}\-]", "");
            return word;
        }

        /// <summary>
        /// Fuzzy similarity score in [0..1] between two normalized strings.
        /// Uses Levenshtein distance with an early-exit length check:
        /// if the length ratio alone is below <paramref name="threshold"/>,
        /// the strings cannot possibly pass the threshold, so Levenshtein is
        /// skipped entirely.
        /// </summary>
        public static float FuzzyMatch(string a, string b, float threshold = 0f)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1f;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0f;
            if (a == b) return 1f;

            int maxLen = Mathf.Max(a.Length, b.Length);
            int minLen = Mathf.Min(a.Length, b.Length);

            // Early exit: if the shorter string is too short relative to the
            // longer one, the similarity can never reach the threshold.
            if (threshold > 0f)
            {
                float lengthRatio = (float)minLen / maxLen;
                if (lengthRatio < threshold)
                    return lengthRatio;
            }

            int dist = LevenshteinDistance(a, b);
            return 1f - (float)dist / maxLen;
        }

        // Overload kept for backward compatibility (static utility callers).
        public static float FuzzyMatch(string a, string b)
            => FuzzyMatch(a, b, 0f);

        private static int LevenshteinDistance(string a, string b)
        {
            int la = a.Length, lb = b.Length;
            int[,] dp = new int[la + 1, lb + 1];
            for (int i = 0; i <= la; i++) dp[i, 0] = i;
            for (int j = 0; j <= lb; j++) dp[0, j] = j;
            for (int i = 1; i <= la; i++)
                for (int j = 1; j <= lb; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    dp[i, j] = Mathf.Min(dp[i - 1, j] + 1,
                                Mathf.Min(dp[i, j - 1] + 1,
                                          dp[i - 1, j - 1] + cost));
                }
            return dp[la, lb];
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Public API
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Begin reading from sentence 0.
        /// Wire to MicrophonePermissionHandler.onPermissionGranted.
        /// </summary>
        public void StartReading()
        {
            LoadSentence(0);
            BeginSpeechSession();
        }

        /// <summary>Restart from sentence 0.</summary>
        public void RestartFromBeginning()
        {
            LoadSentence(0);
            BeginSpeechSession();
        }

        /// <summary>Jump to a specific sentence (0-based).</summary>
        public void JumpToSentence(int i)
        {
            LoadSentence(i);
            BeginSpeechSession();
        }
    }
}
