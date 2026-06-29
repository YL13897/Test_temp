using UnityEngine;

public class ScoreSystem : MonoBehaviour
{
    [Header("Player")]
    [SerializeField] Rigidbody targetRb;

    [Header("Leader")]
    [SerializeField] Rigidbody leaderRb;

    [Header("Scoring Parameters")]
    [SerializeField] float scoreDuration = 2.4f;
    public float penaltyValue = 500f;

    [Header("Composite Score Weights")]
    [SerializeField]  float w_tr = 100f; // Total tracking reward for an episode
    [SerializeField]  float w_emg = 100f; // Total EMG reward for an episode
    [SerializeField]  float fRef = 10f; // Reference force for normalizing the force cost

    private CORC.Demo.M2RoverBridge bridge;
    private RoverHandler rover;
    private float forceInt = 0f;
    private float forceTime = 0f;
    private int lastSectionNumber = 0;

    void Awake()
    {
        if (targetRb == null)
        {
            GameObject player = GameObject.FindGameObjectWithTag("Player");
            if (player != null) targetRb = player.GetComponent<Rigidbody>();
        }

        if (leaderRb == null)
        {
            LeaderHandler leader = FindFirstObjectByType<LeaderHandler>();
            if (leader != null) leaderRb = leader.GetComponent<Rigidbody>();
        }

        bridge = FindFirstObjectByType<CORC.Demo.M2RoverBridge>();
        rover = targetRb != null ? targetRb.GetComponent<RoverHandler>() : null;
        if (rover == null) rover = FindFirstObjectByType<RoverHandler>();
        if (targetRb == null || leaderRb == null)
            Debug.LogError("[ScoreSystem] No Rigidbody assigned or found.");

        if (ScoreManager.Instance == null)
            Debug.LogError("[ScoreSystem] No ScoreManager instance found in the scene.");
    }

    void FixedUpdate()
    {
        if (targetRb == null || leaderRb == null || ScoreManager.Instance == null) return;

        float xRover = GetRoverX();
        float xLeader = GetLeaderX();
        float minX = rover != null ? rover.minX : -4.5f;
        float maxX = rover != null ? rover.maxX : 4.5f;
        ScoreManager.Instance.UpdateBoundaryUI(xRover, minX, maxX);

        if (!ScoreManager.Instance.SectionScoringActive || ScoreManager.Instance.IsScorePaused)
        {
            forceInt = 0f;
            forceTime = 0f;
            lastSectionNumber = 0;
            return;
        }

        int sectionNumber = ExperimentBlockControl.Instance != null ? ExperimentBlockControl.Instance.CurrentSectionNumber : 0;
        if (sectionNumber != lastSectionNumber)
        {
            forceInt = 0f;
            forceTime = 0f;
            lastSectionNumber = sectionNumber;
        }

        if (rover != null && rover.BoundaryContact)
            ScoreManager.Instance.TryApplyBoundaryPenalty(penaltyValue);

        float e = xRover - xLeader;
        bool m2Mode = bridge != null && bridge.unityMode == CORC.Demo.M2RoverBridge.UnityDriveMode.Mode2_M2;
        float handleFx = m2Mode ? GetForceSensorX() : 0f;
        const float trackEps = 0.02f;
        float trackZeroError = 0.25f * 8.5f; // 25% of the total tracking range (8.5)
        float trackNormaliser = Mathf.Sqrt(trackZeroError * trackZeroError + trackEps * trackEps) - trackEps;
        float trackSmoothAbs = Mathf.Sqrt(e * e + trackEps * trackEps) - trackEps;
        float trackCost = w_tr * trackSmoothAbs / Mathf.Max(trackNormaliser, 0.0001f);
        float trackReward = w_tr - trackCost;
        bool emgReady = bridge != null && bridge.HasValidSpi;
        float emgQuality = emgReady
            ? Mathf.Clamp01(bridge.EmgEffortScore / 100f)
            : 1f;

        float wTrack = trackReward / scoreDuration;
        float wEmg = m2Mode ? w_emg * emgQuality / scoreDuration : 0f;

        float dt = Time.fixedDeltaTime;
        forceInt += Mathf.Abs(handleFx) * dt;
        forceTime += dt;
        float fAvg = forceTime > 0f ? forceInt / forceTime : 0f;
        float forceQuality = 1f - Mathf.Abs(handleFx) / fRef;
        float wForce = m2Mode ? 100f * forceQuality / scoreDuration : 0f;
        float totalCost = wTrack + wForce + wEmg; 
        float trackDelta = wTrack * Time.fixedDeltaTime;
        float forceDelta = wForce * Time.fixedDeltaTime;
        float emgDelta = wEmg * Time.fixedDeltaTime;

        ScoreManager.Instance.SetForceAvg(fAvg);
        ScoreManager.Instance.SetCompositeMetrics(handleFx, trackCost, wTrack, wForce, wEmg, totalCost);
        ScoreManager.Instance.AddToScores(trackDelta, forceDelta, emgDelta);
    }

    float GetHandleFx()
    {
        if (bridge == null || bridge.m2 == null) return 0f;
        var m2 = bridge.m2;
        if (!m2.IsInitialised() || m2.Client == null || !m2.Client.IsConnected()) return 0f;
        if (m2.State == null || m2.State["F"] == null || m2.State["F"].Length < 1) return 0f;
        return (float)m2.State["F"][0];
    }

    float GetForceSensorX()
    {
        if (bridge != null && bridge.TryGetInteractionForceX(out float fx))
            return fx;

        return GetHandleFx();
    }

    float GetRoverX()
    {
        if (bridge != null
            && bridge.unityMode == CORC.Demo.M2RoverBridge.UnityDriveMode.Mode2_M2
            && bridge.roverTransform != null)
            return bridge.roverTransform.position.x;

        return targetRb.position.x;
    }

    float GetLeaderX()
    {
        if (bridge != null
            && bridge.unityMode == CORC.Demo.M2RoverBridge.UnityDriveMode.Mode2_M2
            && bridge.leader != null)
            return bridge.leader.transform.position.x;

        return leaderRb.position.x;
    }
}
