using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using TMPro;

namespace CORC.Demo
{
    public class InteractionEstimator : MonoBehaviour
    {
        public enum Mode
        {
            EmgOnly = 0,
            ForceSensorCci = 1
        }

        [Header("Refs")]
        [SerializeField] private M2RoverBridge bridge;
        [SerializeField] private TMP_Text forceCmpText;

        [Header("Mode")]
        [SerializeField] private Mode mode = Mode.ForceSensorCci;

        [Header("Method A")]
        [SerializeField] private float[] weights = Array.Empty<float>();
        [SerializeField] private float bias = 0f;
        [SerializeField] private float spiRest = 0f;
        [SerializeField] private float spiRef = 1f;

        [Header("SPI Smoothing")]
        [SerializeField] private float spiWindowSec = 0.1f;

        [Header("Debug CSV")]
        [SerializeField] private bool logDebugCsv = false;
        [SerializeField] private float debugCsvHz = 50f;
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
        private readonly Queue<SpiSample> spiSamples = new Queue<SpiSample>();
        private float spiSum = 0f;
        private long lastEmgSeq = 0;
        private Mode lastMode;
        private readonly float[] emg = new float[6];
        private readonly int[] map = new int[6];
        private StreamWriter debugWriter;
        private float nextDebugT = 0f;

        private struct SpiSample
        {
            public double t;
            public float v;
        }

        void Awake()
        {
            if (bridge == null)
                bridge = FindFirstObjectByType<M2RoverBridge>();
            lastMode = mode;
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
                if (mode != lastMode)
                {
                    ClearSpi();
                    lastMode = mode;
                }

                if (mode == Mode.EmgOnly)
                {
                    if (!hasEmg)
                    {
                        lastEmgSeq = 0;
                        SetForceFallback();
                        SetSpiFallback();
                        return;
                    }
                    if (emgSeq == lastEmgSeq)
                        return;

                    LogDefaultWeights(map);
                    RefreshMethodA(emg, map, emgT, emgSeq);
                }
                else
                {
                    loggedDefaultWeights = false;
                    RefreshMethodB(emg, map, hasEmg, emgT, emgSeq);
                }
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
                    if (channels[i] > 0)
                        count++;
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

        private void RefreshMethodA(float[] emg, int[] map, double emgT, long emgSeq)
        {
            int count = 0;
            float force = bias;
            float spi = 0f;

            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] <= 0 || i >= weights.Length) continue;
                if (!TryGetNorm(emg, map, i, out float u)) continue;
                force += weights[i] * u;
                spi += Mathf.Abs(weights[i]) * u;
                count++;
            }

            if (count > 0)
            {
                ForceProxy = force;
                Spi = SmoothSpi(spi + bias, emgT);
                float spi01 = Mathf.Clamp01((Spi - spiRest) / Mathf.Max(spiRef - spiRest, 1e-6f));
                StiffnessCmd = Mathf.Lerp(bridge.StiffnessMin, bridge.StiffnessMax, spi01);
                EmgScore = 100f * (1f - spi01);
                HasValidForceProxy = true;
                HasValidSpi = true;
                lastForceProxy = ForceProxy;
                lastSpi = Spi;
                lastK = StiffnessCmd;
                hasLastForceProxy = true;
                hasLastSpi = true;
                lastEmgSeq = emgSeq;
                return;
            }

