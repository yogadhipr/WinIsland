using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinIsland.App.Core
{
    public static class Win32Interop
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_LAYERED = 0x00080000;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const int WM_NCHITTEST = 0x0084;
        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;

        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_DPICHANGED = 0x02E0;

        public const int VK_RBUTTON = 0x02;
        public const int VK_CONTROL = 0x11;
        public const byte VK_MEDIA_NEXT_TRACK = 0xB0;
        public const byte VK_MEDIA_PREV_TRACK = 0xB1;
        public const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public const uint MONITOR_DEFAULTTONULL = 0;
        public const uint MONITOR_DEFAULTTOPRIMARY = 1;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint MONITORINFOF_PRIMARY = 1;

        public enum QUERY_USER_NOTIFICATION_STATE
        {
            QUNS_NOT_PRESENT = 1,
            QUNS_BUSY = 2,
            QUNS_RUNNING_D3D_FULL_SCREEN = 3,
            QUNS_PRESENTATION_MODE = 4,
            QUNS_ACCEPTS_NOTIFICATIONS = 5,
            QUNS_QUIET_TIME = 6,
            QUNS_APP = 7
        }

        public enum MonitorDpiType
        {
            MDT_EFFECTIVE_DPI = 0,
            MDT_ANGULAR_DPI = 1,
            MDT_RAW_DPI = 2,
            MDT_DEFAULT = MDT_EFFECTIVE_DPI
        }

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetDesktopWindow();

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        public class MonitorBounds
        {
            public IntPtr HMonitor;
            public int Left;
            public int Top;
            public int Width;
            public int Height;
            public double DpiScaleX = 1.0;
            public double DpiScaleY = 1.0;
            public bool IsPrimary;
        }

        public static MonitorBounds GetMonitorBounds(IntPtr hMon)
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            GetMonitorInfo(hMon, ref mi);

            uint dpiX = 96, dpiY = 96;
            try
            {
                GetDpiForMonitor(hMon, MonitorDpiType.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
            }
            catch
            {
                dpiX = 96;
                dpiY = 96;
            }

            return new MonitorBounds
            {
                HMonitor = hMon,
                Left = mi.rcMonitor.Left,
                Top = mi.rcMonitor.Top,
                Width = mi.rcMonitor.Right - mi.rcMonitor.Left,
                Height = mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                DpiScaleX = dpiX / 96.0,
                DpiScaleY = dpiY / 96.0,
                IsPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0
            };
        }

        public static IntPtr GetWindowLong(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return GetWindowLongPtr64(hWnd, nIndex);
            return new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        public static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtr64(hWnd, nIndex, dwNewLong);
            return new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        /// <summary>
        /// Configures the window to be a borderless topmost overlay without Alt-Tab presence and without stealing focus.
        /// </summary>
        public static void MakeOverlayWindow(Window window)
        {
            var helper = new WindowInteropHelper(window);
            IntPtr hwnd = helper.EnsureHandle();

            long style = GetWindowLong(hwnd, GWL_EXSTYLE).ToInt64();
            style |= WS_EX_TOOLWINDOW;  // Remove from Alt+Tab
            style |= WS_EX_NOACTIVATE;  // Do not steal keyboard focus on click

            SetWindowLong(hwnd, GWL_EXSTYLE, new IntPtr(style));

            // Ensure window stays topmost
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        /// <summary>
        /// Temporarily enables or disables keyboard activation on the overlay window (for inline TextBox editing).
        /// </summary>
        public static void SetAllowActivate(Window window, bool allowActivate)
        {
            var helper = new WindowInteropHelper(window);
            IntPtr hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) return;

            long style = GetWindowLong(hwnd, GWL_EXSTYLE).ToInt64();
            if (allowActivate)
            {
                style &= ~WS_EX_NOACTIVATE;
                SetWindowLong(hwnd, GWL_EXSTYLE, new IntPtr(style));
                SetForegroundWindow(hwnd);
                window.Activate();
            }
            else
            {
                style |= WS_EX_NOACTIVATE;
                SetWindowLong(hwnd, GWL_EXSTYLE, new IntPtr(style));
            }
        }

        public static void SendMediaKey(byte vk)
        {
            try
            {
                keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
                keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }

        /// <summary>
        /// Detects if an application is running fullscreen (e.g. game, full screen video, or presentation).
        /// </summary>
        public static bool IsFullScreenAppRunning()
        {
            // 1. Shell Query Notification State
            if (SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state) == 0)
            {
                if (state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN ||
                    state == QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY ||
                    state == QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
                {
                    return true;
                }
            }

            // 2. Foreground window bounds check
            IntPtr fgHwnd = GetForegroundWindow();
            if (fgHwnd != IntPtr.Zero && fgHwnd != GetDesktopWindow() && fgHwnd != GetShellWindow())
            {
                if (GetWindowRect(fgHwnd, out RECT rect))
                {
                    IntPtr hMon = MonitorFromWindow(fgHwnd, MONITOR_DEFAULTTONEAREST);
                    if (hMon != IntPtr.Zero)
                    {
                        var bounds = GetMonitorBounds(hMon);
                        if (rect.Left <= bounds.Left && rect.Top <= bounds.Top &&
                            rect.Right >= (bounds.Left + bounds.Width) &&
                            rect.Bottom >= (bounds.Top + bounds.Height))
                        {
                            return true;
                        }
                    }
                    else
                    {
                        int screenW = (int)SystemParameters.PrimaryScreenWidth;
                        int screenH = (int)SystemParameters.PrimaryScreenHeight;
                        if (rect.Left <= 0 && rect.Top <= 0 && rect.Right >= screenW && rect.Bottom >= screenH)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
