using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class ExperimentBlockControl : MonoBehaviour
{
    public static ExperimentBlockControl Instance { get; private set; }

    [Header("Block Config")]
    [SerializeField] int trialsPerBlock = 3;
    [SerializeField] int leftTrialsPerBlock = 1;
    [SerializeField] float RecoverySeconds = 1f;
    [SerializeField] float[] blockProbabilities =
    {
        1f / 32f,
        1f / 16f,
        1f / 8f,
        1f / 4f,
        1f / 2f,
        3f / 4f,
        7f / 8f,
        15f / 16f,
        31f / 32f
    };

    [Header("Refs")]
    [SerializeField] LeaderHandler leader;
    [SerializeField] TMP_Text trialIndexText;
    [SerializeField] TMP_Text countdownText;

    [Header("UI Timing")]
    [SerializeField] float countdownSeconds = 10f;

    int currentBlockIndex = -1;// -1: no block prepared, 0..blockProbabilities.Length-1: prepared block index, blockProbabilities.Length: all blocks completed.
    int trialInBlock = 0;
    bool blockRunning = false;
    bool pendingCompleteTrigger = false;
    float autoPauseAtTime = -1f;// Used for setting addtional recover time before block ends.
                                // -1: no auto-pause scheduled, >=0: time at which to auto-pause, PositiveInfinity: auto-pause requested but not yet scheduled.
    const float AutoPauseRequested = float.PositiveInfinity;
    float recoverySecondsClamped = 0f;
    float nextBlockStartAt = -1f;
    bool waitingForReturnAck = false;
    bool receivedReturnAck = false;
    

    // CurrentBlockProbability: returns the probability for the currently prepared block, or 0 if no block is prepared or all blocks are completed. 
    // The value is clamped to [0,1] to ensure it remains a valid probability.
    public float CurrentBlockProbability
    {   get
    {
            if (currentBlockIndex < 0 || currentBlockIndex >= blockProbabilities.Length)
                return 0f;
            return Mathf.Clamp01(blockProbabilities[currentBlockIndex]);
        }
    }

    public bool HasPreparedBlock => currentBlockIndex >= 0 && currentBlockIndex < blockProbabilities.Length;
    public bool IsRoundComplete => currentBlockIndex >= blockProbabilities.Length;
    public bool ShouldAutoPauseNow => autoPauseAtTime >= 0f && autoPauseAtTime != AutoPauseRequested && Time.time >= autoPauseAtTime;
    public bool CountdownActive => nextBlockStartAt >= 0f && Time.time < nextBlockStartAt;
    public float CountdownRemain => CountdownActive ? nextBlockStartAt - Time.time : 0f;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (leader == null)
            leader = FindFirstObjectByType<LeaderHandler>();

        recoverySecondsClamped = Mathf.Max(0f, RecoverySeconds);
        leftTrialsPerBlock = Mathf.Clamp(leftTrialsPerBlock, 0, trialsPerBlock);
        RefreshUi();
    }

    void Update()
    {
        RefreshUi();
    }

    // PrepareRound(): Start the whole 9-block round from block 1.
    public void PrepareRound()
    {
        currentBlockIndex = 0;
        trialInBlock = 0;
        blockRunning = false;
        pendingCompleteTrigger = false;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        waitingForReturnAck = false;
        receivedReturnAck = false;
        PrepareCurrentBlock();
    }

    // ResetRoundState(): Clear everything and exit the round state.
    public void ResetRoundState()
    {
        currentBlockIndex = -1;
        trialInBlock = 0;
        blockRunning = false;
        pendingCompleteTrigger = false;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        waitingForReturnAck = false;
        receivedReturnAck = false;
        leader.ClearBlockLaneSequence();
        RefreshUi();
    }

    // AbortCurrentBlock(): It is called if block interrupted manually when user presses the manual Return WAIT_START button.
    // This cancels only the current block and prepare to retry that same block later. 
    // Returning to the same block will start from the beginning of that block with a reshuffled lane sequence.
    public void AbortCurrentBlock()
    {
        if (!HasPreparedBlock) return;

        trialInBlock = 0;
        blockRunning = false;
        pendingCompleteTrigger = false;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        waitingForReturnAck = false;
        receivedReturnAck = false;
        PrepareCurrentBlock();
    }

    // CanStartPreparedBlock(): Returns true if the prepared block can be started (i.e., not already running and no auto-pause pending).
    public bool CanStartPreparedBlock()
    {
        return HasPreparedBlock && !blockRunning && autoPauseAtTime < 0f;
    }

    // NotifyBlockStarted(): To be called by the block when it starts, to update the internal state accordingly.
    public void NotifyBlockStarted()
    {
        if (!HasPreparedBlock)
            PrepareRound();

        trialInBlock = 0;
        blockRunning = true;
        pendingCompleteTrigger = false;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        waitingForReturnAck = false;
        receivedReturnAck = false;
        RefreshUi();
    }

    public void NotifyTrialPreviewEntered()
    {
        if (!blockRunning) return;
        if (autoPauseAtTime >= 0f) return;
        if (pendingCompleteTrigger) return;
        if (trialInBlock >= trialsPerBlock) return;

        trialInBlock++;
        if (trialInBlock >= trialsPerBlock)
            pendingCompleteTrigger = true;

        RefreshUi();
    }

    // NotifySectionEntered(): To be called by the block when a new section is entered, and ignore the beginning section (Section_BGIN). 
    public void NotifySectionEntered(string sectionRootName)
    {
        if (!blockRunning) return;
        if (string.IsNullOrEmpty(sectionRootName)) return;
        if (autoPauseAtTime >= 0f) return;
        if (!pendingCompleteTrigger) return;

        // All trials in the block are completed now.
        blockRunning = false;
        pendingCompleteTrigger = false;
        autoPauseAtTime = Time.time + recoverySecondsClamped;
        nextBlockStartAt = Time.time + Mathf.Max(0f, countdownSeconds);
        RefreshUi();
    }

    // MarkAutoPauseRequested(): To be called when an auto-pause is requested.
    public void MarkAutoPauseRequested()
    {
        if (autoPauseAtTime >= 0f)
        {
            autoPauseAtTime = AutoPauseRequested;
            waitingForReturnAck = true;
            receivedReturnAck = false;
        }
    }

    public bool NotifyReturnWaitReady()
    {
        if (!waitingForReturnAck)
            return false;

        receivedReturnAck = true;
        return TryAdvancePendingCompletion();
    }

    // AdvanceBlock(): To be called to move to the next block after completing the current one. 
    // Returns true if successfully advanced to the next block; False if there are no more blocks left.
    public bool AdvanceBlock()
    {
        if (autoPauseAtTime < 0f) return false;
        return AdvanceBlockInternal();
    }

    public bool TryAdvancePendingCompletion()
    {
        if (!waitingForReturnAck) return false;
        if (!receivedReturnAck) return false;
        if (CountdownActive) return false;

        waitingForReturnAck = false;
        receivedReturnAck = false;
        return AdvanceBlockInternal();
    }

    bool AdvanceBlockInternal()
    {
        if (currentBlockIndex + 1 < blockProbabilities.Length)
        {
            currentBlockIndex++;
            PrepareCurrentBlock();
            RefreshUi();
            return true;
        }

        currentBlockIndex = blockProbabilities.Length;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        leader.ClearBlockLaneSequence();
        RefreshUi();
        return false;
    }

    // PrepareCurrentBlock(): To set up the lane sequence for the currently prepared block based on the configured probabilities and trial counts.
    void PrepareCurrentBlock()
    {
        if (!HasPreparedBlock) return;

        trialInBlock = 0;
        blockRunning = false;
        pendingCompleteTrigger = false;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        waitingForReturnAck = false;
        receivedReturnAck = false;
        leader.SetBlockLaneSequence(BuildLaneSequence());
        RefreshUi();
    }

    // BuildLaneSequence(): Helper function to construct the lane sequence for the current block.
    // Returns an array of target X positions for each trial in the block.
    float[] BuildLaneSequence()
    {
        List<float> sequence = new List<float>(trialsPerBlock);
        int centerTrials = Mathf.Max(0, trialsPerBlock - leftTrialsPerBlock);

        for (int i = 0; i < leftTrialsPerBlock; i++)
            sequence.Add(leader.LeftLaneX);

        for (int i = 0; i < centerTrials; i++)
            sequence.Add(leader.CenterLaneX);

        // Fisher-Yates shuffle
        for (int i = sequence.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1); // [0, i] (inclusive of 0, exclusive of i+1)
            (sequence[i], sequence[j]) = (sequence[j], sequence[i]);
        }

        return sequence.ToArray();
    }


    void RefreshUi()
    {
        if (trialIndexText != null)
        {
            if (!HasPreparedBlock || IsRoundComplete)
            {
                trialIndexText.text = "Trial: --";
            }
            else
            {
                int displayTrial = Mathf.Clamp(trialInBlock, 0, Mathf.Max(1, trialsPerBlock));
                trialIndexText.text = $"Trial: {displayTrial}/{trialsPerBlock}";
            }
        }

        if (countdownText != null)
        {
            if (IsRoundComplete)
            {
                countdownText.text = "Round complete";
            }
            else if (CountdownActive)
            {
                countdownText.text = $"Next block in {Mathf.CeilToInt(CountdownRemain)} s";
            }
            else if (HasPreparedBlock && !blockRunning && autoPauseAtTime < 0f)
            {
                countdownText.text = "Ready";
            }
            else
            {
                countdownText.text = string.Empty;
            }
        }
    }
}
