using UnityEngine;
using CORC.Demo;

// EMGScope: A Unity component that visualizes EMG signals in real-time using a custom UI renderer, supporting multiple channels and auto-scaling based on signal amplitude.
[RequireComponent(typeof(RectTransform))]
public class EMGScope : MonoBehaviour
{
    [SerializeField] private EMGFilter emgFilter;
    [SerializeField] private M2RoverBridge emgBridge;
    [SerializeField] private Canvas targetCanvas;
    [SerializeField] private RectTransform scopeRoot;
    [SerializeField] private bool showScopePlots = true;
    [SerializeField] private bool autoScale = false;

    private int visibleSamples = 512;
    private float refreshHz = 15f;
    private float scopeWidth = 900f;
    private float scopeHeight = 100f;
    private float channelSpacing = 12f;
    private float lineWidth = 2f;
    private Vector2 scopeOffset = new Vector2(40f, -40f);
    private float minAutoRange = 0.0001f;
    private float manualRange = 0.0002f;

    private float[][] displayBuffers;
    private EMGScopeUIRenderer[] scopeRenderers;
    private float nextRefreshTime;
    private int[] activeChannels = System.Array.Empty<int>();
    private bool scopeInitialized;


    private void Awake()
    {
        ResolveReferences();
    }

    private void Start()
    {
        InitializeScopeIfReady();
    }

    private void OnEnable()
    {
        nextRefreshTime = 0f;
    }

    private void Update()
    {
        if (!showScopePlots)
            return;

        if (Time.unscaledTime < nextRefreshTime)
            return;

        nextRefreshTime = Time.unscaledTime + 1f / refreshHz;

        if (emgFilter == null)
        {
            return;
        }

        if (!scopeInitialized)
        {
            return;
        }

        int channelCount = activeChannels.Length;

        for (int i = 0; i < channelCount; i++)
        {
            EMGScopeUIRenderer renderer = scopeRenderers[i];
            int sensorId = activeChannels[i];
            int filterChannel = Mathf.Clamp(sensorId - 1, 0, emgFilter.ChannelCount - 1);

            float[] buffer = displayBuffers[i];
            int count = emgFilter.CopyHistory(GetSignalView(), filterChannel, buffer);
            if (count <= 1)
            {
                renderer.Clear();
                continue;
            }

            float maxAbs = ComputeMaxAbs(buffer, count);
            float yScale = autoScale // Auto-scale mode or manual scale mode
                ? ComputeScaleFromRange(maxAbs)
                : ComputeScaleFromRange(Mathf.Max(manualRange, minAutoRange));
            renderer.Render(buffer, count, yScale);
        }
    }


    // ResolveReferences(): Attempt to find and assign references if they are not already assigned via the Inspector.
    private void ResolveReferences()
    {
        if (emgFilter == null)
            emgFilter = FindFirstObjectByType<EMGFilter>();

        if (emgBridge == null)
            emgBridge = FindFirstObjectByType<M2RoverBridge>();

        if (targetCanvas == null)
            targetCanvas = GetComponentInParent<Canvas>();

        if (targetCanvas == null)
            targetCanvas = FindFirstObjectByType<Canvas>();
    }


    // InitializeScope(): Set up the scope display by creating a child UI element for each active EMG channel and configuring its renderer.
    private void InitializeScope()
    {
        int targetCount = activeChannels.Length;
        displayBuffers = new float[targetCount][];
        scopeRenderers = new EMGScopeUIRenderer[targetCount];
        RectTransform root = EnsureScopeRoot();

        if (root == null)
            return;

        for (int i = 0; i < targetCount; i++)
            displayBuffers[i] = new float[visibleSamples];

        for (int i = 0; i < targetCount; i++)
        {
            GameObject child = new GameObject("EMGScopeChannel_" + i);
            child.transform.SetParent(root, false);
            RectTransform rect = child.AddComponent<RectTransform>();
            rect.localScale = Vector3.one;
            EMGScopeUIRenderer renderer = child.AddComponent<EMGScopeUIRenderer>();
            scopeRenderers[i] = renderer;
            ConfigureRenderer(renderer, i);
        }
    }


    // ConfigureRenderer(): Apply consistent configuration settings to a given channel renderer.
    private void ConfigureRenderer(EMGScopeUIRenderer renderer, int stackedIndex)
    {
        if (renderer == null)
            return;

        float baseline = -stackedIndex * (scopeHeight + channelSpacing);
        renderer.Configure(scopeWidth, scopeHeight, baseline, lineWidth);
        renderer.color = Color.cyan;
    }


    // ComputeMaxAbs(): Compute the maximum absolute value in a given sample buffer.
    private float ComputeMaxAbs(float[] samples, int count)
    {
        float maxAbs = minAutoRange;
        for (int i = 0; i < count; i++)
        {
            float value = Mathf.Abs(samples[i]);
            if (value > maxAbs)
                maxAbs = value;
        }

        return maxAbs;
    }


    // ComputeScaleFromRange(): Compute the vertical scale factor for the auto-scale mode.
    private float ComputeScaleFromRange(float range)
    {
        return scopeHeight * 0.45f / Mathf.Max(range, minAutoRange);
    }


    //  CacheActiveChannels(): Query the EMG bridge for currently active sensor channels and cache the result for scope initialization and display.
    private void CacheActiveChannels()
    {
        if (emgBridge == null)
        {
            activeChannels = System.Array.Empty<int>();
            return;
        }

        int[] channels = emgBridge.GetActiveEmgChannels();
        activeChannels = channels != null ? channels : System.Array.Empty<int>();
    }

    private EMGFilter.EmgSignalView GetSignalView()
    {
        if (emgBridge != null)
            return emgBridge.EmgSignalMode;

        return EMGFilter.EmgSignalView.Envelope;
    }


    // TryInitializeScope(): Attempt to initialize the scope if not already initialized.
    private void InitializeScopeIfReady()
    {
        if (!showScopePlots)
            return;

        ResolveReferences();
        CacheActiveChannels();
        if (activeChannels.Length == 0)
            return;

        InitializeScope();
        scopeInitialized = scopeRenderers != null && scopeRenderers.Length == activeChannels.Length;
    }


    // EnsureScopeRoot(): Ensure that the scope root RectTransform exists and is properly configured; create it if necessary.
    private RectTransform EnsureScopeRoot()
    {
        if (scopeRoot != null)
        {
            scopeRoot.sizeDelta = new Vector2(scopeWidth, activeChannels.Length * (scopeHeight + channelSpacing));
            return scopeRoot;
        }

        if (targetCanvas == null)
            return null;

        GameObject rootObject = new GameObject("EMGScopeRoot", typeof(RectTransform));
        rootObject.transform.SetParent(targetCanvas.transform, false);
        scopeRoot = rootObject.GetComponent<RectTransform>();
        scopeRoot.anchorMin = new Vector2(0f, 1f);
        scopeRoot.anchorMax = new Vector2(0f, 1f);
        scopeRoot.pivot = new Vector2(0f, 1f);
        scopeRoot.anchoredPosition = scopeOffset;
        scopeRoot.sizeDelta = new Vector2(scopeWidth, activeChannels.Length * (scopeHeight + channelSpacing));
        return scopeRoot;
    }
}
