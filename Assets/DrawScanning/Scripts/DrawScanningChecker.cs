using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

/// <summary>
/// DrawScanningChecker — main MonoBehaviour for the DrawScanning module.
///
/// Uses WebCamTexture and a simplified stencil-matching algorithm in C#.
/// Works in both Unity Editor and WebGL.
/// </summary>
public class DrawScanningChecker : MonoBehaviour
{
    [Header("Reference Image (Stencil)")]
    [Tooltip("The target drawing the child must reproduce. Must have Read/Write Enabled = true.")]
    public Texture2D referenceImage;

    [Header("Algorithm Settings")]
    [Tooltip("Resolution for processing. Lower is faster. 128 or 256 is recommended.")]
    public int processingResolution = 128;

    [Range(0f, 1f)]
    [Tooltip("Similarity score (0–1) required to accept the drawing.")]
    public float acceptThreshold = 0.55f;

    [Tooltip("Block size for adaptive thresholding (must be odd). Larger = more tolerant to uneven lighting.")]
    [Range(3, 101)]
    public int adaptiveBlockSize = 45;

    [Tooltip("Constant subtracted from the local mean. Higher = less noise captured from paper texture.")]
    [Range(0, 30)]
    public int adaptiveC = 7;

    [Range(0f, 5f)]
    [Tooltip("Penalty multiplier for drawing outside the stencil.")]
    public float penaltyWeight = 1.5f;

    [Header("Camera UI")]
    [Tooltip("RawImage that shows the live webcam feed.")]
    public RawImage cameraPreviewUI;

    [Tooltip("RawImage that shows the semi-transparent reference overlay.")]
    public RawImage overlayUI;

    [Tooltip("RawImage that shows the processed binary silhouette (Debug).")]
    public RawImage processedPreviewUI;

    [Tooltip("Optional direct progress bar reference.")]
    public DrawScanningProgressBar progressBarUI;

    [Header("Camera UI")]
    [Tooltip("Panel shown before camera access is requested. Set to null to skip.")]
    public GameObject cameraPermissionPanel;

    [Header("Camera Settings")]
    public int cameraWidth = 640;
    public int cameraHeight = 480;
    public int cameraFPS = 30;

    [Tooltip("How often (seconds) to process a frame. 0.1 = 10 fps.")]
    public float processingInterval = 0.1f;

    [Header("Events")]
    public UnityEvent<float> OnSimilarityUpdate = new UnityEvent<float>();
    public UnityEvent OnDrawingAccepted = new UnityEvent();
    public UnityEvent OnCameraError = new UnityEvent();
    public UnityEvent OnCameraGranted = new UnityEvent();

    public float CurrentScore { get; private set; }
    public bool IsAccepted { get; private set; }
    public bool IsScanning { get; private set; }

    private WebCamTexture _webCamTexture;
    private RenderTexture _renderTexture;
    private Texture2D _processingTexture;
    private Texture2D _debugTexture;
    private Coroutine _processingCoroutine;

    private bool[] _stencilMask;
    private int _stencilPixelCount;
    private int _nonStencilPixelCount;

    private DrawScanningProgressBar _autoProgressBar;

    private void Awake()
    {
        if (referenceImage == null)
        {
            Debug.LogError("[DrawScanning] referenceImage is not assigned!", this);
            return;
        }

        if (!referenceImage.isReadable)
        {
            Debug.LogError("[DrawScanning] referenceImage is not readable. Enable 'Read/Write Enabled'.", this);
            return;
        }

        _autoProgressBar = progressBarUI != null ? progressBarUI : FindObjectOfType<DrawScanningProgressBar>();

        PrepareStencil();
    }

    private void Start()
    {
        StartScanning();
    }

    private void OnDestroy()
    {
        StopScanning();
    }

    private void OnDisable()
    {
        StopScanning();
    }

    private void PrepareStencil()
    {
        // Create a temporary RenderTexture to scale the reference image
        RenderTexture rt = RenderTexture.GetTemporary(processingResolution, processingResolution, 0);
        Graphics.Blit(referenceImage, rt);

        Texture2D scaledRef = new Texture2D(processingResolution, processingResolution, TextureFormat.RGBA32, false);
        RenderTexture.active = rt;
        scaledRef.ReadPixels(new Rect(0, 0, processingResolution, processingResolution), 0, 0);
        scaledRef.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);

        Color32[] refPixels = scaledRef.GetPixels32();
        _stencilMask = new bool[refPixels.Length];
        _stencilPixelCount = 0;
        _nonStencilPixelCount = 0;

