using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace WinIsland.App.Core
{
    public class SystemTrayService : IDisposable
    {
        private const int NIM_ADD = 0x00000000;
        private const int NIM_DELETE = 0x00000002;
        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;

        private const int WM_APP = 0x8000;
        public const int WM_TRAYCALLBACK = WM_APP + 101;

        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        private static readonly IntPtr IDI_APPLICATION = new IntPtr(32512);

        private IntPtr _hwnd;
        private HwndSource? _hwndSource;
        private bool _isAdded;
        private ContextMenu? _contextMenu;

        public event Action? DoubleClicked;
        public event Action? MenuOpening;

        public void Initialize(Window hostWindow, ContextMenu contextMenu, string tooltip = "WinIsland - Dynamic Island PC")
        {
            _contextMenu = contextMenu;
            _hwnd = new WindowInteropHelper(hostWindow).EnsureHandle();

            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);

            var nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                hWnd = _hwnd,
                uID = 1001,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYCALLBACK,
                hIcon = LoadIcon(IntPtr.Zero, IDI_APPLICATION),
                szTip = tooltip
            };

            _isAdded = Shell_NotifyIcon(NIM_ADD, ref nid);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAYCALLBACK)
            {
                int mouseMsg = (int)lParam;
                if (mouseMsg == WM_LBUTTONDBLCLK)
                {
                    DoubleClicked?.Invoke();
                    handled = true;
                }
                else if (mouseMsg == WM_RBUTTONUP && _contextMenu != null)
                {
                    MenuOpening?.Invoke();
                    _contextMenu.IsOpen = true;
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_isAdded && _hwnd != IntPtr.Zero)
            {
                var nid = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
                    hWnd = _hwnd,
                    uID = 1001
                };
                Shell_NotifyIcon(NIM_DELETE, ref nid);
                _isAdded = false;
            }

            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }
}
