// Minimal 3D follower with explicit X mapping for M2 -> Unity rover alignment.
using UnityEngine;
using CORC.Demo;

public class M2WorldFollower : MonoBehaviour
{
    [Header("Refs")]
    public CORC.CORCM2 m2;
    public M2RoverBridge bridge;
    public Transform marker;
    public Rigidbody markerRb;
    private RoverHandler rover;

    [Header("X Mapping (exact)")]
    [Tooltip("Unity X range.")]
    public float unityXMin = -10f;
    public float unityXMax = 10f;
    
    [Tooltip("Mapped M2 X range in meters.")]
    public float m2XMin = 0.2f;
    public float m2XMax = 0.44f;
    [Tooltip("Reference center: M2 A point and Unity center line.")]
    public float m2CenterX = 0.32f;

    // [Tooltip("Mapped selected M2 axis range in meters.")]
    // public float m2XMin = 0.0f;
    // public float m2XMax = 0.5f;
    // [Tooltip("Reference center: M2 A point on selected axis and Unity center line.")]
    // public float m2CenterX = 0.25f;

    public float unityCenterX = 0f;

    [Header("Motion")]
    [Range(0f, 1f)] public float smooth = 0.005f; // smoothness factor for position updates
    [Tooltip("How quickly sync bias returns to zero after disturbance.")]
    public float biasRecoverRate = 4f;
    [Tooltip("Clamp on disturbance-induced Unity X bias.")]
    [Range(0f, 10f)] public float maxBias = 3f;

    private Vector3 lastPos;
    private float syncXBias = 0f; // disturbance-induced x offset from nominal mapping
    private bool hasBiasBase = false;
    private float biasBaseX = 0f;

    void Awake()
    {
        if (marker == null) marker = transform;
        if (markerRb == null && marker != null) markerRb = marker.GetComponent<Rigidbody>();
        if (bridge == null) bridge = FindFirstObjectByType<M2RoverBridge>();
        rover = marker != null ? marker.GetComponent<RoverHandler>() : null;
        if (rover == null && bridge != null) rover = bridge.rover;
    }

    void FixedUpdate()
    {
        if (marker == null || markerRb == null) return;

        if (IsKeyboardMode())
        {
            ApplyKeyboardBias();
            return;
        }

        int axis = bridge != null ? Mathf.Clamp(bridge.m2Axis, 0, 1) : 0;
        if (m2 == null || !m2.IsInitialised() || m2.Client == null || !m2.Client.IsConnected()) return;
        if (m2.State == null || m2.State["X"] == null || m2.State["X"].Length <= axis) return;

        float m2X = (float)m2.State["X"][axis];

        // VEL mode uses velocity mapping and should not position-sync rover.
        if (IsM2VelMode())
        {
            syncXBias = 0f;
            hasBiasBase = false;
            return;
        }

        float nominalX = MapM2XToUnityX(m2X);

        // POS+HRI mode maps stiffness to disturbance-induced bias.
        if (IsM2HriPosMode())
        {
            if (IsHriPosDisturbanceActive())
            {
                int dir = ExperimentBlockControl.Instance != null ? ExperimentBlockControl.Instance.CurrentDirection : 0;
                float k = bridge != null ? bridge.StiffnessCmd : 0f;
                float k01 = bridge != null
                    ? Mathf.InverseLerp(bridge.StiffnessMin, bridge.StiffnessMax, k)
                    : 0f;
                syncXBias = dir * maxBias * (1f - k01);
            }
            else
            {
                float recoverAlpha = Mathf.Clamp01(biasRecoverRate * Time.fixedDeltaTime);
                syncXBias = Mathf.Lerp(syncXBias, 0f, recoverAlpha);
            }

            syncXBias = Mathf.Clamp(syncXBias, -maxBias, maxBias);
        }
        else
        {
            syncXBias = 0f;
        }
        hasBiasBase = false;

        float targetX = nominalX + syncXBias;
        Vector3 target = BuildTargetPosition(targetX);
        
        ApplySmoothPosition(target);
    }

