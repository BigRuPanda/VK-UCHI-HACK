/**
 * VoskBridge.jslib  (v3 — fixes "no validation" bug)
 *
 * Root cause of the bug:
 *   v2 waited for a 'ready' event from KaldiRecognizer, but vosk-browser
 *   does NOT fire a 'ready' event. This caused the audio pipeline to never
 *   start, resulting in dropped audio frames and no validation.
 *
 * Fix:
 *   1. Removed rec.on('ready', ...) wrapper.
 *   2. Set vb.recognizer and vb.modelReady immediately after creation.
 *   3. The internal Web Worker queues messages, so there is no race condition
 *      between 'create' and 'audioChunk' messages.
 *
 * Unity DllImport surface (unchanged):
 *   StartRecognition(langPtr)
 *   StopRecognition()
 *   SetExpectedWords(wordsJsonPtr)
 *
 * SendMessage callbacks to "SpeechBridge" GameObject:
 *   ReceivePermissionResult("granted" | "denied:<reason>")
 *   ReceiveWord(word)
 *   ReceiveError(errorCode)
 *   ReceiveModelProgress("0".."100")
 *
 * Required files in StreamingAssets/:
 *   vosk/vosk-browser.js        — ccoreilly/vosk-browser UMD bundle
 *   vosk-model-small-ru/        — Vosk Russian model directory
 */

