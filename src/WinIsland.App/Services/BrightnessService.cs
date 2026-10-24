using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WinIsland.App.Services
{
    public class BrightnessService
    {
        private static readonly Lazy<BrightnessService> _instance = new(() => new BrightnessService());
        public static BrightnessService Instance => _instance.Value;

        public event Action<int>? BrightnessChanged;
        public int CurrentBrightness { get; private set; } = 100;

        private int _targetBrightness = 100;
        private bool _isApplying;
        private readonly object _lock = new();

        public BrightnessService()
        {
            CurrentBrightness = ReadCurrentBrightness();
            _targetBrightness = CurrentBrightness;
        }

        public int ReadCurrentBrightness()
        {
            // 1. Try WMI (Laptops, tablets, internal panels - like Lenovo Yoga / Surface)
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (obj["CurrentBrightness"] != null)
                    {
                        int cur = Convert.ToInt32(obj["CurrentBrightness"]);
                        CurrentBrightness = Math.Clamp(cur, 0, 100);
                        return CurrentBrightness;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BrightnessService] WMI Read error: {ex.Message}");
            }

            // 2. Try DDC/CI (External desktop monitors)
            try
            {
                IntPtr hDesktop = GetDesktopWindow();
                IntPtr hMonitor = MonitorFromWindow(hDesktop, 2 /* MONITOR_DEFAULTTONEAREST */);
                if (hMonitor != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) && count > 0)
                {
                    var monitors = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                    {
                        if (GetMonitorBrightness(monitors[0].hPhysicalMonitor, out _, out uint cur, out _))
                        {
                            CurrentBrightness = Math.Clamp((int)cur, 0, 100);
                        }
                        DestroyPhysicalMonitors(count, monitors);
                        return CurrentBrightness;
                    }
                }
            }
            catch { }

            return CurrentBrightness;
        }

        public void SetBrightness(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            CurrentBrightness = percent;
            BrightnessChanged?.Invoke(percent);

            lock (_lock)
            {
                _targetBrightness = percent;
                if (!_isApplying)
                {
                    _isApplying = true;
                    Task.Run(ApplyLoop);
                }
            }
        }

        private async Task ApplyLoop()
        {
            while (true)
            {
                int target;
                lock (_lock)
                {
                    target = _targetBrightness;
                }

                ApplyHardwareBrightness(target);

                await Task.Delay(40);

                lock (_lock)
                {
                    if (_targetBrightness == target)
                    {
                        _isApplying = false;
                        break;
                    }
                }
            }
        }

        private void ApplyHardwareBrightness(int percent)
        {
            bool success = false;

            // 1. Try WMI (Laptops, tablets, internal displays)
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                {
                    obj.InvokeMethod("WmiSetBrightness", new object[] { 1, percent });
                    success = true;
                    break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BrightnessService] WMI Set error: {ex.Message}");
            }

            // 2. Try DDC/CI (External monitors)
            try
            {
                IntPtr hDesktop = GetDesktopWindow();
                IntPtr hMonitor = MonitorFromWindow(hDesktop, 2);
                if (hMonitor != IntPtr.Zero && GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) && count > 0)
                {
                    var monitors = new PHYSICAL_MONITOR[count];
                    if (GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors))
                    {
                        foreach (var mon in monitors)
                        {
                            SetMonitorBrightness(mon.hPhysicalMonitor, (uint)percent);
                        }
                        DestroyPhysicalMonitors(count, monitors);
                        success = true;
                    }
                }
            }
            catch { }

            // 3. Fallback: GDI Gamma Ramp if hardware brightness is not supported
            if (!success)
            {
                SetGammaRampBrightness(percent);
            }
        }

        #region GDI Gamma Ramp Fallback
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDC(string lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool SetDeviceGammaRamp(IntPtr hdc, ref RAMP lpRamp);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct RAMP
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Blue;
        }

        private void SetGammaRampBrightness(int percent)
        {
            try
            {
                IntPtr hdc = CreateDC("DISPLAY", null, null, IntPtr.Zero);
                if (hdc != IntPtr.Zero)
                {
                    var ramp = new RAMP
                    {
                        Red = new ushort[256],
                        Green = new ushort[256],
                        Blue = new ushort[256]
                    };

                    double factor = Math.Clamp(percent / 100.0, 0.05, 1.0);
                    for (int i = 0; i < 256; i++)
                    {
                        int value = (int)(i * 256 * factor);
                        ushort val = (ushort)Math.Clamp(value, 0, 65535);
                        ramp.Red[i] = val;
                        ramp.Green[i] = val;
                        ramp.Blue[i] = val;
                    }

                    SetDeviceGammaRamp(hdc, ref ramp);
                    DeleteDC(hdc);
                }
            }
            catch { }
        }
        #endregion

        #region DDC/CI P/Invoke
        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }
        #endregion
    }
}
