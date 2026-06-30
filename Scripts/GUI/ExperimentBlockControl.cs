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
    [SerializeField] bool addFixedBlocks = true;
    [SerializeField] bool useFixSequence = false;
    [SerializeField] bool useTestSequence = false;
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


    // ============================ 40 Trials setting ==================================

    // One 40-trial line per regular block; each digit indexes blockProbabilities (0-8).
    [SerializeField, HideInInspector] string randomSequence =
        "6803210558315456645447671730105166454406" +
        "1651180305627481384230737863518653205784" +
        "2012754452351875035067866028707885011082" +
        "0271873764622572508016661834403578758145" +
        "2064138864285301024662647742846041023038" +
        "4602725835548327706127047101142323458780" +
        "8676312022113614105183456617767512342251" +
        "3560423533671534738348683837071877801531" +
        "2665235524845824706046421238470208372731";
    // Aligned case codes: 0=Left/LF, 1=Right/RF, 2=Left/RF, 3=Right/LF.
    [SerializeField, HideInInspector] string caseSequence =
        "0122010003313223021012102033233333213111" +
        "1123013113333032213231123320330020311100" +
        "2110011301301211332310310202123203023012" +
        "1130123223022303331213120202301001311120" +
        "3022321320131131231122003033020202001221" +
        "1301221312212131211202231313011300332021" +
        "2230323310020333222220211203121231220230" +
        "2330032010300213310220000322033202031001" +
        "1312000011301030232110010033110220322110";



// ============================ 20 Trials setting ==================================

    // Short 20-trial test sequence; each line is one regular block.
    [SerializeField, HideInInspector] string testRandomSequence =
        "72610880445177532631" +
        "14483145047302600638" +
        "45075250045826277201" +
        "02481255811678237102" +
        "87214183658237846786" +
        "14711168831826065160" +
        "73565540766225737433" +
        "37326236651832756838" +
        "74054802344010304554";
    // Aligned short case codes: 0=Left/LF, 1=Right/RF, 2=Left/RF, 3=Right/LF.
    [SerializeField, HideInInspector] string testCaseSequence =
        "10201232222220100020" +
        "10110210130313002123" +
        "31011203313103103111" +
        "00202301033132211233" +
        "12130021220122331231" +
        "03321112331210200330" +
        "03201300133033011021" +
        "03232213220002222031" +
        "30231112012333323032";

