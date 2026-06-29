using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class ScoreManager : MonoBehaviour
{
    public static ScoreManager Instance { get; private set; }

    [Header("UI")]
    [SerializeField] TextMeshProUGUI ScoreText;
    [SerializeField] TextMeshProUGUI emgScoreText;
    [SerializeField] TextMeshProUGUI trackSectionScoreText;
    [SerializeField] TextMeshProUGUI forceSectionScoreText;
    [SerializeField] TextMeshProUGUI emgSectionScoreText;
    [SerializeField] TextMeshProUGUI LastTotalScore;
    [SerializeField] Slider sectionBar;

    public float GlobalScore { get; private set; }
    public float SectionScore { get; private set; }
    public float EmgScore { get; private set; }
    public float TrackSectionScore { get; private set; }
    public float ForceSectionScore { get; private set; }
    public float ForceAvg { get; private set; }
    public float EmgSectionScore { get; private set; }
    public double EmgTimestamp { get; private set; }
    public string EmgDetails { get; private set; }
    public float HandleFx { get; private set; }
    public float TrackCost { get; private set; }
    public float WTrack { get; private set; }
    public float WForce { get; private set; }
    public float WEmg { get; private set; }
    public float TotalCost { get; private set; }
    public bool HasStartedScoring { get; private set; }
    public bool SectionScoringActive { get; private set; }
    public bool IsScorePaused { get; private set; }
    private bool boundaryPenaltyApplied;
    private CORC.Demo.M2RoverBridge bridge;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        // Optional: persist across scene reloads
        // DontDestroyOnLoad(gameObject);

        if (ScoreText != null) ScoreText.raycastTarget = false;
        if (emgScoreText != null) emgScoreText.raycastTarget = false;
        bridge = FindFirstObjectByType<CORC.Demo.M2RoverBridge>();

        ResetAll();
    }

    public void ResetAll()
    {
        GlobalScore = 0f;
        EmgScore = 0f;
        EmgTimestamp = 0.0;
        EmgDetails = "";
        ResetCompositeMetrics();
        HasStartedScoring = false;
        SectionScoringActive = false;
        IsScorePaused = false;
        ResetSection();
        SetLastTotalScore(0f);
        RefreshUI();
    }

    public void BeginScoring()
    {
        HasStartedScoring = true;
        SectionScoringActive = true;
    }

    public void SetEmgScore(float score, double timestamp)
    {
        EmgScore = Mathf.Max(0f, score);
        EmgTimestamp = timestamp;
        RefreshUI();
    }

    public void SetEmgPreview(float score, double timestamp, string details)
    {
        EmgScore = Mathf.Max(0f, score);
        EmgTimestamp = timestamp;
        EmgDetails = details ?? "";
        RefreshUI();
    }

    public void SetScorePaused(bool paused)
    {
        IsScorePaused = paused;
    }

    public void SetCompositeMetrics(float handleFx, float trackCost, float wTrack, float wForce, float wEmg, float totalCost)
    {
        HandleFx = handleFx;
        TrackCost = trackCost;
        WTrack = wTrack;
        WForce = wForce;
        WEmg = wEmg;
        TotalCost = totalCost;
    }

    public void ResetSection()
    {
        SectionScore = 0f;
        TrackSectionScore = 0f;
        ForceSectionScore = 0f;
        ForceAvg = 0f;
        EmgSectionScore = 0f;
        SectionScoringActive = false;
        boundaryPenaltyApplied = false;
        ResetCompositeMetrics();
        if (sectionBar != null)
        {
            sectionBar.minValue = 0f;
            sectionBar.maxValue = 9f;
            sectionBar.value = 0f;
        }
        RefreshUI();
    }

    public void ResetBlockScores()
    {
        GlobalScore = 0f;
        SectionScore = 0f;
        TrackSectionScore = 0f;
        ForceSectionScore = 0f;
        ForceAvg = 0f;
        EmgSectionScore = 0f;
        ResetCompositeMetrics();
        HasStartedScoring = false;
        SectionScoringActive = false;
        boundaryPenaltyApplied = false;
        SetLastTotalScore(0f);

        if (sectionBar != null)
        {
            sectionBar.minValue = 0f;
            // sectionBar.maxValue = 9f;
            sectionBar.maxValue = 4.5f;
            sectionBar.value = 0f;
        }

        RefreshUI();
    }

    public void AddToScores(float delta)
    {
        GlobalScore += delta;
        SectionScore += delta;

        RefreshUI();
    }

    public void AddToScores(float trackDelta, float forceDelta, float emgDelta)
    {
        float delta = trackDelta + forceDelta + emgDelta;
        GlobalScore += delta;
        SectionScore += delta;
        TrackSectionScore += trackDelta;
        ForceSectionScore += forceDelta;
        EmgSectionScore += emgDelta;

        RefreshUI();
    }

    public void SetForceAvg(float forceAvg)
    {
        ForceAvg = Mathf.Max(0f, forceAvg);
        RefreshUI();
    }

    public void ApplyBoundaryPenalty(float penalty)
    {
        GlobalScore -= penalty;
        SectionScore -= penalty;

        RefreshUI();
    }

    public bool TryApplyBoundaryPenalty(float penalty)
    {
        if (boundaryPenaltyApplied) return false;

        ApplyBoundaryPenalty(Mathf.Abs(penalty));
        boundaryPenaltyApplied = true;
        return true;
    }

    public void UpdateLastTotalScore()
    {
        SetLastTotalScore(SectionScore);
    }

    void SetLastTotalScore(float score)
    {
        if (LastTotalScore != null)
            LastTotalScore.text = $"Last Total: {score:0.00}";
    }

    void ResetCompositeMetrics()
    {
        HandleFx = 0f;
        TrackCost = 0f;
        WTrack = 0f;
        WForce = 0f;
        WEmg = 0f;
        TotalCost = 0f;
    }


    // UI update
    public void UpdateBoundaryUI(float x, float minX, float maxX)
    {
        float halfWidth = 0.5f * (maxX - minX);
        sectionBar.maxValue = halfWidth;
        float distFromCenter = Mathf.Abs(x);
        // Debug.Log($"{distFromCenter}");
        float fill = Mathf.Clamp(distFromCenter, 0f, halfWidth);
        sectionBar.value = fill; 

    }

    void RefreshUI()
    {
        bool m2Mode = bridge != null && bridge.unityMode == CORC.Demo.M2RoverBridge.UnityDriveMode.Mode2_M2;

        if (ScoreText != null)
            ScoreText.text = $"Score: Global: {GlobalScore:0} | Section: {SectionScore:0}";

        if (trackSectionScoreText != null)
            trackSectionScoreText.text = $"Track Section: {TrackSectionScore:0.00}";

        if (forceSectionScoreText != null)
            forceSectionScoreText.text = m2Mode ? $"Force Section: {ForceSectionScore:0.00}" : "Force Section: ---";

        if (emgSectionScoreText != null)
            emgSectionScoreText.text = m2Mode ? $"EMG Section: {EmgSectionScore:0.00}" : "EMG Section: ---";
        
        if (emgScoreText != null)
            emgScoreText.text = string.IsNullOrEmpty(EmgDetails)
                ? $"EMG: {EmgScore:0.00} | t: {EmgTimestamp:0.000}s"
                : EmgDetails;
    }
}
