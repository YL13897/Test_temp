using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class ScoreManager : MonoBehaviour
{
    public static ScoreManager Instance { get; private set; }

    [Header("UI")]
    [SerializeField] TextMeshProUGUI ScoreText;
    [SerializeField] TextMeshProUGUI emgScoreText;
    [SerializeField] TextMeshProUGUI TrialScoreText;
    [SerializeField] TextMeshProUGUI DebugTrialScoreText;
    [SerializeField] bool alwaysShowTrialScore = false;
    [SerializeField] bool showTotalOnly = false;
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
    private float lastTrackScore;
    private float lastForceScore;
    private float lastEmgScore;
    private float lastTotalScore;
    private bool showLiveScore;
    private CORC.Demo.M2RoverBridge bridge;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        // Optional: persist across scene reloads
        // DontDestroyOnLoad(gameObject);

        if (ScoreText != null) ScoreText.raycastTarget = false;
        if (emgScoreText != null) emgScoreText.raycastTarget = false;
        if (TrialScoreText != null) TrialScoreText.raycastTarget = false;
        if (DebugTrialScoreText != null) DebugTrialScoreText.raycastTarget = false;
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
        ClearTrialScore();
        HasStartedScoring = false;
        SectionScoringActive = false;
        IsScorePaused = false;
        ResetSection();
        UpdateTrialScoreVisible();
        RefreshUI();
    }

    public void BeginScoring()
    {
        HasStartedScoring = true;
        SectionScoringActive = true;
        showLiveScore = true;
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
        ClearTrialScore();
        HasStartedScoring = false;
        SectionScoringActive = false;
        boundaryPenaltyApplied = false;
        UpdateTrialScoreVisible();

        if (sectionBar != null)
        {
            sectionBar.minValue = 0f;
            // sectionBar.maxValue = 9f;
            sectionBar.maxValue = 4.5f;
            sectionBar.value = 0f;
        }

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
        lastTrackScore = TrackSectionScore;
        lastForceScore = ForceSectionScore;
        lastEmgScore = EmgSectionScore;
        lastTotalScore = SectionScore;
        showLiveScore = false;
        ShowTrialScore(false);
    }

    public void HideTrialScore()
    {
        if (TrialScoreText != null && !alwaysShowTrialScore)
            TrialScoreText.gameObject.SetActive(false);
    }

    void ShowTrialScore(bool live)
    {
        if (TrialScoreText == null) return;

        TrialScoreText.text = FormatTrialScore(live, showTotalOnly);
        TrialScoreText.gameObject.SetActive(true);
    }

    void UpdateTrialScoreVisible()
    {
        if (alwaysShowTrialScore)
            ShowTrialScore(SectionScoringActive);
        else
            HideTrialScore();
    }

    void ClearTrialScore()
    {
        lastTrackScore = 0f;
        lastForceScore = 0f;
        lastEmgScore = 0f;
        lastTotalScore = 0f;
        showLiveScore = false;
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

    string FormatTrialScore(bool live, bool totalOnly = false)
    {
        float track = live ? TrackSectionScore : lastTrackScore;
        float force = live ? ForceSectionScore : lastForceScore;
        float emg = live ? EmgSectionScore : lastEmgScore;
        float total = live ? SectionScore : lastTotalScore;

        if (totalOnly)
            return $"Total: {total:0.0}";

        return
            $"Track: {track:0.0}\n" +
            $"Force: {force:0.0}\n" +
            $"EMG: {emg:0.0}\n" +
            $"Total: {total:0.0}";
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
        if (ScoreText != null)
            ScoreText.text = $"Block Score: {GlobalScore:0}";

        if (emgScoreText != null)
            emgScoreText.text = string.IsNullOrEmpty(EmgDetails)
                ? $"EMG: {EmgScore:0.00} | t: {EmgTimestamp:0.000}s"
                : EmgDetails;

        if (alwaysShowTrialScore)
            ShowTrialScore(SectionScoringActive);

        if (DebugTrialScoreText != null)
            DebugTrialScoreText.text = FormatTrialScore(showLiveScore);
    }
}
