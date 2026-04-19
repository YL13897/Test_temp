using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class ExperimentBlockControl : MonoBehaviour
{
    struct SectionSpec
    {
        public float leaderTargetX;
        public int previewDirection;

        public SectionSpec(float leaderTargetX, int previewDirection)
        {
            this.leaderTargetX = leaderTargetX;
            this.previewDirection = previewDirection < 0 ? -1 : 1;
        }
    }

    public static ExperimentBlockControl Instance { get; private set; }

    [Header("Block Config")]
    [SerializeField] float RecoverySeconds = 0.8f;
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

    [Header("Episode Mix")]
    [SerializeField] int leftLfCount = 10;
    [SerializeField] int rightRfCount = 10;
    [SerializeField] int leftRfCount = 10;
    [SerializeField] int rightLfCount = 10;

    [Header("Refs")]
    [SerializeField] LeaderHandler leader;
    [SerializeField] TMP_Text sectionIndexText;
    [SerializeField] TMP_Text countdownText;

    [Header("UI Timing")]
    [SerializeField] float countdownSeconds = 10f;

    [Header("Resume Start")]
    [SerializeField] int startBlockIdx = 1;
    [SerializeField] int startSectionIdx = 1;

    int currentBlockIndex = -1;// -1: no block prepared, 0..blockProbabilities.Length-1: prepared block index, blockProbabilities.Length: all blocks completed.
    int sectionInBlock = 0;
    bool blockRunning = false;
    float autoPauseAtTime = -1f;// Used for setting addtional recover time before block ends.
                                // -1: no auto-pause scheduled, >=0: time at which to auto-pause, PositiveInfinity: auto-pause requested but not yet scheduled.
    const float AutoPauseRequested = float.PositiveInfinity;
    float recoverySecondsClamped = 0f;
    float nextBlockStartAt = -1f;
    bool waitingForReturnAck = false;
    bool receivedReturnAck = false;
    int pendingStartSectionOffset = 0; // Used for resuming within a block.
    readonly List<SectionSpec> currentBlockSections = new List<SectionSpec>();
    

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
    public int CurrentBlockNumber => HasPreparedBlock ? currentBlockIndex + 1 : 0;
    public int CurrentSectionNumber
    {
        get
        {
            if (!HasPreparedBlock) return 0;
            if (blockRunning)
                return Mathf.Clamp(sectionInBlock, 1, SectionsPerBlock);
            return Mathf.Clamp(sectionInBlock + 1, 1, SectionsPerBlock);
        }
    }
    public bool ShouldAutoPauseNow => autoPauseAtTime >= 0f && autoPauseAtTime != AutoPauseRequested && Time.time >= autoPauseAtTime;
    public bool CountdownActive => nextBlockStartAt >= 0f && Time.time < nextBlockStartAt;
    public float CountdownRemain => CountdownActive ? nextBlockStartAt - Time.time : 0f;
    public int CurrentDirection => GetUpcomingSectionSpec().previewDirection;
    
    int SectionsPerBlock 
    {
        get {
            int total = leftLfCount + rightRfCount + leftRfCount + rightLfCount;
            return total < 1 ? 1 : total;
        }
    }

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
        RefreshUi();
    }

    void Update()
    {
        RefreshUi();
    }

    // PrepareRound(): Start the whole 9-block round from block 1.
    public void PrepareRound()
    {
        currentBlockIndex = Mathf.Clamp(startBlockIdx - 1, 0, blockProbabilities.Length - 1);
        pendingStartSectionOffset = Mathf.Clamp(startSectionIdx - 1, 0, SectionsPerBlock - 1);
        sectionInBlock = pendingStartSectionOffset;
        blockRunning = false;
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
        sectionInBlock = 0;
        pendingStartSectionOffset = 0;
        blockRunning = false;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        waitingForReturnAck = false;
        receivedReturnAck = false;
        leader.ClearBlockLaneSequence();
        currentBlockSections.Clear();
        RefreshUi();
    }

    // AbortCurrentBlock(): It is called if block interrupted manually when user presses the manual Return WAIT_START button.
    // This cancels only the current block and prepare to retry that same block later. 
    // Returning to the same block will start from the beginning of that block with a reshuffled lane sequence.
    public void AbortCurrentBlock()
    {
        if (!HasPreparedBlock) return;

        pendingStartSectionOffset = 0;
        sectionInBlock = 0;
        blockRunning = false;
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

        blockRunning = true;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        waitingForReturnAck = false;
        receivedReturnAck = false;
        RefreshUi();
    }

    // NotifySectionEntered(): To be called when a non-Section_BGIN section is entered.
    // The section boundary is the single source of truth for section progression within a block.
    public void NotifySectionEntered(string sectionRootName)
    {
        if (!blockRunning) return;
        if (string.IsNullOrEmpty(sectionRootName)) return;
        if (autoPauseAtTime >= 0f) return;

        StartNextSection();
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

    // PrepareCurrentBlock(): To set up the lane sequence for the currently prepared block based on the configured probabilities and section counts.
    void PrepareCurrentBlock()
    {
        if (!HasPreparedBlock) return;

        BuildCurrentBlockSections();
        int startOffset = pendingStartSectionOffset;
        pendingStartSectionOffset = 0;
        sectionInBlock = startOffset;
        blockRunning = false;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        waitingForReturnAck = false;
        receivedReturnAck = false;
        leader.SetBlockLaneSequence(BuildLaneSequence(startOffset));
        RefreshUi();
    }

    void StartNextSection()
    {
        if (sectionInBlock >= SectionsPerBlock)
        {
            CompleteCurrentBlock();
            return; // Prevent incrementing past the max sections
        }

        leader?.TriggerNextScheduledShift();
        sectionInBlock++;
        RefreshUi();
    }

    void CompleteCurrentBlock()
    {
        blockRunning = false;
        autoPauseAtTime = Time.time + recoverySecondsClamped;
        nextBlockStartAt = Time.time + Mathf.Max(0f, countdownSeconds);
        RefreshUi();
    }

    // BuildLaneSequence(): Helper function to construct the lane sequence for the current block.
    // Returns an array of target X positions for each section in the block.
    float[] BuildLaneSequence(int startOffset = 0)
    {
        List<float> sequence = new List<float>(SectionsPerBlock - startOffset);
        for (int i = startOffset; i < SectionsPerBlock; i++)
            sequence.Add(GetSectionSpecAtBlockIndex(i).leaderTargetX);

        return sequence.ToArray();
    }

    void BuildCurrentBlockSections()
    {
        currentBlockSections.Clear();
        if (leader == null) return;

        for (int i = 0; i < leftLfCount; i++)
            currentBlockSections.Add(new SectionSpec(leader.LeftLaneX, -1));
        for (int i = 0; i < rightRfCount; i++)
            currentBlockSections.Add(new SectionSpec(leader.RightLaneX, 1));
        for (int i = 0; i < leftRfCount; i++)
            currentBlockSections.Add(new SectionSpec(leader.LeftLaneX, 1));
        for (int i = 0; i < rightLfCount; i++)
            currentBlockSections.Add(new SectionSpec(leader.RightLaneX, -1));

        if (currentBlockSections.Count == 0)
            currentBlockSections.Add(new SectionSpec(leader.LeftLaneX, -1));

        for (int i = currentBlockSections.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (currentBlockSections[i], currentBlockSections[j]) = (currentBlockSections[j], currentBlockSections[i]);
        }
    }

    SectionSpec GetUpcomingSectionSpec()
    {
        return GetSectionSpecAtBlockIndex(sectionInBlock);
    }

    SectionSpec GetSectionSpecAtBlockIndex(int blockIndex)
    {
        if (currentBlockSections.Count == 0 || blockIndex < 0)
            return new SectionSpec(leader != null ? leader.LeftLaneX : 0f, -1);

        if (blockIndex >= currentBlockSections.Count)
            return currentBlockSections[currentBlockSections.Count - 1];

        return currentBlockSections[blockIndex];
    }


    void RefreshUi()
    {
        if (sectionIndexText != null)
        {
            if (!HasPreparedBlock || IsRoundComplete)
            {
                sectionIndexText.text = "Block: -- | Section: --";
            }
            else
            {
                bool sectionStarted = ScoreManager.Instance != null && ScoreManager.Instance.HasStartedScoring;
                int displaySection = sectionStarted ? sectionInBlock : sectionInBlock + 1;
                sectionIndexText.text = $"Block: {CurrentBlockNumber}/{blockProbabilities.Length} | Section: {displaySection}/{SectionsPerBlock}";
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
                countdownText.text = $"Relax! Reset to center!\n Next block in {Mathf.CeilToInt(CountdownRemain)} s";
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
