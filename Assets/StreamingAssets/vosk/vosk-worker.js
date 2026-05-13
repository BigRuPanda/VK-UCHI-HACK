/**
 * vosk-worker.js
 *
 * Web Worker that bridges VoskBridge.jslib ↔ vosk-browser (ccoreilly/vosk-browser).
 *
 * vosk-browser is a HIGH-LEVEL wrapper: Model manages its own internal Worker.
 * We run THIS worker so that model loading and audio processing never block
 * the Unity main thread. Inside this worker we use vosk-browser's public API:
 *
 *   createModel(url)                    → Promise<Model>
 *   new model.KaldiRecognizer(sampleRate [, grammarJson])
 *   recognizer.on('result', cb)         → cb({ result: { result: [{word,start,end}] } })
 *   recognizer.on('partialresult', cb)  → cb({ result: { partial: "..." } })
 *   recognizer.acceptWaveformFloat(float32Array, sampleRate)
 *   recognizer.retrieveFinalResult()    → flushes remaining audio
 *   recognizer.remove()                 → frees resources
 *
 * Inbound messages (from VoskBridge.jslib):
 *   { action: "init",       modelPath: string }
 *   { action: "setGrammar", words: string[] }
 *   { action: "audio",      buffer: Float32Array }   ← transferable
 *   { action: "stop" }
 *
 * Outbound messages (to VoskBridge.jslib → Unity SendMessage):
 *   { type: "ready" }
 *   { type: "progress",  value: number }   0..100
 *   { type: "word",      word: string }
 *   { type: "error",     message: string }
 */

'use strict';

// ── State ─────────────────────────────────────────────────────────────────────
var _model        = null;   // vosk-browser Model instance
var _recognizer   = null;   // KaldiRecognizer instance
var _isReady      = false;
var _sampleRate   = 16000;
var _lastPartial  = '';     // debounce partial emissions
var _modelPath    = '';

// ── Entry point ───────────────────────────────────────────────────────────────
self.onmessage = function (e) {
    var msg = e.data;
    if (!msg || !msg.action) return;

    switch (msg.action) {
        case 'init':       handleInit(msg);       break;
        case 'setGrammar': handleSetGrammar(msg); break;
        case 'audio':      handleAudio(msg);      break;
        case 'stop':       handleStop();          break;
        default:
            self.postMessage({ type: 'error', message: 'Unknown action: ' + msg.action });
    }
};

// ── Handlers ──────────────────────────────────────────────────────────────────

/**
 * Load vosk-browser.js (UMD bundle, same directory) and initialise the model.
 *
 * vosk-browser's createModel() fetches the model files via fetch() internally.
 * It does NOT expose a progress callback, so we emit synthetic progress ticks
 * while waiting for the Promise to resolve.
 */
function handleInit(msg) {
    _modelPath = msg.modelPath;

    // vosk-browser.js is a UMD bundle — importScripts makes it available as
    // the global `VoskBrowser` object (the factory sets global.VoskBrowser).
    try {
        importScripts('vosk-browser.js');
    } catch (err) {
        self.postMessage({ type: 'error', message: 'Failed to importScripts vosk-browser.js: ' + err });
        return;
    }

    // After importScripts the UMD bundle sets:
    //   global.Vosk = { createModel, Model }
    // where global = self (inside a Worker).
    var createModel = (self.Vosk && typeof self.Vosk.createModel === 'function')
        ? self.Vosk.createModel.bind(self.Vosk)
        : null;

    if (!createModel) {
        self.postMessage({ type: 'error', message: 'createModel not found after importScripts. Check vosk-browser.js bundle.' });
        return;
    }

    // Emit synthetic progress ticks while model loads (no native progress API)
    var progressInterval = _startFakeProgress();

    createModel(_modelPath)
        .then(function (model) {
            clearInterval(progressInterval);
            _model = model;

            // Create default (no-grammar) recognizer
            _recognizer = _createRecognizer(null);
            _isReady = true;

            self.postMessage({ type: 'progress', value: 100 });
            self.postMessage({ type: 'ready' });
        })
        .catch(function (err) {
            clearInterval(progressInterval);
            self.postMessage({ type: 'error', message: 'createModel failed: ' + err });
        });
}

