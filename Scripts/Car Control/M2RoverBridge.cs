
// Unity
using System;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

/* 
    This component acts as a bridge between the CORC M2 robot and the Unity rover control, 
    allowing to drive the rover using M2 handle input and send realtime feedback forces to 
    M2 based on the rover state. 
    The main logic is in Update() where we read M2 state, compute the desired steering and 
    feedback forces, and send commands to both M2 and the rover accordingly.   
*/

namespace CORC.Demo 
{
    public class M2RoverBridge : MonoBehaviour
    {
        [Header("Refs")]
        public CORC.CORCM2 m2;
        public RoverHandler rover;
        public LeaderHandler leader;
        public M2WorldFollower worldFollower;
        public Transform roverTransform;
        public Rigidbody roverRigidbody;

        [Header("Participant Calibration")]
        public float CalibForce = 50f;
        public float StandbyK = 1200f;

        public float m2CenterX = 0.32f; // The M2 handle X position that corresponds to the center reference (point A).

        public enum UnityDriveMode
        {
            Mode1_Keyboard = 1,
            Mode2_M2 = 2
        }

        [Header("Unity Mode")]
        public UnityDriveMode unityMode = UnityDriveMode.Mode1_Keyboard;
        bool blockActive = false;
        [Tooltip("1=V1_HRI, 2=V2_PHRI")]
        public int hriModeCode = 2;
        [Tooltip("1=V1_POS, 2=V2_VEL")]
        public int ctrlModeCode = 1;

        [Header("POS mode (handle offset -> steer)")]
        float handleXDeadZone = 0.005f;
        public float handleXToSteer = 5.0f; // steering command per meter of handle offset in POS mode

        [Header("M2 VEL mode (open-loop damped steer)")]
        public float velHandleXDeadZone = 0.01f;
        public float velOpenLoopKp = 1.0f; // steer response to handle offset
        public float velOpenLoopKd = 2.5f; // damping term from current lateral velocity
        public float velSteerClamp = 0.30f; // VEL-only steering clamp to avoid saturation oscillation
        public bool velDebugWarning = true;
        public float velDebugHz = 10.0f;

        [Header("M2 VEL runtime override")]
        public bool forceVelRuntimeParams = true; // prevent scene/inspector stale values in M2+VEL
        public float forcedVelOpenLoopKp = 1.0f;
        public float forcedVelOpenLoopKd = 2.5f;
        public float forcedVelSteerClamp = 0.30f;

        [Header("Shared steering limits")]
        public float steerClamp = 1.0f;
        
        [Header("Common hotkeys")]
        public bool enableHotkeys = true;

        [Header("M2 to Unity mapping")]
        public float targetWorldXScale = 5.0f; // Scaling factor sync/snap mapping between M2 and Unity, considered as the runtime control gain of M2 handle.
        public float worldXCenter = 0.0f; // The world X coordinate that corresponds to the M2 handle center (zero) position. 
        [Tooltip("Unity centerline X that should correspond to M2 point A.")]
        
        public float unityCenterX = 0.0f; // Unity X coordinate that corresponds to M2 handle point A (the center reference).
        public bool autoSnapOnM2Reconnect = true; // Whether to automatically snap rover to M2-mapped position when M2 reconnects, to prevent jump forces due to position mismatch.

        // public float CmdSendRateHz = 25.0f; // Rate limit for sending feedback commands to M2 (Hz), used for FCR2 command.
        
        bool enableKeyboardInMode1 = true;

        // (1) isPaused=true: completely frozen; 
        // (2) isPaused=false, isDriving=true: normal driving; 
        // (3) isPaused=false, isDriving=false: not moving forward but not frozen (can be continued by external logic)
        private bool isDriving = false;
        private bool isPaused = false;

        // private float nextFeedbackSendTime = 0.0f;
        private float nextVelDebugTime = 0.0f;
        private bool lastM2Connected = false; // Track M2 connection status to detect reconnects for auto-snapping
        private bool hasSentDisturbanceState = false; // To track whether we've sent the disturbance state to M2, to avoid redundant commands
        private bool lastDisturbanceState = false; // To track the last disturbance state sent to M2, for change detection
        private Vector3 roverInitialPosition; // To store the initial pose of the rover for resetting, captured in Awake()
        
        // Flag indicating whether the reference point has been initialized.
        // This is used to ensure that we have a valid reference for computing relative handle movements and corresponding rover target positions,
        // especially after mode switches or block starts.
        private bool syncRefReady = false;

        // The M2 handle X position recorded at the start of the block (or after mode switch), used as the zero point for relative movement.
        private float handleXRef = 0.0f; 
        // The rover X position recorded at the start of the block (or after mode switch), used as the reference point for computing target X based on handle movement.
        private float roverXRef = 0.0f;

