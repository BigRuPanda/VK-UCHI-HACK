/**
 * mediapipe_hands.jslib
 * Unity WebGL JS plugin — initialises MediaPipe Hands, feeds webcam frames,
 * and posts normalised landmark data back to Unity via SendMessage.
 *
 * Exported functions (called from C# via [DllImport("__Internal")]):
 *   HT_InitHandTracking(gameObjectNamePtr, maxHands, minDetectionConfidence, minTrackingConfidence)
 *   HT_StartHandTracking()
 *   HT_StopHandTracking()
 *   HT_IsTracking() -> int (0/1)
 */

var HandTrackingPlugin = {

    // ─── internal state ────────────────────────────────────────────────────────
    $HT_State: {
        unityObjectName: "HandTrackingBridge",
        videoElement:    null,
        canvasElement:   null,
        hands:           null,
        camera:          null,
        isTracking:      false,
        animFrameId:     null,
    },

    // ─── helpers ───────────────────────────────────────────────────────────────

    /** Convert a C# string pointer to a JS string */
    $HT_PtrToStr: function(ptr) {
        return ptr ? UTF8ToString(ptr) : "";
    },

    /** Send a message to a Unity GameObject */
    $HT_Send: function(method, data) {
        var objName = HT_State.unityObjectName;
        var payload = data || "";
        try {
            // In modern Unity WebGL, SendMessage is available globally inside the jslib scope
            if (typeof SendMessage === 'function') {
                SendMessage(objName, method, payload);
                return;
            }
            if (typeof unityInstance !== "undefined" && unityInstance !== null) {
                unityInstance.SendMessage(objName, method, payload);
                return;
            }
            if (typeof window.unityInstance !== "undefined" && window.unityInstance !== null) {
                window.unityInstance.SendMessage(objName, method, payload);
                return;
            }
            if (typeof gameInstance !== "undefined" && gameInstance !== null) {
                gameInstance.SendMessage(objName, method, payload);
                return;
            }
            console.warn("[HandTracking] SendMessage failed: unityInstance not found.");
        } catch(e) {
            console.warn("[HandTracking] SendMessage error:", e);
        }
    },

    // ─── initialise MediaPipe Hands ────────────────────────────────────────────

    HT_InitHandTracking: function(gameObjectNamePtr, maxHands, minDetectionConf, minTrackingConf) {
        HT_State.unityObjectName = HT_PtrToStr(gameObjectNamePtr) || "HandTrackingBridge";

        // Create hidden video element
        if (!HT_State.videoElement) {
            var video = document.createElement("video");
            video.setAttribute("playsinline", "");
            video.setAttribute("autoplay",    "");
            video.setAttribute("muted",       "");
            video.style.position   = "absolute";
            video.style.width      = "1px";
            video.style.height     = "1px";
            video.style.opacity    = "0";
            video.style.pointerEvents = "none";
            document.body.appendChild(video);
            HT_State.videoElement = video;
        }

        // Create offscreen canvas for MediaPipe
        if (!HT_State.canvasElement) {
            var canvas = document.createElement("canvas");
            canvas.width  = 640;
            canvas.height = 480;
            canvas.style.display = "none";
            document.body.appendChild(canvas);
            HT_State.canvasElement = canvas;
        }

        // Fix for Unity/MediaPipe Emscripten Module conflict
        // MediaPipe's loader expects Module.dataFileDownloads to exist, but Unity's Module might not have it.
        if (typeof window.Module === 'undefined') {
            window.Module = {};
        }
        if (typeof window.Module.dataFileDownloads === 'undefined') {
            window.Module.dataFileDownloads = {};
        }

        // Load MediaPipe Hands from local StreamingAssets (offline mode)
        var mpHandsUrl = "StreamingAssets/mediapipe/hands.js";

        function loadScript(url, callback) {
            if (document.querySelector('script[src="' + url + '"]')) {
                callback(); return;
            }
            var s = document.createElement("script");
            s.src = url;
            s.onload  = callback;
            s.onerror = function() {
                HT_Send("OnTrackingError", "Failed to load MediaPipe from: " + url);
            };
            document.head.appendChild(s);
        }

        loadScript(mpHandsUrl, function() {
            try {
                var hands = new Hands({
                    locateFile: function(file) {
                        // Try to load from StreamingAssets first (offline mode)
                        // If that fails, fall back to CDN
                        var localPath = "StreamingAssets/mediapipe/" + file;
                        return localPath;
                    }
                });

                hands.setOptions({
                    maxNumHands:            maxHands            || 2,
                    modelComplexity:        0,                       // 0 = lite, faster
                    minDetectionConfidence: minDetectionConf    || 0.7,
                    minTrackingConfidence:  minTrackingConf     || 0.5,
                    selfieMode:             true,                    // mirror for natural feel
                });

                hands.onResults(function(results) {
                    HT_ProcessResults(results);
                });

                // Wait for models to load before telling C# we are ready
                hands.initialize().then(function() {
                    HT_State.hands = hands;
                    HT_Send("OnTrackingInitialised", "");
                }).catch(function(err) {
                    HT_Send("OnTrackingError", "MediaPipe initialize error: " + (err.message || err));
                });
            } catch(e) {
                HT_Send("OnTrackingError", "MediaPipe init error: " + e.message);
            }
        });
    },

    // ─── process landmark results ──────────────────────────────────────────────

    $HT_ProcessResults: function(results) {
        /*
         * Payload JSON schema:
         * {
         *   "hands": [
         *     {
         *       "label": "Left" | "Right",   // as seen in mirror (selfie) mode
         *       "score": 0.95,
         *       "wrist":    { "x": 0.5, "y": 0.6 },
         *       "indexTip": { "x": 0.4, "y": 0.3 },
         *       "thumbTip": { "x": 0.6, "y": 0.4 },
         *       "pinkyTip": { "x": 0.3, "y": 0.5 }
         *     }
         *   ]
         * }
         * All coords are normalised [0,1], origin top-left.
         */
        var payload = { hands: [] };

        if (results.multiHandLandmarks && results.multiHandedness) {
            for (var i = 0; i < results.multiHandLandmarks.length; i++) {
                var lm    = results.multiHandLandmarks[i];
                var label = results.multiHandedness[i].label;   // "Left" or "Right"
                var score = results.multiHandedness[i].score;

                // Landmark indices (MediaPipe Hands):
                // 0=WRIST, 4=THUMB_TIP, 8=INDEX_TIP, 12=MIDDLE_TIP, 16=RING_TIP, 20=PINKY_TIP
                payload.hands.push({
                    label:     label,
                    score:     score,
                    wrist:     { x: lm[0].x,  y: lm[0].y  },
                    thumbTip:  { x: lm[4].x,  y: lm[4].y  },
                    indexTip:  { x: lm[8].x,  y: lm[8].y  },
                    middleTip: { x: lm[12].x, y: lm[12].y },
                    ringTip:   { x: lm[16].x, y: lm[16].y },
                    pinkyTip:  { x: lm[20].x, y: lm[20].y },
                });
            }
        }

        HT_Send("OnHandData", JSON.stringify(payload));
    },

    // ─── start / stop ──────────────────────────────────────────────────────────

    HT_StartHandTracking: function() {
        if (HT_State.isTracking) return;
        if (!HT_State.hands) {
            HT_Send("OnTrackingError", "Call HT_InitHandTracking before HT_StartHandTracking.");
            return;
        }

        // C# has already requested camera permission via WebCamTexture, so this will succeed instantly.
        navigator.mediaDevices.getUserMedia({ video: { width: 640, height: 480 }, audio: false })
            .then(function(stream) {
                HT_State.videoElement.srcObject = stream;
                HT_State.videoElement.play();
                HT_State.isTracking = true;

                // Pump frames into MediaPipe
                function sendFrame() {
                    if (!HT_State.isTracking) return;
                    if (HT_State.videoElement.readyState >= 2) {
                        HT_State.hands.send({ image: HT_State.videoElement })
                            .catch(function(e) {
                                console.warn("[HandTracking] frame send error:", e);
                            });
                    }
                    HT_State.animFrameId = requestAnimationFrame(sendFrame);
                }
                HT_State.animFrameId = requestAnimationFrame(sendFrame);
                HT_Send("OnTrackingStarted", "");
            })
            .catch(function(err) {
                HT_State.isTracking = false;
                HT_Send("OnTrackingError", "getUserMedia failed: " + (err.message || err.name));
            });
    },

    HT_StopHandTracking: function() {
        HT_State.isTracking = false;

        if (HT_State.animFrameId) {
            cancelAnimationFrame(HT_State.animFrameId);
            HT_State.animFrameId = null;
        }

        if (HT_State.videoElement && HT_State.videoElement.srcObject) {
            HT_State.videoElement.srcObject.getTracks().forEach(function(t) { t.stop(); });
            HT_State.videoElement.srcObject = null;
        }

        HT_Send("OnTrackingStopped", "");
    },

    HT_IsTracking: function() {
        return HT_State.isTracking ? 1 : 0;
    },
};

// Register dependencies so the closure compiler keeps them
autoAddDeps(HandTrackingPlugin, "$HT_State");
autoAddDeps(HandTrackingPlugin, "$HT_PtrToStr");
autoAddDeps(HandTrackingPlugin, "$HT_Send");
autoAddDeps(HandTrackingPlugin, "$HT_ProcessResults");
mergeInto(LibraryManager.library, HandTrackingPlugin);
