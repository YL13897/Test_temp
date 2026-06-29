using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Serialization;
using TMPro;

namespace CORC.Demo
{
    public class InteractionEstimator : MonoBehaviour
    {
        public enum ForceSource
        {
            EmgRegression = 0,
            ForceSensor = 1
        }

        public enum SpiSource
        {
            WeightedCoContraction = 0,
            PairCci = 1
        }

        [Header("Refs")]
        [SerializeField] private M2RoverBridge bridge;
        [SerializeField] private TMP_Text forceCmpText;

        [Header("Mode")]
        [SerializeField] private ForceSource forceSource = ForceSource.ForceSensor;
        [SerializeField] private SpiSource spiSource = SpiSource.WeightedCoContraction;

        [Header("EMG Regression")]
        [SerializeField] private float[] weights = Array.Empty<float>();
        [SerializeField] private float bias = 0f;
        [SerializeField] private float spiRest = 0f;
        [SerializeField] private float spiRef = 1f;

        [Header("SPI Smoothing")]
        [SerializeField] private bool useSpiTriFilter = true;
        [SerializeField] private float spiTriWindowSec = 0.1f;
        [FormerlySerializedAs("spiWindowSec")]
        [SerializeField] private float spiTauSec = 0.1f;

        [Header("Debug CSV")]
        [SerializeField] private bool logDebugCsv = false;
        [SerializeField] private float debugCsvHz = 20f;
        [SerializeField] private string debugCsvDir = @"D:\yixianglin\Desktop\PHRI_Data";

        public float ForceProxy { get; private set; }
        public float Spi { get; private set; }
        public float StiffnessCmd { get; private set; }
        public float EmgScore { get; private set; }
        public bool HasValidForceProxy { get; private set; }
        public bool HasValidSpi { get; private set; }

        private float lastForceProxy = 0f;
        private float lastSpi = 0f;
        private float lastK = 0f;
        private bool hasLastForceProxy = false;
        private bool hasLastSpi = false;
        private bool hasMethodACalibration = false;
        private bool loggedDefaultWeights = false;
        private bool loggedMissingWeightedGroups = false;
        private float[] spiTriBuf = Array.Empty<float>();
        private int spiTriIndex = 0;
        private int spiTriCount = 0;
        private float spiSmooth = 0f;
        private double lastSpiT = 0.0;
        private bool hasSpiSmooth = false;
        private long lastEmgSeq = 0;
        private ForceSource lastForceSource;
        private SpiSource lastSpiSource;
        private readonly float[] emg = new float[6];
        private readonly int[] map = new int[6];
        private readonly float[] norm = new float[6];
        private StreamWriter debugWriter;
        private float nextDebugT = 0f;
        private float lastP = 0f;
        private float lastN = 0f;
        private float lastOldSpi = 0f;
        private float lastEmgForceRaw = 0f;

        void Awake()
        {
            if (bridge == null)
                bridge = FindFirstObjectByType<M2RoverBridge>();
            lastForceSource = forceSource;
            lastSpiSource = spiSource;
            EnsureWeights();
        }

        public void Refresh()
        {
            try
            {
                if (bridge == null)
                {
                    SetForceFallback();
                    SetSpiFallback();
                    return;
                }

                bool hasEmg = bridge.TryGetScoreEmg(emg, map, out double emgT, out long emgSeq);
                EnsureWeights();
                if (forceSource != lastForceSource || spiSource != lastSpiSource)
                {
                    ClearSpi();
                    lastForceSource = forceSource;
                    lastSpiSource = spiSource;
                }

                RefreshForce(hasEmg);
                RefreshSpi(hasEmg, emgT, emgSeq);
            }
            finally
            {
                UpdateForceCmp();
                WriteDebugCsv();
            }
        }

        public void ApplyCalibration(float[] w, float b, float sRest = 0f, float sRef = 1f)
        {
            EnsureWeights();
            int[] channels = bridge != null ? bridge.GetConfiguredEmgChannels() : null;
            int count = 0;
            if (channels != null)
            {
                for (int i = 0; i < channels.Length; i++)
                {
                    if (channels[i] > 0)
                        count++;
                }
            }

            if (w != null && w.Length == weights.Length)
            {
                for (int i = 0; i < weights.Length; i++)
                    weights[i] = w[i];
                hasMethodACalibration = true;
                loggedDefaultWeights = false;
                ClearSpi();
            }
            else if (w != null && w.Length == count)
            {
                int j = 0;
                for (int i = 0; i < weights.Length; i++)
                {
                    if (channels != null && i < channels.Length && channels[i] > 0)
                        weights[i] = w[j++];
                    else
                        weights[i] = 0f;
                }
                hasMethodACalibration = true;
                loggedDefaultWeights = false;
                ClearSpi();
            }

            bias = b;
            if (sRef > sRest)
            {
                spiRest = sRest;
                spiRef = sRef;
            }
            else
            {
                spiRest = b;
                spiRef = b + 1f;
            }
        }