        // ----------------------------------------------------------------------------------------------------------------------
        
        // Delsys EMG background data collection
        [Header("EMG Settings")]
        [SerializeField] private bool delsysEnable;
        [SerializeField] private bool emgRecordFlag = true;
        [SerializeField] private string emgPathOverride = "";
        [SerializeField] private bool scoreLogRecordFlag = true;
        [SerializeField] private string scoreLogPathOverride = "";
        [SerializeField] private EMGFilter.EmgSignalView emgSignalMode = EMGFilter.EmgSignalView.Envelope;
        [SerializeField] private float emgScoreDownsampleHz = 100f;
        [SerializeField] private EMGFilter emgFilter;
        private DelsysEMG delsysEMG = new DelsysEMG();
        [SerializeField] private int emgRecordCount = 0; // To keep track of EMG recordings within the current block.
        private bool emgIsRecording = false;
        private bool scoreLogIsRecording = false;

        [Header("EMG Channel Preview")]
        [SerializeField] private int emgPreviewMaxChannels = 8;
        [SerializeField] private float emgPreviewHz = 10f;
        private TMP_Text emgScoreText;
        private float emgScoreScale = 1000f;
        // private float emgScoreEmaLerp = 0.15f; // Smoothing factor for exponential moving average of EMG score, between 0 (no update) and 1 (no smoothing).
        private float emgScoreFiltered = 0f;
        private double emgScoreTimestamp = 0.0;
        private bool emgShutdown = false;
        private double nextEmgUpdateTime = 0.0; // To control the update rate of EMG preview in the UI, we track the next allowed update time based on the specified preview Hz rate.
        private string emgSessionFilePath;
        private string scoreLogSessionFilePath;
        private StreamWriter scoreLogWriter;
        private int scoreLogFlushInterval = 200;
        private int scoreLogRowsSinceFlush = 0;

        private void ApplyEmgRuntimeSettings()
        {
            delsysEMG.SignalView = emgSignalMode;
        }

        #region Private methods
        private string ConfigEMGFilePath()
        {
            if (!string.IsNullOrEmpty(emgSessionFilePath))
                return emgSessionFilePath;

            if (!string.IsNullOrWhiteSpace(emgPathOverride))
            {
                emgSessionFilePath = emgPathOverride;
                string overrideDir = Path.GetDirectoryName(emgSessionFilePath);
                if (!string.IsNullOrEmpty(overrideDir))
                    Directory.CreateDirectory(overrideDir);
                return emgSessionFilePath;
            }

            string emgDir = @"D:\yixianglin\Desktop\PHRI_Data"; // Default directory for EMG data.
            Directory.CreateDirectory(emgDir);
            string fileName = $"EmgLogs_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            emgSessionFilePath = Path.Combine(emgDir, fileName);
            return emgSessionFilePath;
        }

        private string ConfigScoreLogFilePath()
        {
            if (!string.IsNullOrEmpty(scoreLogSessionFilePath))
                return scoreLogSessionFilePath;

            if (!string.IsNullOrWhiteSpace(scoreLogPathOverride))
            {
                scoreLogSessionFilePath = scoreLogPathOverride;
                string overrideDir = Path.GetDirectoryName(scoreLogSessionFilePath);
                if (!string.IsNullOrEmpty(overrideDir))
                    Directory.CreateDirectory(overrideDir);
                return scoreLogSessionFilePath;
            }

            string logDir = @"D:\yixianglin\Desktop\PHRI_Data";
            Directory.CreateDirectory(logDir);
            string fileName = $"ExpLogs_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            scoreLogSessionFilePath = Path.Combine(logDir, fileName);
            return scoreLogSessionFilePath;
        }

        public bool IsEmgReady()
        {
            return delsysEnable && delsysEMG.IsConnected() && delsysEMG.IsRunning();
        }

        public bool IsScoreLogReady()
        {
            return ScoreManager.Instance != null;
        }

        public bool EmgRecordFlag => emgRecordFlag;
        public bool EmgIsRecording => emgIsRecording;
        public bool ScoreLogRecordFlag => scoreLogRecordFlag;
        public bool ScoreLogIsRecording => scoreLogIsRecording;
        public bool CanStartAnyRecording => (emgRecordFlag && IsEmgReady() && !emgIsRecording) || (scoreLogRecordFlag && IsScoreLogReady() && !scoreLogIsRecording);
        public bool AnyRecordingActive => emgIsRecording || scoreLogIsRecording;
        public int[] GetActiveEmgChannels() => delsysEMG.GetChannelsActiveSensor();

