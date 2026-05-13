/**
 * drawScanning.js / drawScanning.jspre
 * ----------------------------------
 * Client-side real-time drawing scanner for Unity WebGL.
 *
 * Unity loads the `.jspre` copy at runtime. Keep this source file and
 * `drawScanning.jspre` synchronized after every change.
 *
 * Pipeline:
 *   1. Capture webcam frame in browser canvas coordinates (top-left origin).
 *   2. Detect a bright A4-like paper quadrilateral in the frame.
 *   3. Wait until the paper is stable for several processed frames.
 *   4. Perspective-warp the paper to a canonical square canvas.
 *   5. Extract dark drawing strokes from the warped page.
 *   6. Compare strokes with the Unity-assigned reference texture using
 *      coverage, extra-stroke penalty, dilated IoU, chamfer distance and Hu
 *      moments.
 *   7. Send a smoothed score to Unity every processed frame.
 *
 * No server, no external libraries.
 */

(function (global) {
  'use strict';

  var DS = {
    video: null,
    offscreenCanvas: null,
    offscreenCtx: null,
    warpCanvas: null,
    warpCtx: null,
    previewCanvas: null,
    previewCtx: null,

    previewW: 320,
    previewH: 240,
    previewPixels: null,
    previewSkip: 2,
    previewFrameCount: 0,

    procW: 256,
    procH: 256,

    refMask: null,
    refEdges: null,
    refDilated: null,
    refDistance: null,
    refMoments: null,
    refPixels: null,

    processedPixels: null,
    warpedPixels: null,
    heatmapPixels: null,

    isRunning: false,
    rafId: null,
    frameCount: 0,
    frameSkip: 3,

    refReady: false,
    cameraReady: false,
    startPending: false,

    scoreHistory: [],
    scoreHistoryLen: 7,
    emaScore: 0,
    stableAcceptFrames: 0,
    requiredAcceptFrames: 8,

    acceptThreshold: 0.55,
    adaptiveBlockSize: 45,
    adaptiveC: 7,
    huWeight: 0.25,
    topNContours: 3,

    minPaperArea: 0.18,
    maxPaperArea: 0.96,
    stableCornerThreshold: 10.0,
    requiredStablePaperFrames: 3,
    paperStableFrames: 0,
    lastPaperQuad: null,
    lastPaperFound: false,

    gameObjectName: '',
    noPaperTimer: 0,
    noDrawingTimer: 0,
    stuckTimer: 0,
  };

  // ─────────────────────────────────────────────────────────────────────────
  // Public API
  // ─────────────────────────────────────────────────────────────────────────

  global.DS_Init = function (base64png, refW, refH, goName, acceptThr, blockSize, adaptC, huW, topN) {
    DS.gameObjectName = goName || 'DrawScanningChecker';
    DS.acceptThreshold = _numOr(acceptThr, 0.55);
    DS.adaptiveBlockSize = _odd(Math.max(3, Math.round(_numOr(blockSize, 45))));
    DS.adaptiveC = Math.max(0, Math.round(_numOr(adaptC, 7)));
    DS.huWeight = _clamp01(_numOr(huW, 0.25));
    DS.topNContours = Math.max(1, Math.round(_numOr(topN, 3)));

    _ensureCanvases();

    DS.refReady = false;
    DS.cameraReady = false;
    DS.startPending = false;
    DS.scoreHistory = [];
    DS.emaScore = 0;
    DS.stableAcceptFrames = 0;
    DS.paperStableFrames = 0;
    DS.lastPaperQuad = null;
    DS.lastPaperFound = false;

    console.log('[DrawScanning] DS_Init free A4 scanner. goName=' + DS.gameObjectName +
      ' threshold=' + DS.acceptThreshold +
      ' block=' + DS.adaptiveBlockSize +
      ' C=' + DS.adaptiveC +
      ' huWeight=' + DS.huWeight +
      ' topN=' + DS.topNContours);

    _loadReferenceImage(base64png, function () {
      DS.refReady = true;
      console.log('[DrawScanning] Reference processed for free A4 scanner.');
      _sendToUnity('OnScannerReady', '1');
      _tryStartLoop();
    });
  };

  global.DS_StartScanning = function () {
    console.log('[DrawScanning] DS_StartScanning called. running=' + DS.isRunning +
      ' cameraReady=' + DS.cameraReady + ' refReady=' + DS.refReady);
    if (DS.isRunning) return;
    DS.startPending = true;

    if (DS.cameraReady) {
      _tryStartLoop();
      return;
    }

    _startCamera(function () {
      DS.cameraReady = true;
      _sendToUnity('OnCameraGranted', '1');
      _tryStartLoop();
    });
  };

  global.DS_OnCameraStreamReady = function (stream) {
    if (DS.cameraReady) return;
    _attachStream(stream, function () {
      DS.cameraReady = true;
      if (DS.startPending) _tryStartLoop();
    }, function (err) {
      _sendToUnity('OnCameraError', 'video_play_failed: ' + err.message);
    });
  };

  global.DS_RequestCameraPermission = function () {
    // Permission is requested directly in DrawScanningBridge.jslib to preserve
    // the browser user-gesture chain. This function is intentionally kept for
    // backwards compatibility.
  };

  global.DS_SetThresholds = function (acceptThr, blockSize, adaptC, huW, topN) {
    if (typeof acceptThr === 'number') DS.acceptThreshold = _clamp01(acceptThr);
    if (typeof blockSize === 'number' && blockSize > 0) DS.adaptiveBlockSize = _odd(Math.round(blockSize));
    if (typeof adaptC === 'number') DS.adaptiveC = Math.max(0, Math.round(adaptC));
    if (typeof huW === 'number') DS.huWeight = _clamp01(huW);
    if (typeof topN === 'number' && topN > 0) DS.topNContours = Math.round(topN);

    if (DS.refMask) _rebuildReferenceDerivedData();
  };

  global.DS_ResetAccepted = function () {
    DS.stableAcceptFrames = 0;
    DS.scoreHistory = [];
    DS.emaScore = 0;
  };

  global.DS_StopScanning = function () {
    DS.isRunning = false;
    DS.startPending = false;
    DS.cameraReady = false;
    if (DS.rafId) {
      cancelAnimationFrame(DS.rafId);
      DS.rafId = null;
    }
    if (DS.video && DS.video.srcObject) {
      DS.video.srcObject.getTracks().forEach(function (t) { t.stop(); });
      DS.video.srcObject = null;
    }
    if (global._dsStream) {
      global._dsStream.getTracks().forEach(function (t) { t.stop(); });
      global._dsStream = null;
    }
    DS.previewPixels = null;
  };

  global.DS_GetPreviewPixels = function () { return DS.previewPixels; };
  global.DS_GetProcessedPixels = function () { return DS.processedPixels; };
  global.DS_GetReferencePixels = function () { return DS.refPixels; };
  global.DS_GetWarpedPixels = function () { return DS.warpedPixels; };
  global.DS_GetHeatmapPixels = function () { return DS.heatmapPixels; };

  global.DS_GetPreviewSize = function () {
    return JSON.stringify({ w: DS.previewW, h: DS.previewH });
  };

  // ─────────────────────────────────────────────────────────────────────────
  // Camera and loop
  // ─────────────────────────────────────────────────────────────────────────

  function _ensureCanvases() {
    if (!DS.offscreenCanvas) {
      DS.offscreenCanvas = document.createElement('canvas');
      DS.offscreenCanvas.width = DS.procW;
      DS.offscreenCanvas.height = DS.procH;
      DS.offscreenCtx = DS.offscreenCanvas.getContext('2d', { willReadFrequently: true });
    }
    if (!DS.warpCanvas) {
      DS.warpCanvas = document.createElement('canvas');
      DS.warpCanvas.width = DS.procW;
      DS.warpCanvas.height = DS.procH;
      DS.warpCtx = DS.warpCanvas.getContext('2d', { willReadFrequently: true });
    }
    if (!DS.previewCanvas) {
      DS.previewCanvas = document.createElement('canvas');
      DS.previewCanvas.width = DS.previewW;
      DS.previewCanvas.height = DS.previewH;
      DS.previewCtx = DS.previewCanvas.getContext('2d', { willReadFrequently: true });
    }
  }

  function _startCamera(onReady) {
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      _sendToUnity('OnCameraError', 'getUserMedia_not_supported');
      return;
    }

    var constraints = {
      video: {
        width: { ideal: 960 },
        height: { ideal: 720 },
        facingMode: 'environment'
      },
      audio: false
    };

    navigator.mediaDevices.getUserMedia(constraints).then(function (stream) {
      global._dsStream = stream;
      _attachStream(stream, onReady, function (err) {
        _sendToUnity('OnCameraError', 'video_play_failed: ' + err.message);
      });
    }).catch(function (err) {
      _sendToUnity('OnCameraError', err.name + ': ' + err.message);
    });
  }

  function _attachStream(stream, onReady, onError) {
    if (!DS.video) {
      DS.video = document.createElement('video');
      DS.video.setAttribute('playsinline', '');
      DS.video.setAttribute('autoplay', '');
      DS.video.muted = true;
      DS.video.style.display = 'none';
      document.body.appendChild(DS.video);
    }
    DS.video.srcObject = stream;
    DS.video.play().then(onReady).catch(onError);
  }

  function _tryStartLoop() {
    console.log('[DrawScanning] _tryStartLoop pending=' + DS.startPending +
      ' camera=' + DS.cameraReady + ' ref=' + DS.refReady);
    if (!DS.startPending || !DS.cameraReady || !DS.refReady) return;

    DS.startPending = false;
    DS.isRunning = true;
    DS.frameCount = 0;
    DS.previewFrameCount = 0;
    DS.scoreHistory = [];
    DS.emaScore = 0;
    DS.stableAcceptFrames = 0;
    DS.paperStableFrames = 0;
    DS.lastPaperQuad = null;
    DS.lastPaperFound = false;
    DS.noPaperTimer = 0;
    DS.noDrawingTimer = 0;
    DS.stuckTimer = 0;
    _loop();
  }

  var _logFrameInterval = 60;

  function _loop() {
    if (!DS.isRunning) return;
    DS.rafId = requestAnimationFrame(_loop);

    DS.frameCount++;
    if (DS.frameCount % DS.frameSkip !== 0) return;
    if (!DS.video || DS.video.readyState < 2) return;

    _capturePreview();

    var result = _processFrame();
    var smoothed = _smooth(result.score);

    _updateHints(smoothed, result);

    var processedFrame = DS.frameCount / DS.frameSkip;
    if (processedFrame % _logFrameInterval === 1) {
      console.log('[DrawScanning] score=' + smoothed.toFixed(4) +
        ' raw=' + result.score.toFixed(4) +
        ' paper=' + (result.paperFound ? '1' : '0') +
        ' stable=' + DS.paperStableFrames +
        ' coverage=' + result.coverage.toFixed(3) +
        ' extra=' + result.extra.toFixed(3) +
        ' iou=' + result.iou.toFixed(3) +
        ' chamfer=' + result.chamfer.toFixed(3));
    }

    _sendToUnity('OnSimilarityUpdate', smoothed.toFixed(4));
  }

  function _capturePreview() {
    DS.previewFrameCount++;
    if (DS.previewFrameCount % DS.previewSkip !== 0 || !DS.previewCtx) return;
    var pCtx = DS.previewCtx;
    pCtx.setTransform(1, 0, 0, 1, 0, 0);
    pCtx.clearRect(0, 0, DS.previewW, DS.previewH);
    pCtx.drawImage(DS.video, 0, 0, DS.previewW, DS.previewH);

    if (DS.lastPaperFound && DS.lastPaperQuad) {
      var sx = DS.previewW / DS.procW;
      var sy = DS.previewH / DS.procH;
      pCtx.save();
      pCtx.strokeStyle = 'rgba(60,255,120,0.95)';
      pCtx.lineWidth = 3;
      pCtx.beginPath();
      pCtx.moveTo(DS.lastPaperQuad[0].x * sx, DS.lastPaperQuad[0].y * sy);
      for (var i = 1; i < 4; i++) pCtx.lineTo(DS.lastPaperQuad[i].x * sx, DS.lastPaperQuad[i].y * sy);
      pCtx.closePath();
      pCtx.stroke();
      pCtx.restore();
    }

    var img = pCtx.getImageData(0, 0, DS.previewW, DS.previewH);
    DS.previewPixels = _imageDataToUnityRGBA(img, DS.previewW, DS.previewH);
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Main processing
  // ─────────────────────────────────────────────────────────────────────────

  function _processFrame() {
    var W = DS.procW, H = DS.procH;
    var ctx = DS.offscreenCtx;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, W, H);
    ctx.drawImage(DS.video, 0, 0, W, H);

    var frameData = ctx.getImageData(0, 0, W, H);
    var gray = _toGrayscale(frameData.data, W, H);
    var paper = _detectPaper(gray, W, H);

    if (!paper.found) {
      DS.lastPaperFound = false;
      DS.paperStableFrames = 0;
      DS.processedPixels = _emptyUnityRGBA(W * H);
      DS.warpedPixels = _imageDataToUnityRGBA(frameData, W, H);
      DS.heatmapPixels = _emptyUnityRGBA(W * H);
      return _emptyScore(false);
    }

    DS.lastPaperFound = true;
    _updatePaperStability(paper.quad);

    var warpedData = _warpImageData(frameData, W, H, paper.quad, W, H);
    DS.warpedPixels = _imageDataToUnityRGBA(warpedData, W, H);

    var strokeMask = _extractStrokeMask(warpedData.data, W, H, false);
    var filtered = _keepTopComponents(strokeMask, W, H, DS.topNContours, 10);
    var strokeCount = _countNonZero(filtered, W * H);

    DS.processedPixels = _binaryToUnityRGBA(filtered, W, H);

    if (strokeCount < W * H * 0.003) {
      DS.heatmapPixels = _buildHeatmap(filtered, DS.refMask, W, H);
      return _emptyScore(true);
    }

    var metrics = _scoreMasks(filtered, W, H);
    var paperGate = DS.paperStableFrames >= DS.requiredStablePaperFrames ? 1.0 : 0.35;
    var finalScore = _clamp01(metrics.score * paperGate);

    DS.heatmapPixels = _buildHeatmap(filtered, DS.refMask, W, H);

    return {
      score: finalScore,
      paperFound: true,
      coverage: metrics.coverage,
      extra: metrics.extra,
      iou: metrics.iou,
      chamfer: metrics.chamfer,
      hu: metrics.hu
    };
  }

  function _emptyScore(paperFound) {
    return { score: 0, paperFound: !!paperFound, coverage: 0, extra: 1, iou: 0, chamfer: 0, hu: 0 };
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Paper detection
  // ─────────────────────────────────────────────────────────────────────────

  function _detectPaper(gray, W, H) {
    var blurred = _boxBlurGray(gray, W, H, 5);
    var threshold = Math.max(135, _percentile(blurred, 0.70));
    var mask = new Uint8Array(W * H);
    for (var i = 0; i < blurred.length; i++) {
      mask[i] = blurred[i] >= threshold ? 255 : 0;
    }
    mask = _morphClose3x3(mask, W, H);
    mask = _morphOpen3x3(mask, W, H);

    var comp = _largestComponent(mask, W, H, Math.floor(W * H * DS.minPaperArea));
    if (!comp) return { found: false, quad: null };

    var areaRatio = comp.size / (W * H);
    if (areaRatio < DS.minPaperArea || areaRatio > DS.maxPaperArea) return { found: false, quad: null };

    var quad = _componentExtremeQuad(comp.pixels, W);
    quad = _orderQuad(quad);
    if (!_validateQuad(quad, W, H)) return { found: false, quad: null };

    return { found: true, quad: quad };
  }

  function _componentExtremeQuad(pixels, W) {
    var tl = null, tr = null, br = null, bl = null;
    var minSum = Infinity, maxSum = -Infinity, minDiff = Infinity, maxDiff = -Infinity;
    for (var i = 0; i < pixels.length; i++) {
      var idx = pixels[i];
      var x = idx % W;
      var y = (idx / W) | 0;
      var sum = x + y;
      var diff = x - y;
      if (sum < minSum) { minSum = sum; tl = { x: x, y: y }; }
      if (sum > maxSum) { maxSum = sum; br = { x: x, y: y }; }
      if (diff < minDiff) { minDiff = diff; bl = { x: x, y: y }; }
      if (diff > maxDiff) { maxDiff = diff; tr = { x: x, y: y }; }
    }
    return [tl, tr, br, bl];
  }

  function _orderQuad(q) {
    var cx = 0, cy = 0;
    for (var i = 0; i < 4; i++) { cx += q[i].x; cy += q[i].y; }
    cx /= 4; cy /= 4;
    q.sort(function (a, b) { return Math.atan2(a.y - cy, a.x - cx) - Math.atan2(b.y - cy, b.x - cx); });
    var start = 0, best = Infinity;
    for (var j = 0; j < 4; j++) {
      var s = q[j].x + q[j].y;
      if (s < best) { best = s; start = j; }
    }
    var out = [];
    for (var k = 0; k < 4; k++) out.push(q[(start + k) % 4]);
    // After angle sorting from top-left this is usually tl,tr,br,bl in clockwise order.
    if (_polygonArea(out) < 0) out = [out[0], out[3], out[2], out[1]];
    return out;
  }

  function _validateQuad(q, W, H) {
    if (!q || q.length !== 4) return false;
    var area = Math.abs(_polygonArea(q));
    if (area < W * H * DS.minPaperArea) return false;

    var d01 = _dist(q[0], q[1]);
    var d12 = _dist(q[1], q[2]);
    var d23 = _dist(q[2], q[3]);
    var d30 = _dist(q[3], q[0]);
    var minSide = Math.min(d01, d12, d23, d30);
    var maxSide = Math.max(d01, d12, d23, d30);
    if (minSide < W * 0.20 || maxSide > W * 1.60) return false;

    var ratio = Math.max((d01 + d23) / Math.max(d12 + d30, 1), (d12 + d30) / Math.max(d01 + d23, 1));
    if (ratio < 1.05 || ratio > 2.2) return false; // A4 perspective is flexible but not square.

    for (var i = 0; i < 4; i++) {
      var a = q[(i + 3) % 4], b = q[i], c = q[(i + 1) % 4];
      var ang = _angle(a, b, c);
      if (ang < 35 || ang > 145) return false;
    }
    return true;
  }

  function _updatePaperStability(quad) {
    if (!DS.lastPaperQuad || !quad) {
      DS.paperStableFrames = 0;
      DS.lastPaperQuad = quad;
      return;
    }
    var delta = 0;
    for (var i = 0; i < 4; i++) delta += _dist(quad[i], DS.lastPaperQuad[i]);
    delta /= 4;
    if (delta <= DS.stableCornerThreshold) DS.paperStableFrames++;
    else DS.paperStableFrames = 0;
    DS.lastPaperQuad = quad.map(function (p) { return { x: p.x, y: p.y }; });
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Perspective warp
  // ─────────────────────────────────────────────────────────────────────────

  function _warpImageData(srcData, srcW, srcH, quad, dstW, dstH) {
    var dst = DS.warpCtx.createImageData(dstW, dstH);
    var src = srcData.data;
    var out = dst.data;

    // Destination points use top-left canvas coordinates.
    var dstPts = [
      { x: 0, y: 0 },
      { x: dstW - 1, y: 0 },
      { x: dstW - 1, y: dstH - 1 },
      { x: 0, y: dstH - 1 }
    ];
    var Hm = _homographyFrom4Points(dstPts, quad);

    for (var y = 0; y < dstH; y++) {
      for (var x = 0; x < dstW; x++) {
        var p = _applyHomography(Hm, x, y);
        var rgba = _sampleBilinear(src, srcW, srcH, p.x, p.y);
        var di = (y * dstW + x) * 4;
        out[di] = rgba[0];
        out[di + 1] = rgba[1];
        out[di + 2] = rgba[2];
        out[di + 3] = 255;
      }
    }
    return dst;
  }

  function _homographyFrom4Points(srcPts, dstPts) {
    var A = [];
    var b = [];
    for (var i = 0; i < 4; i++) {
      var x = srcPts[i].x, y = srcPts[i].y;
      var u = dstPts[i].x, v = dstPts[i].y;
      A.push([x, y, 1, 0, 0, 0, -u * x, -u * y]); b.push(u);
      A.push([0, 0, 0, x, y, 1, -v * x, -v * y]); b.push(v);
    }
    var h = _solveLinear8(A, b);
    return [h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1];
  }

  function _applyHomography(h, x, y) {
    var den = h[6] * x + h[7] * y + h[8];
    if (Math.abs(den) < 1e-6) den = 1e-6;
    return {
      x: (h[0] * x + h[1] * y + h[2]) / den,
      y: (h[3] * x + h[4] * y + h[5]) / den
    };
  }

  function _solveLinear8(A, b) {
    var n = 8;
    for (var i = 0; i < n; i++) A[i] = A[i].slice().concat([b[i]]);

    for (var col = 0; col < n; col++) {
      var pivot = col;
      for (var row = col + 1; row < n; row++) {
        if (Math.abs(A[row][col]) > Math.abs(A[pivot][col])) pivot = row;
      }
      var tmp = A[col]; A[col] = A[pivot]; A[pivot] = tmp;
      var div = A[col][col];
      if (Math.abs(div) < 1e-10) continue;
      for (var c = col; c <= n; c++) A[col][c] /= div;
      for (var r = 0; r < n; r++) {
        if (r === col) continue;
        var factor = A[r][col];
        for (var cc = col; cc <= n; cc++) A[r][cc] -= factor * A[col][cc];
      }
    }
    var x = new Array(n);
    for (var k = 0; k < n; k++) x[k] = A[k][n] || 0;
    return x;
  }

  function _sampleBilinear(data, W, H, x, y) {
    x = Math.max(0, Math.min(W - 1, x));
    y = Math.max(0, Math.min(H - 1, y));
    var x0 = Math.floor(x), y0 = Math.floor(y);
    var x1 = Math.min(W - 1, x0 + 1), y1 = Math.min(H - 1, y0 + 1);
    var tx = x - x0, ty = y - y0;
    var i00 = (y0 * W + x0) * 4, i10 = (y0 * W + x1) * 4;
    var i01 = (y1 * W + x0) * 4, i11 = (y1 * W + x1) * 4;
    var out = [0, 0, 0];
    for (var c = 0; c < 3; c++) {
      var a = data[i00 + c] * (1 - tx) + data[i10 + c] * tx;
      var b = data[i01 + c] * (1 - tx) + data[i11 + c] * tx;
      out[c] = (a * (1 - ty) + b * ty) | 0;
    }
    return out;
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Stroke extraction and reference preprocessing
  // ─────────────────────────────────────────────────────────────────────────

  function _loadReferenceImage(base64png, onDone) {
    var img = new Image();
    img.onload = function () {
      _ensureCanvases();
      var W = DS.procW, H = DS.procH;
      var ctx = DS.warpCtx;
      ctx.setTransform(1, 0, 0, 1, 0, 0);
      ctx.clearRect(0, 0, W, H);
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(0, 0, W, H);
      _drawImageContain(ctx, img, W, H);
      var imageData = ctx.getImageData(0, 0, W, H);
      DS.refMask = _extractStrokeMask(imageData.data, W, H, true);
      DS.refMask = _keepTopComponents(DS.refMask, W, H, Math.max(DS.topNContours, 5), 3);
      _rebuildReferenceDerivedData();
      if (onDone) onDone();
    };
    img.onerror = function () {
      _sendToUnity('OnCameraError', 'reference_image_load_failed');
    };
    img.src = 'data:image/png;base64,' + base64png;
  }

  function _drawImageContain(ctx, img, W, H) {
    var iw = img.naturalWidth || img.width;
    var ih = img.naturalHeight || img.height;
    var scale = Math.min(W / iw, H / ih);
    var dw = iw * scale;
    var dh = ih * scale;
    var dx = (W - dw) * 0.5;
    var dy = (H - dh) * 0.5;
    ctx.drawImage(img, dx, dy, dw, dh);
  }

  function _rebuildReferenceDerivedData() {
    var W = DS.procW, H = DS.procH;
    DS.refEdges = _edgeMap(DS.refMask, W, H);
    DS.refDilated = _dilateRadius(DS.refMask, W, H, 3);
    DS.refDistance = _distanceTransform(DS.refMask, W, H, 24);
    DS.refMoments = _computeHuMoments(DS.refMask, W, H);
    DS.refPixels = _binaryToUnityRGBA(DS.refMask, W, H);
  }

  function _extractStrokeMask(rgba, W, H, isReference) {
    var gray = _toGrayscale(rgba, W, H);
    var blurred = _boxBlurGray(gray, W, H, isReference ? 3 : 7);
    var binary = _adaptiveThresholdDark(blurred, W, H, DS.adaptiveBlockSize, DS.adaptiveC + (isReference ? 0 : 3));

    // Ignore page border after perspective warp. It often contains shadows or page edges.
    var margin = isReference ? 2 : 9;
    for (var y = 0; y < H; y++) {
      for (var x = 0; x < W; x++) {
        if (x < margin || y < margin || x >= W - margin || y >= H - margin) binary[y * W + x] = 0;
      }
    }

    binary = _removeLongNotebookLines(binary, W, H);
    binary = _morphOpen3x3(binary, W, H);
    binary = _morphClose3x3(binary, W, H);
    return binary;
  }

  function _adaptiveThresholdDark(gray, W, H, blockSize, C) {
    blockSize = _odd(Math.max(3, blockSize));
    var half = (blockSize - 1) >> 1;
    var out = new Uint8Array(gray.length);
    var integral = new Float64Array((W + 1) * (H + 1));
    for (var y = 0; y < H; y++) {
      for (var x = 0; x < W; x++) {
        integral[(y + 1) * (W + 1) + (x + 1)] = gray[y * W + x]
          + integral[y * (W + 1) + (x + 1)]
          + integral[(y + 1) * (W + 1) + x]
          - integral[y * (W + 1) + x];
      }
    }
    for (var yy = 0; yy < H; yy++) {
      for (var xx = 0; xx < W; xx++) {
        var x0 = Math.max(0, xx - half), x1 = Math.min(W - 1, xx + half);
        var y0 = Math.max(0, yy - half), y1 = Math.min(H - 1, yy + half);
        var area = (x1 - x0 + 1) * (y1 - y0 + 1);
        var sum = integral[(y1 + 1) * (W + 1) + (x1 + 1)]
          - integral[y0 * (W + 1) + (x1 + 1)]
          - integral[(y1 + 1) * (W + 1) + x0]
          + integral[y0 * (W + 1) + x0];
        var mean = sum / area;
        out[yy * W + xx] = gray[yy * W + xx] < mean - C ? 255 : 0;
      }
    }
    return out;
  }

  function _removeLongNotebookLines(binary, W, H) {
    var out = binary.slice();
    var minRun = Math.floor(W * 0.45);
    for (var y = 0; y < H; y++) {
      var runStart = -1;
      for (var x = 0; x <= W; x++) {
        var on = x < W && binary[y * W + x] > 0;
        if (on && runStart < 0) runStart = x;
        if ((!on || x === W) && runStart >= 0) {
          var len = x - runStart;
          if (len >= minRun) {
            for (var k = runStart; k < x; k++) out[y * W + k] = 0;
          }
          runStart = -1;
        }
      }
    }
    return out;
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Scoring
  // ─────────────────────────────────────────────────────────────────────────

  function _scoreMasks(mask, W, H) {
    if (!DS.refMask || !DS.refDistance) return { score: 0, coverage: 0, extra: 1, iou: 0, chamfer: 0, hu: 0 };

    var maskDil = _dilateRadius(mask, W, H, 3);
    var maskDist = _distanceTransform(mask, W, H, 24);

    var refCount = 0, covered = 0;
    var drawCount = 0, extraCount = 0;
    var inter = 0, union = 0;
    var chamferSum = 0, chamferN = 0;

    for (var i = 0; i < W * H; i++) {
      var r = DS.refMask[i] > 0;
      var d = mask[i] > 0;
      var rd = DS.refDilated[i] > 0;
      var dd = maskDil[i] > 0;

      if (r) {
        refCount++;
        if (maskDil[i] > 0) covered++;
        chamferSum += Math.min(maskDist[i], 24);
        chamferN++;
      }
      if (d) {
        drawCount++;
        if (!rd) extraCount++;
        chamferSum += Math.min(DS.refDistance[i], 24);
        chamferN++;
      }
      if (rd && dd) inter++;
      if (rd || dd) union++;
    }

    var coverage = refCount > 0 ? covered / refCount : 0;
    var extra = drawCount > 0 ? extraCount / drawCount : 1;
    var iou = union > 0 ? inter / union : 0;
    var chamferNorm = chamferN > 0 ? 1.0 - Math.min(1.0, (chamferSum / chamferN) / 16.0) : 0;

    var hu = 0;
    var moments = _computeHuMoments(mask, W, H);
    if (moments && DS.refMoments) {
      var dist = _matchShapes(DS.refMoments, moments);
      hu = 1.0 / (1.0 + dist * 2.0);
    }

    var huW = Math.min(0.45, DS.huWeight);
    var baseW = 1.0 - huW;
    var combined = baseW * (
      coverage * 0.42 +
      iou * 0.26 +
      chamferNorm * 0.22 +
      (1.0 - extra) * 0.10
    ) + huW * hu;

    // Strong penalty for random scribbles: good coverage alone is not enough.
    combined *= (1.0 - Math.min(0.75, extra * 0.85));

    return {
      score: _clamp01(combined),
      coverage: coverage,
      extra: extra,
      iou: iou,
      chamfer: chamferNorm,
      hu: hu
    };
  }

  function _smooth(score) {
    score = _clamp01(score);
    DS.scoreHistory.push(score);
    if (DS.scoreHistory.length > DS.scoreHistoryLen) DS.scoreHistory.shift();

    var sorted = DS.scoreHistory.slice().sort(function (a, b) { return a - b; });
    var median = sorted[(sorted.length / 2) | 0];
    DS.emaScore = DS.emaScore <= 0 ? median : DS.emaScore * 0.68 + median * 0.32;
    var smoothed = _clamp01(DS.emaScore);

    if (smoothed >= DS.acceptThreshold && DS.paperStableFrames >= DS.requiredStablePaperFrames) {
      DS.stableAcceptFrames++;
    } else if (smoothed < Math.max(0.05, DS.acceptThreshold - 0.10)) {
      DS.stableAcceptFrames = 0;
    }

    // Unity side still fires OnDrawingAccepted when CurrentScore >= acceptThreshold.
    // Hold the score slightly below the threshold until enough stable frames pass.
    if (smoothed >= DS.acceptThreshold && DS.stableAcceptFrames < DS.requiredAcceptFrames) {
      smoothed = Math.min(smoothed, DS.acceptThreshold - 0.01);
    }
    return _clamp01(smoothed);
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Image primitives
  // ─────────────────────────────────────────────────────────────────────────

  function _toGrayscale(rgba, W, H) {
    var gray = new Uint8Array(W * H);
    for (var i = 0; i < W * H; i++) {
      var r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
      gray[i] = (0.299 * r + 0.587 * g + 0.114 * b) | 0;
    }
    return gray;
  }

  function _boxBlurGray(gray, W, H, radius) {
    radius = Math.max(1, radius | 0);
    var out = new Uint8Array(gray.length);
    var integral = new Float64Array((W + 1) * (H + 1));
    for (var y = 0; y < H; y++) {
      for (var x = 0; x < W; x++) {
        integral[(y + 1) * (W + 1) + (x + 1)] = gray[y * W + x]
          + integral[y * (W + 1) + (x + 1)]
          + integral[(y + 1) * (W + 1) + x]
          - integral[y * (W + 1) + x];
      }
    }
    for (var yy = 0; yy < H; yy++) {
      for (var xx = 0; xx < W; xx++) {
        var x0 = Math.max(0, xx - radius), x1 = Math.min(W - 1, xx + radius);
        var y0 = Math.max(0, yy - radius), y1 = Math.min(H - 1, yy + radius);
        var area = (x1 - x0 + 1) * (y1 - y0 + 1);
        var sum = integral[(y1 + 1) * (W + 1) + (x1 + 1)]
          - integral[y0 * (W + 1) + (x1 + 1)]
          - integral[(y1 + 1) * (W + 1) + x0]
          + integral[y0 * (W + 1) + x0];
        out[yy * W + xx] = (sum / area) | 0;
      }
    }
    return out;
  }

  function _percentile(arr, p) {
    var hist = new Int32Array(256);
    for (var i = 0; i < arr.length; i++) hist[arr[i]]++;
    var target = Math.floor(arr.length * p);
    var count = 0;
    for (var v = 0; v < 256; v++) {
      count += hist[v];
      if (count >= target) return v;
    }
    return 255;
  }

  function _morphOpen3x3(binary, W, H) { return _morphDilate3x3(_morphErode3x3(binary, W, H), W, H); }
  function _morphClose3x3(binary, W, H) { return _morphErode3x3(_morphDilate3x3(binary, W, H), W, H); }

  function _morphDilate3x3(binary, W, H) {
    var out = new Uint8Array(binary.length);
    for (var y = 1; y < H - 1; y++) {
      for (var x = 1; x < W - 1; x++) {
        var max = 0;
        for (var dy = -1; dy <= 1; dy++) {
          for (var dx = -1; dx <= 1; dx++) {
            if (binary[(y + dy) * W + (x + dx)] > max) max = binary[(y + dy) * W + (x + dx)];
          }
        }
        out[y * W + x] = max;
      }
    }
    return out;
  }

  function _morphErode3x3(binary, W, H) {
    var out = new Uint8Array(binary.length);
    for (var y = 1; y < H - 1; y++) {
      for (var x = 1; x < W - 1; x++) {
        var min = 255;
        for (var dy = -1; dy <= 1; dy++) {
          for (var dx = -1; dx <= 1; dx++) {
            if (binary[(y + dy) * W + (x + dx)] < min) min = binary[(y + dy) * W + (x + dx)];
          }
        }
        out[y * W + x] = min;
      }
    }
    return out;
  }

  function _dilateRadius(binary, W, H, radius) {
    var out = binary.slice();
    for (var r = 0; r < radius; r++) out = _morphDilate3x3(out, W, H);
    return out;
  }

  function _edgeMap(binary, W, H) {
    var edges = new Uint8Array(W * H);
    for (var y = 1; y < H - 1; y++) {
      for (var x = 1; x < W - 1; x++) {
        var idx = y * W + x;
        if (!binary[idx]) continue;
        if (!binary[idx - 1] || !binary[idx + 1] || !binary[idx - W] || !binary[idx + W]) edges[idx] = 255;
      }
    }
    return edges;
  }

  function _distanceTransform(mask, W, H, maxDistance) {
    var inf = maxDistance || 32;
    var dist = new Float32Array(W * H);
    for (var i = 0; i < W * H; i++) dist[i] = mask[i] ? 0 : inf;

    for (var y = 0; y < H; y++) {
      for (var x = 0; x < W; x++) {
        var idx = y * W + x;
        var v = dist[idx];
        if (x > 0) v = Math.min(v, dist[idx - 1] + 1);
        if (y > 0) v = Math.min(v, dist[idx - W] + 1);
        if (x > 0 && y > 0) v = Math.min(v, dist[idx - W - 1] + 1.414);
        if (x < W - 1 && y > 0) v = Math.min(v, dist[idx - W + 1] + 1.414);
        dist[idx] = v;
      }
    }
    for (var yy = H - 1; yy >= 0; yy--) {
      for (var xx = W - 1; xx >= 0; xx--) {
        var idx2 = yy * W + xx;
        var vv = dist[idx2];
        if (xx < W - 1) vv = Math.min(vv, dist[idx2 + 1] + 1);
        if (yy < H - 1) vv = Math.min(vv, dist[idx2 + W] + 1);
        if (xx < W - 1 && yy < H - 1) vv = Math.min(vv, dist[idx2 + W + 1] + 1.414);
        if (xx > 0 && yy < H - 1) vv = Math.min(vv, dist[idx2 + W - 1] + 1.414);
        dist[idx2] = vv;
      }
    }
    return dist;
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Connected components
  // ─────────────────────────────────────────────────────────────────────────

  function _largestComponent(binary, W, H, minSize) {
    var visited = new Uint8Array(W * H);
    var best = null;
    var dirs = [1, -1, W, -W];

    for (var i = 0; i < W * H; i++) {
      if (visited[i] || binary[i] === 0) continue;
      var stack = [i];
      visited[i] = 1;
      var pixels = [];
      while (stack.length) {
        var p = stack.pop();
        pixels.push(p);
        var x = p % W;
        for (var d = 0; d < 4; d++) {
          var np = p + dirs[d];
          if (np < 0 || np >= W * H) continue;
          if ((d === 0 && x === W - 1) || (d === 1 && x === 0)) continue;
          if (!visited[np] && binary[np] > 0) {
            visited[np] = 1;
            stack.push(np);
          }
        }
      }
      if (pixels.length >= minSize && (!best || pixels.length > best.size)) {
        best = { size: pixels.length, pixels: pixels };
      }
    }
    return best;
  }

  function _keepTopComponents(binary, W, H, topN, minSize) {
    var labels = new Int32Array(W * H);
    var labelCount = 0;
    var sizes = [];
    var dirs = [1, -1, W, -W];

    for (var i = 0; i < W * H; i++) {
      if (labels[i] || binary[i] === 0) continue;
      labelCount++;
      var size = 0;
      var stack = [i];
      labels[i] = labelCount;
      while (stack.length) {
        var p = stack.pop();
        size++;
        var x = p % W;
        for (var d = 0; d < 4; d++) {
          var np = p + dirs[d];
          if (np < 0 || np >= W * H) continue;
          if ((d === 0 && x === W - 1) || (d === 1 && x === 0)) continue;
          if (!labels[np] && binary[np] > 0) {
            labels[np] = labelCount;
            stack.push(np);
          }
        }
      }
      if (size >= minSize) sizes.push({ id: labelCount, size: size });
    }

    sizes.sort(function (a, b) { return b.size - a.size; });
    var keep = {};
    for (var j = 0; j < Math.min(topN, sizes.length); j++) keep[sizes[j].id] = true;
    var out = new Uint8Array(W * H);
    for (var k = 0; k < W * H; k++) if (keep[labels[k]]) out[k] = 255;
    return out;
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Hu moments
  // ─────────────────────────────────────────────────────────────────────────

  function _computeHuMoments(binary, W, H) {
    var m00 = 0, m10 = 0, m01 = 0, m20 = 0, m11 = 0, m02 = 0, m30 = 0, m21 = 0, m12 = 0, m03 = 0;
    for (var y = 0; y < H; y++) {
      for (var x = 0; x < W; x++) {
        if (!binary[y * W + x]) continue;
        m00 += 1; m10 += x; m01 += y; m20 += x * x; m11 += x * y; m02 += y * y;
        m30 += x * x * x; m21 += x * x * y; m12 += x * y * y; m03 += y * y * y;
      }
    }
    if (m00 < 1) return null;
    var cx = m10 / m00, cy = m01 / m00;
    var mu20 = m20 - cx * m10;
    var mu02 = m02 - cy * m01;
    var mu11 = m11 - cx * m01;
    var mu30 = m30 - 3 * cx * m20 + 2 * cx * cx * m10;
    var mu03 = m03 - 3 * cy * m02 + 2 * cy * cy * m01;
    var mu21 = m21 - 2 * cx * m11 - cy * m20 + 2 * cx * cx * m01;
    var mu12 = m12 - 2 * cy * m11 - cx * m02 + 2 * cy * cy * m10;
    var m00sq = m00 * m00;
    var n20 = mu20 / m00sq, n02 = mu02 / m00sq, n11 = mu11 / m00sq;
    var m0025 = Math.pow(m00, 2.5);
    var n30 = mu30 / m0025, n03 = mu03 / m0025, n21 = mu21 / m0025, n12 = mu12 / m0025;
    var h = new Float64Array(7);
    h[0] = n20 + n02;
    h[1] = (n20 - n02) * (n20 - n02) + 4 * n11 * n11;
    h[2] = (n30 - 3 * n12) * (n30 - 3 * n12) + (3 * n21 - n03) * (3 * n21 - n03);
    h[3] = (n30 + n12) * (n30 + n12) + (n21 + n03) * (n21 + n03);
    h[4] = (n30 - 3 * n12) * (n30 + n12) * ((n30 + n12) * (n30 + n12) - 3 * (n21 + n03) * (n21 + n03))
      + (3 * n21 - n03) * (n21 + n03) * (3 * (n30 + n12) * (n30 + n12) - (n21 + n03) * (n21 + n03));
    h[5] = (n20 - n02) * ((n30 + n12) * (n30 + n12) - (n21 + n03) * (n21 + n03))
      + 4 * n11 * (n30 + n12) * (n21 + n03);
    h[6] = (3 * n21 - n03) * (n30 + n12) * ((n30 + n12) * (n30 + n12) - 3 * (n21 + n03) * (n21 + n03))
      - (n30 - 3 * n12) * (n21 + n03) * (3 * (n30 + n12) * (n30 + n12) - (n21 + n03) * (n21 + n03));
    for (var i = 0; i < 7; i++) h[i] = h[i] !== 0 ? -Math.sign(h[i]) * Math.log10(Math.abs(h[i])) : 0;
    return h;
  }

  function _matchShapes(a, b) {
    var dist = 0;
    for (var i = 0; i < 7; i++) {
      if (Math.abs(a[i]) < 1e-10 || Math.abs(b[i]) < 1e-10) continue;
      dist += Math.abs(1.0 / a[i] - 1.0 / b[i]);
    }
    return dist;
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Debug pixel conversion
  // ─────────────────────────────────────────────────────────────────────────

  function _imageDataToUnityRGBA(imageData, W, H) {
    var src = imageData.data;
    var dst = new Uint8ClampedArray(W * H * 4);
    for (var y = 0; y < H; y++) {
      var srcRow = y * W * 4;
      var dstRow = (H - 1 - y) * W * 4;
      dst.set(src.subarray(srcRow, srcRow + W * 4), dstRow);
    }
    return dst;
  }

  function _binaryToUnityRGBA(binary, W, H) {
    var rgba = new Uint8ClampedArray(W * H * 4);
    for (var y = 0; y < H; y++) {
      for (var x = 0; x < W; x++) {
        var si = y * W + x;
        var di = ((H - 1 - y) * W + x) * 4;
        var v = binary[si] ? 255 : 0;
        rgba[di] = v; rgba[di + 1] = v; rgba[di + 2] = v; rgba[di + 3] = 255;
      }
    }
    return rgba;
  }

  function _emptyUnityRGBA(n) {
    var rgba = new Uint8ClampedArray(n * 4);
    for (var i = 0; i < n; i++) rgba[i * 4 + 3] = 255;
    return rgba;
  }

  function _buildHeatmap(mask, ref, W, H) {
    var rgba = new Uint8ClampedArray(W * H * 4);
    if (!ref) return _emptyUnityRGBA(W * H);
    for (var y = 0; y < H; y++) {
      for (var x = 0; x < W; x++) {
        var si = y * W + x;
        var di = ((H - 1 - y) * W + x) * 4;
        var d = mask[si] > 0, r = ref[si] > 0;
        if (d && r) { rgba[di] = 80; rgba[di + 1] = 255; rgba[di + 2] = 80; }
        else if (r) { rgba[di] = 80; rgba[di + 1] = 140; rgba[di + 2] = 255; }
        else if (d) { rgba[di] = 255; rgba[di + 1] = 80; rgba[di + 2] = 80; }
        else { rgba[di] = 0; rgba[di + 1] = 0; rgba[di + 2] = 0; }
        rgba[di + 3] = 255;
      }
    }
    return rgba;
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Hints and Unity communication
  // ─────────────────────────────────────────────────────────────────────────

  var _lastHintTime = 0;
  var _hintCooldown = 5000;

  function _updateHints(score, result) {
    var now = performance.now();
    if (now - _lastHintTime < _hintCooldown) return;

    if (!result.paperFound) {
      DS.noPaperTimer += DS.frameSkip;
      if (DS.noPaperTimer > 90) {
        _sendToUnity('OnHint', 'show_paper');
        DS.noPaperTimer = 0;
        _lastHintTime = now;
      }
      return;
    }
    DS.noPaperTimer = 0;

    if (result.paperFound && score < 0.08) {
      DS.noDrawingTimer += DS.frameSkip;
      if (DS.noDrawingTimer > 120) {
        _sendToUnity('OnHint', 'show_drawing');
        DS.noDrawingTimer = 0;
        _lastHintTime = now;
      }
    } else {
      DS.noDrawingTimer = 0;
    }

    if (score > 0.25 && score < DS.acceptThreshold - 0.05) {
      DS.stuckTimer += DS.frameSkip;
      if (DS.stuckTimer > 240) {
        _sendToUnity('OnHint', 'hold_still');
        DS.stuckTimer = 0;
        _lastHintTime = now;
      }
    } else {
      DS.stuckTimer = 0;
    }
  }

  var _methodMap = {
    'OnSimilarityUpdate': 'JS_OnSimilarityUpdate',
    'OnScannerReady': 'JS_OnScannerReady',
    'OnCameraError': 'JS_OnCameraError',
    'OnCameraGranted': 'JS_OnCameraGranted',
    'OnHint': 'JS_OnHint'
  };

  function _sendToUnity(method, value) {
    var unityMethod = _methodMap[method] || method;
    if (typeof SendMessage === 'function') {
      SendMessage(DS.gameObjectName, unityMethod, value);
    } else if (typeof global.SendMessage === 'function') {
      global.SendMessage(DS.gameObjectName, unityMethod, value);
    } else {
      console.warn('[DrawScanning] SendMessage not available. method=' + unityMethod + ' value=' + value);
    }
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Math helpers
  // ─────────────────────────────────────────────────────────────────────────

  function _numOr(v, fallback) { return typeof v === 'number' && isFinite(v) ? v : fallback; }
  function _odd(v) { v = v | 0; return v % 2 === 0 ? v + 1 : v; }
  function _clamp01(v) { return Math.max(0, Math.min(1, v)); }
  function _countNonZero(binary, n) { var c = 0; for (var i = 0; i < n; i++) if (binary[i]) c++; return c; }
  function _dist(a, b) { var dx = a.x - b.x, dy = a.y - b.y; return Math.sqrt(dx * dx + dy * dy); }

  function _polygonArea(q) {
    var area = 0;
    for (var i = 0; i < q.length; i++) {
      var j = (i + 1) % q.length;
      area += q[i].x * q[j].y - q[j].x * q[i].y;
    }
    return area * 0.5;
  }

  function _angle(a, b, c) {
    var abx = a.x - b.x, aby = a.y - b.y;
    var cbx = c.x - b.x, cby = c.y - b.y;
    var dot = abx * cbx + aby * cby;
    var la = Math.sqrt(abx * abx + aby * aby);
    var lc = Math.sqrt(cbx * cbx + cby * cby);
    if (la < 1e-6 || lc < 1e-6) return 0;
    var cos = Math.max(-1, Math.min(1, dot / (la * lc)));
    return Math.acos(cos) * 180 / Math.PI;
  }

}(window));