// ============================================================================
    
    
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

    int currentBlockIndex = -1;
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
    bool hasActiveSection = false;
    readonly List<SectionSpec> currentBlockSections = new List<SectionSpec>();

    public float CurrentProbability
    {
        get
        {
            if (!HasPreparedBlock)
                return 0f;

            int block = currentBlockIndex;
            if (addFixedBlocks)
            {
                if (block < 2)
                    return block;
                block -= 2;
            }

            if (useFixSequence)
                return Mathf.Clamp01(blockProbabilities[block]);

            int section = Mathf.Clamp(hasActiveSection ? sectionInBlock : sectionInBlock + 1, 0, SectionsPerBlock - 1);
            int trial = block * SectionsPerBlock + section;
            int probability = ActiveRandomSequence[trial] - '0';
            return Mathf.Clamp01(blockProbabilities[probability]);
        }
    }

    int TotalBlocks => blockProbabilities.Length + (addFixedBlocks ? 2 : 0);
    public bool HasPreparedBlock => currentBlockIndex >= 0 && currentBlockIndex < TotalBlocks;
    public bool IsRoundComplete => currentBlockIndex >= TotalBlocks;
    public bool HasActiveSection => hasActiveSection;
    public int CurrentBlockNumber => HasPreparedBlock ? currentBlockIndex + 1 : 0;
    public int CurrentSectionNumber
    {
        get
        {
            if (!HasPreparedBlock) return 0;
            if (hasActiveSection)
                return Mathf.Clamp(sectionInBlock + 1, 1, SectionsPerBlock);
            return Mathf.Clamp(sectionInBlock + 2, 1, SectionsPerBlock);
        }
    }
    public bool ShouldAutoPauseNow => autoPauseAtTime >= 0f && autoPauseAtTime != AutoPauseRequested && Time.time >= autoPauseAtTime;
    public bool CountdownActive => nextBlockStartAt >= 0f && Time.time < nextBlockStartAt;
    public float CountdownRemain => CountdownActive ? nextBlockStartAt - Time.time : 0f;
    public int CurrentDirection => GetCurrentOrUpcomingSectionSpec().previewDirection;

    public bool TriggerDisturbance(float probability)
    {
        int section = Mathf.Clamp(hasActiveSection ? sectionInBlock : sectionInBlock + 1, 0, SectionsPerBlock - 1);
        int trial = currentBlockIndex * SectionsPerBlock + section;
        // A stable but different random draw for each trial.
        int seed = unchecked(GlobalRandomSeed.Seed + trial);
        return new System.Random(seed).NextDouble() < Mathf.Clamp01(probability);
    }
    
    int SectionsPerBlock 
    {
        get {
            int total = ActiveLeftLfCount + ActiveRightRfCount + ActiveLeftRfCount + ActiveRightLfCount;
            return total < 1 ? 1 : total;
        }
    }
    int ActiveLeftLfCount => useTestSequence ? 5 : leftLfCount;
    int ActiveRightRfCount => useTestSequence ? 5 : rightRfCount;
    int ActiveLeftRfCount => useTestSequence ? 5 : leftRfCount;
    int ActiveRightLfCount => useTestSequence ? 5 : rightLfCount;
    string ActiveRandomSequence => useTestSequence ? testRandomSequence : randomSequence;
    string ActiveCaseSequence => useTestSequence ? testCaseSequence : caseSequence;

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

    // PrepareRound(): Start the whole round from the configured block.
    public void PrepareRound()
    {
        currentBlockIndex = Mathf.Clamp(startBlockIdx - 1, 0, TotalBlocks - 1);
        pendingStartSectionOffset = Mathf.Clamp(startSectionIdx - 1, 0, SectionsPerBlock - 1);
        sectionInBlock = pendingStartSectionOffset - 1;
        blockRunning = false;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        waitingForReturnAck = false;
        receivedReturnAck = false;
        hasActiveSection = false;
        PrepareCurrentBlock();
    }

    // ResetRoundState(): Clear everything and exit the round state.
    public void ResetRoundState()
    {
        currentBlockIndex = -1;
        sectionInBlock = -1;
        pendingStartSectionOffset = 0;
        blockRunning = false;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        waitingForReturnAck = false;
        receivedReturnAck = false;
        hasActiveSection = false;
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
        sectionInBlock = -1;
        blockRunning = false;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        waitingForReturnAck = false;
        receivedReturnAck = false;
        hasActiveSection = false;
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
        hasActiveSection = false;
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
        if (currentBlockIndex + 1 < TotalBlocks)
        {
            currentBlockIndex++;
            PrepareCurrentBlock();
            RefreshUi();
            return true;
        }

        currentBlockIndex = TotalBlocks;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        RefreshUi();
        return false;
    }

    // PrepareCurrentBlock(): To set up the lane sequence for the currently prepared block based on the configured probabilities and section counts.
    void PrepareCurrentBlock()
    {
        if (!HasPreparedBlock) return;

        BuildCurrentBlockSections();
        pendingStartSectionOffset = 0;
        sectionInBlock = pendingStartSectionOffset - 1;
        blockRunning = false;
        autoPauseAtTime = -1f;
        nextBlockStartAt = -1f;
        waitingForReturnAck = false;
        receivedReturnAck = false;
        hasActiveSection = false;
        RefreshUi();
    }

    void StartNextSection()
    {
        int nextSectionIndex = sectionInBlock + 1;
        if (nextSectionIndex >= SectionsPerBlock)
        {
            CompleteCurrentBlock();
            return; // Prevent incrementing past the max sections
        }

        sectionInBlock = nextSectionIndex;
        hasActiveSection = true;
        leader?.SetTargetLane(GetSectionSpecAtBlockIndex(sectionInBlock).leaderTargetX);
        RefreshUi();
    }

    void CompleteCurrentBlock()
    {
        blockRunning = false;
        hasActiveSection = false;
        autoPauseAtTime = Time.time + recoverySecondsClamped;
        nextBlockStartAt = Time.time + Mathf.Max(0f, countdownSeconds);
        RefreshUi();
    }

    void BuildCurrentBlockSections()
    {
        currentBlockSections.Clear();
        if (leader == null) return;

        int block = currentBlockIndex - (addFixedBlocks ? 2 : 0);
        if (!useFixSequence && block >= 0)
        {
            int start = block * SectionsPerBlock;
            for (int i = 0; i < SectionsPerBlock; i++)
            {
                char value = ActiveCaseSequence[start + i];
                float x = value == '0' || value == '2' ? leader.LeftLaneX : leader.RightLaneX;
                int direction = value == '0' || value == '3' ? -1 : 1;
                currentBlockSections.Add(new SectionSpec(x, direction));
            }
            return;
        }

        for (int i = 0; i < ActiveLeftLfCount; i++)
            currentBlockSections.Add(new SectionSpec(leader.LeftLaneX, -1));
        for (int i = 0; i < ActiveRightRfCount; i++)
            currentBlockSections.Add(new SectionSpec(leader.RightLaneX, 1));
        for (int i = 0; i < ActiveLeftRfCount; i++)
            currentBlockSections.Add(new SectionSpec(leader.LeftLaneX, 1));
        for (int i = 0; i < ActiveRightLfCount; i++)
            currentBlockSections.Add(new SectionSpec(leader.RightLaneX, -1));

        if (currentBlockSections.Count == 0)
            currentBlockSections.Add(new SectionSpec(leader.LeftLaneX, -1));

        for (int i = currentBlockSections.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (currentBlockSections[i], currentBlockSections[j]) = (currentBlockSections[j], currentBlockSections[i]);
        }
    }

    SectionSpec GetCurrentOrUpcomingSectionSpec()
    {
        int sectionIndex = hasActiveSection ? sectionInBlock : sectionInBlock + 1;
        return GetSectionSpecAtBlockIndex(sectionIndex);
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
                int displaySection = hasActiveSection ? sectionInBlock + 1 : sectionInBlock + 2;
                displaySection = Mathf.Clamp(displaySection, 1, SectionsPerBlock);
                sectionIndexText.text = $"Block: {CurrentBlockNumber}/{TotalBlocks} | Section: {displaySection}/{SectionsPerBlock}";
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
                countdownText.text = $"Relax! Reset to centre!\n Next block in {Mathf.CeilToInt(CountdownRemain)} s";
            }
            else if (HasPreparedBlock && !blockRunning && autoPauseAtTime < 0f)
            {
                countdownText.text = "Ready!";
            }
            else
            {
                countdownText.text = string.Empty;
            }
        }
    }
}