        private bool TryStartEmgRecording()
        {
            if (!emgRecordFlag || !IsEmgReady() || emgIsRecording) return false;

            string emgPath = ConfigEMGFilePath();
            delsysEMG.StartRecording(emgPath, Time.timeAsDouble);
            emgIsRecording = true;
            emgRecordCount++;
            return true;
        }

        private bool TryStopEmgRecording()
        {
            if (!emgIsRecording) return false;

            if (delsysEMG.IsConnected())
                delsysEMG.StopRecording();

            emgIsRecording = false;
            return true;
        }

        private bool TryStartScoreLogRecording()
        {
            if (!scoreLogRecordFlag || !IsScoreLogReady() || scoreLogIsRecording) return false;

            if (scoreLogWriter == null)
            {
                string logPath = ConfigScoreLogFilePath();
                bool append = File.Exists(logPath) && new FileInfo(logPath).Length > 0;
                scoreLogWriter = new StreamWriter(logPath, append, Encoding.ASCII);
                scoreLogRowsSinceFlush = 0;
                if (!append)
                {
                    scoreLogWriter.WriteLine("t,x_rover,x_leader,handle_fx,track_cost,force_cost,emg_cost,block_index,section_index");
                    scoreLogWriter.Flush();
                }
            }

            scoreLogIsRecording = true;
            return true;
        }

        private bool TryStopScoreLogRecording()
        {
            if (!scoreLogIsRecording) return false;

            try { scoreLogWriter?.Flush(); } catch { }
            scoreLogRowsSinceFlush = 0;
            scoreLogIsRecording = false;
            return true;
        }

        private void CloseScoreLogSession()
        {
            try { scoreLogWriter?.Flush(); } catch { }
            try { scoreLogWriter?.Dispose(); } catch { }
            scoreLogWriter = null;
            scoreLogRowsSinceFlush = 0;
            scoreLogIsRecording = false;
        }

        public bool StartAuxRecordingManual()
        {
            bool changed = false;
            if (TryStartEmgRecording()) changed = true;
            if (TryStartScoreLogRecording()) changed = true;
            return changed;
        }

        public bool StopAuxRecordingManual()
        {
            bool changed = false;
            if (TryStopEmgRecording()) changed = true;
            if (TryStopScoreLogRecording()) changed = true;
            return changed;
        }

        private void ShutdownEMG()
        {
            if (emgShutdown) return;
            emgShutdown = true;

            try { StopAuxRecordingManual(); } catch (Exception ex) { Debug.LogWarning($"[M2RoverBridge] EMG/score log stop during shutdown failed: {ex.Message}"); }
            try { CloseScoreLogSession(); } catch (Exception ex) { Debug.LogWarning($"[M2RoverBridge] score log close during shutdown failed: {ex.Message}"); }
            if (!delsysEnable) return;
            try { if (delsysEMG.IsRunning()) delsysEMG.StopAcquisition(); } catch (Exception ex) { Debug.LogWarning($"[M2RoverBridge] EMG stop acquisition during shutdown failed: {ex.Message}"); }
            try { if (delsysEMG.IsConnected()) delsysEMG.Close(); } catch (Exception ex) { Debug.LogWarning($"[M2RoverBridge] EMG close during shutdown failed: {ex.Message}"); }
        }

        private void UpdateEmgScorePreview()
        {
            double now = Time.unscaledTimeAsDouble;
            double interval = 1.0 / emgPreviewHz;
            if (now < nextEmgUpdateTime) return;
            nextEmgUpdateTime = now + interval;

            bool hasText = emgScoreText != null;

            bool active = delsysEnable && delsysEMG.IsConnected() && delsysEMG.IsRunning();
            if (!active)
            {
                string inactiveText = $"EMGSc: -- | t: {Time.timeAsDouble:0.0}s";
                if (hasText) emgScoreText.text = inactiveText;
                ScoreManager.Instance?.SetEmgPreview(0f, Time.timeAsDouble, inactiveText);
                return;
            }

            ApplyEmgRuntimeSettings();

            float[] emg = delsysEMG.GetScoreEnvelopeData();
            if (emg == null || emg.Length == 0)
            {
                string emptyText = $"EMGSc: -- | t: {Time.timeAsDouble:0.0}s";
                if (hasText) emgScoreText.text = emptyText;
                ScoreManager.Instance?.SetEmgPreview(0f, Time.timeAsDouble, emptyText);
                return;
            }

            // Compute a simple EMG score from the downsampled envelope signal across all channels.
            float absMean = 0f;
            for (int i = 0; i < emg.Length; i++)
                absMean += Mathf.Abs(emg[i]);
            absMean /= emg.Length;

            float emgScore = absMean * emgScoreScale;

            // emgScoreFiltered = Mathf.Lerp(emgScoreFiltered, emgScore, Mathf.Clamp01(emgScoreEmaLerp));
            emgScoreFiltered = emgScore;
            emgScoreTimestamp = Time.timeAsDouble;

            string previewText = BuildEmgPreviewText(emg);
            if (hasText) emgScoreText.text = previewText;
            ScoreManager.Instance?.SetEmgPreview(emgScoreFiltered, emgScoreTimestamp, previewText);
        }

