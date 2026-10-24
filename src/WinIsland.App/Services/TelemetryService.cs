using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace WinIsland.App.Services
{
    public class TelemetryData
    {
        public float CpuUsagePercent { get; set; }
        public float RamUsagePercent { get; set; }
        public double AppMemoryMb { get; set; }
        public int BatteryPercent { get; set; }
        public bool IsCharging { get; set; }
        public bool HasBattery { get; set; }
    }

    public class TelemetryService : IDisposable
    {
        private static readonly Lazy<TelemetryService> _instance = new(() => new TelemetryService());
        public static TelemetryService Instance => _instance.Value;

        private Timer? _pollTimer;
        private long _prevIdleTime;
        private long _prevKernelTime;
        private long _prevUserTime;

        public event Action<TelemetryData>? TelemetryUpdated;
        public TelemetryData Current { get; private set; } = new TelemetryData();

        public TelemetryService()
        {
            Start(2000);
        }

        public void Start(int intervalMs = 2000)
        {
            _pollTimer?.Dispose();
            // Initialize CPU baseline
            GetCpuTimes(out _prevIdleTime, out _prevKernelTime, out _prevUserTime);

            _pollTimer = new Timer(OnPoll, null, 100, intervalMs);
        }

        public void Stop()
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        private void OnPoll(object? state)
        {
            try
            {
                float cpu = CalculateCpuUsage();
                var (ramPercent, appMemMb) = GetRamMetrics();
                var (batPercent, isCharging, hasBat) = GetPowerMetrics();

                Current = new TelemetryData
                {
                    CpuUsagePercent = cpu,
                    RamUsagePercent = ramPercent,
                    AppMemoryMb = appMemMb,
                    BatteryPercent = batPercent,
                    IsCharging = isCharging,
                    HasBattery = hasBat
                };

                Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    TelemetryUpdated?.Invoke(Current);
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TelemetryService] Poll error: {ex.Message}");
            }
        }

        private float CalculateCpuUsage()
        {
            if (!GetCpuTimes(out long idleTime, out long kernelTime, out long userTime))
                return 0f;

            long usr = userTime - _prevUserTime;
            long ker = kernelTime - _prevKernelTime;
            long idl = idleTime - _prevIdleTime;

            _prevUserTime = userTime;
            _prevKernelTime = kernelTime;
            _prevIdleTime = idleTime;

            long sys = ker + usr;
            if (sys <= 0) return 0f;

            float cpuPercent = (float)((sys - idl) * 100.0 / sys);
            return Math.Clamp(cpuPercent, 0f, 100f);
        }

        private static (float ramPercent, double appMemMb) GetRamMetrics()
        {
            float ramPercent = 0f;
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                ramPercent = memStatus.dwMemoryLoad;
            }

            double appMemMb = 0.0;
            using (var proc = Process.GetCurrentProcess())
            {
                appMemMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            }

            return (ramPercent, appMemMb);
        }

        private static (int batteryPercent, bool isCharging, bool hasBattery) GetPowerMetrics()
        {
            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
            {
                bool hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryLifePercent <= 100;
                bool isCharging = status.ACLineStatus == 1;
                int percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 100;

                return (percent, isCharging, hasBattery);
            }
            return (100, true, false);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public long ToLong() => ((long)dwHighDateTime << 32) | dwLowDateTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        private static bool GetCpuTimes(out long idle, out long kernel, out long user)
        {
            if (GetSystemTimes(out FILETIME fIdle, out FILETIME fKernel, out FILETIME fUser))
            {
                idle = fIdle.ToLong();
                kernel = fKernel.ToLong();
                user = fUser.ToLong();
                return true;
            }
            idle = kernel = user = 0;
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte Reserved1;
            public uint BatteryLifeTime;
            public uint BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        public void Dispose()
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
    }
}