mergeInto(LibraryManager.library, {

    // ── Shared state ──────────────────────────────────────────────────────────
    $VB: {
        model:          null,    // vosk-browser Model instance
        recognizer:     null,    // KaldiRecognizer instance (ready to use)
        audioCtx:       null,    // AudioContext @ 16 kHz
        micStream:      null,    // MediaStream from getUserMedia
        processorNode:  null,    // ScriptProcessorNode
        sourceNode:     null,    // MediaStreamAudioSourceNode

        // Readiness flags — audio only flows when BOTH are true
        modelReady:     false,   // Model loaded AND KaldiRecognizer fired 'ready'
        micReady:       false,   // getUserMedia succeeded + audio graph built

        // True when both modelReady && micReady — gate for onaudioprocess
        isReady:        false,

        scriptLoaded:   false,   // vosk-browser.js injected into page
        sampleRate:     16000,

        // Grammar to apply once the recognizer is ready
        // (SetExpectedWords may be called before StartRecognition)
        pendingGrammar: null,    // string | null

        // Prevent duplicate StartRecognition calls while loading
        loadInProgress: false,

        // ── Runtime-configurable parameters (set via SetRecognitionConfig) ────
        // Milliseconds to wait after the last 'result' event before forwarding
        // words to Unity. Merges syllabic results. 0 = disabled.
        resultDebounceMsec: 600,

        // Live debounce state — shared across recognizer rebuilds
        resultTimer:  null,
        pendingWords: [],
    },

    // ── Resolve StreamingAssets URL ───────────────────────────────────────────
    $VB_saUrl: function () {
        if (typeof Module !== 'undefined' && Module.streamingAssetsUrl)
            return Module.streamingAssetsUrl.replace(/\/$/, '');
        if (typeof window !== 'undefined' && window.STREAMING_ASSETS_URL)
            return window.STREAMING_ASSETS_URL.replace(/\/$/, '');
        return window.location.href.replace(/\/[^/]*$/, '') + '/StreamingAssets';
    },

    // ── Inject vosk-browser.js <script> once ─────────────────────────────────
    $VB_loadScript: function (url, onLoad, onError) {
        if (VB.scriptLoaded) { onLoad(); return; }
        var s = document.createElement('script');
        s.src = url;
        s.onload  = function () { VB.scriptLoaded = true; onLoad(); };
        s.onerror = function () { onError('Cannot load ' + url); };
        document.head.appendChild(s);
    },

    // ── Update isReady and connect mic when both sides are up ─────────────────
    $VB_tryActivate: function () {
        var vb = VB;
        if (!vb.modelReady || !vb.micReady) return;
        vb.isReady = true;
        // Connect audio graph (sourceNode → processorNode → destination)
        if (vb.sourceNode && vb.processorNode && vb.audioCtx) {
            try {
                vb.sourceNode.connect(vb.processorNode);
                vb.processorNode.connect(vb.audioCtx.destination);
            } catch (e) {
                SendMessage('SpeechBridge', 'ReceiveError', 'connect-mic:' + e.message);
            }
        }
    },

    // ── Build (or rebuild) KaldiRecognizer ────────────────────────────────────
    //
    // vosk-browser KaldiRecognizer lifecycle:
    //   1. new KaldiRecognizer(sampleRate [, grammarJson])
    //      → posts "create_recognizer" to internal Worker (async)
    //   2. Worker queues messages, so we can immediately send audio chunks
    //      without waiting for a 'ready' event (which vosk-browser doesn't fire).
    //
    $VB_buildRecognizer: function (grammarJson, onReady) {
        var vb = VB;
        if (!vb.model) return;   // model not loaded yet — caller must retry

        // Tear down previous recognizer
        if (vb.recognizer) {
            vb.recognizer.remove();
            vb.recognizer = null;
        }
        vb.modelReady = false;   // block audio until new recognizer is ready
        vb.isReady    = false;

        // KaldiRecognizer(sampleRate) or KaldiRecognizer(sampleRate, grammarJson).
        // NOTE: vosk-browser's KaldiRecognizer only accepts a JSGF grammar string
        // as the second argument — passing any other JSON causes a WASM function
        // signature mismatch. Endpointer rules are NOT configurable via this API.
        var rec = grammarJson
            ? new vb.model.KaldiRecognizer(vb.sampleRate, grammarJson)
            : new vb.model.KaldiRecognizer(vb.sampleRate);

        vb.recognizer  = rec;
        vb.modelReady  = true;
        if (onReady) onReady();
        VB_tryActivate();

        // ── Final result: debounced emit ──────────────────────────────────────
        // Words are buffered for vb.resultDebounceMsec after the last result event.
        // This merges rapid successive results that occur when a child reads
        // syllable-by-syllable (e.g. "мо" + "ст" arrive as two result events
        // within ~500 ms and are sent to Unity together so _syllableBuffer can
        // concatenate them into "мост").
        // The timer and pending list live on VB so SetRecognitionConfig can
        // update the debounce interval without rebuilding the recognizer.
        rec.on('result', function (msg) {
            var res = msg && msg.result;
            if (!res) return;

            var words = [];
            if (Array.isArray(res.result) && res.result.length > 0) {
                res.result.forEach(function (w) {
                    if (w && w.word) words.push(w.word.toLowerCase());
                });
            } else if (res.text && res.text.trim().length > 0) {
                res.text.trim().split(/\s+/).forEach(function (w) {
                    if (w) words.push(w.toLowerCase());
                });
            }
            if (words.length === 0) return;

            if (vb.resultDebounceMsec <= 0) {
                // Debounce disabled — send immediately
                words.forEach(function (w) {
                    SendMessage('SpeechBridge', 'ReceiveWord', w);
                });
                return;
            }

            // Accumulate and (re)start the debounce timer
            vb.pendingWords = vb.pendingWords.concat(words);
            clearTimeout(vb.resultTimer);
            vb.resultTimer = setTimeout(function () {
                var batch = vb.pendingWords.slice();
                vb.pendingWords = [];
                batch.forEach(function (w) {
                    SendMessage('SpeechBridge', 'ReceiveWord', w);
                });
            }, vb.resultDebounceMsec);
        });

    },

    // ── Request microphone and build audio graph ──────────────────────────────
    $VB_requestMic: function () {
        var vb = VB;
        if (vb.micStream) {
            // Already have mic — just mark ready and try to activate
            vb.micReady = true;
            VB_tryActivate();
            return;
        }

        var setupAudioGraph = function(stream) {
            vb.micStream = stream;

            var AudioCtx  = window.AudioContext || window.webkitAudioContext;
            vb.audioCtx   = new AudioCtx({ sampleRate: vb.sampleRate });
            vb.sourceNode = vb.audioCtx.createMediaStreamSource(stream);

            // 4096-sample mono ScriptProcessorNode
            vb.processorNode = vb.audioCtx.createScriptProcessor(4096, 1, 1);
            vb.processorNode.onaudioprocess = function (ev) {
                // Gate: only feed audio when recognizer is confirmed ready
                if (!vb.isReady || !vb.recognizer) return;
                var live = ev.inputBuffer.getChannelData(0);
                var copy = new Float32Array(live.length);
                copy.set(live);
                try {
                    vb.recognizer.acceptWaveformFloat(copy, vb.sampleRate);
                } catch (e) {
                    // Non-fatal — log to console, don't crash Unity
                    console.warn('[VoskBridge] acceptWaveformFloat:', e.message || e);
                }
            };

            vb.micReady = true;
            VB_tryActivate();
        };

        // Try to reuse the global stream from MicrophonePlugin.jslib
        if (window._micStream) {
            console.log('[VoskBridge] Reusing existing window._micStream');
            setupAudioGraph(window._micStream);
            return;
        }

        navigator.mediaDevices.getUserMedia({ audio: true, video: false })
            .then(setupAudioGraph)
            .catch(function (err) {
                var reason = (err && err.name) ? err.name : String(err);
                SendMessage('SpeechBridge', 'ReceivePermissionResult', 'denied:' + reason);
                SendMessage('SpeechBridge', 'ReceiveError', 'mic-denied:' + reason);
            });
    },

    // ═════════════════════════════════════════════════════════════════════════
    // PUBLIC API
    // ═════════════════════════════════════════════════════════════════════════

    /**
     * StartRecognition — called by SpeechBridge.cs StartListening().
     *
     * Sequence:
     *   1. Inject vosk-browser.js (once)
     *   2. new Model(url) → wait for Model 'load' event
     *   3. new KaldiRecognizer() → synchronous setup
     *   4. getUserMedia → build audio graph
     *   5. When BOTH (3) and (4) done → connect graph, fire ReceivePermissionResult("granted")
     */
    StartRecognition: function (langPtr) {
        var vb    = VB;
        var saUrl = VB_saUrl();

        if (vb.loadInProgress && !vb.model) {
            // Already loading — ignore duplicate call
            return;
        }

        // If model already loaded, just (re)start mic
        if (vb.model && vb.recognizer) {
            VB_requestMic();
            return;
        }

        vb.loadInProgress = true;

        // ── Step 1: load vosk-browser.js ─────────────────────────────────────
        VB_loadScript(
            saUrl + '/vosk/vosk-browser.js',
            function () {
                // vosk-browser UMD exposes window.Vosk = { Model, ... }
                if (!window.Vosk || typeof window.Vosk.Model !== 'function') {
                    SendMessage('SpeechBridge', 'ReceiveError', 'vosk:Vosk.Model-not-found');
                    vb.loadInProgress = false;
                    return;
                }

                // ── Step 2: load model ────────────────────────────────────────
                // Synthetic progress ticks while model loads (vosk-browser does
                // not expose a real progress callback for the model fetch).
                var pct = 0;
                var progressTimer = setInterval(function () {
                    pct = Math.min(90, pct + 4);
                    SendMessage('SpeechBridge', 'ReceiveModelProgress', String(pct));
                }, 700);

                // vosk-browser 0.0.8 requires a ZIP archive URL, NOT a directory path.
                // The Model constructor fetches the ZIP and unpacks it inside WASM memory.
                // Pack the model folder as: StreamingAssets/vosk-model-small-ru.zip
                //   cd StreamingAssets && zip -r vosk-model-small-ru.zip vosk-model-small-ru/
                var model = new window.Vosk.Model(saUrl + '/vosk-model-small-ru.zip');

                model.addEventListener('load', function (event) {
                    clearInterval(progressTimer);

                    if (!event.detail || !event.detail.result) {
                        SendMessage('SpeechBridge', 'ReceiveError',
                                    'vosk:model-load-failed');
                        vb.loadInProgress = false;
                        return;
                    }

                    vb.model = model;
                    SendMessage('SpeechBridge', 'ReceiveModelProgress', '95');

                    // ── Step 3: create KaldiRecognizer, wait for 'ready' ─────
                    // Apply pending grammar if SetExpectedWords was called early
                    var grammar = vb.pendingGrammar;
                    vb.pendingGrammar = null;

                    VB_buildRecognizer(grammar, function () {
                        // Recognizer is now confirmed ready
                        SendMessage('SpeechBridge', 'ReceiveModelProgress', '100');
                        vb.loadInProgress = false;
                    });

                    // ── Step 4: request mic (parallel with recognizer init) ───
                    VB_requestMic();
                });

                model.addEventListener('error', function (event) {
                    clearInterval(progressTimer);
                    // CustomEvent.detail may contain { message, error, status } etc.
                    // Avoid "[object CustomEvent]" by inspecting detail fields.
                    var detail = event && event.detail;
                    var msg = 'unknown';
                    if (detail) {
                        if (typeof detail === 'string') {
                            msg = detail;
                        } else if (detail.message) {
                            msg = detail.message;
                        } else if (detail.error) {
                            msg = String(detail.error);
                        } else if (detail.status) {
                            msg = 'HTTP ' + detail.status;
                        } else {
                            try { msg = JSON.stringify(detail); } catch (_) { msg = 'parse-error'; }
                        }
                    }
                    SendMessage('SpeechBridge', 'ReceiveError', 'vosk:model-error:' + msg);
                    vb.loadInProgress = false;
                });
            },
            function (errMsg) {
                SendMessage('SpeechBridge', 'ReceiveError',
                            'vosk:script-load-failed:' + errMsg);
                vb.loadInProgress = false;
            }
        );
    },

    /**
     * StopRecognition — called by SpeechBridge.cs StopListening().
     * Disconnects the audio graph and stops mic tracks.
     * The Model and KaldiRecognizer are kept alive for fast restart.
     */
    StopRecognition: function () {
        var vb = VB;

        vb.isReady  = false;
        vb.micReady = false;

        if (vb.processorNode) { try { vb.processorNode.disconnect(); } catch (_) {} }
        if (vb.sourceNode)    { try { vb.sourceNode.disconnect();    } catch (_) {} }

        if (vb.micStream) {
            // DO NOT stop tracks if we are sharing window._micStream!
            if (vb.micStream !== window._micStream) {
                vb.micStream.getTracks().forEach(function (t) {
                    try { t.stop(); } catch (_) {}
                });
            }
            vb.micStream     = null;
            vb.sourceNode    = null;
            vb.processorNode = null;
        }

        // Flush any buffered audio in the recognizer
        if (vb.recognizer) {
            try { vb.recognizer.retrieveFinalResult(); } catch (_) {}
        }
    },

    /**
     * SetExpectedWords — called by SpeechBridge.cs UpdateExpectedWords().
     *
     * Grammar restriction is disabled to allow full vocabulary recognition
     * (prevents eager matching of long words from a single syllable).
     * Therefore, this function is now a no-op.
     */
    SetExpectedWords: function (wordsJsonPtr) {
        // No-op
    },

    /**
     * SetRecognitionConfig — called by SpeechBridge.cs ApplyRecognitionConfig().
     *
     * Accepts a JSON string: { "resultDebounceMsec": 600 }
     *
     * resultDebounceMsec takes effect immediately on the next result event.
     *
     * NOTE: Vosk endpointer rules are NOT configurable via the vosk-browser JS API.
     * KaldiRecognizer only accepts a JSGF grammar string as its second constructor
     * argument — passing endpointer JSON causes a WASM function signature mismatch.
     */
    SetRecognitionConfig: function (configJsonPtr) {
        var vb  = VB;
        var str = UTF8ToString(configJsonPtr);
        var cfg;
        try { cfg = JSON.parse(str); } catch (e) {
            console.warn('[VoskBridge] SetRecognitionConfig: invalid JSON —', str);
            return;
        }

        if (typeof cfg.resultDebounceMsec === 'number') {
            vb.resultDebounceMsec = cfg.resultDebounceMsec;
            // If debounce was just disabled, flush any pending words immediately
            if (vb.resultDebounceMsec <= 0 && vb.pendingWords.length > 0) {
                clearTimeout(vb.resultTimer);
                var batch = vb.pendingWords.slice();
                vb.pendingWords = [];
                batch.forEach(function (w) {
                    SendMessage('SpeechBridge', 'ReceiveWord', w);
                });
            }
        }

        console.log('[VoskBridge] SetRecognitionConfig applied: resultDebounceMsec=' + vb.resultDebounceMsec);
    },

    // ── Dependency declarations ───────────────────────────────────────────────
    StartRecognition__deps: [
        '$VB', '$VB_saUrl', '$VB_loadScript',
        '$VB_buildRecognizer', '$VB_requestMic', '$VB_tryActivate'
    ],
    StopRecognition__deps:    ['$VB'],
    SetExpectedWords__deps:   ['$VB', '$VB_buildRecognizer'],
    SetRecognitionConfig__deps: ['$VB'],
});