        private string BuildEmgPreviewText(float[] raw)
        {
            StringBuilder sb = new StringBuilder(160);
            sb.Append("EMGSc: ").Append(emgScoreFiltered.ToString("0.0000"))
              .Append(" | t: ").Append(emgScoreTimestamp.ToString("0.0")).Append('s');

            int[] activeChannels = delsysEMG.GetChannelsActiveSensor();
            int previewCount = Mathf.Min(
                Mathf.Min(raw.Length, activeChannels.Length),
                Mathf.Clamp(emgPreviewMaxChannels, 1, 8));

            for (int i = 0; i < previewCount; i++)
            {
                sb.Append('\n')
                  .Append("S").Append(activeChannels[i].ToString("D2")) //D2: zero-padded 2-digit channel number
                  .Append(": ").Append(raw[i].ToString("0.0000000"));
            }

            return sb.ToString();
        }
        #endregion


        public void SetCalibForce(string value)
        {
            if (float.TryParse(value, out float parsed))
            {
                CalibForce = Mathf.Clamp(parsed, 1.0f, 100.0f);
                Debug.Log($"[M2RoverBridge] Participant CalibForce updated to: {CalibForce} N");
            }
        }

        public void SetStandbyK(string value)
        {
            if (float.TryParse(value, out float parsed))
            {
                StandbyK = Mathf.Clamp(parsed, 10f, 2000f);
                if (m2 != null && m2.IsInitialised() && m2.Client != null && m2.Client.IsConnected())
                {
                    m2.SendCmd("STBK", new double[] { StandbyK });
                }
                Debug.Log($"[M2RoverBridge] Standby K updated to: {StandbyK}");
            }
        }

        // ----------------------------------------------------------------------------------------------------------------------
        // ------ Unity lifecycle methods ------

        void Awake()
        {
            emgIsRecording = false;

            if (rover == null)
                rover = FindFirstObjectByType<RoverHandler>();
            if (leader == null)
                leader = FindFirstObjectByType<LeaderHandler>();
            if (rover != null && roverTransform == null)
                roverTransform = rover.transform;
            if (rover != null && roverRigidbody == null)
                roverRigidbody = rover.GetComponent<Rigidbody>();
            if (worldFollower == null)
                worldFollower = FindFirstObjectByType<M2WorldFollower>();

            if (roverTransform != null)
            {
                roverInitialPosition = roverTransform.position;
            }

            // Keep centerline consistent
            if (Mathf.Abs(unityCenterX) < 1e-6f) unityCenterX = worldXCenter;

            if (!GlobalRandomSeed.IsInitialized)
                UnityEngine.Random.InitState(GlobalRandomSeed.Seed);


            #region Initialize EMG sensors
            //Initialse Delsys EMG sensor
            if (delsysEnable)
            {
                if (emgFilter == null)
                    emgFilter = FindFirstObjectByType<EMGFilter>();

                delsysEMG.Init(emgFilter, emgScoreDownsampleHz);
                ApplyEmgRuntimeSettings();
                bool connected = delsysEMG.Connect();
                if (connected){
                    delsysEMG.StartAcquisition();
                    Debug.LogWarning("Delsys EMG initialized and acquisition started.");
                }
                else
                    Debug.LogWarning("[M2RoverBridge] EMG init failed: cannot connect to Delsys server.");
            }
            #endregion

        }

        private void WriteScoreLogRow()
        {
            if (!scoreLogIsRecording || scoreLogWriter == null || ScoreManager.Instance == null) return;

            float xRover = roverTransform != null ? roverTransform.position.x : 0f;
            float xLeader = leader != null ? leader.transform.position.x : 0f;
            int blockIndex = ExperimentBlockControl.Instance != null ? ExperimentBlockControl.Instance.CurrentBlockNumber : 0;
            int sectionIndex = ExperimentBlockControl.Instance != null ? ExperimentBlockControl.Instance.CurrentSectionNumber : 0;

            StringBuilder line = new StringBuilder(160);
            line.Append(Time.timeAsDouble.ToString("F6")).Append(',')
                .Append(xRover.ToString("F6")).Append(',')
                .Append(xLeader.ToString("F6")).Append(',')
                .Append(ScoreManager.Instance.HandleFx.ToString("F6")).Append(',')
                .Append(ScoreManager.Instance.WTrack.ToString("F6")).Append(',')
                .Append(ScoreManager.Instance.WForce.ToString("F6")).Append(',')
                .Append(ScoreManager.Instance.WEmg.ToString("F6")).Append(',')
                .Append(blockIndex).Append(',')
                .Append(sectionIndex);

            scoreLogWriter.WriteLine(line.ToString());
            scoreLogRowsSinceFlush++;

            // Flush the writer every scoreLogFlushInterval rows to ensure data is written to disk in a timely manner without flushing on every single write.
            if (scoreLogRowsSinceFlush >= scoreLogFlushInterval)
            {
                scoreLogWriter.Flush();
                scoreLogRowsSinceFlush = 0;
            }
        }

