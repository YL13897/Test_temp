using UnityEngine;

public class ScoreSystem : MonoBehaviour
{
    [Header("Player")]
    [SerializeField] Rigidbody targetRb;

    [Header("Leader")]
    [SerializeField] Rigidbody leaderRb;

    [Header("Scoring Parameters")]
    public float scoreAccumRate = 1.0f; // points deducted per second per unit total cost
    public float CappedWidthX = 1.0f; // retained for pointer/full-alignment band logic
    public float penaltyValue = 500f;

    [Header("Composite Score Weights")]
    [SerializeField] float w_tr = 0.1f;
    [SerializeField] float w_u = 0.0001f;
    [SerializeField] float w_emg = 2000f;

    [Header("Global Boundary")]
    public float minX = -4.5f;
    public float maxX = 4.5f;
    public bool enableBoundaryClamp = true;

    [Header("Penalty Control")]
    public float penaltyCooldown = 1.0f; // seconds between penalties while touching boundary

    private CORC.Demo.M2RoverBridge bridge;

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
            return;
        }

        ScoreManager.Instance.UpdateBoundaryUI(targetRb.position.x, minX, maxX);

        bool boundaryContact = DetectBoundaryContact();
        // Global gate: even if multiple callers fire, penalty can apply at most once per cooldown window.
        if (boundaryContact)
        {   
            ScoreManager.Instance.TryApplyBoundaryPenalty(penaltyValue, penaltyCooldown);
            return;
        }

        float xRover = targetRb.position.x;
        float xLeader = leaderRb.position.x;
        float xError = xLeader - xRover;
        float handleFx = GetHandleFx();

        float trackCost = CappedWidthX * CappedWidthX - xError * xError; // <0 inside band, 0 at boundary, >0 outside band
        float forceCost = handleFx * handleFx;
        float emgCost = ScoreManager.Instance.EmgScore;
        
        float wTrack = w_tr * trackCost;
        float wForce = w_u * forceCost;
        float wEmg = w_emg * emgCost;
        float totalCost = wTrack + wForce + wEmg;
        float deltaScore = scoreAccumRate * totalCost * Time.fixedDeltaTime;

        ScoreManager.Instance.SetCompositeMetrics(handleFx, wTrack, wForce, wEmg, totalCost);
        ScoreManager.Instance.AddToScores(deltaScore);
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
}
