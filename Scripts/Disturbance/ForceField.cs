using System;
using UnityEngine;

public class ForceField : MonoBehaviour
{
    public static bool HasPlayerEnteredAnyField { get; private set; } = false;
    // Unified disturbance source u(t) generated in Unity. Current design uses binary 0/1.
    public static float DisturbanceU { get; private set; } = 0f;
    // Estimated disturbance duration used by M2 as a safety auto-off timeout.
    public static float DisturbanceDurationSec { get; private set; } = 0.4f;
    public static event Action OnFirstPlayerEnteredAnyField;

    [SerializeField] ForceFieldPreview forceFieldPreview;

    // IsActiveThisRun: Indicates whether this force field instance is active for the current player traversal, determined by sampling the trigger probability on entry.
    // get; -> Anyone can read the value
    // private set; Only this class can change the value
    public bool IsActiveThisRun { get; private set; }
    private bool playerInsideThisActiveField = false;
    private float disturbanceElapsedSec = 0f;

    void Awake()
    {
        if (forceFieldPreview == null)
            forceFieldPreview = GetComponent<ForceFieldPreview>();
    }

    // ResetFirstEntryFlag(): Resets the static flags related to player entry and disturbance.
    public static void ResetFirstEntryFlag()
    {
        HasPlayerEnteredAnyField = false;
        DisturbanceU = 0f;
    }

    // OnEnable(): is called immediately after an object or script component becomes active.
    void OnEnable()
    {
        IsActiveThisRun = false;
        playerInsideThisActiveField = false;
        disturbanceElapsedSec = 0f;
    }

    // OnDisable(): Robustness protection - In case the field gets disabled while the player is still inside, we should reset the state to avoid "stuck in disturbance" issues.
    // OnDisable() is called when the behaviour becomes disabled or inactive.
    void OnDisable()
    {
        if (playerInsideThisActiveField)
        {
            playerInsideThisActiveField = false;
            DisturbanceU = 0f;
            disturbanceElapsedSec = 0f;
        }
    }


    // ------------------------------------------- Core Logic -----------------------------------
    /*
    OnTriggerEnter() ensures that when player enters an active disturbance field, Unity disturbance source u(t) is enabled,
    and when the player exits, u(t) is disabled.
    */

    // OnTriggerEnter(): Detects when the player enters the force field trigger, samples the disturbance state based on the defined probability,
    // and updates the disturbance source u(t) accordingly.
    void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;

        // if (!GlobalRandomSeed.IsInitialized)
        //     UnityEngine.Random.InitState(GlobalRandomSeed.Seed);

        if (!HasPlayerEnteredAnyField)
        {
            HasPlayerEnteredAnyField = true;
            OnFirstPlayerEnteredAnyField?.Invoke();
        }

        // Read the trigger probability for this force field from the ForceFieldPreview component.
        float Probability = forceFieldPreview.triggerProbability;

        // sample ONCE per section reuse
        IsActiveThisRun = ExperimentBlockControl.Instance != null
            ? ExperimentBlockControl.Instance.TriggerDisturbance(Probability)
            : UnityEngine.Random.value < Probability;


        // ----------------------- For testing: keep the field always active -----------------------

        // IsActiveThisRun = true;

        // -----------------------------------------------------------------------------------------


        if (!IsActiveThisRun) return;

        if (!playerInsideThisActiveField)
        {
            playerInsideThisActiveField = true;
            disturbanceElapsedSec = 0f;
            DisturbanceU = 1f;
        }
    }

    // OnTriggerExit(): Detects when the player exits the force field trigger and updates the disturbance state accordingly.
    void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag("Player")) return;
        if (playerInsideThisActiveField)
        {
            playerInsideThisActiveField = false;
            DisturbanceU = 0f;
            disturbanceElapsedSec = 0f;
        }
    }

    void FixedUpdate()
    {
        if (!IsActiveThisRun) return;
        if (DisturbanceU <= 0f) return;

        // Safe guard: If disturbance duration is exceeded, automatically turn off the disturbance.
        disturbanceElapsedSec += Time.fixedDeltaTime;
        if (disturbanceElapsedSec >= DisturbanceDurationSec)
        {
            DisturbanceU = 0f;
            return;
        }

        // ForceField only defines the disturbance timing. The active mode decides how to apply it.
    }

}