            SetForceFallback();
            SetSpiFallback();
            lastEmgSeq = emgSeq;
        }

        private void RefreshMethodB(float[] emg, int[] map, bool hasEmg, double emgT, long emgSeq)
        {
            if (bridge.TryGetInteractionForceX(out float fx))
            {
                ForceProxy = fx;
                HasValidForceProxy = true;
                lastForceProxy = fx;
                hasLastForceProxy = true;
            }
            else
            {
                SetForceFallback();
            }

            if (!hasEmg)
            {
                lastEmgSeq = 0;
                SetSpiFallback();
                return;
            }
            if (emgSeq == lastEmgSeq)
                return;

            float spi = 0f;
            int count = 0;
            AddPair(ref spi, ref count, emg, map, 0, 1);
            AddPair(ref spi, ref count, emg, map, 2, 3);
            AddPair(ref spi, ref count, emg, map, 4, 5);

            if (count > 0)
            {
                Spi = SmoothSpi(spi / count, emgT);
                float spi01 = 0.5f * Spi;
                StiffnessCmd = Mathf.Lerp(bridge.StiffnessMin, bridge.StiffnessMax, spi01);
                EmgScore = 100f * (1f - spi01);
                HasValidSpi = true;
                lastSpi = Spi;
                lastK = StiffnessCmd;
                hasLastSpi = true;
                lastEmgSeq = emgSeq;
                return;
            }

            SetSpiFallback();
            lastEmgSeq = emgSeq;
        }

        // AddPair() computes a contribution to the SPI score from a pair of EMG channels (a,b) that are expected to be antagonistic.
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
            return true;
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
            if (spiSamples.Count > 0 && t < spiSamples.Peek().t)
                ClearSpi();

            double window = spiWindowSec;
            spiSamples.Enqueue(new SpiSample { t = t, v = value });
            spiSum += value;
            while (spiSamples.Count > 0 && t - spiSamples.Peek().t > window)
                spiSum -= spiSamples.Dequeue().v;

            return spiSum / spiSamples.Count;
        }

        private void ClearSpi()
        {
            spiSamples.Clear();
            spiSum = 0f;
            lastEmgSeq = 0;
        }

        // EnsureWeights() checks if the weights array is properly initialized for the 6 EMG slots. 
        // If not, it creates a new array and sets weights to 1 for any slot that has a configured EMG channel, and 0 for others.
        private void EnsureWeights()
        {
            if (weights != null && weights.Length == 6)
                return;

            float[] next = new float[6];
            int[] map = bridge != null ? bridge.GetConfiguredEmgChannels() : null;
            for (int i = 0; i < next.Length; i++)
                next[i] = map != null && i < map.Length && map[i] > 0 ? 1f : 0f;
            weights = next;
        }

        private void LogDefaultWeights(int[] map)
        {
            if (hasMethodACalibration || loggedDefaultWeights || map == null)
                return;

            string[] names = { "cc11", "cc12", "cc21", "cc22", "cc31", "cc32" };
            System.Text.StringBuilder sb = new System.Text.StringBuilder("Method A using default weights=1 for slots: ");
            bool first = true;
            for (int i = 0; i < map.Length && i < names.Length; i++)
            {
                if (map[i] <= 0) continue;
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

            string emgText = mode == Mode.EmgOnly && HasValidForceProxy ? $"{ForceProxy:F1} N" : "-";
            string sensorText = bridge != null && bridge.TryGetInteractionForceX(out float fx) ? $"{fx:F1} N" : "-";
            forceCmpText.text = $"ForceCmp: EMG {emgText} | Sensor {sensorText}";
        }

        private void WriteDebugCsv()
        {
            if (!logDebugCsv || bridge == null || !bridge.IsTrialLoggingActive)
            {
                CloseDebugCsv();
                return;
            }

            float interval = debugCsvHz > 0f ? 1f / debugCsvHz : 0f;
            if (interval > 0f && Time.time < nextDebugT)
                return;
            nextDebugT = Time.time + interval;

            if (debugWriter == null)
            {
                Directory.CreateDirectory(debugCsvDir);
                string path = Path.Combine(debugCsvDir, $"InteractionEstimatorDebug_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                debugWriter = new StreamWriter(path, false, Encoding.ASCII);
                debugWriter.WriteLine("unity_time,mode,k,force_proxy,force_sensor_x,has_force_proxy,has_sensor_force,spi,emg_score");
            }

            float sensorFx = 0f;
            bool hasSensor = bridge != null && bridge.TryGetInteractionForceX(out sensorFx);
            string proxyText = HasValidForceProxy ? ForceProxy.ToString("F6", CultureInfo.InvariantCulture) : "";
            string sensorText = hasSensor ? sensorFx.ToString("F6", CultureInfo.InvariantCulture) : "";
            debugWriter.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0:F4},{1},{2:F6},{3},{4},{5},{6},{7:F6},{8:F6}",
                Time.time, mode, StiffnessCmd, proxyText, sensorText, HasValidForceProxy, hasSensor, Spi, EmgScore));
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
