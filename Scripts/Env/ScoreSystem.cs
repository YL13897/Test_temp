using UnityEngine;

public class ScoreSystem : MonoBehaviour
{
    [Header("Player")]
    [SerializeField] Rigidbody targetRb;

    [Header("Leader")]
    [SerializeField] Rigidbody leaderRb;

    [Header("Scoring Parameters")]
    [SerializeField] float sectionDuration = 4f;
    public float penaltyValue = 500f;

    [Header("Composite Score Weights")]
    [SerializeField]  float w_tr = 100f; // Total tracking reward for an episode
    [SerializeField]  float w_emg = 100f;
    [SerializeField]  float fRef = 30f;

    [Header("Global Boundary")]
    public float minX = -4.5f;
    public float maxX = 4.5f;
    public bool enableBoundaryClamp = true;

    [Header("Penalty Control")]
    public float penaltyCooldown = 1.0f; // seconds between penalties while touching boundary

    private CORC.Demo.M2RoverBridge bridge;
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
        if (targetRb == null || leaderRb == null)
            Debug.LogError("[ScoreSystem] No Rigidbody assigned or found.");

        if (ScoreManager.Instance == null)
            Debug.LogError("[ScoreSystem] No ScoreManager instance found in the scene.");
    }

    void FixedUpdate()
    {
        if (targetRb == null || leaderRb == null || ScoreManager.Instance == null) return;

        if (!ScoreManager.Instance.HasStartedScoring || ScoreManager.Instance.IsScorePaused)
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

        float xRover = GetRoverX();
        float xLeader = GetLeaderX();
        ScoreManager.Instance.UpdateBoundaryUI(xRover, minX, maxX);

        bool boundaryContact = DetectBoundaryContact();
        // Global gate: even if multiple callers fire, penalty can apply at most once per cooldown window.
        if (boundaryContact)
        {   
            ScoreManager.Instance.TryApplyBoundaryPenalty(penaltyValue, penaltyCooldown);
            // Removed 'return;' so normal score keeps accumulating while on the boundary.
        }

        float e = xRover - xLeader;
        float handleFx = GetForceSensorX();
        const float trackEps = 0.02f;
        float trackNormaliser = Mathf.Sqrt(4f + trackEps * trackEps) - trackEps;
        float trackSmoothAbs = Mathf.Sqrt(e * e + trackEps * trackEps) - trackEps;
        float trackCost = w_tr * trackSmoothAbs / Mathf.Max(trackNormaliser, 0.0001f);
        float trackReward = w_tr - trackCost;
        bool emgReady = bridge != null && bridge.HasValidSpi;
        float emgCost = emgReady
            ? Mathf.Clamp01(1f - bridge.EmgEffortScore / 100f)
            : 0f;

        float wTrack = trackReward / sectionDuration; // reward scaled to section duration
        float wEmg = -w_emg * emgCost;

        float dt = Time.fixedDeltaTime;
        forceInt += Mathf.Abs(handleFx) * dt;
        forceTime += dt;
        float fAvg = forceTime > 0f ? forceInt / forceTime : 0f;
        float wForce = -100f * Mathf.Abs(handleFx) / (Mathf.Max(fRef, 0.0001f) * Mathf.Max(sectionDuration, 0.0001f));
        float totalCost = wTrack + wForce + wEmg; 
        float trackDelta = wTrack * Time.fixedDeltaTime;
        float forceDelta = wForce * Time.fixedDeltaTime;
        float emgDelta = wEmg * Time.fixedDeltaTime;

        ScoreManager.Instance.SetForceAvg(fAvg);
        ScoreManager.Instance.SetCompositeMetrics(handleFx, trackCost, wTrack, wForce, wEmg, totalCost);
        ScoreManager.Instance.AddToScores(trackDelta, forceDelta, emgDelta);
    }

    bool DetectBoundaryContact()
    {
        if (!enableBoundaryClamp) return false;

        Vector3 pos = targetRb.position;
        Vector3 vel = targetRb.linearVelocity;
        bool hitBoundary = false;

        if (pos.x <= minX)
        {
            pos.x = minX;
            if (vel.x < 0f) vel.x = 0f;
            hitBoundary = true;
        }
        else if (pos.x >= maxX)
        {
            pos.x = maxX;
            if (vel.x > 0f) vel.x = 0f;
            hitBoundary = true;
        }

        if (hitBoundary)
        {
            targetRb.position = pos;
            targetRb.linearVelocity = vel;
        }

        return hitBoundary;
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
