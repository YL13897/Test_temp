using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CORC.Demo
{
    public class CalibrationManager : MonoBehaviour
    {
        public enum ExternalProgramType
        {
            Python = 0,
            Executable = 1
        }

        [Serializable]
        private class CalibrationSample
        {
            public bool keyboardMode;
            public bool hasM2;
            public double m2Time;
            public double[] m2Position = Array.Empty<double>();
            public double[] m2Force = Array.Empty<double>();
            public float[] emg = Array.Empty<float>();
            public int[] emgChannels = Array.Empty<int>();
            public int[] emgSlots = Array.Empty<int>();
            public float calibForce;
            public float standbyK;
            public long unityTicksUtc;
        }

        [Serializable]
        private class CalibrationResponse
        {
            public float calibForce;
            public float standbyK;
            public float emgScale;
            public float threshold;
            public float[] emgRest = Array.Empty<float>();
            public float[] emgBracing = Array.Empty<float>();
            public float[] emgRef = Array.Empty<float>();
            public float[] emgForceWeights = Array.Empty<float>();
            public float emgForceBias;
            public float spiRest;
            public float spiRef;
            public string note;
        }

        [Header("Refs")]
        public M2RoverBridge bridge;
        public InteractionEstimator estimator;

        [Header("External Launch")]
        public bool autoLaunchExternalProgram = true;
        public ExternalProgramType externalProgramType = ExternalProgramType.Python;
        public string pythonExecutable = "C:\\Users\\yixianglin\\AppData\\Local\\Programs\\Python\\Python313\\python.exe";
        public string externalProgramPath = "D:\\Unity Projects\\Rover\\Assets\\Test\\Calibration\\calibration_client.py";
        public string externalProgramArgs = ""; // Additional command-line arguments for the external program (optional)

        [Header("Local TCP")]
        public string listenAddress = "127.0.0.1";
        public int listenPort = 25001;
        public float sampleSendHz = 40f;

        [Header("Last Returned Params")]
        public float lastReturnedEmgScale;
        public float lastReturnedThreshold;
        [TextArea]
        public string lastResponseNote = "";

        public bool IsListenerRunning => listener != null;
        public bool IsClientConnected => client != null && client.Connected;
        public bool IsCalibrationActive => IsClientConnected || (externalProcess != null && !externalProcess.HasExited);
        public string LastStatus { get; private set; } = "Idle";

        private TcpListener listener;
        private Thread listenerThread;
        private TcpClient client;
        private StreamWriter clientWriter;
        private StreamReader clientReader;
        private readonly ConcurrentQueue<string> inboundLines = new ConcurrentQueue<string>();
        private readonly object clientLock = new object();
        private Process externalProcess;
        private float nextSendTime;
        private float sampleSendInterval = 0.025f;

        public bool CanStartCalibration()
        {
            ClearExitedExternalProcess();
            return !IsCalibrationActive;
        }

        public bool StartCalibrationSession(bool keyboardMode)
        {
            if (!CanStartCalibration())
                return false;

            StopTcpRuntime();
            StartListener();

            if (autoLaunchExternalProgram && !string.IsNullOrWhiteSpace(externalProgramPath))
            {
                if (!StartExternalProcess())
                    return false;
            }

            LastStatus = keyboardMode
                ? "Calibration started in keyboard debug mode."
                : "Calibration started. Waiting for external client...";
            nextSendTime = 0f;
            return true;
        }

        public void StopCalibrationSession()
        {
            StopExternalProcess();
            StopTcpRuntime();
            LastStatus = "Calibration stopped.";
        }

        private void Awake()
        {
            if (bridge == null)
                bridge = FindFirstObjectByType<M2RoverBridge>();
            if (estimator == null)
                estimator = FindFirstObjectByType<InteractionEstimator>();
            sampleSendInterval = sampleSendHz > 0f ? 1f / sampleSendHz : 0.025f;
        }

        private void Update()
        {
            DrainInboundMessages();

            if (!IsClientConnected)
                return;

            if (Time.unscaledTime < nextSendTime)
                return;

            nextSendTime = Time.unscaledTime + sampleSendInterval;
            SendSnapshot();
        }

        private void OnDestroy()
        {
            StopCalibrationSession();
        }

        private void OnApplicationQuit()
        {
            StopCalibrationSession();
        }

        private void StartListener()
        {
            if (listener != null)
                return;

            IPAddress ip = IPAddress.Parse(listenAddress);
            listener = new TcpListener(ip, listenPort);
            listener.Start();

            listenerThread = new Thread(ListenLoop) { IsBackground = true };
            listenerThread.Start();
        }

        private void ListenLoop()
        {
            try
            {
                LastStatus = $"Listening on {listenAddress}:{listenPort}";
                while (listener != null)
                {
                    TcpClient accepted = listener.AcceptTcpClient();
                    accepted.NoDelay = true;
                    ConfigureClient(accepted);
                    LastStatus = "External client connected.";
                }
            }
            catch (SocketException ex)
            {
                if (listener != null)
                {
                    LastStatus = $"Calibration listener error: {ex.Message}";
                    Debug.LogWarning($"[CalibrationManager] Listener socket error: {ex.Message}");
                }
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                listenerThread = null;
            }
        }

        private void ConfigureClient(TcpClient accepted)
        {
            lock (clientLock)
            {
                CloseClientLocked();
                client = accepted;
                NetworkStream stream = client.GetStream();
                clientWriter = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                clientReader = new StreamReader(stream, Encoding.UTF8);
                var receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
                receiveThread.Start();
            }
        }

        private void ReceiveLoop()
        {
            try
            {
                while (true)
                {
                    StreamReader reader;
                    lock (clientLock)
                    {
                        reader = clientReader;
                    }

                    if (reader == null)
                        break;

                    string line = reader.ReadLine();

                    if (line == null)
                        break;

                    if (!string.IsNullOrWhiteSpace(line))
                        inboundLines.Enqueue(line);
                }
            }
            catch (IOException ex)
            {
                Debug.LogWarning($"[CalibrationManager] Receive loop closed: {ex.Message}");
            }
            finally
            {
                lock (clientLock)
                {
                    CloseClientLocked();
                }
                LastStatus = "External client disconnected.";
            }
        }

        private void DrainInboundMessages()
        {
            while (inboundLines.TryDequeue(out string line))
            {
                CalibrationResponse response = null;
                try
                {
                    response = JsonUtility.FromJson<CalibrationResponse>(line);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CalibrationManager] Failed to parse response: {ex.Message}");
                }

                if (response == null)
                    continue;

                ApplyResponse(response);
            }
        }

        // Applies the received calibration response to the M2RoverBridge and updates the last returned parameters and status message accordingly.
        private void ApplyResponse(CalibrationResponse response)
        {
            if (bridge != null)
            {
                if (response.calibForce > 0f)
                {
                    bridge.CalibForce = response.calibForce;
                    FindFirstObjectByType<M2UiPanel>()?.RefreshCalibForceInput();
                }
                if (response.standbyK > 0f)
                    bridge.SetStandbyK(response.standbyK.ToString());
                bridge.SetCalibrationEmg(response.emgRest, response.emgBracing, response.emgRef);
            }

            estimator?.ApplyCalibration(response.emgForceWeights, response.emgForceBias, response.spiRest, response.spiRef);

            lastReturnedEmgScale = response.emgScale;
            lastReturnedThreshold = response.threshold;
            lastResponseNote = response.note ?? string.Empty;
            LastStatus = string.IsNullOrWhiteSpace(response.note)
                ? "Calibration parameters received."
                : response.note;
        }


        // SendSnapshot() is used to stream current Unity data to the external calibration GUI over TCP.
        // It builds a snapshot of the current calibration state, including M2 data if available, and sends it to the connected client as a JSON string.
        private void SendSnapshot()
        {
            if (bridge == null)
                return;

            var sample = new CalibrationSample
            {
                keyboardMode = bridge.unityMode == M2RoverBridge.UnityDriveMode.Mode1_Keyboard,
                calibForce = bridge.CalibForce,
                standbyK = bridge.StandbyK,
                unityTicksUtc = DateTime.UtcNow.Ticks
            };

            sample.hasM2 = bridge.TryGetM2Sample(out double m2Time, out double[] position, out double[] force);
            sample.m2Time = m2Time;
            sample.m2Position = sample.hasM2 ? position : Array.Empty<double>();
            sample.m2Force = sample.hasM2 ? force : Array.Empty<double>();

            float[] emg = bridge.GetScoreEnvelopeData();
            sample.emg = emg;
            sample.emgChannels = emg.Length > 0 ? bridge.GetActiveEmgChannels() : Array.Empty<int>();
            sample.emgSlots = bridge.GetConfiguredEmgChannels();

            string line = JsonUtility.ToJson(sample);

            lock (clientLock)
            {
                if (clientWriter == null)
                    return;

                try
                {
                    clientWriter.WriteLine(line);
                }
                catch (IOException ex)
                {
                    Debug.LogWarning($"[CalibrationManager] Send failed: {ex.Message}");
                    CloseClientLocked();
                    LastStatus = "External client disconnected during send.";
                }
            }
        }

        private bool StartExternalProcess()
        {
            ClearExitedExternalProcess();

            if (externalProcess != null && !externalProcess.HasExited)
                return true;

            try
            {
                string fileName = BuildProcessFileName();
                string args = BuildProcessArgs();
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false, // Set to true if you want to use the system shell to start the process (e.g., for .exe files)
                    CreateNoWindow = false, // Set to true if you don't want a console window to appear (useful for Python scripts)
                    WorkingDirectory = ResolveWorkingDirectory()
                };
                externalProcess = Process.Start(startInfo);
                return externalProcess != null;
            }
            catch (Exception ex)
            {
                LastStatus = $"External launch failed: {ex.Message}";
                Debug.LogWarning($"[CalibrationManager] External launch failed: {ex.Message}");
                return false;
            }
        }

        private void ClearExitedExternalProcess()
        {
            if (externalProcess == null || !externalProcess.HasExited)
                return;

            externalProcess.Dispose();
            externalProcess = null;
        }

        private string BuildProcessFileName()
        {
            return externalProgramType == ExternalProgramType.Python
                ? pythonExecutable
                : externalProgramPath;
        }

        // Builds the command-line arguments for launching the external program, including the script path (for Python) and the TCP connection parameters. Additional user-defined arguments are appended if provided.
        private string BuildProcessArgs()
        {
            string baseArgs;
            if (externalProgramType == ExternalProgramType.Python)
            {
                string quotedScript = string.IsNullOrWhiteSpace(externalProgramPath) ? string.Empty : $"\"{externalProgramPath}\"";
                baseArgs = $"{quotedScript} --host {listenAddress} --port {listenPort}";
            }
            else
            {
                baseArgs = $"--host {listenAddress} --port {listenPort}";
            }

            if (string.IsNullOrWhiteSpace(externalProgramArgs))
                return baseArgs.Trim();
            return $"{baseArgs} {externalProgramArgs}".Trim();
        }

        private string ResolveWorkingDirectory()
        {
            if (!string.IsNullOrWhiteSpace(externalProgramPath))
            {
                string dir = Path.GetDirectoryName(externalProgramPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir;
            }
            return Application.dataPath;
        }

        private void StopExternalProcess()
        {
            if (externalProcess == null)
                return;

            try
            {
                if (!externalProcess.HasExited)
                    externalProcess.Kill();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CalibrationManager] External stop failed: {ex.Message}");
            }
            finally
            {
                externalProcess.Dispose();
                externalProcess = null;
            }
        }

        private void StopTcpRuntime()
        {
            lock (clientLock)
            {
                CloseClientLocked();
            }

            try { listener?.Stop(); } catch { }
            listener = null;

            if (listenerThread != null && listenerThread.IsAlive)
            {
                try { listenerThread.Join(200); } catch { }
            }
            listenerThread = null;
        }

        private void CloseClientLocked()
        {
            try { clientWriter?.Dispose(); } catch { }
            try { clientReader?.Dispose(); } catch { }
            try { client?.Close(); } catch { }
            clientWriter = null;
            clientReader = null;
            client = null;
        }
    }
}