        private void RefreshForce(bool hasEmg)
        {
            if (forceSource == ForceSource.ForceSensor)
            {
                loggedDefaultWeights = false;
                if (bridge.TryGetInteractionForceX(out float fx))
                {
                    ForceProxy = fx;
                    HasValidForceProxy = true;
                    lastForceProxy = fx;
                    hasLastForceProxy = true;
                    return;
                }

                SetForceFallback();
                return;
            }

            if (!hasEmg)
            {
                lastEmgForceRaw = 0f;
                SetForceFallback();
                return;
            }

            LogDefaultWeights(map);
            float pos = 0f;
            float neg = 0f;
            bool hasPos = false;
            bool hasNeg = false;

            for (int i = 0; i < map.Length; i++)
            {
                if (!TryGetNorm(emg, map, i, out norm[i])) continue;
                if (i >= weights.Length) continue;

                float wi = weights[i];
                if (float.IsNaN(wi) || float.IsInfinity(wi)) continue;
                float aw = Mathf.Abs(wi);
                if (aw <= 0f) continue;

                if (wi > 0f)
                {
                    pos += aw * norm[i];
                    hasPos = true;
                }
                else if (wi < 0f)
                {
                    neg += aw * norm[i];
                    hasNeg = true;
                }
            }

            lastEmgForceRaw = pos - neg;
            ForceProxy = bias + lastEmgForceRaw;
            if (hasPos || hasNeg)
            {
                HasValidForceProxy = true;
                lastForceProxy = ForceProxy;
                hasLastForceProxy = true;
                return;
            }

            SetForceFallback();
        }

        private void RefreshSpi(bool hasEmg, double emgT, long emgSeq)
        {
            if (!hasEmg)
            {
                lastEmgSeq = 0;
                lastP = 0f;
                lastN = 0f;
                lastOldSpi = 0f;
                lastEmgForceRaw = 0f;
                Array.Clear(norm, 0, norm.Length);
                SetSpiFallback();
                return;
            }
            if (emgSeq == lastEmgSeq)
                return;

            for (int i = 0; i < norm.Length; i++)
                norm[i] = 0f;
            for (int i = 0; i < map.Length && i < norm.Length; i++)
                TryGetNorm(emg, map, i, out norm[i]);

            float pos = 0f;
            float neg = 0f;
            float oldSpi = 0f;
            bool hasPos = false;
            bool hasNeg = false;

            for (int i = 0; i < map.Length; i++)
            {
                if (!TryGetNorm(emg, map, i, out float u)) continue;
                if (i >= weights.Length) continue;

                float wi = weights[i];
                if (float.IsNaN(wi) || float.IsInfinity(wi)) continue;
                float aw = Mathf.Abs(wi);
                if (aw <= 0f) continue;

                oldSpi += aw * u;
                if (wi > 0f)
                {
                    pos += aw * u;
                    hasPos = true;
                }
                else if (wi < 0f)
                {
                    neg += aw * u;
                    hasNeg = true;
                }
            }

            lastP = pos;
            lastN = neg;
            lastOldSpi = oldSpi;
            lastEmgForceRaw = pos - neg;

            if (spiSource == SpiSource.PairCci)
            {
                loggedDefaultWeights = false;
                float spi = 0f;
                int count = 0;
                AddPair(ref spi, ref count, emg, map, 0, 1);
                AddPair(ref spi, ref count, emg, map, 2, 3);
                AddPair(ref spi, ref count, emg, map, 4, 5);

                if (count > 0)
                {
                    ApplySpi(spi / count, emgT, 0.5f);
                    lastEmgSeq = emgSeq;
                    return;
                }

                SetSpiFallback();
                lastEmgSeq = emgSeq;
                return;
            }

            LogDefaultWeights(map);
            if (!hasPos || !hasNeg)
            {
                if (!loggedMissingWeightedGroups)
                {
                    Debug.LogWarning("[InteractionEstimator] Weighted co-contraction needs valid positive and negative muscle groups.");
                    loggedMissingWeightedGroups = true;
                }
                SetSpiFallback();
                lastEmgSeq = emgSeq;
                return;
            }

            loggedMissingWeightedGroups = false;
            float spiCo = pos + neg - Mathf.Abs(pos - neg);
            ApplySpi(spiCo, emgT, 1f);
            lastEmgSeq = emgSeq;
        }

