using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WinIsland.App.Core;
using WinIsland.App.Services;
using WinIsland.App.ViewModels;

namespace WinIsland.App
{
    public partial class MainWindow : Window
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "WinIsland";

        private readonly MainIslandViewModel _viewModel;
        private readonly DispatcherTimer _hoverTimer;
        private readonly DispatcherTimer _collapseWatchdogTimer;
        private readonly DispatcherTimer _fullscreenCheckTimer;
        private readonly DispatcherTimer _visualizerTimer;
        private readonly DispatcherTimer _ghostModeTimer;
        private readonly DispatcherTimer _rightClickHoldTimer;
        private readonly DispatcherTimer _toastAutoDismissTimer;
        private readonly DispatcherTimer _hudFeedbackTimer;
        private DispatcherTimer? _windowsNotifPollTimer;
        private SystemTrayService? _trayService;

        private bool _isExpanded;
        private bool _isTransitioning;
        private bool _isHiddenByFullscreen;
        private bool _isDraggingFile;
        private bool _isGhostModeActive;
        private bool _isRightMouseDown;
        private bool _isShowingCompactToast;
        private bool _isInlineEditing;

        private int _currentSlideIndex; // 0: Music, 1: Pomodoro, 2: TodoList, 3: Notifications, 4: FileDrop
        private bool _isLyricsOpen;
        private bool _isCalendarOpen;

        // Todo Editor & Calendar state
        private TodoTaskItem? _editingTask;
        private string _selectedTaskColor = "#30D158";
        private DateTime _editingTaskDate = DateTime.Today;
        private int _editStartHour = 8;
        private int _editStartMin;
        private int _editEndHour = 9;
        private int _editEndMin;
        private string _activeTimeSegment = string.Empty;
        private DateTime _calendarDisplayDate = DateTime.Today;
        private DateTime _selectedCalendarDate = DateTime.Today;
        private bool _isCalendarMonthMode;

        // Windows Notification Listener tracking
        private readonly HashSet<uint> _seenNotificationIds = new();
        private bool _initialNotificationsSeeded;

        private IntPtr _currentHMonitor = IntPtr.Zero;
        private IntPtr _pendingHMonitor = IntPtr.Zero;
        private int _monitorPendingTicks;
        private int _outsideTicks;
        private Point _dragStartPoint;
        private FileShelfItem? _draggedItem;
        private readonly Random _random = new();

        public static readonly DependencyProperty CapsuleCornerRadiusProperty =
            DependencyProperty.Register(nameof(CapsuleCornerRadius), typeof(double), typeof(MainWindow),
                new PropertyMetadata(22.0, (d, e) =>
                {
                    if (d is MainWindow w && w.MainIslandBorder != null)
                    {
                        w.MainIslandBorder.CornerRadius = new CornerRadius((double)e.NewValue);
                    }
                }));

        public double CapsuleCornerRadius
        {
            get => (double)GetValue(CapsuleCornerRadiusProperty);
            set => SetValue(CapsuleCornerRadiusProperty, value);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        public MainWindow()
        {
            InitializeComponent();

            _viewModel = new MainIslandViewModel();
            DataContext = _viewModel;

            // 1. Hover Intent Trigger Timer (250ms delay)
            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _hoverTimer.Tick += OnHoverTimerTick;

            // 2. Win32 Cursor Watchdog Timer (100ms interval for auto-collapse outside 12px tolerance)
            _collapseWatchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _collapseWatchdogTimer.Tick += OnCollapseWatchdogTick;
            _collapseWatchdogTimer.Start();

            // 3. Fullscreen monitoring timer (Auto-hide in games/videos - Zero Idle CPU)
            _fullscreenCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _fullscreenCheckTimer.Tick += OnFullscreenCheck;
            _fullscreenCheckTimer.Start();

            // 4. Audio Equalizer ticker (Dynamic bounce on visualizer bars)
            _visualizerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _visualizerTimer.Tick += OnVisualizerTick;
            _visualizerTimer.Start();

            // 5. Temporary Ghost Mode Timer (3 seconds click-through)
            _ghostModeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _ghostModeTimer.Tick += OnGhostModeTimerTick;

            // 6. Right-Click Hold Timer (200ms threshold for Klik Kanan Tahan)
            _rightClickHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _rightClickHoldTimer.Tick += OnRightClickHoldTimerTick;

            // 7. Live Toast Auto-Dismiss Timer (3.5s)
            _toastAutoDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3500) };
            _toastAutoDismissTimer.Tick += (s, e) =>
            {
                _toastAutoDismissTimer.Stop();
                DismissCompactToast();
            };

            // 8. Transient HUD Feedback Pill Timer (1.4s)
            _hudFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            _hudFeedbackTimer.Tick += (s, e) =>
            {
                _hudFeedbackTimer.Stop();
                HudFeedbackBadge.Visibility = Visibility.Collapsed;
            };

            // Connect ViewModel Notifications
            _viewModel.NotificationRequested += (app, title, msg, accent) =>
            {
                Dispatcher.Invoke(() => ShowNotificationToast(app, title, msg, accent));
            };

            // Connect TodoTasks updates to re-render 24h timeline bar
            _viewModel.TodoTasksUpdated += () =>
            {
                Dispatcher.Invoke(RenderTimelineBar);
            };

            // Hook ViewModel events & Services
            _viewModel.HoverController.ExpandRequested += () => Dispatcher.Invoke(() => _ = ExpandIslandAsync());
            _viewModel.HoverController.CollapseRequested += () => Dispatcher.Invoke(() => _ = CollapseIslandAsync());

