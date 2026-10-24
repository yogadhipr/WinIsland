using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WinIsland.App.Services
{
    public class ClipboardItem
    {
        public string TextPreview { get; set; } = string.Empty;
        public string FullText { get; set; } = string.Empty;
        public BitmapSource? ImagePreview { get; set; }
        public bool IsImage { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class ClipboardService : IDisposable
    {
        private static readonly Lazy<ClipboardService> _instance = new(() => new ClipboardService());
        public static ClipboardService Instance => _instance.Value;

        public const int WM_CLIPBOARDUPDATE = 0x031D;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        public ObservableCollection<ClipboardItem> History { get; } = new ObservableCollection<ClipboardItem>();
        public event Action? HistoryChanged;

        private IntPtr _hookedHwnd = IntPtr.Zero;
        private HwndSource? _hwndSource;
        private string? _lastSeenText;

        public void Hook(Window window)
        {
            var helper = new WindowInteropHelper(window);
            _hookedHwnd = helper.EnsureHandle();

            _hwndSource = HwndSource.FromHwnd(_hookedHwnd);
            _hwndSource?.AddHook(WndProc);

            AddClipboardFormatListener(_hookedHwnd);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                OnClipboardUpdate();
            }
            return IntPtr.Zero;
        }

        public void OnClipboardUpdate()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (string.IsNullOrWhiteSpace(text) || text == _lastSeenText) return;

                    _lastSeenText = text;
                    string preview = text.Length > 80 ? text.Substring(0, 80).Replace("\r\n", " ").Replace("\n", " ") + "..." : text.Replace("\r\n", " ").Replace("\n", " ");

                    var item = new ClipboardItem
                    {
                        FullText = text,
                        TextPreview = preview,
                        IsImage = false,
                        Timestamp = DateTime.Now
                    };

                    InsertItem(item);
                }
                else if (Clipboard.ContainsImage())
                {
                    var img = Clipboard.GetImage();
                    if (img != null)
                    {
                        img.Freeze();
                        var item = new ClipboardItem
                        {
                            ImagePreview = img,
                            TextPreview = "[Image]",
                            IsImage = true,
                            Timestamp = DateTime.Now
                        };

                        InsertItem(item);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClipboardService] Error: {ex.Message}");
            }
        }

        private void InsertItem(ClipboardItem item)
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                History.Insert(0, item);
                while (History.Count > 10)
                {
                    History.RemoveAt(History.Count - 1);
                }
                HistoryChanged?.Invoke();
            });
        }

        public void CopyToClipboard(ClipboardItem item)
        {
            try
            {
                if (item.IsImage && item.ImagePreview != null)
                {
                    Clipboard.SetImage(item.ImagePreview);
                }
                else if (!string.IsNullOrEmpty(item.FullText))
                {
                    _lastSeenText = item.FullText;
                    Clipboard.SetText(item.FullText);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClipboardService] Copy error: {ex.Message}");
            }
        }

        public void Clear()
        {
            History.Clear();
            HistoryChanged?.Invoke();
        }

        public void Dispose()
        {
            if (_hookedHwnd != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(_hookedHwnd);
                _hookedHwnd = IntPtr.Zero;
            }
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }
}