        // ----------------------------------------------------------------------------------------------------------------------
        // --- Public methods to be called by UI or other components ---

        // Switch between keyboard control and M2 control modes.
        // When switching from M2 to keyboard, also send SESS command to end M2 session and stop the current block.
        public void SetUnityMode(int mode)
        {
            UnityDriveMode targetMode = mode == 2 ? UnityDriveMode.Mode2_M2 : UnityDriveMode.Mode1_Keyboard;

            // If switching from M2 to keyboard mode, end the M2 session and block to ensure a clean transition.
            if (unityMode == UnityDriveMode.Mode2_M2 && targetMode == UnityDriveMode.Mode1_Keyboard)
            {
                if (m2 != null && m2.IsInitialised() && m2.Client != null && m2.Client.IsConnected())
                    m2.SendCmd("SESS");
                NotifyBlockEnd();
            }

            unityMode = targetMode;

            // Keyboard mode enabled, reset states to prepare for keyboard control.
            if (unityMode == UnityDriveMode.Mode1_Keyboard)
            {   
                blockActive = false;
                enableKeyboardInMode1 = true;
                isPaused = false;
                isDriving = false;
                worldFollower?.ResetBias();
                Debug.Log("Switched to Keyboard control mode.");
            }

            // M2 control mode enabled, reset states to prepare for M2 control.
            else
            {
                blockActive = false;
                enableKeyboardInMode1 = false;
                syncRefReady = false;
                worldFollower?.ResetBias();
                Debug.Log("Switched to M2 control mode.");
            }
        }

        // Apply the HRI and control modes received from M2. 
        public void ApplyM2Modes(int hriMode, int ctrlMode)
        {
            bool ctrlChanged = ctrlModeCode != ctrlMode;
            hriModeCode = hriMode;
            ctrlModeCode = ctrlMode;
            if (ctrlChanged) syncRefReady = false;
        }

        // This should be called when Unity receives the block-begin event from the M2 flow.
        public void NotifyBlockBegin()
        {
            if (unityMode == UnityDriveMode.Mode2_M2)
            {
                blockActive = true;
                syncRefReady = false; 
                if (!isPaused) isDriving = true;
                worldFollower?.ResetBias();

                StartAuxRecordingManual();

                if (ScoreManager.Instance != null) ScoreManager.Instance.SetScorePaused(false);
            }
        }

        // This should be called when the current block ends.
        public void NotifyBlockEnd()
        {
            StopAuxRecordingManual();

            blockActive = false;
            syncRefReady = false;
            if (unityMode == UnityDriveMode.Mode2_M2 && isDriving) isDriving = false;
            worldFollower?.ResetBias();
            hasSentDisturbanceState = false;
            lastDisturbanceState = false;
            if (ScoreManager.Instance != null) ScoreManager.Instance.SetScorePaused(true);
        }

        public void StopBlockMotionImmediate()
        {
            isDriving = false;
            ApplyRoverSteer(0f);
            rover?.Brake();
            leader?.Brake();
            if (ScoreManager.Instance != null) ScoreManager.Instance.SetScorePaused(true);
        }

        // Reset the rover to its initial pose, and clear velocities. This can be called on block start or when needed.
        public void ResetRoverToInitialPose()
        {
            if (roverTransform == null) return;

            roverTransform.position = roverInitialPosition;
            roverTransform.rotation = Quaternion.identity;

            if (roverRigidbody != null)
            {
                roverRigidbody.linearVelocity = Vector3.zero;
                roverRigidbody.angularVelocity = Vector3.zero;
            }

            syncRefReady = false;
        }

