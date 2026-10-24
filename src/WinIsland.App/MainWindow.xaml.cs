using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        private SystemTrayService? _trayService;

        private bool _isExpanded;
        private bool _isTransitioning;
        private bool _isHiddenByFullscreen;
        private bool _isDraggingFile;
        private bool _isGhostModeActive;
        private bool _isRightMouseDown;
        private bool _isShowingCompactToast;

        private int _currentSlideIndex; // 0: Music, 1: Pomodoro, 2: TodoList, 3: Notifications, 4: Hydration, 5: FileDrop
        private bool _isLyricsOpen;
        private bool _isCalendarOpen;

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

            // Connect ViewModel Notifications
            _viewModel.NotificationRequested += (app, title, msg, accent) =>
            {
                Dispatcher.Invoke(() => ShowNotificationToast(app, title, msg, accent));
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
                        e.PropertyName == nameof(MainIslandViewModel.IsPomoRunning))
                    {
                        UpdateUiStates();
                    }

                    if (e.PropertyName == nameof(MainIslandViewModel.PomodoroProgressFraction))
                    {
                        UpdatePomodoroProgressArcs();
                    }
                });
            };

            FileShelfService.Instance.Items.CollectionChanged += (s, e) =>
            {
                Dispatcher.Invoke(UpdateUiStates);
            };

            AudioHudService.Instance.VolumeChanged += (vol, muted) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (VolumeSlider != null && Math.Abs(VolumeSlider.Value - vol * 100) > 1)
                    {
                        VolumeSlider.Value = vol * 100;
                    }
                    if (TxtVolumePercent != null)
                    {
                        TxtVolumePercent.Text = $"{(int)(vol * 100)}%";
                    }
                });
            };

            BrightnessService.Instance.BrightnessChanged += (br) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (BrightnessSlider != null && Math.Abs(BrightnessSlider.Value - br) > 1)
                    {
                        BrightnessSlider.Value = br;
                    }
                    if (TxtBrightnessPercent != null)
                    {
                        TxtBrightnessPercent.Text = $"{br}%";
                    }
                });
            };

            Loaded += OnMainWindowLoaded;
        }

        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            ScrubberTrackContainer.SizeChanged += (s, ev) => UpdateMediaProgress();
            StartPetWalkingAnimation();
            InitBrightnessAndVolume();
            NavigateToSlide(0, immediate: true);
            UpdatePomodoroProgressArcs();
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

            var itemMusic = new MenuItem { Header = "Music Player" };
            itemMusic.Click += (s, e) => { NavigateToSlide(0); ExpandIsland(); };

            var itemPomo = new MenuItem { Header = "Pomodoro Timer" };
            itemPomo.Click += (s, e) => { NavigateToSlide(1); ExpandIsland(); };

            var itemTodo = new MenuItem { Header = "TodoList & Calendar" };
            itemTodo.Click += (s, e) => { NavigateToSlide(2); ExpandIsland(); };

            var itemNotify = new MenuItem { Header = "Notification Center" };
            itemNotify.Click += (s, e) => { NavigateToSlide(3); ExpandIsland(); };

            var itemExit = new MenuItem { Header = "Exit WinIsland" };
            itemExit.Click += (s, e) => Application.Current.Shutdown();

            menu.Items.Add(itemExpand);
            menu.Items.Add(itemCompact);
            menu.Items.Add(new Separator());
            menu.Items.Add(itemMusic);
            menu.Items.Add(itemPomo);
            menu.Items.Add(itemTodo);
            menu.Items.Add(itemNotify);
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

                    // Auto-collapse check for expanded state (after 200ms outside)
                    if (_isExpanded && !_isTransitioning && !_isDraggingFile)
                    {
                        if (!isOverIsland)
                        {
                            _outsideTicks++;
                            if (_outsideTicks >= 2) // 200ms
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
            CollapseIsland();
        }

        public void ExpandIsland() => _ = ExpandIslandAsync();

        public void CollapseIsland() => _ = CollapseIslandAsync();

        /// <summary>
        /// Staggered Expand:
        /// 1. Fade-out CompactView (80ms)
        /// 2. CompactView.Visibility = Collapsed
        /// 3. Morph MainIslandBorder size 220x38 -> TargetWidth x 215, CornerRadius 22 -> 28 with BackEase (260ms)
        /// 4. ExpandedView.Visibility = Visible & Fade-in (120ms)
        /// </summary>
        public async Task ExpandIslandAsync()
        {
            if (_isExpanded || _isTransitioning) return;
            _isTransitioning = true;
            _hoverTimer.Stop();

            var tcs = new TaskCompletionSource<bool>();

            try
            {
                double targetWidth = GetCurrentTargetWidth();

                var fadeOutCompact = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(80));
                fadeOutCompact.Completed += (s, e) =>
                {
                    CompactView.Visibility = Visibility.Collapsed;

                    var backEase = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut };
                    var widthAnim = new DoubleAnimation(220, targetWidth, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                    var heightAnim = new DoubleAnimation(38, 215, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                    var radiusAnim = new DoubleAnimation(22, 28, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };

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
        /// 3. Morph MainIslandBorder size CurrentWidth x 215 -> 220x38, CornerRadius 28 -> 22 with BackEase (260ms)
        /// 4. CompactView.Visibility = Visible & Fade-in (120ms)
        /// </summary>
        public async Task CollapseIslandAsync()
        {
            if (!_isExpanded || _isTransitioning) return;
            _isTransitioning = true;
            _hoverTimer.Stop();

            var tcs = new TaskCompletionSource<bool>();

            try
            {
                double currentWidth = MainIslandBorder.ActualWidth;

                var fadeOutExpanded = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(80));
                fadeOutExpanded.Completed += (s, e) =>
                {
                    ExpandedView.Visibility = Visibility.Collapsed;

                    var backEase = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut };
                    var widthAnim = new DoubleAnimation(currentWidth, 220, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                    var heightAnim = new DoubleAnimation(215, 38, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                    var radiusAnim = new DoubleAnimation(28, 22, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };

                    widthAnim.Completed += (s2, e2) =>
                    {
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
            // Wide Mode (590px): when Music Live Lyrics is open OR when TodoList Calendar Timeline is open
            if ((_currentSlideIndex == 0 && _isLyricsOpen) || (_currentSlideIndex == 2 && _isCalendarOpen))
            {
                return 590.0;
            }
            return 450.0;
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
            NavigateToSlide(_currentSlideIndex - 1);
        }

        private void OnNextSlideClicked(object sender, RoutedEventArgs e)
        {
            NavigateToSlide(_currentSlideIndex + 1);
        }

        private void OnDotClicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && int.TryParse(elem.Tag?.ToString(), out int idx))
            {
                NavigateToSlide(idx);
            }
        }

        private void OnDockBtnClicked(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && int.TryParse(elem.Tag?.ToString(), out int idx))
            {
                NavigateToSlide(idx);
            }
        }

        public void NavigateToSlide(int newIndex, bool immediate = false)
        {
            _currentSlideIndex = (newIndex % 6 + 6) % 6;

            // Update Slide Views Visibility
            SlideMusic.Visibility = _currentSlideIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            SlidePomodoro.Visibility = _currentSlideIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            SlideTodoList.Visibility = _currentSlideIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            SlideNotifications.Visibility = _currentSlideIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
            SlideHydration.Visibility = _currentSlideIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
            SlideFileDrop.Visibility = _currentSlideIndex == 5 ? Visibility.Visible : Visibility.Collapsed;

            // Update Top Left Title Tag
            TopSlideTitle.Text = _currentSlideIndex switch
            {
                0 => "Music",
                1 => "Pomodoro",
                2 => "TodoList",
                3 => "Notify",
                4 => "Control",
                5 => "File Shelf",
                _ => ""
            };

            // Update Pagination Dots (Active is elongated white pill 16x5, inactive 5x5 #48484A)
            UpdatePaginationDots();

            // Update Micro Dock Icons
            UpdateMicroDockColors();

            // Adjust Width if needed (Wide 590 vs Standard 450)
            if (_isExpanded && !immediate)
            {
                AnimateCapsuleWidth(GetCurrentTargetWidth());
            }

            UpdateUiStates();
        }

        private void UpdatePaginationDots()
        {
            Border[] dots = { Dot0, Dot1, Dot2, Dot3, Dot4, Dot5 };
            var activeBrush = Brushes.White;
            var inactiveBrush = (Brush)new BrushConverter().ConvertFromString("#48484A")!;

            for (int i = 0; i < dots.Length; i++)
            {
                if (i == _currentSlideIndex)
                {
                    dots[i].Width = 16;
                    dots[i].Background = activeBrush;
                }
                else
                {
                    dots[i].Width = 5;
                    dots[i].Background = inactiveBrush;
                }
            }
        }

        private void UpdateMicroDockColors()
        {
            var inactiveBrush = (Brush)new BrushConverter().ConvertFromString("#8E8E93")!;

            // 1. Music (Pink/Coral #FF2D55)
            DockIconMusic.Fill = _currentSlideIndex == 0
                ? (Brush)new BrushConverter().ConvertFromString("#FF2D55")!
                : inactiveBrush;

            // 2. Pomodoro (Red/Orange #FF453A)
            DockIconPomodoro.Stroke = _currentSlideIndex == 1
                ? (Brush)new BrushConverter().ConvertFromString("#FF453A")!
                : inactiveBrush;

            // 3. TodoList (Green #30D158)
            DockIconTodoList.Stroke = _currentSlideIndex == 2
                ? (Brush)new BrushConverter().ConvertFromString("#30D158")!
                : inactiveBrush;

            // 4. Notifications (Blue #0A84FF)
            DockIconNotifications.Stroke = _currentSlideIndex == 3
                ? (Brush)new BrushConverter().ConvertFromString("#0A84FF")!
                : inactiveBrush;

            // 5. Hydration (Cyan #5AC8FA)
            DockIconHydration.Stroke = _currentSlideIndex == 4
                ? (Brush)new BrushConverter().ConvertFromString("#5AC8FA")!
                : inactiveBrush;

            // 6. File Drop (Orange #FF9F0A)
            DockIconFileDrop.Stroke = _currentSlideIndex == 5
                ? (Brush)new BrushConverter().ConvertFromString("#FF9F0A")!
                : inactiveBrush;
        }

        private void OnBottomNavMouseEnter(object sender, MouseEventArgs e)
        {
            var fadeOutDots = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(100));
            fadeOutDots.Completed += (s, ev) =>
            {
                PaginationDotsPanel.Visibility = Visibility.Collapsed;
                MicroDockBorder.Visibility = Visibility.Visible;
                var fadeInDock = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120));
                MicroDockBorder.BeginAnimation(UIElement.OpacityProperty, fadeInDock);
            };
            PaginationDotsPanel.BeginAnimation(UIElement.OpacityProperty, fadeOutDots);
        }

        private void OnBottomNavMouseLeave(object sender, MouseEventArgs e)
        {
            var fadeOutDock = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(100));
            fadeOutDock.Completed += (s, ev) =>
            {
                MicroDockBorder.Visibility = Visibility.Collapsed;
                PaginationDotsPanel.Visibility = Visibility.Visible;
                var fadeInDots = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120));
                PaginationDotsPanel.BeginAnimation(UIElement.OpacityProperty, fadeInDots);
            };
            MicroDockBorder.BeginAnimation(UIElement.OpacityProperty, fadeOutDock);
        }

        #endregion

        #region Slide 0: Music Player & Split-View Live Synced Lyrics

        private void OnLyricsToggleClicked(object sender, RoutedEventArgs e)
        {
            _isLyricsOpen = !_isLyricsOpen;

            if (_isLyricsOpen)
            {
                LyricsSplitPanel.Visibility = Visibility.Visible;
                LyricsToggleBorder.Background = (Brush)new BrushConverter().ConvertFromString("#2EFFFFFF")!;
                LyricsQuoteGlyph.Fill = (Brush)new BrushConverter().ConvertFromString("#FF9F0A")!;
            }
            else
            {
                LyricsSplitPanel.Visibility = Visibility.Collapsed;
                LyricsToggleBorder.Background = (Brush)new BrushConverter().ConvertFromString("#14FFFFFF")!;
                LyricsQuoteGlyph.Fill = (Brush)new BrushConverter().ConvertFromString("#8E8E93")!;
            }

            AnimateCapsuleWidth(GetCurrentTargetWidth());
            UpdateUiStates();
        }

        private void OnVolumeIconClicked(object sender, RoutedEventArgs e)
        {
            NavigateToSlide(4); // Navigate to Slide 4 (Control Center with full sliders)
        }

        #endregion

        #region Slide 1: Pomodoro Circular Ring & Flexible Time

        private void UpdatePomodoroProgressArcs()
        {
            double frac = _viewModel.PomodoroProgressFraction;

            // 1. Main Pomodoro Ring (78x78px, Center 39,39, Radius 33)
            PomodoroProgressArc.Data = CreateArcGeometry(new Point(39, 39), 33, frac);

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
            e.Handled = true;
        }

        private void OnPomodoroTimeClicked(object sender, MouseButtonEventArgs e)
        {
            // Toggle between 15m, 25m, 45m, 50m
            int currentMinutes = int.Parse(_viewModel.PomoTimeRemaining.Split(':')[0]);
            int next = currentMinutes switch
            {
                15 => 25,
                25 => 45,
                45 => 50,
                50 => 15,
                _ => 25
            };
            _viewModel.SetPomoDuration(next, true);
            UpdatePomodoroProgressArcs();
        }

        private void OnPomodoroShortBreakClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.SetPomoDuration(5, isFocus: false);
            UpdatePomodoroProgressArcs();
        }

        private void OnPomodoroFocusClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.SetPomoDuration(25, isFocus: true);
            UpdatePomodoroProgressArcs();
        }

        private void OnPomodoroPlayClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.TogglePomoCommand.Execute(null);
            UpdateUiStates();
        }

        private void OnPomodoroResetClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.ResetPomoCommand.Execute(null);
            UpdatePomodoroProgressArcs();
            UpdateUiStates();
        }

        #endregion

        #region Slide 2: TodoList & Expandable Calendar Timeline

        private void OnCalendarToggleClicked(object sender, RoutedEventArgs e)
        {
            _isCalendarOpen = !_isCalendarOpen;

            if (_isCalendarOpen)
            {
                CalendarSplitPanel.Visibility = Visibility.Visible;
                CalBorder.Background = (Brush)new BrushConverter().ConvertFromString("#30D158")!;
                CalGlyph.Stroke = Brushes.Black;
            }
            else
            {
                CalendarSplitPanel.Visibility = Visibility.Collapsed;
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

        private void OnEditTaskClicked(object sender, RoutedEventArgs e)
        {
            // Add a new quick task or toggle status
            _viewModel.AddTaskCommand.Execute(null);
        }

        #endregion

        #region Slide 3: Notification Center & Live Toast System

        public void ShowNotificationToast(string appName, string title, string message, string accentHex)
        {
            _viewModel.AddNotification(appName, title, message, accentHex);

            // If Compact: Expand capsule to Width 340, Height 58 for 3.5 seconds
            if (!_isExpanded)
            {
                _isShowingCompactToast = true;

                // Configure Toast Card UI
                CompactToastSender.Text = appName;
                CompactToastMessage.Text = $"{title}: {message}";
                CompactToastAppInitial.Text = !string.IsNullOrEmpty(appName) ? appName[0].ToString().ToUpper() : "N";

                var accentBrush = (Brush)new BrushConverter().ConvertFromString(accentHex)!;
                CompactToastBadgeBorder.Background = new SolidColorBrush(Color.FromArgb(40, 48, 209, 88)); // Soft tint
                CompactToastAppInitial.Foreground = accentBrush;

                // Set Ambient Glow with accentHex
                AmbientGlowBorder.Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 1),
                    EndPoint = new Point(0.5, 0),
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop((Color)ColorConverter.ConvertFromString(accentHex), 0.0),
                        new GradientStop(Color.FromArgb(50, 0, 0, 0), 0.6),
                        new GradientStop(Colors.Transparent, 1.0)
                    }
                };
                AmbientGlowBorder.Visibility = Visibility.Visible;

                CompactStandardContent.Visibility = Visibility.Collapsed;
                CompactToastContent.Visibility = Visibility.Visible;

                var widthAnim = new DoubleAnimation(MainIslandBorder.ActualWidth, 340, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut }
                };
                var heightAnim = new DoubleAnimation(MainIslandBorder.ActualHeight, 58, TimeSpan.FromMilliseconds(220))
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
                // If Expanded: briefly show ambient glow and ping audio
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            }
        }

        private void DismissCompactToast()
        {
            if (!_isShowingCompactToast || _isExpanded) return;

            _isShowingCompactToast = false;
            _toastAutoDismissTimer.Stop();

            CompactToastContent.Visibility = Visibility.Collapsed;
            CompactStandardContent.Visibility = Visibility.Visible;
            AmbientGlowBorder.Visibility = Visibility.Collapsed;

            var widthAnim = new DoubleAnimation(MainIslandBorder.ActualWidth, 220, TimeSpan.FromMilliseconds(220))
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
            DismissCompactToast();
        }

        private void OnTestToastClicked(object sender, RoutedEventArgs e)
        {
            string[] apps = { "WhatsApp", "Discord", "Pomodoro" };
            string[] titles = { "Sarah Miller", "Dev Channel", "Focus Session" };
            string[] messages = { "Hey, check the new Dynamic Island build!", "PR #42 merged into main with 0 errors", "Time for a 5-minute break!" };
            string[] colors = { "#30D158", "#0A84FF", "#FF3B30" };

            int choice = _random.Next(apps.Length);
            ShowNotificationToast(apps[choice], titles[choice], messages[choice], colors[choice]);
        }

        private void InitWindowsNotificationListener()
        {
            try
            {
                // Safe check for Windows UserNotificationListener
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

        #endregion

        #region Slide 4: Hydration & Controls

        private void OnHydrationClicked(object sender, MouseButtonEventArgs e)
        {
            _viewModel.LogHydrationCommand.Execute(null);
        }

        private void OnCameraMirrorClicked(object sender, RoutedEventArgs e)
        {
            // Privacy Camera Mirror: trigger notification preview
            ShowNotificationToast("Camera", "Privacy Shutter", "Camera mirror active for 10 seconds", "#5AC8FA");
        }

        #endregion

        #region Slide 5: File Drop Tray

        private void OnCopyPngClicked(object sender, RoutedEventArgs e)
        {
            if (ClipboardService.Instance.History.Count > 0)
            {
                var first = ClipboardService.Instance.History[0];
                ClipboardService.Instance.CopyToClipboard(first);
            }
        }

        private void OnZipFilesClicked(object sender, RoutedEventArgs e)
        {
            if (FileShelfService.Instance.Items.Count == 0) return;

            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string zipPath = Path.Combine(desktop, $"WinIsland_Archive_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

                using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    foreach (var item in FileShelfService.Instance.Items)
                    {
                        if (File.Exists(item.FilePath))
                        {
                            archive.CreateEntryFromFile(item.FilePath, item.FileName);
                        }
                    }
                }

                ShowNotificationToast("File Shelf", "ZIP Archive Created", Path.GetFileName(zipPath), "#FF9F0A");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OnZipFilesClicked] Error: {ex.Message}");
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

            if (!hasActiveMedia)
            {
                CompactVisualizer.Visibility = Visibility.Collapsed;
                MediaProgressBar.Visibility = Visibility.Collapsed;
                TxtCurrentTime.Text = "00:00";
                TxtRemainingTime.Text = "-00:00";
                ScrubberFill.Width = 0;
                ExpandedVisualizer.Visibility = Visibility.Collapsed;
                NoMediaPlaceholder.Visibility = Visibility.Visible;
                ActiveMediaDetails.Visibility = Visibility.Collapsed;

                BtnPrevTrack.Opacity = 0.3;
                BtnPrevTrack.IsEnabled = false;
                BtnPlayPause.Opacity = 0.3;
                BtnPlayPause.IsEnabled = false;
                BtnNextTrack.Opacity = 0.3;
                BtnNextTrack.IsEnabled = false;
                AlbumArtBorder.Opacity = 0.5;

                AmbientGlowBorder.Visibility = Visibility.Collapsed;
            }
            else if (!isPlaying)
            {
                CompactVisualizer.Visibility = Visibility.Visible;
                ExpandedVisualizer.Visibility = Visibility.Visible;
                MediaProgressBar.Visibility = Visibility.Visible;
                NoMediaPlaceholder.Visibility = Visibility.Collapsed;
                ActiveMediaDetails.Visibility = Visibility.Visible;

                SetVisualizerBarsStatic();
                SetPlayButtonGlyph(isPlaying: false);
                AlbumArtBorder.Opacity = 0.75;

                BtnPrevTrack.Opacity = 1.0;
                BtnPrevTrack.IsEnabled = true;
                BtnPlayPause.Opacity = 1.0;
                BtnPlayPause.IsEnabled = true;
                BtnNextTrack.Opacity = 1.0;
                BtnNextTrack.IsEnabled = true;

                AmbientGlowBorder.Visibility = Visibility.Collapsed;
            }
            else
            {
                CompactVisualizer.Visibility = Visibility.Visible;
                ExpandedVisualizer.Visibility = Visibility.Visible;
                MediaProgressBar.Visibility = Visibility.Visible;
                NoMediaPlaceholder.Visibility = Visibility.Collapsed;
                ActiveMediaDetails.Visibility = Visibility.Visible;

                SetPlayButtonGlyph(isPlaying: true);
                AlbumArtBorder.Opacity = 1.0;

                BtnPrevTrack.Opacity = 1.0;
                BtnPrevTrack.IsEnabled = true;
                BtnPlayPause.Opacity = 1.0;
                BtnPlayPause.IsEnabled = true;
                BtnNextTrack.Opacity = 1.0;
                BtnNextTrack.IsEnabled = true;

                // Ambient glow when lyrics open and music is playing
                AmbientGlowBorder.Visibility = _isLyricsOpen && _currentSlideIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                EmptyFilesText.Text = "Drop files here to hold";
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

        #region Visualizers & Pet Animation

        private void StartPetWalkingAnimation()
        {
            var walkAnim = new DoubleAnimation
            {
                From = 0,
                To = 390,
                Duration = TimeSpan.FromSeconds(10),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            PetWalkTransform.BeginAnimation(TranslateTransform.XProperty, walkAnim);
        }

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

        #region Minimalist Volume & Brightness Controls

        private void InitBrightnessAndVolume()
        {
            float vol = AudioHudService.Instance.CurrentVolume;
            if (VolumeSlider != null) VolumeSlider.Value = vol * 100;
            if (TxtVolumePercent != null) TxtVolumePercent.Text = $"{(int)(vol * 100)}%";

            int br = BrightnessService.Instance.CurrentBrightness;
            if (BrightnessSlider != null) BrightnessSlider.Value = br;
            if (TxtBrightnessPercent != null) TxtBrightnessPercent.Text = $"{br}%";
        }

        private void OnVolumeSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtVolumePercent != null)
            {
                int val = (int)e.NewValue;
                TxtVolumePercent.Text = $"{val}%";
                AudioHudService.Instance.SetVolume(val / 100.0f);
            }
        }

        private void OnBrightnessSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int val = (int)e.NewValue;
            if (TxtBrightnessPercent != null) TxtBrightnessPercent.Text = $"{val}%";
            BrightnessService.Instance.SetBrightness(val);
        }

        private void OnMainIslandMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // Shift + Wheel -> Brightness
                double delta = e.Delta > 0 ? 5 : -5;
                int newBr = Math.Clamp(BrightnessService.Instance.CurrentBrightness + (int)delta, 0, 100);
                BrightnessService.Instance.SetBrightness(newBr);
                if (BrightnessSlider != null) BrightnessSlider.Value = newBr;
                if (TxtBrightnessPercent != null) TxtBrightnessPercent.Text = $"{newBr}%";
            }
            else
            {
                // Wheel -> Volume
                float delta = e.Delta > 0 ? 0.02f : -0.02f;
                AudioHudService.Instance.ChangeVolume(delta);
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
                NavigateToSlide(5);
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
                    NavigateToSlide(5);
                    ExpandIsland();
                    UpdateUiStates();
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
            _trayService?.Dispose();
            _viewModel.HoverController.Dispose();
            AudioHudService.Instance.Dispose();
            ClipboardService.Instance.Dispose();
            TelemetryService.Instance.Dispose();
            base.OnClosed(e);
        }
    }
}