        private void ApplySpi(float rawSpi, double emgT, float normScale)
        {
            Spi = SmoothSpi(rawSpi, emgT);
            float spi01 = Mathf.Clamp01((Spi - spiRest) / Mathf.Max(spiRef - spiRest, 1e-6f));
            if (normScale != 1f)
                spi01 = Mathf.Clamp01(spi01 * normScale);

            StiffnessCmd = Mathf.Lerp(bridge.StiffnessMin, bridge.StiffnessMax, spi01);
            EmgScore = 100f * (1f - spi01);
            HasValidSpi = true;
            lastSpi = Spi;
            lastK = StiffnessCmd;
            hasLastSpi = true;
        }

        private void AddPair(ref float spi, ref int count, float[] emg, int[] map, int a, int b)
        {
            if (a >= map.Length || b >= map.Length) return;
            int chA = map[a];
            int chB = map[b];
            if (chA <= 0 || chB <= 0) return;
            if (!TryGetNorm(emg, map, a, out float ua)) return;
            if (!TryGetNorm(emg, map, b, out float ub)) return;

            float high = Mathf.Max(ua, ub);
            if (high < 1e-4f) return;

            float low = Mathf.Min(ua, ub);
            spi += (low / high) * (low + high);
            count++;
        }

        private bool TryGetNorm(float[] emg, int[] map, int slot, out float u)
        {
            u = 0f;
            if (emg == null || map == null || slot < 0 || slot >= map.Length || map[slot] <= 0)
                return false;
            if (slot >= emg.Length)
                return false;
            if (bridge.EmgRest == null || bridge.EmgRef == null)
                return false;
            if (slot >= bridge.EmgRest.Length || slot >= bridge.EmgRef.Length)
                return false;

            float rest = bridge.EmgRest[slot];
            float reference = bridge.EmgRef[slot];
            float range = reference - rest;
            if (Mathf.Abs(range) < 1e-6f)
                return false;

            u = Mathf.Clamp01((emg[slot] - rest) / range);
            return !(float.IsNaN(u) || float.IsInfinity(u));
        }

        private void SetForceFallback()
        {
            ForceProxy = hasLastForceProxy ? lastForceProxy : 0f;
            HasValidForceProxy = false;
        }

        private void SetSpiFallback()
        {
            Spi = hasLastSpi ? lastSpi : 0f;
            StiffnessCmd = hasLastSpi ? lastK : 0f;
            if (hasLastSpi && bridge != null && bridge.StiffnessMax > bridge.StiffnessMin)
            {
                float k01 = (lastK - bridge.StiffnessMin) / (bridge.StiffnessMax - bridge.StiffnessMin);
                EmgScore = 100f * (1f - k01);
            }
            else
            {
                EmgScore = 0f;
            }
            HasValidSpi = false;
        }

        private float SmoothSpi(float value, double t)
        {
            if (!hasSpiSmooth || t <= lastSpiT)
            {
                ClearTri();
                spiSmooth = TriSpi(value, 0.01f);
                lastSpiT = t;
                hasSpiSmooth = true;
                return spiSmooth;
            }

            float dt = (float)(t - lastSpiT);
            value = TriSpi(value, dt);
            float alpha = 1f - Mathf.Exp(-dt / Mathf.Max(spiTauSec, 1e-6f));
            spiSmooth += alpha * (value - spiSmooth);
            lastSpiT = t;
            return spiSmooth;
        }

        private float TriSpi(float value, float dt)
        {
            if (!useSpiTriFilter)
                return value;

            int size = Mathf.Max(1, Mathf.RoundToInt(spiTriWindowSec / Mathf.Max(dt, 1e-6f)));
            if (spiTriBuf.Length != size)
            {
                spiTriBuf = new float[size];
                spiTriIndex = 0;
                spiTriCount = 0;
            }

            spiTriBuf[spiTriIndex] = value;
            spiTriIndex = (spiTriIndex + 1) % size;
            if (spiTriCount < size)
                spiTriCount++;

            float sum = 0f;
            float weightSum = 0f;
            for (int i = 0; i < spiTriCount; i++)
            {
                int idx = (spiTriIndex - spiTriCount + i + size) % size;
                float weight = i + 1f;
                sum += spiTriBuf[idx] * weight;
                weightSum += weight;
            }
            return sum / Mathf.Max(weightSum, 1e-6f);
        }