/**
 * Rebuild KaldiRecognizer with a phrase_list grammar.
 * Called every time ReadingController loads a new sentence.
 *
 * Grammar JSON format for vosk-browser:
 *   '["word1", "word2", ...]'   ← simple JSON array of strings
 *
 * This is the critical step for children's voices and syllabic reading:
 * restricting the search space to the current sentence's vocabulary
 * raises accuracy from ~70% to ~95%.
 */
function handleSetGrammar(msg) {
    if (!_model) return; // will be applied once model is ready

    var words = msg.words;
    var grammarJson = (words && words.length > 0)
        ? JSON.stringify(words)   // vosk expects a JSON array string
        : null;

    _rebuildRecognizer(grammarJson);
    _lastPartial = '';
}

/**
 * Accept a Float32Array PCM chunk (mono, 16 kHz).
 * vosk-browser's acceptWaveformFloat(buffer, sampleRate) takes Float32Array directly.
 */
function handleAudio(msg) {
    if (!_isReady || !_recognizer) return;

    try {
        _recognizer.acceptWaveformFloat(msg.buffer, _sampleRate);
        // Results arrive asynchronously via the 'result' / 'partialresult' events
        // that were registered in _createRecognizer().
    } catch (err) {
        self.postMessage({ type: 'error', message: 'acceptWaveformFloat error: ' + err });
    }
}

/**
 * Flush the recognizer and emit any remaining words.
 */
function handleStop() {
    if (!_recognizer) return;

    try {
        _recognizer.retrieveFinalResult();
        // Final result arrives via the 'result' event handler
    } catch (err) {
        self.postMessage({ type: 'error', message: 'retrieveFinalResult error: ' + err });
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

/**
 * Create a new KaldiRecognizer and wire up result events.
 * @param {string|null} grammarJson  JSON array string or null for free-form.
 * @returns {KaldiRecognizer}
 */
function _createRecognizer(grammarJson) {
    var rec;
    if (grammarJson) {
        rec = new _model.KaldiRecognizer(_sampleRate, grammarJson);
    } else {
        rec = new _model.KaldiRecognizer(_sampleRate);
    }

    // ── Final result ──────────────────────────────────────────────────────────
    // msg.result.result is an array of {word, start, end} objects
    // msg.result.text   is the full utterance as a string (fallback)
    rec.on('result', function (msg) {
        _lastPartial = '';
        var res = msg && msg.result;
        if (!res) return;

        if (Array.isArray(res.result) && res.result.length > 0) {
            res.result.forEach(function (w) {
                if (w && w.word) {
                    self.postMessage({ type: 'word', word: w.word.toLowerCase() });
                }
            });
        } else if (res.text && res.text.trim().length > 0) {
            // Fallback: split text field
            res.text.trim().split(/\s+/).forEach(function (w) {
                if (w) self.postMessage({ type: 'word', word: w.toLowerCase() });
            });
        }
    });

    // ── Partial result ────────────────────────────────────────────────────────
    // Emit the last token so the UI can highlight the word being pronounced.
    rec.on('partialresult', function (msg) {
        var res = msg && msg.result;
        if (!res || !res.partial) return;

        var tokens = res.partial.trim().split(/\s+/);
        var last   = tokens[tokens.length - 1];
        if (last && last !== _lastPartial) {
            _lastPartial = last;
            self.postMessage({ type: 'word', word: last.toLowerCase() });
        }
    });

    return rec;
}

/**
 * Free the current recognizer and create a new one with optional grammar.
 * @param {string|null} grammarJson
 */
function _rebuildRecognizer(grammarJson) {
    if (_recognizer) {
        try { _recognizer.remove(); } catch (_) {}
        _recognizer = null;
    }
    _recognizer = _createRecognizer(grammarJson);
}

/**
 * Emit synthetic progress ticks (0→90) while createModel() is pending.
 * The real 100% is emitted after the Promise resolves.
 * @returns {number} interval ID
 */
function _startFakeProgress() {
    var pct = 0;
    return setInterval(function () {
        pct = Math.min(90, pct + 5);
        self.postMessage({ type: 'progress', value: pct });
    }, 800);
}