            _viewModel.PropertyChanged += (s, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (e.PropertyName == nameof(MainIslandViewModel.MediaInfo) ||
                        e.PropertyName == nameof(MainIslandViewModel.Telemetry) ||
                        e.PropertyName == nameof(MainIslandViewModel.CurrentState) ||
                        e.PropertyName == nameof(MainIslandViewModel.IsPomoRunning) ||
                        e.PropertyName == nameof(MainIslandViewModel.IsMuted) ||
                        e.PropertyName == nameof(MainIslandViewModel.VolumePercent))
                    {
                        UpdateUiStates();
                    }

                    if (e.PropertyName == nameof(MainIslandViewModel.PomodoroProgressFraction))
                    {
                        UpdatePomodoroProgressArcs();
                    }

                    if (e.PropertyName == nameof(MainIslandViewModel.CurrentTimeString))
                    {
                        RenderTimelineBar();
                    }
                });
            };

            FileShelfService.Instance.Items.CollectionChanged += (s, e) =>
            {
                Dispatcher.Invoke(UpdateUiStates);
            };

            Loaded += OnMainWindowLoaded;
        }

        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            ScrubberTrackContainer.SizeChanged += (s, ev) => UpdateMediaProgress();
            TimelineTrackHost.SizeChanged += (s, ev) => RenderTimelineBar();
            NavigateToSlide(0, immediate: true);
            UpdatePomodoroProgressArcs();
            RenderCalendar();
            RenderTimelineBar();
            UpdateUiStates();
            InitWindowsNotificationListener();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Make borderless topmost overlay without stealing focus or Alt-Tab presence
            Win32Interop.MakeOverlayWindow(this);

            // Hook clipboard service
            ClipboardService.Instance.Hook(this);

            // Hook WM_NCHITTEST for transparent click-through outside the capsule
            var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            hwndSource?.AddHook(WndProc);

            // Position window according to Display Target setting (Primary or Active Cursor Monitor)
            UpdateIslandPosition(force: true);

            // Initialize System Tray Icon
            InitializeTray();
        }

        private void InitializeTray()
        {
            var menu = new ContextMenu();
            var itemExpand = new MenuItem { Header = "Expand Island" };
            itemExpand.Click += (s, e) => ExpandIsland();

            var itemCompact = new MenuItem { Header = "Compact Mode" };
            itemCompact.Click += (s, e) => CollapseIsland();

            var itemMusic = new MenuItem { Header = "🎵 Music Player" };
            itemMusic.Click += (s, e) => { NavigateToSlide(0); ExpandIsland(); };

            var itemPomo = new MenuItem { Header = "⏱️ Pomodoro Timer" };
            itemPomo.Click += (s, e) => { NavigateToSlide(1); ExpandIsland(); };

            var itemTodo = new MenuItem { Header = "✅ TodoList & Calendar" };
            itemTodo.Click += (s, e) => { NavigateToSlide(2); ExpandIsland(); };

            var itemNotify = new MenuItem { Header = "🔔 Notification Center" };
            itemNotify.Click += (s, e) => { NavigateToSlide(3); ExpandIsland(); };

            var itemFiles = new MenuItem { Header = "📁 File Tray" };
            itemFiles.Click += (s, e) => { NavigateToSlide(4); ExpandIsland(); };

            // Settings Submenu
            var settingsMenu = new MenuItem { Header = "⚙️ Settings" };
            var s = SettingsManager.Instance.Current;

            var itemPrimaryMon = new MenuItem { Header = "Display: Primary Monitor", IsCheckable = true, IsChecked = s.DisplayTarget != "ActiveCursor" };
            var itemCursorMon = new MenuItem { Header = "Display: Active Cursor Monitor", IsCheckable = true, IsChecked = s.DisplayTarget == "ActiveCursor" };

            itemPrimaryMon.Click += (_, _) =>
            {
                s.DisplayTarget = "Primary";
                itemPrimaryMon.IsChecked = true;
                itemCursorMon.IsChecked = false;
                SettingsManager.Instance.SaveSettings(s);
                UpdateIslandPosition(force: true);
            };
            itemCursorMon.Click += (_, _) =>
            {
                s.DisplayTarget = "ActiveCursor";
                itemCursorMon.IsChecked = true;
                itemPrimaryMon.IsChecked = false;
                SettingsManager.Instance.SaveSettings(s);
                UpdateIslandPosition(force: true);
            };

            var itemAutoHideFs = new MenuItem { Header = "Auto-Hide in Fullscreen", IsCheckable = true, IsChecked = s.EnableAutoHideFullScreen };
            itemAutoHideFs.Click += (_, _) =>
            {
                s.EnableAutoHideFullScreen = itemAutoHideFs.IsChecked;
                SettingsManager.Instance.SaveSettings(s);
            };

            var itemRequireAlt = new MenuItem { Header = "Require Alt Key to Expand", IsCheckable = true, IsChecked = s.RequireAltKey };
            itemRequireAlt.Click += (_, _) =>
            {
                s.RequireAltKey = itemRequireAlt.IsChecked;
                SettingsManager.Instance.SaveSettings(s);
            };

            var itemStartup = new MenuItem { Header = "Run on Windows Startup", IsCheckable = true, IsChecked = s.RunOnStartup };
            itemStartup.Click += (_, _) =>
            {
                s.RunOnStartup = itemStartup.IsChecked;
                SettingsManager.Instance.SaveSettings(s);
                SetRunOnStartupRegistry(s.RunOnStartup);
            };

            settingsMenu.Items.Add(itemPrimaryMon);
            settingsMenu.Items.Add(itemCursorMon);
            settingsMenu.Items.Add(new Separator());
            settingsMenu.Items.Add(itemAutoHideFs);
            settingsMenu.Items.Add(itemRequireAlt);
            settingsMenu.Items.Add(itemStartup);

            var itemExit = new MenuItem { Header = "Exit WinIsland" };
            itemExit.Click += (s2, e2) => Application.Current.Shutdown();

            menu.Items.Add(itemExpand);
            menu.Items.Add(itemCompact);
            menu.Items.Add(new Separator());
            menu.Items.Add(itemMusic);
            menu.Items.Add(itemPomo);
            menu.Items.Add(itemTodo);
            menu.Items.Add(itemNotify);
            menu.Items.Add(itemFiles);
            menu.Items.Add(new Separator());
            menu.Items.Add(settingsMenu);
            menu.Items.Add(new Separator());
            menu.Items.Add(itemExit);

            _trayService = new SystemTrayService();
            _trayService.Initialize(this, menu);
            _trayService.DoubleClicked += () =>
            {
                if (_isExpanded) CollapseIsland();
                else ExpandIsland();
            };
        }

        private static void SetRunOnStartupRegistry(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (key == null) return;
                if (enable)
                {
                    string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(AppName, $"\"{exePath}\"");
                    }
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SetRunOnStartupRegistry] Error: {ex.Message}");
            }
        }

        private void ShowHudFeedback(string text)
        {
            if (HudFeedbackBadge == null || HudFeedbackText == null) return;
            HudFeedbackText.Text = text;
            HudFeedbackBadge.Visibility = Visibility.Visible;
            _hudFeedbackTimer.Stop();
            _hudFeedbackTimer.Start();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_isGhostModeActive)
            {
                if (msg == Win32Interop.WM_NCHITTEST)
                {
                    handled = true;
                    return (IntPtr)Win32Interop.HTTRANSPARENT;
                }
            }
            else if (msg == Win32Interop.WM_NCHITTEST)
            {
                int x = (short)(lParam.ToInt64() & 0xFFFF);
                int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

                try
                {
                    Point windowPt = PointFromScreen(new Point(x, y));
                    GeneralTransform transform = MainIslandBorder.TransformToAncestor(this);
                    Rect capsuleBounds = transform.TransformBounds(new Rect(0, 0, MainIslandBorder.ActualWidth, MainIslandBorder.ActualHeight));

                    if (capsuleBounds.Contains(windowPt))
                    {
                        handled = true;
                        return (IntPtr)Win32Interop.HTCLIENT;
                    }
                    else
                    {
                        handled = true;
                        return (IntPtr)Win32Interop.HTTRANSPARENT;
                    }
                }
                catch
                {
                    handled = true;
                    return (IntPtr)Win32Interop.HTTRANSPARENT;
                }
            }
            else if (msg == Win32Interop.WM_DISPLAYCHANGE || msg == Win32Interop.WM_DPICHANGED)
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateIslandPosition(force: true)));
            }
            return IntPtr.Zero;
        }

        #region Win32 Cursor Watchdog, Hover & Ghost Mode

        private void OnMainIslandMouseEnter(object sender, MouseEventArgs e)
        {
            // If holding Ctrl while mouse enters, trigger Temporary Ghost Mode
            if ((Win32Interop.GetAsyncKeyState(Win32Interop.VK_CONTROL) & 0x8000) != 0)
            {
                ActivateTemporaryGhostMode(3.0);
                return;
            }

            if (_isGhostModeActive) return;

            if (_viewModel.Settings.RequireAltKey && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                return;
            }

            if (!_isExpanded && !_isTransitioning)
            {
                _hoverTimer.Stop();
                _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_viewModel.Settings.HoverDelayMs, 50, 1500));
                _hoverTimer.Start();
            }
        }

        private void OnMainIslandMouseLeave(object sender, MouseEventArgs e)
        {
            _hoverTimer.Stop();
        }

        private void OnMainIslandPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isRightMouseDown = true;
            _rightClickHoldTimer.Stop();
            _rightClickHoldTimer.Start();
        }

        private void OnMainIslandPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isRightMouseDown = false;
            _rightClickHoldTimer.Stop();
        }

        private void OnRightClickHoldTimerTick(object? sender, EventArgs e)
        {
            _rightClickHoldTimer.Stop();
            if (_isRightMouseDown)
            {
                ActivateTemporaryGhostMode(3.0);
            }
        }

        public void ActivateTemporaryGhostMode(double durationSeconds = 3.0)
        {
            _isGhostModeActive = true;
            _hoverTimer.Stop();

            double targetOpacity = Math.Clamp(_viewModel.Settings.GhostOpacityPercent / 100.0, 0.05, 1.0);

            var anim = new DoubleAnimation(this.Opacity, targetOpacity, TimeSpan.FromMilliseconds(100));
            this.BeginAnimation(UIElement.OpacityProperty, anim);

            ApplyWindowTransparency(true);

            _ghostModeTimer.Stop();
            _ghostModeTimer.Interval = TimeSpan.FromSeconds(durationSeconds);
            _ghostModeTimer.Start();
        }

        public void DeactivateGhostMode()
        {
            if (!_isGhostModeActive) return;

            bool isCtrlDown = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_CONTROL) & 0x8000) != 0;
            bool isRightDown = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_RBUTTON) & 0x8000) != 0;

            if (isCtrlDown || isRightDown) return;

            _ghostModeTimer.Stop();
            _isGhostModeActive = false;

            ApplyWindowTransparency(false);

            var anim = new DoubleAnimation(this.Opacity, 1.0, TimeSpan.FromMilliseconds(150));
            this.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private void ApplyWindowTransparency(bool transparent)
        {
            try
            {
                var helper = new WindowInteropHelper(this);
                IntPtr hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return;

                long style = Win32Interop.GetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE).ToInt64();
                if (transparent)
                {
                    style |= Win32Interop.WS_EX_TRANSPARENT;
                }
                else
                {
                    style &= ~Win32Interop.WS_EX_TRANSPARENT;
                }
                Win32Interop.SetWindowLong(hwnd, Win32Interop.GWL_EXSTYLE, new IntPtr(style));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ApplyWindowTransparency] Error: {ex.Message}");
            }
        }

        private void OnGhostModeTimerTick(object? sender, EventArgs e)
        {
            DeactivateGhostMode();
        }

        private void OnHoverTimerTick(object? sender, EventArgs e)
        {
            _hoverTimer.Stop();
            if (!_isExpanded && !_isTransitioning && !_isGhostModeActive)
            {
                ExpandIsland();
            }
        }

        private async void OnCollapseWatchdogTick(object? sender, EventArgs e)
        {
            // 1. Dynamic Multi-Monitor Cursor Tracking (when collapsed)
            if (!_isExpanded && !_isTransitioning && _viewModel.Settings.DisplayTarget == "ActiveCursor")
            {
                if (GetCursorPos(out POINT curPt))
                {
                    var pt = new Win32Interop.POINT { X = curPt.X, Y = curPt.Y };
                    IntPtr cursorMon = Win32Interop.MonitorFromPoint(pt, Win32Interop.MONITOR_DEFAULTTONEAREST);
                    if (cursorMon != IntPtr.Zero && cursorMon != _currentHMonitor)
                    {
                        if (cursorMon == _pendingHMonitor)
                        {
                            _monitorPendingTicks++;
                            if (_monitorPendingTicks >= 2)
                            {
                                _monitorPendingTicks = 0;
                                _pendingHMonitor = IntPtr.Zero;
                                UpdateIslandPosition();
                            }
                        }
                        else
                        {
                            _pendingHMonitor = cursorMon;
                            _monitorPendingTicks = 1;
                        }
                    }
                    else
                    {
                        _pendingHMonitor = IntPtr.Zero;
                        _monitorPendingTicks = 0;
                    }
                }
            }

            // 2. Win32 Cursor Watchdog for Ghost Mode trigger & Auto-collapse
            if (GetCursorPos(out POINT screenPt))
            {
                try
                {
                    Point windowPt = PointFromScreen(new Point(screenPt.X, screenPt.Y));
                    GeneralTransform transform = MainIslandBorder.TransformToAncestor(this);
                    Rect capsuleBounds = transform.TransformBounds(new Rect(0, 0, MainIslandBorder.ActualWidth, MainIslandBorder.ActualHeight));

                    // 12px tolerance on all 4 sides
                    Rect toleranceBounds = new Rect(
                        capsuleBounds.X - 12,
                        capsuleBounds.Y - 12,
                        capsuleBounds.Width + 24,
                        capsuleBounds.Height + 24
                    );

                    bool isOverIsland = toleranceBounds.Contains(windowPt);
                    bool isCtrlDown = (Win32Interop.GetAsyncKeyState(Win32Interop.VK_CONTROL) & 0x8000) != 0;

                    if (isOverIsland && isCtrlDown && !_isGhostModeActive)
                    {
                        ActivateTemporaryGhostMode(3.0);
                    }

                    // Do not auto-collapse while user is typing in an inline editor or dragging files
                    if (_isExpanded && !_isTransitioning && !_isDraggingFile && !_isInlineEditing)
                    {
                        if (!isOverIsland)
                        {
                            _outsideTicks++;
                            if (_outsideTicks >= 3) // 300ms
                            {
                                _outsideTicks = 0;
                                await CollapseIslandAsync();
                            }
                        }
                        else
                        {
                            _outsideTicks = 0;
                        }
                    }
                    else
                    {
                        _outsideTicks = 0;
                    }
                }
                catch
                {
                    _outsideTicks = 0;
                }
            }
        }

        public void UpdateIslandPosition(bool force = false)
        {
            try
            {
                IntPtr targetHMon;
                var s = SettingsManager.Instance.Current;

                if (s.DisplayTarget == "ActiveCursor")
                {
                    if (GetCursorPos(out POINT pt))
                    {
                        var win32Pt = new Win32Interop.POINT { X = pt.X, Y = pt.Y };
                        targetHMon = Win32Interop.MonitorFromPoint(win32Pt, Win32Interop.MONITOR_DEFAULTTONEAREST);
                    }
                    else
                    {
                        targetHMon = Win32Interop.MonitorFromPoint(new Win32Interop.POINT { X = 0, Y = 0 }, Win32Interop.MONITOR_DEFAULTTOPRIMARY);
                    }
                }
                else
                {
                    targetHMon = Win32Interop.MonitorFromPoint(new Win32Interop.POINT { X = 0, Y = 0 }, Win32Interop.MONITOR_DEFAULTTOPRIMARY);
                }

                if (targetHMon == IntPtr.Zero) return;

                if (!force && targetHMon == _currentHMonitor) return;

                _currentHMonitor = targetHMon;

                var bounds = Win32Interop.GetMonitorBounds(targetHMon);

                int physicalWidth = (int)Math.Round(this.Width * bounds.DpiScaleX);
                int physicalHeight = (int)Math.Round(this.Height * bounds.DpiScaleY);

                int physicalX = bounds.Left + (bounds.Width - physicalWidth) / 2;
                int physicalY = bounds.Top;

                var helper = new WindowInteropHelper(this);
                IntPtr hwnd = helper.Handle;
                if (hwnd != IntPtr.Zero)
                {
                    Win32Interop.SetWindowPos(
                        hwnd,
                        Win32Interop.HWND_TOPMOST,
                        physicalX,
                        physicalY,
                        physicalWidth,
                        physicalHeight,
                        Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateIslandPosition] Error: {ex.Message}");
            }
        }

        private void OnMinimizeClicked(object sender, RoutedEventArgs e)
        {
            CloseAnyInlineEditors();
            CollapseIsland();
        }

        public void ExpandIsland() => _ = ExpandIslandAsync();

        public void CollapseIsland() => _ = CollapseIslandAsync();

        private void CloseAnyInlineEditors()
        {
            if (PomodoroEditBorder != null && PomodoroEditBorder.Visibility == Visibility.Visible)
            {
                PomodoroEditBorder.Visibility = Visibility.Collapsed;
                PomodoroTimeText.Visibility = Visibility.Visible;
            }
            if (TimePickerOverlayBorder != null && TimePickerOverlayBorder.Visibility == Visibility.Visible)
            {
                TimePickerOverlayBorder.Visibility = Visibility.Collapsed;
            }
            if (TaskEditorPanel != null && TaskEditorPanel.Visibility == Visibility.Visible)
            {
                TaskEditorPanel.Visibility = Visibility.Collapsed;
            }
            if (_isInlineEditing)
            {
                _isInlineEditing = false;
                Win32Interop.SetAllowActivate(this, false);
            }
        }

        /// <summary>
        /// Staggered Expand:
        /// 1. Fade-out CompactView (80ms)
        /// 2. CompactView.Visibility = Collapsed
        /// 3. Morph MainIslandBorder size -> TargetWidth x 215, CornerRadius -> 30 with BackEase (260ms)
        /// 4. ExpandedView.Visibility = Visible & Fade-in (120ms)
        /// </summary>
        public async Task ExpandIslandAsync()
        {
            if (_isExpanded || _isTransitioning) return;
            _isTransitioning = true;
            _hoverTimer.Stop();

            // Clean up any active compact toast state so collapsing later returns to normal CompactStandardContent
            if (_isShowingCompactToast)
            {
                _isShowingCompactToast = false;
                _toastAutoDismissTimer.Stop();
                CompactToastContent.Visibility = Visibility.Collapsed;
                CompactStandardContent.Visibility = Visibility.Visible;
                if (!(_currentSlideIndex == 0 && _isLyricsOpen))
                {
                    AmbientBottomGlow.Visibility = Visibility.Collapsed;
                }
            }

            var tcs = new TaskCompletionSource<bool>();

            try
            {
                double startWidth = MainIslandBorder.ActualWidth > 0 ? MainIslandBorder.ActualWidth : 240;
                double startHeight = MainIslandBorder.ActualHeight > 0 ? MainIslandBorder.ActualHeight : 38;
                double targetWidth = GetCurrentTargetWidth();

                var fadeOutCompact = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(80));
                fadeOutCompact.Completed += (s, e) =>
                {
                    CompactView.Visibility = Visibility.Collapsed;

                    var backEase = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut };
                    var widthAnim = new DoubleAnimation(startWidth, targetWidth, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                    var heightAnim = new DoubleAnimation(startHeight, 236, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                    var radiusAnim = new DoubleAnimation(22, 30, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };

                    widthAnim.Completed += (s2, e2) =>
                    {
                        ExpandedView.Visibility = Visibility.Visible;
                        UpdateUiStates();
                        var fadeInExpanded = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120));
                        fadeInExpanded.Completed += (s3, e3) =>
                        {
                            _isExpanded = true;
                            _viewModel.CurrentState = IslandState.Expanded;
                            tcs.TrySetResult(true);
                        };
                        ExpandedView.BeginAnimation(UIElement.OpacityProperty, fadeInExpanded);
                    };

                    MainIslandBorder.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
                    MainIslandBorder.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
                    this.BeginAnimation(CapsuleCornerRadiusProperty, radiusAnim);
                };

                CompactView.BeginAnimation(UIElement.OpacityProperty, fadeOutCompact);
                await tcs.Task;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExpandIslandAsync] Error: {ex.Message}");
                tcs.TrySetResult(false);
            }
            finally
            {
                _isTransitioning = false;
            }
        }

        /// <summary>
        /// Staggered Collapse:
        /// 1. Fade-out ExpandedView (80ms)
        /// 2. ExpandedView.Visibility = Collapsed
        /// 3. Morph MainIslandBorder size CurrentWidth x 215 -> 220x38, CornerRadius 30 -> 22 with BackEase (260ms)
        /// 4. CompactView.Visibility = Visible & Fade-in (120ms)
        /// </summary>
        public async Task CollapseIslandAsync()
        {
            if (!_isExpanded || _isTransitioning) return;
            _isTransitioning = true;
            _hoverTimer.Stop();
            CloseAnyInlineEditors();

            var tcs = new TaskCompletionSource<bool>();

            try
            {
                double currentWidth = MainIslandBorder.ActualWidth;
                double compactWidth = _viewModel.IsPomoRunning ? 265.0 : 240.0;

                var fadeOutExpanded = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(80));
                fadeOutExpanded.Completed += (s, e) =>
                {
                    ExpandedView.Visibility = Visibility.Collapsed;
                    AmbientBottomGlow.Visibility = Visibility.Collapsed;

                    var backEase = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut };
                    var widthAnim = new DoubleAnimation(currentWidth, compactWidth, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                    var heightAnim = new DoubleAnimation(236, 38, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                    var radiusAnim = new DoubleAnimation(30, 22, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };

                    widthAnim.Completed += (s2, e2) =>
                    {
                        CompactStandardContent.Visibility = Visibility.Visible;
                        CompactToastContent.Visibility = Visibility.Collapsed;
                        CompactView.Visibility = Visibility.Visible;
                        UpdateUiStates();
                        var fadeInCompact = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120));
                        fadeInCompact.Completed += (s3, e3) =>
                        {
                            _isExpanded = false;
                            _viewModel.CurrentState = IslandState.Compact;
                            tcs.TrySetResult(true);
                        };
                        CompactView.BeginAnimation(UIElement.OpacityProperty, fadeInCompact);
                    };

                    MainIslandBorder.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
                    MainIslandBorder.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
                    this.BeginAnimation(CapsuleCornerRadiusProperty, radiusAnim);
                };

                ExpandedView.BeginAnimation(UIElement.OpacityProperty, fadeOutExpanded);
                await tcs.Task;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CollapseIslandAsync] Error: {ex.Message}");
                tcs.TrySetResult(false);
            }
            finally
            {
                _isTransitioning = false;
            }
        }

        private double GetCurrentTargetWidth()
        {
            // Wide Mode (700px): when Music Live Lyrics is open OR when TodoList Calendar Timeline is open
            if ((_currentSlideIndex == 0 && _isLyricsOpen) || (_currentSlideIndex == 2 && _isCalendarOpen))
            {
                return 700.0;
            }
            return 490.0;
        }

        private void AnimateCapsuleWidth(double targetWidth)
        {
            if (!_isExpanded) return;

            var ease = new BackEase { Amplitude = 0.15, EasingMode = EasingMode.EaseOut };
            var widthAnim = new DoubleAnimation(MainIslandBorder.ActualWidth, targetWidth, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = ease
            };
            MainIslandBorder.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
        }

        #endregion

        #region Carousel Architecture & Morphing Bottom Dock

        private void OnPrevSlideClicked(object sender, RoutedEventArgs e)
        {
            CloseAnyInlineEditors();
            NavigateToSlide(_currentSlideIndex - 1);
        }

        private void OnNextSlideClicked(object sender, RoutedEventArgs e)
        {
            CloseAnyInlineEditors();
            NavigateToSlide(_currentSlideIndex + 1);
        }

        private void OnDotClicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && int.TryParse(elem.Tag?.ToString(), out int idx))
            {
                CloseAnyInlineEditors();
                NavigateToSlide(idx);
            }
        }

        private void OnDockBtnClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && int.TryParse(elem.Tag?.ToString(), out int idx))
            {
                CloseAnyInlineEditors();
                NavigateToSlide(idx);
            }
        }

        public void NavigateToSlide(int newIndex, bool immediate = false)
        {
            _currentSlideIndex = (newIndex % 5 + 5) % 5;

            // Update Slide Views Visibility
            SlideMusic.Visibility = _currentSlideIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            SlidePomodoro.Visibility = _currentSlideIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            SlideTodoList.Visibility = _currentSlideIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            SlideNotifications.Visibility = _currentSlideIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
            SlideFileDrop.Visibility = _currentSlideIndex == 4 ? Visibility.Visible : Visibility.Collapsed;

            // Update Pagination Dots (Active is elongated white pill 16x4, inactive 4x4 #55FFFFFF)
            UpdatePaginationDots();

            // Update Micro Dock Icons
            UpdateMicroDockColors();

            // Adjust Width if needed (Wide 640 vs Standard 460)
            if (_isExpanded && !immediate)
            {
                AnimateCapsuleWidth(GetCurrentTargetWidth());
            }

            // Sync Ambient Bottom Glow for Slide 0 if lyrics were active
            if (_currentSlideIndex == 0 && _isLyricsOpen)
            {
                AmbientBottomGlow.Visibility = Visibility.Visible;
                AmbientBottomGlow.Opacity = 1.0;
            }
            else if (!_isShowingCompactToast)
            {
                AmbientBottomGlow.Visibility = Visibility.Collapsed;
                AmbientBottomGlow.Opacity = 0.0;
            }

            if (_currentSlideIndex == 2)
            {
                RenderCalendar();
                RenderTimelineBar();
            }

            UpdateUiStates();
        }

        private void UpdatePaginationDots()
        {
            Border[] dots = { Dot0, Dot1, Dot2, Dot3, Dot4 };
            var activeBrush = Brushes.White;
            var inactiveBrush = (Brush)new BrushConverter().ConvertFromString("#55FFFFFF")!;

            for (int i = 0; i < dots.Length; i++)
            {
                if (i == _currentSlideIndex)
                {
                    dots[i].Width = 16;
                    dots[i].Background = activeBrush;
                }
                else
                {
                    dots[i].Width = 4;
                    dots[i].Background = inactiveBrush;
                }
            }
        }

        private void UpdateMicroDockColors()
        {
            var inactiveBrush = (Brush)new BrushConverter().ConvertFromString("#8E8E93")!;

            // 1. Music (Pink/Red #FF2D55)
            DockMusicIcon.Fill = _currentSlideIndex == 0
                ? (Brush)new BrushConverter().ConvertFromString("#FF2D55")!
                : inactiveBrush;

            // 2. Pomodoro (Red/Orange #FF453A)
            DockPomodoroIcon.Stroke = _currentSlideIndex == 1
                ? (Brush)new BrushConverter().ConvertFromString("#FF453A")!
                : inactiveBrush;

            // 3. TodoList (Green #30D158)
            DockTodoIcon.Stroke = _currentSlideIndex == 2
                ? (Brush)new BrushConverter().ConvertFromString("#30D158")!
                : inactiveBrush;

            // 4. Notifications (Blue #0A84FF)
            DockNotifyIcon.Stroke = _currentSlideIndex == 3
                ? (Brush)new BrushConverter().ConvertFromString("#0A84FF")!
                : inactiveBrush;

            // 5. File Tray (Purple #BF5AF2)
            DockFileTrayIcon.Stroke = _currentSlideIndex == 4
                ? (Brush)new BrushConverter().ConvertFromString("#BF5AF2")!
                : inactiveBrush;
        }

        private void OnBottomDockMouseEnter(object sender, MouseEventArgs e)
        {
            var fadeOutDots = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(100));
            fadeOutDots.Completed += (s, ev) =>
            {
                PaginationDotsPanel.Visibility = Visibility.Collapsed;
                BottomIconDockBorder.Visibility = Visibility.Visible;
                var fadeInDock = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120));
                BottomIconDockBorder.BeginAnimation(UIElement.OpacityProperty, fadeInDock);
            };
            PaginationDotsPanel.BeginAnimation(UIElement.OpacityProperty, fadeOutDots);
        }

        private void OnBottomDockMouseLeave(object sender, MouseEventArgs e)
        {
            var fadeOutDock = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(100));
            fadeOutDock.Completed += (s, ev) =>
            {
                BottomIconDockBorder.Visibility = Visibility.Collapsed;
                PaginationDotsPanel.Visibility = Visibility.Visible;
                var fadeInDots = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120));
                PaginationDotsPanel.BeginAnimation(UIElement.OpacityProperty, fadeInDots);
            };
            BottomIconDockBorder.BeginAnimation(UIElement.OpacityProperty, fadeOutDock);
        }

        #endregion

        #region Slide 0: Music Player & Split-View Live Synced Lyrics

        private void OnLyricsToggleClicked(object sender, RoutedEventArgs e)
        {
            _isLyricsOpen = !_isLyricsOpen;

            if (_isLyricsOpen)
            {
                LyricsPanel.Visibility = Visibility.Visible;
                LyricsSplitColumn.Width = GridLength.Auto;
                LyricsToggleBorder.Background = (Brush)new BrushConverter().ConvertFromString("#2EFFFFFF")!;
                LyricsQuoteGlyph.Fill = (Brush)new BrushConverter().ConvertFromString("#FF9F0A")!;
                _ = _viewModel.EnsureLyricsLoadedAsync();

                // Amber/warm bottom glow for lyrics
                AmbientBottomGlow.Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop(Colors.Transparent, 0.0),
                        new GradientStop((Color)ColorConverter.ConvertFromString("#CC9A4820"), 1.0)
                    }
                };
                AmbientBottomGlow.Visibility = Visibility.Visible;
                var fadeInGlow = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200));
                AmbientBottomGlow.BeginAnimation(UIElement.OpacityProperty, fadeInGlow);
            }
            else
            {
                LyricsPanel.Visibility = Visibility.Collapsed;
                LyricsSplitColumn.Width = GridLength.Auto;
                LyricsToggleBorder.Background = (Brush)new BrushConverter().ConvertFromString("#14FFFFFF")!;
                LyricsQuoteGlyph.Fill = (Brush)new BrushConverter().ConvertFromString("#8E8E93")!;

                var fadeOutGlow = new DoubleAnimation(AmbientBottomGlow.Opacity, 0.0, TimeSpan.FromMilliseconds(150));
                fadeOutGlow.Completed += (s, ev) =>
                {
                    if (!_isLyricsOpen && !_isShowingCompactToast)
                    {
                        AmbientBottomGlow.Visibility = Visibility.Collapsed;
                    }
                };
                AmbientBottomGlow.BeginAnimation(UIElement.OpacityProperty, fadeOutGlow);
            }

            AnimateCapsuleWidth(GetCurrentTargetWidth());
            UpdateUiStates();
        }

        private void OnVolumeIconClicked(object sender, RoutedEventArgs e)
        {
            AudioHudService.Instance.ToggleMute();
            bool muted = AudioHudService.Instance.IsMuted;
            int vol = (int)Math.Round(AudioHudService.Instance.CurrentVolume * 100);
            ShowHudFeedback(muted ? "🔇 Muted" : $"🔊 {vol}%");
            UpdateUiStates();
        }

        #endregion

        #region Slide 1: Pomodoro Circular Ring & Flexible Time

        private void UpdatePomodoroProgressArcs()
        {
            double frac = _viewModel.PomodoroProgressFraction;

            // 1. Main Pomodoro Ring (82x82px, Center 41,41, Radius 37.5)
            PomodoroProgressArc.Data = CreateArcGeometry(new Point(41, 41), 37.5, frac);

            // 2. Top Header Mini Ring (14x14px, Center 7,7, Radius 5)
            TopMiniProgressArc.Data = CreateArcGeometry(new Point(7, 7), 5, frac);
        }

        private Geometry CreateArcGeometry(Point center, double radius, double fraction)
        {
            if (fraction <= 0.001)
            {
                fraction = 0.001;
            }
            if (fraction >= 0.999)
            {
                fraction = 0.999;
            }

            double angle = fraction * 360.0;
            double rad = (angle - 90.0) * (Math.PI / 180.0);

            Point startPoint = new Point(center.X, center.Y - radius);
            Point endPoint = new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
            bool isLargeArc = angle > 180.0;

            var geom = new PathGeometry();
            var fig = new PathFigure { StartPoint = startPoint, IsClosed = false };
            fig.Segments.Add(new ArcSegment(endPoint, new Size(radius, radius), 0, isLargeArc, SweepDirection.Clockwise, true));
            geom.Figures.Add(fig);
            return geom;
        }

        private void OnPomodoroTimeMouseWheel(object sender, MouseWheelEventArgs e)
        {
            int deltaMins = e.Delta > 0 ? 1 : -1;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                deltaMins *= 5;
            }
            _viewModel.AdjustPomoMinutes(deltaMins);
            UpdatePomodoroProgressArcs();
            ShowHudFeedback($"⏱️ {_viewModel.PomoTimeRemaining}");
            e.Handled = true;
        }

        private void OnPomodoroTimeClicked(object sender, MouseButtonEventArgs e)
        {
            // Open inline custom minutes editor
            string currentMins = _viewModel.PomoTimeRemaining.Split(':')[0].TrimStart('0');
            if (string.IsNullOrEmpty(currentMins)) currentMins = "1";

            PomodoroTimeText.Visibility = Visibility.Collapsed;
            PomodoroEditBorder.Visibility = Visibility.Visible;
            TxtPomodoroMinutesInput.Text = currentMins;

            _isInlineEditing = true;
            Win32Interop.SetAllowActivate(this, true);
            TxtPomodoroMinutesInput.Focus();
            TxtPomodoroMinutesInput.SelectAll();
            e.Handled = true;
        }

        private void CommitPomodoroMinutesEdit()
        {
            if (PomodoroEditBorder.Visibility != Visibility.Visible) return;

            if (int.TryParse(TxtPomodoroMinutesInput.Text.Trim(), out int mins) && mins > 0)
            {
                mins = Math.Clamp(mins, 1, 180);
                _viewModel.SetPomoDuration(mins, _viewModel.PomoModeName == "Focus time");
                UpdatePomodoroProgressArcs();
            }

            PomodoroEditBorder.Visibility = Visibility.Collapsed;
            PomodoroTimeText.Visibility = Visibility.Visible;
            _isInlineEditing = false;
            Win32Interop.SetAllowActivate(this, false);
        }

        private void OnPomodoroMinutesKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitPomodoroMinutesEdit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                PomodoroEditBorder.Visibility = Visibility.Collapsed;
                PomodoroTimeText.Visibility = Visibility.Visible;
                _isInlineEditing = false;
                Win32Interop.SetAllowActivate(this, false);
                e.Handled = true;
            }
        }

        private void OnPomodoroMinutesLostFocus(object sender, RoutedEventArgs e)
        {
            CommitPomodoroMinutesEdit();
        }

        private void OnPomodoroShortBreakClicked(object sender, RoutedEventArgs e)
        {
            CloseAnyInlineEditors();
            _viewModel.SetPomoDuration(5, isFocus: false);
            UpdatePomodoroProgressArcs();
        }

        private void OnPomodoroFocusClicked(object sender, RoutedEventArgs e)
        {
            CloseAnyInlineEditors();
            _viewModel.SetPomoDuration(25, isFocus: true);
            UpdatePomodoroProgressArcs();
        }

        private void OnPomodoroPlayClicked(object sender, RoutedEventArgs e)
        {
            CloseAnyInlineEditors();
            _viewModel.TogglePomoCommand.Execute(null);
            UpdateUiStates();
        }

        private void OnPomodoroResetClicked(object sender, RoutedEventArgs e)
        {
            CloseAnyInlineEditors();
            _viewModel.ResetPomoCommand.Execute(null);
            UpdatePomodoroProgressArcs();
            UpdateUiStates();
        }

        #endregion

        #region Slide 2: TodoList, Inline Task Editor & Expandable Calendar Timeline

        private void OnCalendarToggleClicked(object sender, RoutedEventArgs e)
        {
            _isCalendarOpen = !_isCalendarOpen;

            if (_isCalendarOpen)
            {
                CalendarExpandPanel.Visibility = Visibility.Visible;
                CalendarColumn.Width = GridLength.Auto;
                CalBorder.Background = (Brush)new BrushConverter().ConvertFromString("#2630D158")!;
                CalGlyph.Stroke = (Brush)new BrushConverter().ConvertFromString("#30D158")!;
                RenderCalendar();
                RenderTimelineBar();
            }
            else
            {
                CalendarExpandPanel.Visibility = Visibility.Collapsed;
                CalendarColumn.Width = GridLength.Auto;
                CalBorder.Background = (Brush)new BrushConverter().ConvertFromString("#1C1C1E")!;
                CalGlyph.Stroke = (Brush)new BrushConverter().ConvertFromString("#8E8E93")!;
            }

            AnimateCapsuleWidth(GetCurrentTargetWidth());
            UpdateUiStates();
        }

        private void OnTaskItemClicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is TodoTaskItem task)
            {
                _viewModel.ToggleTaskCommand.Execute(task);
            }
        }

        private void OnAddTaskClicked(object sender, RoutedEventArgs e)
        {
            OpenTaskEditor(null);
        }

        private void OnEditTaskClicked(object sender, RoutedEventArgs e)
        {
            // Open editor for the first uncompleted task on the selected date (or first task, or new task if empty)
            var target = _viewModel.FilteredTodoTasks.FirstOrDefault(t => !t.IsCompleted)
                         ?? _viewModel.FilteredTodoTasks.FirstOrDefault()
                         ?? _viewModel.TodoTasks.FirstOrDefault();
            OpenTaskEditor(target);
        }

        private void OnEditSingleTaskClicked(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement btn && btn.Tag is TodoTaskItem task)
            {
                OpenTaskEditor(task);
            }
        }

        private void OnDeleteSingleTaskClicked(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement btn && btn.Tag is TodoTaskItem task)
            {
                _viewModel.DeleteTask(task);
                RenderCalendar();
                ShowHudFeedback("🗑️ Task Deleted");
            }
        }

        private void OpenTaskEditor(TodoTaskItem? taskToEdit)
        {
            _editingTask = taskToEdit;
            if (TimePickerOverlayBorder != null)
            {
                TimePickerOverlayBorder.Visibility = Visibility.Collapsed;
            }

            if (taskToEdit != null)
            {
                TxtTaskEditorHeader.Text = "Edit Task";
                TxtTaskTitleInput.Text = taskToEdit.Title;
                _editingTaskDate = taskToEdit.TaskDate;
                (_editStartHour, _editStartMin, _editEndHour, _editEndMin) = taskToEdit.GetParsedTimeParts();
                SelectTaskColorDot(taskToEdit.CategoryColor);
            }
            else
            {
                TxtTaskEditorHeader.Text = "Add Task";
                TxtTaskTitleInput.Text = "";
                _editingTaskDate = _selectedCalendarDate;
                int nowH = DateTime.Now.Hour;
                _editStartHour = nowH;
                _editStartMin = 0;
                _editEndHour = (nowH + 1) % 24;
                _editEndMin = 0;
                SelectTaskColorDot("#30D158");
            }

            UpdateTaskEditorDateTimeUi();

            TaskEditorPanel.Visibility = Visibility.Visible;
            _isInlineEditing = true;
            Win32Interop.SetAllowActivate(this, true);
            TxtTaskTitleInput.Focus();
            TxtTaskTitleInput.SelectAll();
        }

        private void UpdateTaskEditorDateTimeUi()
        {
            if (TxtTaskEditorDate != null)
            {
                TxtTaskEditorDate.Text = _editingTaskDate.Date == DateTime.Today
                    ? $"Today ({_editingTaskDate:dd MMM})"
                    : _editingTaskDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
            }
            if (BtnStartHour != null) BtnStartHour.Content = _editStartHour.ToString("D2");
            if (BtnStartMin != null) BtnStartMin.Content = _editStartMin.ToString("D2");
            if (BtnEndHour != null) BtnEndHour.Content = _editEndHour.ToString("D2");
            if (BtnEndMin != null) BtnEndMin.Content = _editEndMin.ToString("D2");
        }

        private void SyncEditorDateWithCalendar()
        {
            _selectedCalendarDate = _editingTaskDate.Date;
            _calendarDisplayDate = _editingTaskDate.Date;
            _viewModel.SelectedTaskDate = _editingTaskDate.Date;
            UpdateTaskEditorDateTimeUi();
            RenderCalendar();
        }

        private void OnTaskDatePrevClicked(object sender, RoutedEventArgs e)
        {
            _editingTaskDate = _editingTaskDate.AddDays(-1);
            SyncEditorDateWithCalendar();
        }

        private void OnTaskDateNextClicked(object sender, RoutedEventArgs e)
        {
            _editingTaskDate = _editingTaskDate.AddDays(1);
            SyncEditorDateWithCalendar();
        }

        private void OnTaskDateSelectorWheel(object sender, MouseWheelEventArgs e)
        {
            int delta = e.Delta > 0 ? 1 : -1;
            _editingTaskDate = _editingTaskDate.AddDays(delta);
            SyncEditorDateWithCalendar();
            e.Handled = true;
        }

        private void OnTaskDateTextClicked(object sender, MouseButtonEventArgs e)
        {
            // Toggle Calendar panel open so user can also click any date directly on the calendar
            if (!_isCalendarOpen)
            {
                OnCalendarToggleClicked(sender, new RoutedEventArgs());
            }
            e.Handled = true;
        }

        private void OnTimeSegmentMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is FrameworkElement btn && btn.Tag is string segment)
            {
                int dir = e.Delta > 0 ? 1 : -1;
                int minStep = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 5;

                switch (segment)
                {
                    case "StartHour":
                        _editStartHour = (_editStartHour + dir + 24) % 24;
                        break;
                    case "StartMin":
                        _editStartMin = (_editStartMin + dir * minStep + 60) % 60;
                        break;
                    case "EndHour":
                        _editEndHour = (_editEndHour + dir + 24) % 24;
                        break;
                    case "EndMin":
                        _editEndMin = (_editEndMin + dir * minStep + 60) % 60;
                        break;
                }

                UpdateTaskEditorDateTimeUi();
                e.Handled = true;
            }
        }

        private void OnTimeSegmentClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement btn && btn.Tag is string segment)
            {
                _activeTimeSegment = segment;
                OpenTimePickerOverlay(segment);
            }
        }

        private void OpenTimePickerOverlay(string segment)
        {
            if (TimePickerOverlayBorder == null || TimePickerValuesGrid == null || TxtTimePickerOverlayTitle == null) return;

            bool isHour = segment.EndsWith("Hour", StringComparison.Ordinal);
            TxtTimePickerOverlayTitle.Text = segment switch
            {
                "StartHour" => "Pilih Jam Mulai (00–23)",
                "StartMin" => "Pilih Menit Mulai (00–55)",
                "EndHour" => "Pilih Jam Selesai (00–23)",
                _ => "Pilih Menit Selesai (00–55)"
            };

            int currentVal = segment switch
            {
                "StartHour" => _editStartHour,
                "StartMin" => _editStartMin,
                "EndHour" => _editEndHour,
                _ => _editEndMin
            };

            TimePickerValuesGrid.Children.Clear();
            var greenBrush = (Brush)new BrushConverter().ConvertFromString("#30D158")!;
            var normalBg = (Brush)new BrushConverter().ConvertFromString("#24242E")!;

            if (isHour)
            {
                for (int h = 0; h < 24; h++)
                {
                    int captured = h;
                    bool selected = h == currentVal;
                    var itemBtn = CreateTimeGridCellButton(h.ToString("D2"), selected, greenBrush, normalBg, () =>
                    {
                        if (_activeTimeSegment == "StartHour")
                        {
                            _editStartHour = captured;
                            if (_editEndHour <= _editStartHour)
                            {
                                _editEndHour = (_editStartHour + 1) % 24;
                            }
                        }
                        else
                        {
                            _editEndHour = captured;
                        }
                        UpdateTaskEditorDateTimeUi();
                        TimePickerOverlayBorder.Visibility = Visibility.Collapsed;
                    });
                    TimePickerValuesGrid.Children.Add(itemBtn);
                }
            }
            else
            {
                for (int m = 0; m < 60; m += 5)
                {
                    int captured = m;
                    bool selected = m == currentVal;
                    var itemBtn = CreateTimeGridCellButton(m.ToString("D2"), selected, greenBrush, normalBg, () =>
                    {
                        if (_activeTimeSegment == "StartMin") _editStartMin = captured;
                        else _editEndMin = captured;
                        UpdateTaskEditorDateTimeUi();
                        TimePickerOverlayBorder.Visibility = Visibility.Collapsed;
                    });
                    TimePickerValuesGrid.Children.Add(itemBtn);
                }
            }

            TimePickerOverlayBorder.Visibility = Visibility.Visible;
        }

        private static Border CreateTimeGridCellButton(string label, bool isSelected, Brush activeBrush, Brush normalBrush, Action onClick)
        {
            var bd = new Border
            {
                Background = isSelected ? activeBrush : normalBrush,
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(1.5),
                Padding = new Thickness(0, 3, 0, 3),
                Cursor = Cursors.Hand
            };

            var tb = new TextBlock
            {
                Text = label,
                FontSize = 9.5,
                FontWeight = isSelected ? FontWeights.Bold : FontWeights.SemiBold,
                Foreground = isSelected ? Brushes.Black : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            bd.Child = tb;
            bd.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
            return bd;
        }

        private void OnCloseTimePickerOverlayClicked(object sender, RoutedEventArgs e)
        {
            if (TimePickerOverlayBorder != null)
            {
                TimePickerOverlayBorder.Visibility = Visibility.Collapsed;
            }
        }

        private void SelectTaskColorDot(string hex)
        {
            _selectedTaskColor = string.IsNullOrWhiteSpace(hex) ? "#30D158" : hex;
            Border[] dots = { ColorDotGreen, ColorDotBlue, ColorDotOrange, ColorDotPurple };
            foreach (var d in dots)
            {
                if (d == null) continue;
                bool match = string.Equals(d.Tag?.ToString(), _selectedTaskColor, StringComparison.OrdinalIgnoreCase);
                d.BorderBrush = match ? Brushes.White : Brushes.Transparent;
                d.BorderThickness = new Thickness(match ? 1.5 : 0);
            }
        }

        private void OnTaskColorSelected(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is string hex)
            {
                SelectTaskColorDot(hex);
            }
        }

        private void OnSaveTaskEditorClicked(object sender, RoutedEventArgs e)
        {
            CommitTaskEditor();
        }

        private void OnCancelTaskEditorClicked(object sender, RoutedEventArgs e)
        {
            CloseAnyInlineEditors();
        }

        private void OnTaskEditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitTaskEditor();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (TimePickerOverlayBorder != null && TimePickerOverlayBorder.Visibility == Visibility.Visible)
                {
                    TimePickerOverlayBorder.Visibility = Visibility.Collapsed;
                    e.Handled = true;
                    return;
                }
                CloseAnyInlineEditors();
                e.Handled = true;
            }
        }

        private void CommitTaskEditor()
        {
            string title = TxtTaskTitleInput.Text.Trim();
            string timeRange = $"{_editStartHour:D2}:{_editStartMin:D2}-{_editEndHour:D2}:{_editEndMin:D2}";

            if (!string.IsNullOrWhiteSpace(title))
            {
                _selectedCalendarDate = _editingTaskDate.Date;
                _calendarDisplayDate = _editingTaskDate.Date;

                if (_editingTask != null)
                {
                    _viewModel.UpdateTask(_editingTask, title, _editingTaskDate, timeRange, _selectedTaskColor);
                    ShowHudFeedback($"✓ Saved ({_editingTaskDate:dd MMM})");
                }
                else
                {
                    _viewModel.AddCustomTask(title, _editingTaskDate, timeRange, _selectedTaskColor);
                    ShowHudFeedback($"✓ Added ({_editingTaskDate:dd MMM})");
                }

                RenderCalendar();
                RenderTimelineBar();
            }

            CloseAnyInlineEditors();
        }

        private void OnCalendarPrevClicked(object sender, RoutedEventArgs e)
        {
            _calendarDisplayDate = _isCalendarMonthMode
                ? _calendarDisplayDate.AddMonths(-1)
                : _calendarDisplayDate.AddDays(-7);
            RenderCalendar();
        }

        private void OnCalendarNextClicked(object sender, RoutedEventArgs e)
        {
            _calendarDisplayDate = _isCalendarMonthMode
                ? _calendarDisplayDate.AddMonths(1)
                : _calendarDisplayDate.AddDays(7);
            RenderCalendar();
        }

        private void OnCalendarModeChanged(object sender, RoutedEventArgs e)
        {
            if (RbCalendarMonth == null || RbCalendarWeek == null) return;
            _isCalendarMonthMode = RbCalendarMonth.IsChecked == true;
            RenderCalendar();
        }

        private void RenderCalendar()
        {
            if (CalendarDatesGrid == null || TxtCalendarMonthYear == null) return;

            TxtCalendarMonthYear.Text = _calendarDisplayDate.ToString("MMM yyyy", CultureInfo.InvariantCulture);
            CalendarDatesGrid.Children.Clear();

            if (TimelineSectionPanel != null)
            {
                TimelineSectionPanel.Visibility = _isCalendarMonthMode ? Visibility.Collapsed : Visibility.Visible;
            }

            var greenBrush = (Brush)new BrushConverter().ConvertFromString("#30D158")!;
            var todayBorderBrush = (Brush)new BrushConverter().ConvertFromString("#6630D158")!;
            var hasTaskBorderBrush = (Brush)new BrushConverter().ConvertFromString("#8830D158")!;
            var normalTextBrush = (Brush)new BrushConverter().ConvertFromString("#C7C7CC")!;
            var dimTextBrush = (Brush)new BrushConverter().ConvertFromString("#48484A")!;

            if (!_isCalendarMonthMode)
            {
                // Week Mode: 7 days starting Sunday of _calendarDisplayDate's week
                int diff = (int)_calendarDisplayDate.DayOfWeek;
                DateTime startOfWeek = _calendarDisplayDate.AddDays(-diff);

                for (int i = 0; i < 7; i++)
                {
                    DateTime day = startOfWeek.AddDays(i);
                    bool isSelected = day.Date == _selectedCalendarDate.Date;
                    bool isToday = day.Date == DateTime.Today;
                    bool hasTasks = _viewModel.HasTasksOnDate(day);

                    var cell = new Border
                    {
                        Width = 20,
                        Height = 20,
                        Margin = new Thickness(0, 2, 0, 2),
                        CornerRadius = new CornerRadius(10),
                        Background = isSelected ? greenBrush : (isToday ? todayBorderBrush : Brushes.Transparent),
                        BorderBrush = (!isSelected && hasTasks) ? hasTaskBorderBrush : Brushes.Transparent,
                        BorderThickness = new Thickness((!isSelected && hasTasks) ? 1.2 : 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor = Cursors.Hand,
                        Tag = day
                    };

                    var tb = new TextBlock
                    {
                        Text = day.Day.ToString(),
                        FontSize = 9.5,
                        FontWeight = (isSelected || isToday || hasTasks) ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = isSelected ? Brushes.Black : normalTextBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    cell.Child = tb;
                    cell.MouseLeftButtonUp += OnCalendarDayCellClicked;
                    CalendarDatesGrid.Children.Add(cell);
                }
            }
            else
            {
                // Compact Month Mode: 28 cells (4 weeks centered around current display month)
                DateTime firstOfMonth = new DateTime(_calendarDisplayDate.Year, _calendarDisplayDate.Month, 1);
                DateTime startGrid = firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);

                // If selected date is in the last rows of the month, shift window by 1-2 weeks so whole month is reachable
                if (_calendarDisplayDate.Day > 21)
                {
                    startGrid = startGrid.AddDays(7);
                }

                for (int i = 0; i < 28; i++)
                {
                    DateTime day = startGrid.AddDays(i);
                    bool isCurrentMonth = day.Month == _calendarDisplayDate.Month;
                    bool isSelected = day.Date == _selectedCalendarDate.Date;
                    bool isToday = day.Date == DateTime.Today;
                    bool hasTasks = _viewModel.HasTasksOnDate(day);

                    var cell = new Border
                    {
                        Width = 20,
                        Height = 16,
                        Margin = new Thickness(0, 1, 0, 1),
                        CornerRadius = new CornerRadius(8),
                        Background = isSelected ? greenBrush : (isToday ? todayBorderBrush : Brushes.Transparent),
                        BorderBrush = (!isSelected && hasTasks) ? hasTaskBorderBrush : Brushes.Transparent,
                        BorderThickness = new Thickness((!isSelected && hasTasks) ? 1.2 : 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor = Cursors.Hand,
                        Tag = day
                    };

                    var tb = new TextBlock
                    {
                        Text = day.Day.ToString(),
                        FontSize = 8.5,
                        FontWeight = (isSelected || isToday || hasTasks) ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = isSelected ? Brushes.Black : (isCurrentMonth ? normalTextBrush : dimTextBrush),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    cell.Child = tb;
                    cell.MouseLeftButtonUp += OnCalendarDayCellClicked;
                    CalendarDatesGrid.Children.Add(cell);
                }
            }
        }

        private void OnCalendarDayCellClicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement cell && cell.Tag is DateTime dt)
            {
                _selectedCalendarDate = dt.Date;
                _calendarDisplayDate = dt.Date;
                _viewModel.SelectedTaskDate = dt.Date;

                // If Task Editor is currently open, also update the task's date to the clicked calendar date!
                if (TaskEditorPanel != null && TaskEditorPanel.Visibility == Visibility.Visible)
                {
                    _editingTaskDate = dt.Date;
                    UpdateTaskEditorDateTimeUi();
                }

                RenderCalendar();
                RenderTimelineBar();
                ShowHudFeedback($"📅 {dt:ddd, d MMM}");
            }
        }

        private void RenderTimelineBar()
        {
            if (TimelinePillsCanvas == null || TimelineCurrentTimeLine == null || TimelineTrackHost == null) return;

            double trackWidth = TimelineTrackHost.ActualWidth > 20 ? TimelineTrackHost.ActualWidth : 186.0;
            TimelinePillsCanvas.Width = trackWidth;
            TimelinePillsCanvas.Children.Clear();

            foreach (var task in _viewModel.FilteredTodoTasks)
            {
                double startRatio = Math.Clamp(task.StartHour / 24.0, 0.0, 0.96);
                double durRatio = Math.Clamp(task.DurationHours / 24.0, 0.03, 1.0 - startRatio);

                double left = startRatio * trackWidth;
                double width = Math.Max(6.0, durRatio * trackWidth);

                Brush pillBrush;
                try
                {
                    pillBrush = (Brush)new BrushConverter().ConvertFromString(task.CategoryColor)!;
                }
                catch
                {
                    pillBrush = Brushes.LimeGreen;
                }

                var pill = new Border
                {
                    Width = width,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = pillBrush,
                    Opacity = task.IsCompleted ? 0.45 : 1.0,
                    ToolTip = $"{task.Title} ({task.DisplaySchedule})"
                };

                Canvas.SetLeft(pill, left);
                Canvas.SetTop(pill, 3);
                TimelinePillsCanvas.Children.Add(pill);
            }

            // Position current time white line
            var now = DateTime.Now;
            double nowHours = now.Hour + now.Minute / 60.0;
            double nowX = Math.Clamp((nowHours / 24.0) * (trackWidth - 2.0), 0.0, trackWidth - 2.0);
            TimelineCurrentTimeLine.Margin = new Thickness(nowX, 0, 0, 0);
            TimelineCurrentTimeLine.ToolTip = $"Now ({now:HH:mm})";
        }

        #endregion

        #region Slide 3: Notification Center & Live Toast System

        private void OnNotificationCardClicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement card && card.DataContext is NotificationItem item)
            {
                _viewModel.MarkNotificationRead(item);
            }
        }

        private void OnCompactToastBodyClicked(object sender, MouseButtonEventArgs e)
        {
            // Clicking the compact toast opens Notification Center (Slide 3)
            _toastAutoDismissTimer.Stop();
            _isShowingCompactToast = false;
            CompactToastContent.Visibility = Visibility.Collapsed;
            CompactStandardContent.Visibility = Visibility.Visible;
            NavigateToSlide(3, immediate: true);
            ExpandIsland();
        }

        public void ShowNotificationToast(string appName, string title, string message, string accentHex, bool addToCollection = true)
        {
            if (addToCollection)
            {
                _viewModel.AddNotification(appName, title, message, accentHex);
            }

            // If Compact: Expand capsule to Width 350, Height 60 for 3.5 seconds
            if (!_isExpanded)
            {
                _isShowingCompactToast = true;

                // Configure Toast Card UI
                CompactToastSender.Text = appName;
                CompactToastMessage.Text = $"{title}: {message}";
                CompactToastAppInitial.Text = !string.IsNullOrEmpty(appName) ? appName[0].ToString().ToUpper() : "N";

                Color parsedAccent;
                try
                {
                    parsedAccent = (Color)ColorConverter.ConvertFromString(accentHex);
                }
                catch
                {
                    parsedAccent = Color.FromRgb(48, 209, 88);
                }

                CompactToastBadgeBorder.Background = new SolidColorBrush(Color.FromArgb(45, parsedAccent.R, parsedAccent.G, parsedAccent.B));
                CompactToastAppInitial.Foreground = new SolidColorBrush(parsedAccent);

                // Set Ambient Glow with accentHex
                AmbientBottomGlow.Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop(Colors.Transparent, 0.0),
                        new GradientStop(parsedAccent, 1.0)
                    }
                };
                AmbientBottomGlow.Visibility = Visibility.Visible;
                AmbientBottomGlow.Opacity = 0.85;

                CompactStandardContent.Visibility = Visibility.Collapsed;
                CompactToastContent.Visibility = Visibility.Visible;

                var widthAnim = new DoubleAnimation(MainIslandBorder.ActualWidth, 350, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut }
                };
                var heightAnim = new DoubleAnimation(MainIslandBorder.ActualHeight, 60, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut }
                };

                MainIslandBorder.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
                MainIslandBorder.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);

                _toastAutoDismissTimer.Stop();
                _toastAutoDismissTimer.Start();
            }
            else
            {
                ShowHudFeedback($"🔔 {appName}: {title}");
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            }
        }

        private void DismissCompactToast()
        {
            if (!_isShowingCompactToast) return;

            _isShowingCompactToast = false;
            _toastAutoDismissTimer.Stop();

            CompactToastContent.Visibility = Visibility.Collapsed;
            CompactStandardContent.Visibility = Visibility.Visible;
            if (!(_isExpanded && _currentSlideIndex == 0 && _isLyricsOpen))
            {
                AmbientBottomGlow.Visibility = Visibility.Collapsed;
            }

            if (_isExpanded) return;

            double compactWidth = _viewModel.IsPomoRunning ? 265.0 : 240.0;
            var widthAnim = new DoubleAnimation(MainIslandBorder.ActualWidth, compactWidth, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut }
            };
            var heightAnim = new DoubleAnimation(MainIslandBorder.ActualHeight, 38, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut }
            };

            MainIslandBorder.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
            MainIslandBorder.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
        }

        private void OnDismissToastClicked(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            DismissCompactToast();
        }

        private async void OnTestToastClicked(object sender, RoutedEventArgs e)
        {
            // Generate a live notification from real system status (Battery / Media / Todo / Pomodoro)
            string app;
            string title;
            string message;
            string color;

            if (_viewModel.MediaInfo.HasActiveSession && !string.IsNullOrWhiteSpace(_viewModel.MediaInfo.Title) && _viewModel.MediaInfo.Title != "No Media Playing")
            {
                app = "Now Playing";
                title = _viewModel.MediaInfo.Title;
                message = _viewModel.MediaInfo.Artist;
                color = "#FF2D55";
            }
            else if (_viewModel.FilteredTodoTasks.Count > 0)
            {
                var nextTask = _viewModel.FilteredTodoTasks.FirstOrDefault(t => !t.IsCompleted) ?? _viewModel.FilteredTodoTasks[0];
                app = "TodoList";
                title = nextTask.Title;
                message = $"{nextTask.DisplaySchedule} ({_viewModel.TodoProgressText} selesai)";
                color = "#30D158";
            }
            else
            {
                app = "System Status";
                title = $"Battery {_viewModel.BatteryText}";
                message = $"CPU {_viewModel.CpuText} • RAM {_viewModel.RamText} • Vol {_viewModel.VolumeText}";
                color = "#0A84FF";
            }

            _viewModel.AddNotification(app, title, message, color);

            if (_isExpanded)
            {
                await CollapseIslandAsync();
            }
            ShowNotificationToast(app, title, message, color, addToCollection: false);
        }

        private void InitWindowsNotificationListener()
        {
            try
            {
                var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
                if (listener != null)
                {
                    Task.Run(async () =>
                    {
                        try
                        {
                            var status = await listener.RequestAccessAsync();
                            if (status == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed)
                            {
                                Debug.WriteLine("[UserNotificationListener] Access granted for Windows notifications.");
                                await Dispatcher.InvokeAsync(StartWindowsNotificationPolling);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[UserNotificationListener] Access request error: {ex.Message}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[InitWindowsNotificationListener] Note: {ex.Message}");
            }
        }

        private void StartWindowsNotificationPolling()
        {
            _windowsNotifPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _windowsNotifPollTimer.Tick += async (s, e) => await PollWindowsNotificationsAsync();
            _windowsNotifPollTimer.Start();
            _ = PollWindowsNotificationsAsync();
        }

        private async Task PollWindowsNotificationsAsync()
        {
            try
            {
                var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
                if (listener == null) return;

                var notifs = await listener.GetNotificationsAsync(Windows.UI.Notifications.NotificationKinds.Toast);
                if (notifs == null) return;

                bool isFirstSeed = !_initialNotificationsSeeded;
                _initialNotificationsSeeded = true;

                foreach (var n in notifs)
                {
                    if (!_seenNotificationIds.Add(n.Id)) continue;

                    string appName = "Windows";
                    try
                    {
                        if (n.AppInfo?.DisplayInfo?.DisplayName is { Length: > 0 } dispName)
                        {
                            appName = dispName;
                        }
                    }
                    catch { }

                    var binding = n.Notification?.Visual?.GetBinding(Windows.UI.Notifications.KnownNotificationBindings.ToastGeneric);
                    if (binding != null)
                    {
                        var texts = binding.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                        if (texts.Count > 0)
                        {
                            string title = texts[0];
                            string body = texts.Count > 1 ? string.Join(" ", texts.Skip(1)) : title;
                            if (isFirstSeed)
                            {
                                // Populate Notification Center silently with existing real Windows notifications
                                _viewModel.AddNotification(appName, title, body, "#0A84FF");
                            }
                            else
                            {
                                ShowNotificationToast(appName, title, body, "#0A84FF");
                            }
                        }
                    }
                }
            }
            catch { }
        }

        #endregion

        #region Slide 4: File Drop Tray

        private void OnBrowseFilesClicked(object sender, MouseButtonEventArgs e)
        {
            BrowseAndAddFiles();
        }

        private void OnBrowseFilesButtonClicked(object sender, RoutedEventArgs e)
        {
            BrowseAndAddFiles();
        }

        private void BrowseAndAddFiles()
        {
            try
            {
                _isInlineEditing = true;
                var dlg = new OpenFileDialog
                {
                    Title = "Add Files to WinIsland File Tray",
                    Multiselect = true
                };
                if (dlg.ShowDialog(this) == true && dlg.FileNames.Length > 0)
                {
                    FileShelfService.Instance.AddFiles(dlg.FileNames);
                    UpdateUiStates();
                    ShowHudFeedback($"📁 +{dlg.FileNames.Length} File(s)");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BrowseAndAddFiles] Error: {ex.Message}");
            }
            finally
            {
                _isInlineEditing = false;
            }
        }

        private void OnCopyPngClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                string[] imageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

                // 1. Check if File Tray has an image file -> Copy image bitmap & file drop to Clipboard
                var imageItem = FileShelfService.Instance.Items.FirstOrDefault(i =>
                    !i.IsDirectory && imageExts.Contains(Path.GetExtension(i.FilePath).ToLowerInvariant()) && File.Exists(i.FilePath));

                if (imageItem != null)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(imageItem.FilePath);
                    bmp.EndInit();
                    bmp.Freeze();

                    var dataObj = new DataObject();
                    dataObj.SetImage(bmp);
                    var dropList = new StringCollection { imageItem.FilePath };
                    dataObj.SetFileDropList(dropList);
                    Clipboard.SetDataObject(dataObj, true);

                    ShowHudFeedback("📋 Image Copied!");
                    return;
                }

                // 2. If Clipboard currently has a screenshot/image -> Save it as PNG into File Tray!
                if (Clipboard.ContainsImage())
                {
                    var clipImg = Clipboard.GetImage();
                    if (clipImg != null)
                    {
                        string tempFolder = Path.Combine(Path.GetTempPath(), "WinIsland");
                        Directory.CreateDirectory(tempFolder);
                        string pngPath = Path.Combine(tempFolder, $"Clipboard_{DateTime.Now:yyyyMMdd_HHmmss}.png");

                        using (var fs = new FileStream(pngPath, FileMode.Create))
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(clipImg));
                            encoder.Save(fs);
                        }

                        FileShelfService.Instance.AddFiles(new[] { pngPath });
                        UpdateUiStates();
                        ShowHudFeedback("📋 Saved PNG to Tray!");
                        return;
                    }
                }

                // 3. If File Tray has any files -> Copy all files to Clipboard
                if (FileShelfService.Instance.Items.Count > 0)
                {
                    var dropList = new StringCollection();
                    foreach (var item in FileShelfService.Instance.Items)
                    {
                        if (File.Exists(item.FilePath) || Directory.Exists(item.FilePath))
                        {
                            dropList.Add(item.FilePath);
                        }
                    }
                    if (dropList.Count > 0)
                    {
                        Clipboard.SetFileDropList(dropList);
                        ShowHudFeedback($"📋 Copied {dropList.Count} File(s)!");
                        return;
                    }
                }

                // 4. Fallback to Clipboard history
                if (ClipboardService.Instance.History.Count > 0)
                {
                    ClipboardService.Instance.CopyToClipboard(ClipboardService.Instance.History[0]);
                    ShowHudFeedback("📋 Clipboard Restored!");
                }
                else
                {
                    ShowHudFeedback("📋 Tray & Clipboard Empty");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OnCopyPngClicked] Error: {ex.Message}");
            }
        }

        private void OnZipFilesClicked(object sender, RoutedEventArgs e)
        {
            if (FileShelfService.Instance.Items.Count == 0)
            {
                ShowHudFeedback("📦 Add files first");
                return;
            }

            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string zipPath = Path.Combine(desktop, $"WinIsland_Archive_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

                var snapshot = FileShelfService.Instance.Items.ToList();
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    foreach (var item in snapshot)
                    {
                        if (File.Exists(item.FilePath))
                        {
                            string entryName = item.FileName;
                            int counter = 1;
                            while (!usedNames.Add(entryName))
                            {
                                entryName = $"{Path.GetFileNameWithoutExtension(item.FileName)}_{counter++}{Path.GetExtension(item.FileName)}";
                            }
                            archive.CreateEntryFromFile(item.FilePath, entryName);
                        }
                        else if (Directory.Exists(item.FilePath))
                        {
                            string baseDir = Path.GetDirectoryName(item.FilePath) ?? item.FilePath;
                            foreach (var file in Directory.EnumerateFiles(item.FilePath, "*", SearchOption.AllDirectories))
                            {
                                string relPath = Path.GetRelativePath(baseDir, file);
                                if (usedNames.Add(relPath))
                                {
                                    archive.CreateEntryFromFile(file, relPath);
                                }
                            }
                        }
                    }
                }

                FileShelfService.Instance.AddFiles(new[] { zipPath });
                UpdateUiStates();
                ShowHudFeedback("📦 ZIP Saved to Desktop!");
                ShowNotificationToast("File Tray", "ZIP Archive Created", Path.GetFileName(zipPath), "#BF5AF2");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OnZipFilesClicked] Error: {ex.Message}");
                ShowHudFeedback("⚠️ ZIP Failed");
            }
        }

        #endregion

        #region Conditional UI/UX State Machine (UpdateUiStates)

        public void UpdateUiStates()
        {
            // 1. Media Status
            bool hasActiveMedia = _viewModel.MediaInfo.HasActiveSession &&
                                  !string.IsNullOrWhiteSpace(_viewModel.MediaInfo.Title) &&
                                  _viewModel.MediaInfo.Title != "No Media Playing";
            bool isPlaying = hasActiveMedia && _viewModel.MediaInfo.IsPlaying;

            // Keep playback buttons enabled so user can always wake/control media via SMTC or media keys
            PrevMediaBtn.Opacity = hasActiveMedia ? 1.0 : 0.65;
            PrevMediaBtn.IsEnabled = true;
            PlayPauseMediaBtn.Opacity = hasActiveMedia ? 1.0 : 0.65;
            PlayPauseMediaBtn.IsEnabled = true;
            NextMediaBtn.Opacity = hasActiveMedia ? 1.0 : 0.65;
            NextMediaBtn.IsEnabled = true;

            if (!hasActiveMedia)
            {
                CompactVisualizer.Visibility = Visibility.Collapsed;
                TxtCurrentTime.Text = "00:00";
                TxtRemainingTime.Text = "-00:00";
                ScrubberFill.Width = 0;
                ExpandedVisualizer.Visibility = Visibility.Collapsed;
                SetPlayButtonGlyph(isPlaying: false);
                AlbumArtBorder.Opacity = 0.6;

                if (!_isLyricsOpen && !_isShowingCompactToast)
                {
                    AmbientBottomGlow.Visibility = Visibility.Collapsed;
                }
            }
            else if (!isPlaying)
            {
                CompactVisualizer.Visibility = Visibility.Visible;
                ExpandedVisualizer.Visibility = Visibility.Visible;

                SetVisualizerBarsStatic();
                SetPlayButtonGlyph(isPlaying: false);
                AlbumArtBorder.Opacity = 0.8;

                if (!_isLyricsOpen && !_isShowingCompactToast)
                {
                    AmbientBottomGlow.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                CompactVisualizer.Visibility = Visibility.Visible;
                ExpandedVisualizer.Visibility = Visibility.Visible;

                SetPlayButtonGlyph(isPlaying: true);
                AlbumArtBorder.Opacity = 1.0;

                // Ambient glow when lyrics open and music is playing
                if (_isLyricsOpen && _currentSlideIndex == 0)
                {
                    AmbientBottomGlow.Visibility = Visibility.Visible;
                    AmbientBottomGlow.Opacity = 1.0;
                }
            }

            // 1B. Volume / Mute Icon Status
            if (VolumeIconPath != null)
            {
                if (_viewModel.IsMuted)
                {
                    VolumeIconPath.Stroke = (Brush)new BrushConverter().ConvertFromString("#FF453A")!;
                    VolumeIconPath.Data = Geometry.Parse("M 1,4 H 3.5 L 7,1 V 11 L 3.5,8 H 1 Z M 9,4 L 12,8 M 12,4 L 9,8");
                }
                else
                {
                    VolumeIconPath.Stroke = (Brush)new BrushConverter().ConvertFromString("#8E8E93")!;
                    VolumeIconPath.Data = Geometry.Parse("M 1,4 H 3.5 L 7,1 V 11 L 3.5,8 H 1 Z M 9,3.5 C 10.5,5 10.5,7 9,8.5");
                }
            }

            // 2. Battery Status
            bool isCharging = _viewModel.Telemetry.IsCharging;
            int batteryPercent = _viewModel.Telemetry.BatteryPercent;

            if (isCharging)
            {
                CompactChargingBoltPath.Visibility = Visibility.Visible;
                var greenBrush = (Brush)new BrushConverter().ConvertFromString("#30D158")!;
                var greenBg = (Brush)new BrushConverter().ConvertFromString("#1F30D158")!;
                CompactChargingBoltPath.Stroke = greenBrush;
                CompactChargingBoltPath.Fill = greenBrush;
                CompactBatteryText.Foreground = greenBrush;
                CompactBatteryBadge.Background = greenBg;
            }
            else if (batteryPercent <= 20)
            {
                CompactChargingBoltPath.Visibility = Visibility.Collapsed;
                var redBrush = (Brush)new BrushConverter().ConvertFromString("#FF453A")!;
                var redBg = (Brush)new BrushConverter().ConvertFromString("#1FFF453A")!;
                CompactBatteryText.Foreground = redBrush;
                CompactBatteryBadge.Background = redBg;
            }
            else
            {
                CompactChargingBoltPath.Visibility = Visibility.Collapsed;
                var neutralBrush = (Brush)new BrushConverter().ConvertFromString("#EBEBF5")!;
                var neutralBg = (Brush)new BrushConverter().ConvertFromString("#12FFFFFF")!;
                CompactBatteryText.Foreground = neutralBrush;
                CompactBatteryBadge.Background = neutralBg;
            }

            // 3. File Shelf Status
            int filesCount = FileShelfService.Instance.Items.Count;
            if (filesCount == 0)
            {
                EmptyFilesText.Text = "Drop files here to hold (or click to browse)";
                EmptyFilesView.Visibility = Visibility.Visible;
                FilesListScrollViewer.Visibility = Visibility.Collapsed;
            }
            else
            {
                EmptyFilesView.Visibility = Visibility.Collapsed;
                FilesListScrollViewer.Visibility = Visibility.Visible;
            }

            // 4. Pomodoro Running Status
            bool isPomodoroRunning = _viewModel.IsPomoRunning;
            if (isPomodoroRunning)
            {
                CompactPomodoroBadge.Visibility = Visibility.Visible;
                PomodoroPlayGlyph.Data = Geometry.Parse("M 4,3.5 H 6 V 11.5 H 4 Z M 8,3.5 H 10 V 11.5 H 8 Z");
            }
            else
            {
                CompactPomodoroBadge.Visibility = Visibility.Collapsed;
                PomodoroPlayGlyph.Data = Geometry.Parse("M 5,3.5 L 11.5,7.5 L 5,11.5 Z");
            }
        }

        private void SetVisualizerBarsStatic()
        {
            var grayBrush = (Brush)new BrushConverter().ConvertFromString("#636366")!;
            MiniBar1.Height = 2;
            MiniBar2.Height = 2;
            MiniBar3.Height = 2;
            MiniBar4.Height = 2;
            MiniBar1.Background = grayBrush;
            MiniBar2.Background = grayBrush;
            MiniBar3.Background = grayBrush;
            MiniBar4.Background = grayBrush;

            EqBar1.Height = 2;
            EqBar2.Height = 2;
            EqBar3.Height = 2;
            EqBar4.Height = 2;
            EqBar5.Height = 2;
            EqBar1.Background = grayBrush;
            EqBar2.Background = grayBrush;
            EqBar3.Background = grayBrush;
            EqBar4.Background = grayBrush;
            EqBar5.Background = grayBrush;
        }

        private void SetPlayButtonGlyph(bool isPlaying)
        {
            if (PlayPauseGlyph == null) return;
            if (isPlaying)
            {
                PlayPauseGlyph.Data = Geometry.Parse("M 4,3.5 H 6 V 11.5 H 4 Z M 8,3.5 H 10 V 11.5 H 8 Z");
            }
            else
            {
                PlayPauseGlyph.Data = Geometry.Parse("M 5,3.5 L 11.5,7.5 L 5,11.5 Z");
            }
        }

        #endregion

        #region Visualizers

        private void OnVisualizerTick(object? sender, EventArgs e)
        {
            bool hasActiveMedia = _viewModel.MediaInfo.HasActiveSession &&
                                  !string.IsNullOrWhiteSpace(_viewModel.MediaInfo.Title) &&
                                  _viewModel.MediaInfo.Title != "No Media Playing";
            bool isPlaying = hasActiveMedia && _viewModel.MediaInfo.IsPlaying;

            if (hasActiveMedia)
            {
                UpdateMediaProgress();
            }

            if (!isPlaying)
            {
                SetVisualizerBarsStatic();
                return;
            }

            float peak = AudioHudService.Instance.ReadPeakLevel();
            double dynamicFactor = Math.Max(0.35, peak * 2.2);

            MiniBar1.Height = Math.Max(2, (3 + _random.NextDouble() * 5) * dynamicFactor);
            MiniBar2.Height = Math.Max(2, (4 + _random.NextDouble() * 7) * dynamicFactor);
            MiniBar3.Height = Math.Max(2, (3 + _random.NextDouble() * 6) * dynamicFactor);
            MiniBar4.Height = Math.Max(2, (2 + _random.NextDouble() * 5) * dynamicFactor);

            var whiteBrush = Brushes.White;
            var greenBrush = (Brush)new BrushConverter().ConvertFromString("#30D158")!;

            MiniBar1.Background = whiteBrush;
            MiniBar2.Background = greenBrush;
            MiniBar3.Background = whiteBrush;
            MiniBar4.Background = greenBrush;

            EqBar1.Height = Math.Max(2, (3 + _random.NextDouble() * 7) * dynamicFactor);
            EqBar2.Height = Math.Max(2, (6 + _random.NextDouble() * 9) * dynamicFactor);
            EqBar3.Height = Math.Max(2, (8 + _random.NextDouble() * 10) * dynamicFactor);
            EqBar4.Height = Math.Max(2, (5 + _random.NextDouble() * 8) * dynamicFactor);
            EqBar5.Height = Math.Max(2, (3 + _random.NextDouble() * 6) * dynamicFactor);

            EqBar1.Background = whiteBrush;
            EqBar2.Background = whiteBrush;
            EqBar3.Background = greenBrush;
            EqBar4.Background = whiteBrush;
            EqBar5.Background = whiteBrush;
        }

        #endregion

        #region Interactive Scrubber Logic

        private bool _isScrubbing;

        private void OnScrubberMouseEnter(object sender, MouseEventArgs e)
        {
            ScrubberTrackBackground.Height = 5;
            ScrubberFill.Background = Brushes.White;
        }

        private void OnScrubberMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isScrubbing)
            {
                ScrubberTrackBackground.Height = 4;
                ScrubberFill.Background = Brushes.White;
            }
        }

        private void OnScrubberMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_viewModel.MediaInfo.HasActiveSession || _viewModel.MediaInfo.Duration <= TimeSpan.Zero)
                return;

            _isScrubbing = true;
            ScrubberTrackContainer.CaptureMouse();
            ScrubberTrackBackground.Height = 5;
            UpdateScrubPosition(e.GetPosition(ScrubberTrackContainer).X);
        }

        private void OnScrubberMouseMove(object sender, MouseEventArgs e)
        {
            if (_isScrubbing)
            {
                UpdateScrubPosition(e.GetPosition(ScrubberTrackContainer).X);
            }
        }

        private void OnScrubberMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isScrubbing)
            {
                _isScrubbing = false;
                ScrubberTrackContainer.ReleaseMouseCapture();

                if (!ScrubberTrackContainer.IsMouseOver)
                {
                    ScrubberTrackBackground.Height = 4;
                }

                double trackWidth = ScrubberTrackContainer.ActualWidth;
                if (trackWidth > 0 && _viewModel.MediaInfo.Duration > TimeSpan.Zero)
                {
                    double mouseX = e.GetPosition(ScrubberTrackContainer).X;
                    double ratio = Math.Clamp(mouseX / trackWidth, 0.0, 1.0);
                    var targetTime = _viewModel.MediaInfo.StartTime + TimeSpan.FromMilliseconds(_viewModel.MediaInfo.Duration.TotalMilliseconds * ratio);
                    _ = MediaControlService.Instance.TrySeekAsync(targetTime);
                    _viewModel.UpdateLyricsForPosition(targetTime);
                }
            }
        }

        private void UpdateScrubPosition(double mouseX)
        {
            double trackWidth = ScrubberTrackContainer.ActualWidth;
            if (trackWidth <= 0 || _viewModel.MediaInfo.Duration <= TimeSpan.Zero) return;

            double ratio = Math.Clamp(mouseX / trackWidth, 0.0, 1.0);
            ScrubberFill.Width = trackWidth * ratio;

            var previewPos = _viewModel.MediaInfo.StartTime + TimeSpan.FromMilliseconds(_viewModel.MediaInfo.Duration.TotalMilliseconds * ratio);
            var previewRemaining = _viewModel.MediaInfo.Duration - (previewPos - _viewModel.MediaInfo.StartTime);
            if (previewRemaining < TimeSpan.Zero) previewRemaining = TimeSpan.Zero;

            TxtCurrentTime.Text = _viewModel.MediaInfo.Duration.TotalHours >= 1
                ? previewPos.ToString(@"hh\:mm\:ss")
                : previewPos.ToString(@"mm\:ss");

            TxtRemainingTime.Text = "-" + (_viewModel.MediaInfo.Duration.TotalHours >= 1
                ? previewRemaining.ToString(@"hh\:mm\:ss")
                : previewRemaining.ToString(@"mm\:ss"));

            _viewModel.UpdateLyricsForPosition(previewPos);
        }

        private void UpdateMediaProgress()
        {
            if (_isScrubbing) return;

            var (currentPos, duration, progress) = MediaControlService.Instance.GetCurrentProgress();

            if (duration > TimeSpan.Zero)
            {
                string curStr = duration.TotalHours >= 1
                    ? currentPos.ToString(@"hh\:mm\:ss")
                    : currentPos.ToString(@"mm\:ss");

                var remaining = duration - currentPos;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

                string remStr = "-" + (duration.TotalHours >= 1
                    ? remaining.ToString(@"hh\:mm\:ss")
                    : remaining.ToString(@"mm\:ss"));

                if (TxtCurrentTime.Text != curStr) TxtCurrentTime.Text = curStr;
                if (TxtRemainingTime.Text != remStr) TxtRemainingTime.Text = remStr;

                double trackWidth = ScrubberTrackContainer.ActualWidth;
                if (trackWidth > 0)
                {
                    ScrubberFill.Width = Math.Clamp(trackWidth * progress, 0, trackWidth);
                }

                _viewModel.UpdateLyricsForPosition(currentPos);
            }
            else
            {
                if (currentPos > TimeSpan.Zero)
                {
                    string curStr = currentPos.ToString(@"mm\:ss");
                    if (TxtCurrentTime.Text != curStr) TxtCurrentTime.Text = curStr;
                }
                else
                {
                    if (TxtCurrentTime.Text != "00:00") TxtCurrentTime.Text = "00:00";
                }
                if (TxtRemainingTime.Text != "-00:00") TxtRemainingTime.Text = "-00:00";
                ScrubberFill.Width = 0;
            }
        }

        #endregion

        #region Minimalist Volume & Brightness Controls via MouseWheel

        private void OnMainIslandMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;

            // If on Pomodoro slide, let wheel adjust Pomodoro minutes
            if (_isExpanded && _currentSlideIndex == 1)
            {
                OnPomodoroTimeMouseWheel(sender, e);
                return;
            }

            // If inside a ScrollViewer on Todo, Notifications, or File Tray slides, allow list scrolling unless Ctrl/Alt held
            if (_isExpanded && (_currentSlideIndex == 2 || _currentSlideIndex == 3 || _currentSlideIndex == 4) &&
                !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // Shift + Wheel -> Brightness
                int delta = e.Delta > 0 ? 5 : -5;
                int newBr = Math.Clamp(BrightnessService.Instance.CurrentBrightness + delta, 0, 100);
                BrightnessService.Instance.SetBrightness(newBr);
                ShowHudFeedback($"☀️ {newBr}%");
            }
            else
            {
                // Wheel -> Volume
                float delta = e.Delta > 0 ? 0.02f : -0.02f;
                AudioHudService.Instance.ChangeVolume(delta);
                int newVol = (int)Math.Round(AudioHudService.Instance.CurrentVolume * 100);
                ShowHudFeedback($"🔊 {newVol}%");
                UpdateUiStates();
            }
            e.Handled = true;
        }

        #endregion

        #region User Interactions (Drag & Drop File Shelf)

        private void OnMainIslandDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                _isDraggingFile = true;
                e.Effects = DragDropEffects.Copy;
                NavigateToSlide(4);
                ExpandIsland();
            }
            e.Handled = true;
        }

        private void OnMainIslandDragLeave(object sender, DragEventArgs e)
        {
            _isDraggingFile = false;
            e.Handled = true;
        }

        private void OnMainIslandDrop(object sender, DragEventArgs e)
        {
            _isDraggingFile = false;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    FileShelfService.Instance.AddFiles(files);
                    NavigateToSlide(4);
                    ExpandIsland();
                    UpdateUiStates();
                    ShowHudFeedback($"📁 +{files.Length} File(s)");
                }
            }
            e.Handled = true;
        }

        private void OnFileCardMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement card && card.DataContext is FileShelfItem item)
            {
                _dragStartPoint = e.GetPosition(this);
                _draggedItem = item;
            }
        }

        private void OnFileCardMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggedItem != null)
            {
                Point currentPos = e.GetPosition(this);
                Vector diff = _dragStartPoint - currentPos;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _isDraggingFile = true;
                    try
                    {
                        var fileDrop = new DataObject(DataFormats.FileDrop, new string[] { _draggedItem.FilePath });
                        DragDrop.DoDragDrop(this, fileDrop, DragDropEffects.Copy);
                    }
                    finally
                    {
                        _isDraggingFile = false;
                        _draggedItem = null;
                    }
                }
            }
        }

        private void OnClearFilesClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.ClearFilesCommand.Execute(null);
            UpdateUiStates();
            ShowHudFeedback("🗑️ Tray Cleared");
        }

        #endregion

        #region Fullscreen Monitoring

        private void OnFullscreenCheck(object? sender, EventArgs e)
        {
            if (!_viewModel.Settings.EnableAutoHideFullScreen) return;

            bool isFs = Win32Interop.IsFullScreenAppRunning();
            if (isFs && !_isHiddenByFullscreen)
            {
                _isHiddenByFullscreen = true;
                _viewModel.HideIsland();
                Visibility = Visibility.Collapsed;
            }
            else if (!isFs && _isHiddenByFullscreen)
            {
                _isHiddenByFullscreen = false;
                Visibility = Visibility.Visible;
                _viewModel.RestoreFromHidden();
            }
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            _hoverTimer.Stop();
            _collapseWatchdogTimer.Stop();
            _fullscreenCheckTimer.Stop();
            _visualizerTimer.Stop();
            _ghostModeTimer.Stop();
            _rightClickHoldTimer.Stop();
            _toastAutoDismissTimer.Stop();
            _hudFeedbackTimer.Stop();
            _windowsNotifPollTimer?.Stop();
            _trayService?.Dispose();
            _viewModel.HoverController.Dispose();
            AudioHudService.Instance.Dispose();
            ClipboardService.Instance.Dispose();
            TelemetryService.Instance.Dispose();
            base.OnClosed(e);
        }
    }
}
