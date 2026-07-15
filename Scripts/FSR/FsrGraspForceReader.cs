using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace CORC.Demo
{
    public sealed class FsrGraspForceReader : MonoBehaviour
    {
        [Header("Serial Input")]
        [SerializeField] private string portName = "COM8";
        [SerializeField] private int baudRate = 9600;
        [SerializeField] private float staleAfterSeconds = 0.5f;

        [Header("Calibration")]
        [SerializeField] private float calibrationA = 1.1204106f;
        [SerializeField] private float calibrationB = 28.447392f;
        [SerializeField] private float vcc = 5f;
        [SerializeField] private float fixedResistorOhms = 33000f;

        
        private string status = "Disabled";
        private int receivedPackets = 0;
        private int rejectedPackets = 0;

        private const int ReadSleepMs = 100; // ms
        private const int ReconnectMs = 5000; // ms
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        private readonly object dataLock = new object();
        private Thread readThread;
        private IntPtr serialHandle = InvalidHandle;
        private volatile bool running;
        private volatile bool retryEnabled = true;
        private bool hasSample;
        private float voltage;
        private float forceN;
        private DateTime lastSampleUtc;

        public string Status
        {
            get
            {
                lock (dataLock)
                    return status;
            }
        }

        public void SetReaderEnabled(bool enabled)
        {
            if (enabled)
                StartReader();
            else
                StopReader();
        }

        public void SetRetryEnabled(bool enabled)
        {
            retryEnabled = enabled;
        }

        public void StartReader()
        {
            if (running || string.IsNullOrWhiteSpace(portName))
                return;

            running = true;
            SetStatus($"Opening {portName}");
            readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "FSR Grasp Force Reader"
            };
            readThread.Start();
        }

        public void StopReader()
        {
            running = false;
            CloseSerial();

            if (readThread != null && readThread.IsAlive)
                readThread.Join(200);

            readThread = null;
            ClearSample();
            SetStatus("Disabled");
        }

        public bool TryGetForce(out float force)
        {
            force = 0f;
            lock (dataLock)
            {
                if (!IsFreshSample())
                    return false;

                force = forceN;
                return true;
            }
        }

        public bool TryGetSample(out float fsrVoltage, out float fsrForceN)
        {
            fsrVoltage = 0f;
            fsrForceN = 0f;

            lock (dataLock)
            {
                if (!IsFreshSample())
                    return false;

                fsrVoltage = voltage;
                fsrForceN = forceN;
                return true;
            }
        }

        private bool IsFreshSample()
        {
            return hasSample && (DateTime.UtcNow - lastSampleUtc).TotalSeconds <= staleAfterSeconds;
        }

        private void ReadLoop()
        {
            byte[] buffer = new byte[128];
            StringBuilder line = new StringBuilder(128);

            while (running)
            {
                if (!retryEnabled)
                {
                    Thread.Sleep(ReadSleepMs);
                    continue;
                }

                IntPtr handle = OpenSerial();
                if (handle == InvalidHandle)
                {
                    ClearSample();
                    SetStatus($"Open {portName} failed");
                    Thread.Sleep(ReconnectMs);
                    continue;
                }

                serialHandle = handle;
                SetStatus($"Listening {portName}");

                while (running)
                {
                    if (!ReadFile(handle, buffer, (uint)buffer.Length, out uint bytesRead, IntPtr.Zero))
                    {
                        ClearSample();
                        SetStatus($"Read {portName} failed");
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        Thread.Sleep(ReadSleepMs);
                        continue;
                    }

                    for (int i = 0; i < bytesRead; i++)
                        AppendByte(line, buffer[i]);
                }

                CloseSerial();
                while (running && !retryEnabled)
                    Thread.Sleep(ReadSleepMs);
                if (running)
                    Thread.Sleep(ReconnectMs);
            }
        }

        private IntPtr OpenSerial()
        {
            IntPtr handle = CreateFile(
                @"\\.\" + portName.Trim(),
                GenericReadWrite,
                0,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (handle == InvalidHandle || handle == IntPtr.Zero)
                return InvalidHandle;

            Dcb dcb = new Dcb();
            dcb.DCBlength = (uint)Marshal.SizeOf(typeof(Dcb));
            if (!GetCommState(handle, ref dcb))
            {
                CloseHandle(handle);
                return InvalidHandle;
            }

            dcb.BaudRate = (uint)Math.Max(1, baudRate);
            dcb.ByteSize = 8;
            dcb.Parity = 0;
            dcb.StopBits = 0;
            dcb.Flags = 1;

            if (!SetCommState(handle, ref dcb))
            {
                CloseHandle(handle);
                return InvalidHandle;
            }

            CommTimeouts timeouts = new CommTimeouts
            {
                ReadIntervalTimeout = 20,
                ReadTotalTimeoutMultiplier = 0,
                ReadTotalTimeoutConstant = 20,
                WriteTotalTimeoutMultiplier = 0,
                WriteTotalTimeoutConstant = 0
            };
            SetCommTimeouts(handle, ref timeouts);
            PurgeComm(handle, PurgeRxClear);
            return handle;
        }

        private void CloseSerial()
        {
            IntPtr handle = serialHandle;
            serialHandle = InvalidHandle;
            if (handle != InvalidHandle)
                CloseHandle(handle);
        }

        private void AppendByte(StringBuilder line, byte value)
        {
            char c = (char)value;
            if (c == '\n' || c == '\r')
            {
                if (line.Length > 0)
                {
                    ProcessLine(line.ToString());
                    line.Length = 0;
                }
                return;
            }

            if (line.Length < 256)
                line.Append(c);
            else
                line.Length = 0;
        }

        private void ProcessLine(string line)
        {
            if (TryParseRaw(line, out float fsrRaw))
            {
                lock (dataLock)
                    receivedPackets++;
                UpdateSample(fsrRaw);
                return;
            }

            lock (dataLock)
            {
                rejectedPackets++;
                status = $"Rejected #{rejectedPackets}: {line.Trim()}";
            }
        }

        private bool TryParseRaw(string line, out float fsrRaw)
        {
            fsrRaw = 0f;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string text = line.Trim();
            int comma = text.IndexOf(',');
            string firstValue = comma >= 0 ? text.Substring(0, comma) : text;
            return float.TryParse(firstValue, NumberStyles.Float, CultureInfo.InvariantCulture, out fsrRaw);
        }

        private void UpdateSample(float fsrRaw)
        {
            float fsrVoltage = fsrRaw * vcc / 1023f;
            float fsrConductance = VoltageToConductance(fsrVoltage);
            float fsrForce = calibrationA > 1e-6f
                ? Math.Max(0f, (fsrConductance - calibrationB) / calibrationA)
                : 0f;

            lock (dataLock)
            {
                voltage = fsrVoltage;
                forceN = fsrForce;
                lastSampleUtc = DateTime.UtcNow;
                hasSample = true;
                status = $"OK packets={receivedPackets} V={fsrVoltage:0.000} F={fsrForce:0.00}N";
            }
        }

        private void ClearSample()
        {
            lock (dataLock)
                hasSample = false;
        }

        private void SetStatus(string value)
        {
            lock (dataLock)
                status = value;
        }

        private float VoltageToConductance(float fsrVoltage)
        {
            if (fsrVoltage <= 0.01f || fsrVoltage >= vcc - 0.01f)
                return 0f;

            float resistance = fixedResistorOhms * (vcc - fsrVoltage) / fsrVoltage;
            return resistance > 1e-6f ? 1000000f / resistance : 0f;
        }

        private void OnDisable()
        {
            StopReader();
        }

        private void OnDestroy()
        {
            StopReader();
        }

        private void OnApplicationQuit()
        {
            StopReader();
        }

        private const uint GenericReadWrite = 0xC0000000;
        private const uint OpenExisting = 3;
        private const uint PurgeRxClear = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        private struct Dcb
        {
            public uint DCBlength;
            public uint BaudRate;
            public uint Flags;
            public ushort wReserved;
            public ushort XonLim;
            public ushort XoffLim;
            public byte ByteSize;
            public byte Parity;
            public byte StopBits;
            public byte XonChar;
            public byte XoffChar;
            public byte ErrorChar;
            public byte EofChar;
            public byte EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CommTimeouts
        {
            public uint ReadIntervalTimeout;
            public uint ReadTotalTimeoutMultiplier;
            public uint ReadTotalTimeoutConstant;
            public uint WriteTotalTimeoutMultiplier;
            public uint WriteTotalTimeoutConstant;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr file, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetCommState(IntPtr file, ref Dcb dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetCommState(IntPtr file, ref Dcb dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetCommTimeouts(IntPtr file, ref CommTimeouts timeouts);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool PurgeComm(IntPtr file, uint flags);
    }
}