        for (int i = 0; i < refPixels.Length; i++)
        {
            // Assuming the reference image has a transparent background and dark lines
            // Or white background and dark lines. We check if it's dark or opaque.
            Color32 c = refPixels[i];
            float gray = (c.r * 0.299f + c.g * 0.587f + c.b * 0.114f);
            bool isStencil = c.a > 128 && gray < 128; // Dark and opaque

            _stencilMask[i] = isStencil;
            if (isStencil) _stencilPixelCount++;
            else _nonStencilPixelCount++;
        }

        Destroy(scaledRef);

        if (overlayUI != null)
        {
            overlayUI.texture = referenceImage;
            // Make it semi-transparent
            Color col = overlayUI.color;
            col.a = 0.33f;
            overlayUI.color = col;
        }
    }

    public void StartScanning()
    {
        if (IsScanning) return;
        if (referenceImage == null) return;

        IsScanning = true;
        IsAccepted = false;
        CurrentScore = 0f;

        if (cameraPermissionPanel != null)
        {
            cameraPermissionPanel.SetActive(true);
        }
        else
        {
            StartCamera();
        }
    }

    public void RequestCameraPermission()
    {
        StartCamera();
    }

    public void StopScanning()
    {
        if (!IsScanning) return;
        IsScanning = false;

        if (_processingCoroutine != null)
        {
            StopCoroutine(_processingCoroutine);
            _processingCoroutine = null;
        }

        if (_webCamTexture != null && _webCamTexture.isPlaying)
        {
            _webCamTexture.Stop();
        }
    }

    public void ResetAcceptedState()
    {
        IsAccepted = false;
    }

    private void StartCamera()
    {
        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogWarning("[DrawScanning] No webcam devices found.");
            OnCameraError?.Invoke();
            IsScanning = false;
            return;
        }

        // Use the first available camera (usually the default one)
        _webCamTexture = new WebCamTexture(WebCamTexture.devices[0].name, cameraWidth, cameraHeight, cameraFPS);

        if (cameraPreviewUI != null)
        {
            cameraPreviewUI.texture = _webCamTexture;
            // Normal orientation (not mirrored)
            cameraPreviewUI.uvRect = new Rect(0, 0, 1, 1);
        }

        _webCamTexture.Play();
        
        if (cameraPermissionPanel != null)
        {
            cameraPermissionPanel.SetActive(false);
        }
        
        OnCameraGranted?.Invoke();

        _renderTexture = new RenderTexture(processingResolution, processingResolution, 0);
        _processingTexture = new Texture2D(processingResolution, processingResolution, TextureFormat.RGBA32, false);
        
        if (processedPreviewUI != null)
        {
            _debugTexture = new Texture2D(processingResolution, processingResolution, TextureFormat.RGBA32, false);
            processedPreviewUI.texture = _debugTexture;
        }

        _processingCoroutine = StartCoroutine(ProcessingLoop());
    }

    private IEnumerator ProcessingLoop()
    {
        WaitForSeconds wait = new WaitForSeconds(processingInterval);

        while (IsScanning)
        {
            yield return wait;

            if (_webCamTexture == null || !_webCamTexture.isPlaying || _webCamTexture.width <= 16)
                continue;

            // 1. Blit camera to RenderTexture (handles scaling and format conversion)
            // Normal orientation (not mirrored)
            Graphics.Blit(_webCamTexture, _renderTexture, new Vector2(1, 1), new Vector2(0, 0));

            // 2. Read pixels
            RenderTexture.active = _renderTexture;
            _processingTexture.ReadPixels(new Rect(0, 0, processingResolution, processingResolution), 0, 0);
            _processingTexture.Apply();
            RenderTexture.active = null;

            Color32[] pixels = _processingTexture.GetPixels32();
            Color32[] debugPixels = _debugTexture != null ? new Color32[pixels.Length] : null;

            // 3. Adaptive Thresholding (Local Binarization)
            // First, convert to grayscale array
            float[] grayPixels = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                grayPixels[i] = (c.r * 0.299f + c.g * 0.587f + c.b * 0.114f);
            }

            // Build integral image for fast local mean calculation
            int w = processingResolution;
            int h = processingResolution;
            float[] integral = new float[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    integral[(y + 1) * (w + 1) + (x + 1)] = grayPixels[y * w + x]
                        + integral[y * (w + 1) + (x + 1)]
                        + integral[(y + 1) * (w + 1) + x]
                        - integral[y * (w + 1) + x];
                }
            }

            int halfBlock = Mathf.Max(1, adaptiveBlockSize / 2);
            bool[] drawnMask = new bool[w * h];

            // 3.1 Process pixels with adaptive threshold
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int x0 = Mathf.Max(0, x - halfBlock);
                    int x1 = Mathf.Min(w - 1, x + halfBlock);
                    int y0 = Mathf.Max(0, y - halfBlock);
                    int y1 = Mathf.Min(h - 1, y + halfBlock);

                    int area = (x1 - x0 + 1) * (y1 - y0 + 1);
                    float sum = integral[(y1 + 1) * (w + 1) + (x1 + 1)]
                              - integral[y0 * (w + 1) + (x1 + 1)]
                              - integral[(y1 + 1) * (w + 1) + x0]
                              + integral[y0 * (w + 1) + x0];

                    float localMean = sum / area;
                    int i = y * w + x;
                    
                    // A pixel is considered "drawn" if it is significantly darker than its local neighborhood
                    drawnMask[i] = grayPixels[i] < (localMean - adaptiveC);
                }
            }

            // 3.2 Fill contours (Flood fill from edges to find background, then invert)
            bool[] backgroundMask = new bool[w * h];
            System.Collections.Generic.Stack<int> stack = new System.Collections.Generic.Stack<int>();

            // Seed the edges
            for (int x = 0; x < w; x++)
            {
                if (!drawnMask[x]) { backgroundMask[x] = true; stack.Push(x); }
                if (!drawnMask[(h - 1) * w + x]) { backgroundMask[(h - 1) * w + x] = true; stack.Push((h - 1) * w + x); }
            }
            for (int y = 0; y < h; y++)
            {
                if (!drawnMask[y * w]) { backgroundMask[y * w] = true; stack.Push(y * w); }
                if (!drawnMask[y * w + w - 1]) { backgroundMask[y * w + w - 1] = true; stack.Push(y * w + w - 1); }
            }

            // Flood fill
            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };
            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                int cx = idx % w;
                int cy = idx / w;

                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + dx[d];
                    int ny = cy + dy[d];

                    if (nx >= 0 && nx < w && ny >= 0 && ny < h)
                    {
                        int nidx = ny * w + nx;
                        if (!drawnMask[nidx] && !backgroundMask[nidx])
                        {
                            backgroundMask[nidx] = true;
                            stack.Push(nidx);
                        }
                    }
                }
            }

            // Invert background to get filled shapes
            for (int i = 0; i < w * h; i++)
            {
                drawnMask[i] = !backgroundMask[i];
            }

            int drawnInside = 0;
            int drawnOutside = 0;

            // 3.3 Compare with stencil
            for (int i = 0; i < w * h; i++)
            {
                bool isDrawn = drawnMask[i];
                bool isStencil = _stencilMask[i];

                if (isDrawn)
                {
                    if (isStencil) drawnInside++;
                    else drawnOutside++;
                }

                if (debugPixels != null)
                {
                    if (isDrawn && isStencil) debugPixels[i] = new Color32(0, 255, 0, 255); // Green: Good
                    else if (isDrawn && !isStencil) debugPixels[i] = new Color32(255, 0, 0, 255); // Red: Bad
                    else if (isStencil) debugPixels[i] = new Color32(0, 0, 255, 128); // Blue: Missing
                    else debugPixels[i] = new Color32(0, 0, 0, 255); // Black: Background
                }
            }

            if (_debugTexture != null)
            {
                _debugTexture.SetPixels32(debugPixels);
                _debugTexture.Apply();
            }

            // 4. Calculate Score
            float coverage = _stencilPixelCount > 0 ? (float)drawnInside / _stencilPixelCount : 0f;
            float penalty = _nonStencilPixelCount > 0 ? (float)drawnOutside / _nonStencilPixelCount : 0f;

            float score = Mathf.Clamp01(coverage - (penalty * penaltyWeight));
            
            // Smooth score slightly
            CurrentScore = Mathf.Lerp(CurrentScore, score, 0.5f);

            // Normalize score so that acceptThreshold is 1.0 (100%)
            float normalizedScore = Mathf.Clamp01(CurrentScore / acceptThreshold);

            OnSimilarityUpdate?.Invoke(normalizedScore);
            if (_autoProgressBar != null) _autoProgressBar.OnScoreUpdate(normalizedScore);

            if (!IsAccepted && CurrentScore >= acceptThreshold)
            {
                IsAccepted = true;
                OnDrawingAccepted?.Invoke();
                if (_autoProgressBar != null && _autoProgressBar.isActiveAndEnabled)
                {
                    _autoProgressBar.OnDrawingAccepted();
                }
            }
        }
    }
}