        // Reset Unity-side runtime state without reloading scene or disconnecting M2.
        public void SoftResetUnityStateKeepM2Connection()
        {
            blockActive = false;
            syncRefReady = false;
            hasSentDisturbanceState = false;
            lastDisturbanceState = false;
            isPaused = false;
            isDriving = false;

            ResetRoverToInitialPose();
            ApplyRoverSteer(0f);
            // SendFeedbackForce(0f, 0f);

            if (ScoreManager.Instance != null)
            {
                ScoreManager.Instance.ResetAll();
                ScoreManager.Instance.SetScorePaused(true);
            }

            ForceField.ResetFirstEntryFlag();

            if (GlobalRandomSeed.IsInitialized)
                UnityEngine.Random.InitState(GlobalRandomSeed.Seed);

            var road = FindFirstObjectByType<EndlessRoad>();
            if (road != null)
                road.ResetRoadAroundPlayer();

            var fields = FindObjectsByType<ForceField>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var f in fields)
            {
                if (f == null) continue;
                var go = f.gameObject;
                if (!go.activeSelf) continue;
                go.SetActive(false); 
                go.SetActive(true); 
            }
        }

        public void ResetForNextExperimentBlock()
        {
            blockActive = false;
            syncRefReady = false;
            hasSentDisturbanceState = false;
            lastDisturbanceState = false;
            isPaused = false;
            isDriving = false;
            emgRecordCount = 0;

            ResetRoverToInitialPose();
            ApplyRoverSteer(0f);
            worldFollower?.ResetBias();
            leader?.ResetForBlockStart();

            if (ScoreManager.Instance != null)
            {
                ScoreManager.Instance.ResetBlockScores();
                ScoreManager.Instance.SetScorePaused(true);
            }

            ForceField.ResetFirstEntryFlag();

            var road = FindFirstObjectByType<EndlessRoad>();
            if (road != null)
                road.ResetRoadAroundPlayer();

            var fields = FindObjectsByType<ForceField>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            // Reset all force fields, including inactive ones, to ensure they are in the correct state for the new block.
            foreach (var f in fields) 
            {
                if (f == null) continue;
                var go = f.gameObject;
                bool wasActive = go.activeSelf;
                go.SetActive(false);
                go.SetActive(wasActive);
            }
        }

        // =======================================================================================
        // --- Sync logic  ---

        // Safely attempt to read the M2 handle X position from the M2 state.
        private bool TryGetM2HandleX(out float handleX)
        {
            handleX = 0f;
            if (m2 == null || !m2.IsInitialised() || m2.Client == null || !m2.Client.IsConnected()) return false;
            if (m2.State == null || m2.State["X"] == null || m2.State["X"].Length < 1) return false;
            handleX = (float)m2.State["X"][0];
            if (float.IsNaN(handleX) || float.IsInfinity(handleX)) return false;
            return true;
        }

        // Re-synchronize rover position to current M2 mapping without recalibrating A reference.
        public bool SyncRoverToCurrentM2MappedX()
        {
            if (!TryGetM2HandleX(out float handleX)) return false; // Can't get handle X, can't sync

            EnsureSyncReference(handleX);
            SnapRoverToMappedX(handleX);
            return true;
        }

        // Ensure that the reference points for handle X and rover X are initialized, to allow computing relative movements and target positions.
        private void EnsureSyncReference(float handleX)
        {
            if (syncRefReady) return;
            // Set the reference to the current handle position
            handleXRef = handleX; 
            if (roverTransform != null)
                roverXRef = roverTransform.position.x;
            else
                roverXRef = worldXCenter;

            syncRefReady = true;
        }

        // Compute the target rover X position based on the handle X position, using the reference points and scaling factor.
        private float ComputeTargetXFromHandle(float handleX)
        {
            float handleXRel = handleX - handleXRef;
            return roverXRef + handleXRel * targetWorldXScale;
        }

        // Hard-sync rover X to mapped target, used on reconnect/mode switch to prevent large position mismatches that can cause jump forces..
        private void SnapRoverToMappedX(float handleX)
        {
            if (roverTransform == null) return;

            float targetX = ComputeTargetXFromHandle(handleX);
            Vector3 pos = roverTransform.position;
            pos.x = targetX;
            roverTransform.position = pos;

            if (roverRigidbody != null)
            {
                Vector3 v = roverRigidbody.linearVelocity;
                v.x = 0f;
                roverRigidbody.linearVelocity = v;
                roverRigidbody.angularVelocity = Vector3.zero;
            }
        }


        // =======================================================================================
        // --- POS control logic ---

        // Compute steering for POS mode with two paradigms toggled by usePosSyncCorrection.
        private float ComputeSteerPosMode(float handleX)
        {
            float handleXRel = handleX - handleXRef;
            if (Mathf.Abs(handleXRel) < handleXDeadZone) handleXRel = 0f;

            float steer;
            steer = handleXRel * handleXToSteer;
            return Mathf.Clamp(steer, -steerClamp, steerClamp);
        }


        // =======================================================================================
        // --- M2 feedback logic ---


    // ----------------------- For testing ----------------------------

    // Check feedback send timer and send feedback if it's time. This helps to limit the feedback send rate to M2.
        // private bool SendFeedbackTimer()
        // {
        //     float now = Time.time;
        //     if (now < nextFeedbackSendTime) return false;