    // BuildTargetPosition: construct the target position for the marker from world X target.
    private Vector3 BuildTargetPosition(float worldX)
    {
        float yWorld = marker.position.y;

        return new Vector3(worldX, yWorld, marker.position.z);
    }

    // Center-preserving linear map. By default this does NOT clamp input range.
    private float MapM2XToUnityX(float m2X)
    {
        float denom = Mathf.Max(1e-6f, m2XMax - m2XMin);

        float m2Input = m2X;

        // Pure linear scaling around center: m2CenterX -> unityCenterX.
        float slope = (unityXMax - unityXMin) / denom;
        return unityCenterX + (m2Input - m2CenterX) * slope;
    }

    private bool IsM2HriPosMode()
    {
        if (bridge == null) return false;
        if (bridge.unityMode != M2RoverBridge.UnityDriveMode.Mode2_M2) return false;
        if (bridge.hriModeCode != 1) return false;
        if (bridge.ctrlModeCode != 1) return false;
        return true;
    }

    private bool IsKeyboardMode()
    {
        return bridge != null && bridge.unityMode == M2RoverBridge.UnityDriveMode.Mode1_Keyboard;
    }

    private bool IsM2VelMode()
    {
        if (bridge == null) return false;
        if (bridge.unityMode != M2RoverBridge.UnityDriveMode.Mode2_M2) return false;
        return bridge.ctrlModeCode == 2;
    }

    private bool IsHriPosDisturbanceActive()
    {
        if (!IsM2HriPosMode()) return false;
        return ForceField.DisturbanceU > 0.5f;
    }

    public void ResetBias()
    {
        syncXBias = 0f;
        lastPos = Vector3.zero;
        hasBiasBase = false;
    }

    private void ApplyKeyboardBias()
    {
        bool active = ForceField.DisturbanceU > 0.5f;
        if (active)
        {
            if (!hasBiasBase)
            {
                biasBaseX = markerRb.position.x;
                lastPos = markerRb.position;
                hasBiasBase = true;
            }

            int dir = ExperimentBlockControl.Instance != null ? ExperimentBlockControl.Instance.CurrentDirection : 0;
            if (dir == 0) dir = 1;
            syncXBias = dir * maxBias;
        }
        else
        {
            float recoverAlpha = Mathf.Clamp01(biasRecoverRate * Time.fixedDeltaTime);
            syncXBias = Mathf.Lerp(syncXBias, 0f, recoverAlpha);
            if (Mathf.Abs(syncXBias) < 1e-3f)
            {
                syncXBias = 0f;
                hasBiasBase = false;
                return;
            }
        }

        if (!hasBiasBase) return;
        Vector3 target = BuildTargetPosition(biasBaseX + Mathf.Clamp(syncXBias, -maxBias, maxBias));
        ApplySmoothPosition(target);
    }

    // ApplySmoothPosition: Smoothly move the marker towards the target position. The 'smooth' parameter controls the responsiveness.
    private void ApplySmoothPosition(Vector3 target)
    {
        if (rover != null && rover.enableBoundaryClamp)
            target.x = Mathf.Clamp(target.x, rover.minX, rover.maxX);

        float alpha = smooth > 0f ? 1f - Mathf.Pow(1f - smooth, Time.fixedDeltaTime * 60f) : 1f; // frame-rate independent smoothing
        lastPos = Vector3.Lerp(lastPos == Vector3.zero ? marker.position : lastPos, target, alpha); // use lastPos to avoid snapping on first frame
        if (rover != null && rover.enableBoundaryClamp)
            lastPos.x = Mathf.Clamp(lastPos.x, rover.minX, rover.maxX);

        Vector3 next = markerRb.position;
        next.x = lastPos.x;
        markerRb.position = next;
    }

}