        private void ClearTri()
        {
            spiTriIndex = 0;
            spiTriCount = 0;
        }

        private void ClearSpi()
        {
            ClearTri();
            spiSmooth = 0f;
            lastSpiT = 0.0;
            hasSpiSmooth = false;
            lastEmgSeq = 0;
        }

        private void EnsureWeights()
        {
            if (weights != null && weights.Length == 6)
                return;

            float[] next = new float[6];
            int[] currentMap = bridge != null ? bridge.GetConfiguredEmgChannels() : null;
            for (int i = 0; i < next.Length; i++)
                next[i] = currentMap != null && i < currentMap.Length && currentMap[i] > 0 ? 1f : 0f;
            weights = next;
        }

        private void LogDefaultWeights(int[] currentMap)
        {
            if (hasMethodACalibration || loggedDefaultWeights || currentMap == null)
                return;

            string[] names = { "cc11", "cc12", "cc21", "cc22", "cc31", "cc32" };
            StringBuilder sb = new StringBuilder("Using default weights=1 for slots: ");
            bool first = true;
            for (int i = 0; i < currentMap.Length && i < names.Length; i++)
            {
                if (currentMap[i] <= 0) continue;
                if (!first) sb.Append(", ");
                sb.Append(names[i]);
                first = false;
            }
            Debug.LogWarning($"[InteractionEstimator] {sb}");
            loggedDefaultWeights = true;
        }

        private void UpdateForceCmp()
        {
            if (forceCmpText == null)
                return;

            string emgText = forceSource == ForceSource.EmgRegression && HasValidForceProxy ? $"{ForceProxy:F1} N" : "-";
            string sensorText = bridge != null && bridge.TryGetInteractionForceX(out float fx) ? $"{fx:F1} N" : "-";
            forceCmpText.text = $"ForceCmp: EMG {emgText} | Sensor {sensorText}";
        }

        private void WriteDebugCsv()
        {
            if (!logDebugCsv || bridge == null)
            {
                CloseDebugCsv();
                return;
            }
            if (!bridge.IsTrialLoggingActive)
                return;

            float interval = debugCsvHz > 0f ? 1f / debugCsvHz : 0f;
            if (interval > 0f && Time.time < nextDebugT)
                return;
            nextDebugT = Time.time + interval;

            if (debugWriter == null)
            {
                Directory.CreateDirectory(debugCsvDir);
                string path = Path.Combine(debugCsvDir, $"InteractionEstimatorDebug_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                debugWriter = new StreamWriter(path, false, Encoding.ASCII);
                debugWriter.WriteLine("unity_time,u1,u2,u3,u4,u5,u6,p,n,spi_old,spi,force_emg_proxy,force_sensor_x,k,emg_score");
            }

            float sensorFx = 0f;
            bool hasSensor = bridge != null && bridge.TryGetInteractionForceX(out sensorFx);
            string proxyText = hasMethodACalibration ? (bias + lastEmgForceRaw).ToString("F6", CultureInfo.InvariantCulture) : "";
            string sensorText = hasSensor ? sensorFx.ToString("F6", CultureInfo.InvariantCulture) : "";
            debugWriter.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0:F4},{1:F6},{2:F6},{3:F6},{4:F6},{5:F6},{6:F6},{7:F6},{8:F6},{9:F6},{10:F6},{11},{12},{13:F6},{14:F6}",
                Time.time,
                norm[0], norm[1], norm[2], norm[3], norm[4], norm[5],
                lastP, lastN, lastOldSpi, Spi, proxyText, sensorText,
                StiffnessCmd, EmgScore));
        }

        private void CloseDebugCsv()
        {
            if (debugWriter == null)
                return;

            try { debugWriter.Flush(); } catch { }
            try { debugWriter.Dispose(); } catch { }
            debugWriter = null;
        }

        void OnDestroy()
        {
            CloseDebugCsv();
        }

        void OnApplicationQuit()
        {
            CloseDebugCsv();
        }
    }
}