        //     nextFeedbackSendTime = now + 1.0f / CmdSendRateHz;
        //     return true;
        // }

    // Send the computed feedback force to M2 using the FRC2 command (No needed currently, just keep it for later testing).
        // private void SendFeedbackForce(float fx, float fy, bool rateLimited = true)
        // {
        //     if (rateLimited && !SendFeedbackTimer()) return;
        //     if (!blockActive) return;
        //     if (m2 == null || !m2.IsInitialised() || m2.Client == null || !m2.Client.IsConnected()) return;
        //     m2.SendCmd("FRC2", new double[] { fx, fy }); // M2 set all to zeros, no need to send.
        // }

    // ----------------------------------------------------------------



        // Overload for sending zero force without parameters.
        private void TrySendDisturbanceStateToM2()
        {
            if (!blockActive) return;
            if (m2 == null || !m2.IsInitialised() || m2.Client == null || !m2.Client.IsConnected()) return;

            // Use the same Unity disturbance source u(t) for both local simulation and M2 signaling.
            bool state = ForceField.DisturbanceU > 0.5f;
            int direction = ExperimentBlockControl.Instance != null ? ExperimentBlockControl.Instance.CurrentDirection : -1;
            
            // Send dynamic disturbance magnitude (-X or +X N) calculated from participant's CalibForce
            double disturbanceMagnitude = (double)(CalibForce * 0.4f);
            disturbanceMagnitude = Mathf.Clamp((float)disturbanceMagnitude, 1f, 50.0f); // Clamp the disturbance magnitude to prevent excessively large forces.
            double disturbanceCmd = state ? (direction < 0 ? -disturbanceMagnitude : disturbanceMagnitude) : 0.0;

            if (!hasSentDisturbanceState || state != lastDisturbanceState)
            {
                m2.SendCmd("DSTR", new double[] { disturbanceCmd }); // Send disturbance command to M2.
                lastDisturbanceState = state;
                hasSentDisturbanceState = true;
            }
        }



        // =======================================================================================
        // --- VEL control logic ---

        // Apply runtime-override gains for open-loop damped VEL mode.
        private void ApplyForcedVelParamsIfEnabled()
        {
            if (!forceVelRuntimeParams) return;
            velOpenLoopKp = forcedVelOpenLoopKp;
            velOpenLoopKd = forcedVelOpenLoopKd;
            velSteerClamp = forcedVelSteerClamp;
        }

        // Compute the handle X offset relative to the reference, applying deadzone.
        private float ComputeVelHandleXRel(float handleX)
        {
            // Use block-start center when ready to avoid fixed-center mismatch.
            float velCenterX = syncRefReady ? handleXRef : m2CenterX;
            float handleXRel = handleX - velCenterX;
            if (Mathf.Abs(handleXRel) < velHandleXDeadZone) handleXRel = 0f;
            return handleXRel;
        }

        // VEL mode (open-loop damped): map handle offset to steer and apply direction-aware damping.
        private float ComputeVelSteerOpenLoopDamped(float handleXRel, out float currentVx)
        {
            currentVx = roverRigidbody != null ? roverRigidbody.linearVelocity.x : 0f;

            float steer = velOpenLoopKp * handleXRel;
            if (Mathf.Abs(steer) < 1e-4f) return 0f;
            return Mathf.Clamp(steer, -velSteerClamp, velSteerClamp);
        }

        // Debug helper to log VEL mode computations at a limited rate to avoid spamming the console.
        private void WarnVelRealtime(float handleXRel, float currentVx, float steer)
        {
            if (!velDebugWarning) return;

            float now = Time.time;
            float interval = velDebugHz > 0f ? (1f / velDebugHz) : 0f;
            if (now < nextVelDebugTime) return;
            nextVelDebugTime = now + interval;

            Debug.LogWarning(
                $"[M2 VEL #{GetInstanceID()}] handleXRel={handleXRel:F4}, currentVx={currentVx:F4}, steer={steer:F4}, kp={velOpenLoopKp:F3}, kd={velOpenLoopKd:F3}, clamp={velSteerClamp:F3}");
        }


        // =======================================================================================
        // --- Common control application logic ---
        private void ApplyRoverSteer(float steer)
        {
            rover.SetInputRaw(new Vector2(steer, 0f));
        }

        private void PosMode(float handleX)
        {
            float steer = ComputeSteerPosMode(handleX);
            ApplyRoverSteer(steer);
            // SendFeedbackForce(0f, 0f);
        }

        private void ApplyVelMode(float handleXRel)
        {
            float steer = ComputeVelSteerOpenLoopDamped(handleXRel, out float currentVx);
            WarnVelRealtime(handleXRel, currentVx, steer);
            ApplyRoverSteer(steer);
            // SendFeedbackForce(0f, 0f);
        }

