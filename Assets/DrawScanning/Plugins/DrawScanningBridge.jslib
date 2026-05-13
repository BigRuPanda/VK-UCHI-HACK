/**
 * DrawScanningBridge.jslib
 * -------------------------
 * Unity WebGL JavaScript plugin (jslib).
 * Bridges C# calls (via [DllImport("__Internal")]) to the drawScanning.js module.
 *
 * All functions prefixed with DS_ to avoid name collisions.
 *
 * Unity → JS direction: functions defined here, called from C# via DllImport.
 * JS → Unity direction: drawScanning.js calls SendMessage(goName, method, value).
 */

mergeInto(LibraryManager.library, {

  /**
   * Inject and initialise the drawScanning.js module.
   *
   * @param base64Ptr    Pointer to Base64-encoded PNG string of the reference image
   * @param refW         Reference image original width (informational)
   * @param refH         Reference image original height (informational)
   * @param goNamePtr    Pointer to Unity GameObject name string (for SendMessage)
   * @param acceptThr    Score threshold (float, 0–1) to fire OnDrawingAccepted
   * @param blockSize    Adaptive threshold block size (odd int, e.g. 45)
   * @param adaptC       Adaptive threshold C offset (int, e.g. 7)
   * @param huW          Hu Moments weight vs Edge IoU (float 0–1, e.g. 0.5)
   * @param topN         Number of largest contours to keep (int, e.g. 3)
   */
  DS_InitScanner: function (base64Ptr, refW, refH, goNamePtr, acceptThr, blockSize, adaptC, huW, topN) {
    var base64 = UTF8ToString(base64Ptr);
    var goName = UTF8ToString(goNamePtr);

    // DS_Init is defined in drawScanning.jspre (loaded as global JS before Unity module).
    // Check both bare name and window.DS_Init for robustness across Unity versions.
    var initFn = (typeof DS_Init === 'function') ? DS_Init
               : (typeof window !== 'undefined' && typeof window.DS_Init === 'function') ? window.DS_Init
               : null;

    if (!initFn) {
      console.error('[DrawScanning] drawScanning.jspre is not loaded. ' +
        'Make sure drawScanning.jspre is placed in Assets/DrawScanning/Plugins/.');
      return;
    }

    initFn(base64, refW, refH, goName, acceptThr, blockSize, adaptC, huW, topN);
  },

  /**
   * Start the webcam capture and real-time processing loop.
   * Triggers OnScannerReady → OnSimilarityUpdate callbacks on the Unity side.
   *
   * NOTE: We use window['DS_StartScanning'] exclusively — NOT the bare identifier
   * DS_StartScanning, which inside this jslib body would refer to THIS function
   * itself (causing infinite recursion). The real implementation lives in
   * drawScanning.jspre and is exposed on window by that module.
   */
  DS_StartScanning: function () {
    var fn = window['DS_StartScanning'];
    if (typeof fn === 'function') { fn(); }
    else { console.error('[DrawScanning] DS_StartScanning not found on window — is drawScanning.jspre loaded?'); }
  },

  /**
   * Stop the processing loop and release the camera stream.
   */
  DS_StopScanning: function () {
    var fn = window['DS_StopScanning'];
    if (typeof fn === 'function') { fn(); }
    else { console.error('[DrawScanning] DS_StopScanning not found on window.'); }
  },

  /**
   * Update CV parameters at runtime without re-initialising.
   */
  DS_SetThresholds: function (acceptThr, blockSize, adaptC, huW, topN) {
    var fn = window['DS_SetThresholds'];
    if (typeof fn === 'function') { fn(acceptThr, blockSize, adaptC, huW, topN); }
    else { console.error('[DrawScanning] DS_SetThresholds not found on window.'); }
  },

  /**
   * Reset the "accepted" flag so OnDrawingAccepted can fire again.
   */
  DS_ResetAccepted: function () {
    var fn = window['DS_ResetAccepted'];
    if (typeof fn === 'function') { fn(); }
    else { console.error('[DrawScanning] DS_ResetAccepted not found on window.'); }
  },

  /**
   * Request camera permission from the browser without starting the CV loop.
   *
   * MUST be called from a direct user-gesture handler (button click) so the
   * browser shows the permission dialog. On success fires JS_OnCameraGranted
   * on the Unity GameObject; on failure fires JS_OnCameraError.
   *
   * IMPORTANT: getUserMedia is called DIRECTLY inside this jslib function body.
   * Any indirection (e.g. calling window.someFunction()) breaks the user-gesture
   * chain and causes browsers to silently block the permission dialog.
   * This is the same pattern used by MicrophonePlugin.jslib.
   *
   * Typical flow:
   *   1. Unity shows in-game "Allow Camera" panel.
   *   2. Player clicks the panel button → C# calls DS_RequestCameraPermission.
   *   3. Browser shows native permission dialog (gesture preserved).
   *   4. On grant → JS fires JS_OnCameraGranted → C# hides panel, calls DS_StartScanning.
   *   5. On deny  → JS fires JS_OnCameraError  → C# shows "Camera unavailable" UI.
   */
  DS_RequestCameraPermission: function (goNamePtr) {
    var goName = UTF8ToString(goNamePtr);

    // If camera is already open, fire granted immediately
    if (window._dsStream) {
      SendMessage(goName, 'JS_OnCameraGranted', '1');
      return;
    }

    // getUserMedia called DIRECTLY here — preserves the browser user-gesture chain.
    // Do NOT wrap this in window.someFunction() or setTimeout — that breaks the gesture.
    var constraints = {
      video: { width: { ideal: 640 }, height: { ideal: 480 }, facingMode: 'environment' },
      audio: false
    };

    var gum = (navigator.mediaDevices && navigator.mediaDevices.getUserMedia)
      ? function(c, ok, err) { navigator.mediaDevices.getUserMedia(c).then(ok).catch(err); }
      : function(c, ok, err) {
          var legacy = navigator.getUserMedia || navigator.webkitGetUserMedia || navigator.mozGetUserMedia;
          if (legacy) { legacy.call(navigator, c, ok, err); }
          else { err(new Error('getUserMedia not supported')); }
        };

    gum(
      constraints,
      function(stream) {
        console.log('[DrawScanning] Camera permission granted.');
        // Store stream globally so DS_StartScanning can reuse it without a second getUserMedia call
        window._dsStream = stream;
        // Notify drawScanning.js that the stream is ready (sets DS.cameraReady, DS.video)
        if (typeof DS_OnCameraStreamReady === 'function') {
          DS_OnCameraStreamReady(stream);
        }
        SendMessage(goName, 'JS_OnCameraGranted', '1');
      },
      function(err) {
        console.error('[DrawScanning] Camera permission denied:', err.name, err.message);
        SendMessage(goName, 'JS_OnCameraError', err.name + ': ' + err.message);
      }
    );
  },

  /**
   * Copy the latest preview frame RGBA pixels into Unity managed memory.
   *
   * The preview frame is a raw RGBA image captured from the live webcam feed
   * at previewW × previewH resolution (default 320 × 240).
   * Unity C# allocates a byte[] of size previewW * previewH * 4 and passes
   * a pinned pointer to this function.
   *
   * @param pixelPtr  Pointer into Unity WASM heap (pre-allocated, pinned byte[])
   * @param byteLen   Length of the buffer in bytes (must be >= previewW*previewH*4)
   * @returns         1 if pixels were copied successfully, 0 if no frame available yet
   *
   * Called from DrawScanningChecker.cs via [DllImport("__Internal")].
   */
  DS_GetPreviewFrame: function (pixelPtr, byteLen) {
    var fn = window['DS_GetPreviewPixels'];
    if (typeof fn !== 'function') return 0;
    var pixels = fn();
    if (!pixels || pixels.length < byteLen) return 0;
    HEAPU8.set(pixels.subarray(0, byteLen), pixelPtr);
    return 1;
  },

  /**
   * Copy the latest CV-processed binary silhouette pixels into Unity managed memory.
   * White (255,255,255,255) = foreground strokes, Black (0,0,0,255) = background.
   * Size: procW * procH * 4 bytes (default 256*256*4 = 262144).
   *
   * @param pixelPtr  Pointer into Unity WASM heap (pre-allocated, pinned byte[])
   * @param byteLen   Length of the buffer in bytes (must be >= procW*procH*4)
   * @returns         1 if pixels were copied successfully, 0 if no frame available yet
   */
  DS_GetProcessedFrame: function (pixelPtr, byteLen) {
    var fn = window['DS_GetProcessedPixels'];
    if (typeof fn !== 'function') return 0;
    var pixels = fn();
    if (!pixels || pixels.length < byteLen) return 0;
    HEAPU8.set(pixels.subarray(0, byteLen), pixelPtr);
    return 1;
  },

  /**
   * Copy the pre-computed reference image binary silhouette pixels into Unity managed memory.
   * White = foreground, Black = background. Static after DS_Init completes.
   * Size: procW * procH * 4 bytes (default 256*256*4 = 262144).
   *
   * @param pixelPtr  Pointer into Unity WASM heap (pre-allocated, pinned byte[])
   * @param byteLen   Length of the buffer in bytes (must be >= procW*procH*4)
   * @returns         1 if pixels were copied successfully, 0 if reference not ready yet
   */
  DS_GetReferenceFrame: function (pixelPtr, byteLen) {
    var fn = window['DS_GetReferencePixels'];
    if (typeof fn !== 'function') return 0;
    var pixels = fn();
    if (!pixels || pixels.length < byteLen) return 0;
    HEAPU8.set(pixels.subarray(0, byteLen), pixelPtr);
    return 1;
  }

});