        private void VelMode(float handleXRel)
        {
            ApplyVelMode(handleXRel);
        }

        private void HandleKeyboardModeFixedUpdate()
        {
            if (!enableKeyboardInMode1 || rover == null) return;
            Vector2 input = new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
            rover.SetInput(input);
        }


        // ----------------------------------------------------------------------------------------------------------------------
        // --- Unity lifecycle methods ---

        void Update()
        {
            // if (rover == null)
            //     rover = FindFirstObjectByType<RoverHandler>();
            // if (leader == null)
            //     leader = FindFirstObjectByType<LeaderHandler>();
            // if (rover != null && roverTransform == null)
            //     roverTransform = rover.transform;
            // if (rover != null && roverRigidbody == null)
            //     roverRigidbody = rover.GetComponent<Rigidbody>();

            bool connectedNow = m2 != null && m2.IsInitialised() && m2.Client != null && m2.Client.IsConnected();

            // Detect M2 reconnect to trigger auto-sync if enabled, to prevent large position mismatches that can cause jump forces.
            if (unityMode == UnityDriveMode.Mode2_M2 && connectedNow && !lastM2Connected && autoSnapOnM2Reconnect)
            {
                SyncRoverToCurrentM2MappedX();
                hasSentDisturbanceState = false;
            }
            if (!connectedNow)
            {
                hasSentDisturbanceState = false;
            }
            lastM2Connected = connectedNow;

            if (unityMode == UnityDriveMode.Mode2_M2 && blockActive)
                TrySendDisturbanceStateToM2();

            if (enableHotkeys)
            {
                
                bool ctrl = Input.GetKey(KeyCode.LeftControl);
                if (Input.GetKeyDown(KeyCode.T) && !ctrl)
                {
                    Debug.LogWarning("Hotkey T detected");
                    isPaused = false;
                    isDriving = true;
                    // if (ScoreManager.Instance != null) ScoreManager.Instance.SetScorePaused(!blockActive);
                }
                if (Input.GetKeyDown(KeyCode.T) && ctrl)
                {
                    isPaused = true;
                    isDriving = false;
                    // if (ScoreManager.Instance != null) ScoreManager.Instance.SetScorePaused(true);
                }

                if (Input.GetKeyDown(KeyCode.R) && ctrl )
                {   
                    SceneManager.LoadScene(SceneManager.GetActiveScene().name);
                }
            }


            rover.SetPreserveLateralVelocity(unityMode == UnityDriveMode.Mode2_M2);
            rover.SetPaused(isPaused);
            rover.SetDriving(isDriving && !isPaused);

            leader.SetPreserveLateralVelocity(unityMode == UnityDriveMode.Mode2_M2);
            leader.SetPaused(isPaused);
            leader.SetDriving(isDriving && !isPaused);

            if (ScoreManager.Instance != null)
                ScoreManager.Instance.SetScorePaused(isPaused || (unityMode == UnityDriveMode.Mode2_M2 && !blockActive));

            UpdateEmgScorePreview();
            WriteScoreLogRow();

        }    


        void FixedUpdate()
        {
            if (rover == null) return; // Safety check

            if (unityMode == UnityDriveMode.Mode1_Keyboard) // Keyboard control mode
            {
                HandleKeyboardModeFixedUpdate();
                return;
            }

            // Can't get handle X, can't proceed with M2 control
            else if (!TryGetM2HandleX(out float handleX) && blockActive)
            {
                // SendFeedbackForce(0f, 0f);
                Debug.LogWarning("[M2RoverBridge] Unable to read M2 handle X position. Check M2 connection and state.");
                return;
            }

            // M2 control mode but block not active, ensure rover is not moving and send zero feedback.
            else if (!blockActive)
            {
                ApplyRoverSteer(0f);
                return;
            }

            // M2 control mode
            else{ 
                EnsureSyncReference(handleX);

                int ctrl = ctrlModeCode;
                int hri = hriModeCode;

                if (ctrl == 1) // POS mode
                {
                    PosMode(handleX);
                    return;
                }

                if (ctrl == 2)// VEL mode
                {
                    ApplyForcedVelParamsIfEnabled(); 
                    float handleXRel = ComputeVelHandleXRel(handleX);
                    VelMode(handleXRel);
                    return;
                }
            }
        }

        // Ensure EMG resources are properly released when the object is destroyed or application quits.
        void OnDestroy() 
        {
            ShutdownEMG();
        }

        // Also ensure EMG shutdown on application quit to cover cases where the object might not be destroyed properly.
        private void OnApplicationQuit() 
        {
            ShutdownEMG();
        }

    }
}
