using System;
using System.Diagnostics;
using System.IO;
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
        private SystemTrayService? _trayService;

        private bool _isExpanded;
        private bool _isTransitioning;
        private bool _isHiddenByFullscreen;
        private bool _isDraggingFile;
        private int _outsideTicks;
        private bool _isInitializingSettings = true;
        private Point _dragStartPoint;
        private FileShelfItem? _draggedItem;
        private readonly Random _random = new();

        public static readonly DependencyProperty CapsuleCornerRadiusProperty =
            DependencyProperty.Register(nameof(CapsuleCornerRadius), typeof(double), typeof(MainWindow),
                new PropertyMetadata(20.0, (d, e) =>
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

            // 3. Fullscreen monitoring timer (Auto-hide in games/videos)
            _fullscreenCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _fullscreenCheckTimer.Tick += OnFullscreenCheck;
            _fullscreenCheckTimer.Start();

            // 4. Audio Equalizer ticker (Dynamic bounce on visualizer bars)
            _visualizerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _visualizerTimer.Tick += OnVisualizerTick;
            _visualizerTimer.Start();

            // Hook ViewModel events & Services
            _viewModel.HoverController.ExpandRequested += () => Dispatcher.Invoke(() => _ = ExpandIslandAsync());
            _viewModel.HoverController.CollapseRequested += () => Dispatcher.Invoke(() => _ = CollapseIslandAsync());

            _viewModel.PropertyChanged += (s, e) =>
            {
                Dispatcher.Invoke(UpdateUiStates);
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
                    if (SettingsBrightnessSlider != null && Math.Abs(SettingsBrightnessSlider.Value - br) > 1)
                    {
                        SettingsBrightnessSlider.Value = br;
                    }
                    if (TxtBrightnessPercent != null)
                    {
                        TxtBrightnessPercent.Text = $"{br}%";
                    }
                    if (TxtSettingsBrightnessPercent != null)
                    {
                        TxtSettingsBrightnessPercent.Text = $"{br}%";
                    }
                });
            };

            Loaded += OnMainWindowLoaded;
        }

        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            ScrubberTrackContainer.SizeChanged += (s, ev) => UpdateMediaProgress();
            StartPetWalkingAnimation();
            LoadCurrentSettings();
            InitBrightnessAndVolume();
            UpdateUiStates();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Center window horizontally at top of screen with 0 top coordinate (Margin="0,12,0,0" provides floating gap)
            double screenW = SystemParameters.PrimaryScreenWidth;
            Left = (screenW - Width) / 2.0;
            Top = 0.0;

            // Make borderless topmost overlay without stealing focus or Alt-Tab presence
            Win32Interop.MakeOverlayWindow(this);

            // Hook clipboard service
            ClipboardService.Instance.Hook(this);

            // Hook WM_NCHITTEST for transparent click-through outside the capsule
            var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            hwndSource?.AddHook(WndProc);

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

            var itemSettings = new MenuItem { Header = "Settings" };
            itemSettings.Click += (s, e) =>
            {
                SelectTab("Settings");
                ExpandIsland();
            };

            var itemExit = new MenuItem { Header = "Exit WinIsland" };
            itemExit.Click += (s, e) => Application.Current.Shutdown();

            menu.Items.Add(itemExpand);
            menu.Items.Add(itemCompact);
            menu.Items.Add(new Separator());
            menu.Items.Add(itemSettings);
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
            if (msg == Win32Interop.WM_NCHITTEST)
            {
                int x = (short)(lParam.ToInt64() & 0xFFFF);
                int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

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
            return IntPtr.Zero;
        }

        #region Win32 Cursor Watchdog & Hover Navigation

        private void OnMainIslandMouseEnter(object sender, MouseEventArgs e)
        {
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

        private void OnHoverTimerTick(object? sender, EventArgs e)
        {
            _hoverTimer.Stop();
            if (!_isExpanded && !_isTransitioning)
            {
                ExpandIsland();
            }
        }

        private async void OnCollapseWatchdogTick(object? sender, EventArgs e)
        {
            if (!_isExpanded || _isTransitioning || _isDraggingFile)
            {
                _outsideTicks = 0;
                return;
            }

            if (GetCursorPos(out POINT screenPt))
            {
                Point windowPt = PointFromScreen(new Point(screenPt.X, screenPt.Y));
                GeneralTransform transform = MainIslandBorder.TransformToAncestor(this);
                Rect capsuleBounds = transform.TransformBounds(new Rect(0, 0, MainIslandBorder.ActualWidth, MainIslandBorder.ActualHeight));

                // 12px tolerance on all 4 sides (Top, Bottom, Left, Right)
                Rect toleranceBounds = new Rect(
                    capsuleBounds.X - 12,
                    capsuleBounds.Y - 12,
                    capsuleBounds.Width + 24,
                    capsuleBounds.Height + 24
                );

                if (!toleranceBounds.Contains(windowPt))
                {
                    _outsideTicks++;
                    if (_outsideTicks >= 2) // 2 ticks * 100ms = 200ms
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
        /// 3. Morph MainIslandBorder size 210x38 -> 430x240, CornerRadius 20 -> 26 with BackEase (260ms)
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
                var fadeOutCompact = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(80));
                fadeOutCompact.Completed += (s, e) =>
                {
                    CompactView.Visibility = Visibility.Collapsed;

                    var backEase = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
                    var widthAnim = new DoubleAnimation(210, 430, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                    var heightAnim = new DoubleAnimation(38, 240, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                    var radiusAnim = new DoubleAnimation(20, 26, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };

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
        /// 3. Morph MainIslandBorder size 430x240 -> 210x38, CornerRadius 26 -> 20 with BackEase (260ms)
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
                var fadeOutExpanded = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(80));
                fadeOutExpanded.Completed += (s, e) =>
                {
                    ExpandedView.Visibility = Visibility.Collapsed;

                    var backEase = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut };
                    var widthAnim = new DoubleAnimation(430, 210, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                    var heightAnim = new DoubleAnimation(240, 38, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                    var radiusAnim = new DoubleAnimation(26, 20, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };

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

        #endregion

        #region Conditional UI/UX State Machine (UpdateUiStates)

        /// <summary>
        /// Updates the visibility and presentation based on real-time state conditions:
        /// 1. Media Status (_hasActiveMedia & _isPlaying)
        /// 2. Battery Status (_isCharging & _batteryPercent)
        /// 3. File Shelf Status (_droppedFiles.Count)
        /// 4. Tools Status (Pomodoro & Clipboard)
        /// </summary>
        public void UpdateUiStates()
        {
            // --- 1. IF-ELSE STATUS MUSIK ---
            bool hasActiveMedia = _viewModel.MediaInfo.HasActiveSession &&
                                  !string.IsNullOrWhiteSpace(_viewModel.MediaInfo.Title) &&
                                  _viewModel.MediaInfo.Title != "No Media Playing";
            bool isPlaying = hasActiveMedia && _viewModel.MediaInfo.IsPlaying;

            if (!hasActiveMedia)
            {
                // Compact: Hide visualizer cleanly
                CompactVisualizer.Visibility = Visibility.Collapsed;

                // Tab Music:
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
            }
            else if (!isPlaying)
            {
                // Paused:
                CompactVisualizer.Visibility = Visibility.Visible;
                ExpandedVisualizer.Visibility = Visibility.Visible;
                MediaProgressBar.Visibility = Visibility.Visible;
                NoMediaPlaceholder.Visibility = Visibility.Collapsed;
                ActiveMediaDetails.Visibility = Visibility.Visible;

                // Static flat lines (Height = 2, color = #636366)
                SetVisualizerBarsStatic();

                // Play glyph (▶)
                SetPlayButtonGlyph(isPlaying: false);
                AlbumArtBorder.Opacity = 0.75;

                BtnPrevTrack.Opacity = 1.0;
                BtnPrevTrack.IsEnabled = true;
                BtnPlayPause.Opacity = 1.0;
                BtnPlayPause.IsEnabled = true;
                BtnNextTrack.Opacity = 1.0;
                BtnNextTrack.IsEnabled = true;

                UpdateMediaProgress();
            }
            else
            {
                // Playing:
                CompactVisualizer.Visibility = Visibility.Visible;
                ExpandedVisualizer.Visibility = Visibility.Visible;
                MediaProgressBar.Visibility = Visibility.Visible;
                NoMediaPlaceholder.Visibility = Visibility.Collapsed;
                ActiveMediaDetails.Visibility = Visibility.Visible;

                // Pause glyph (⏸)
                SetPlayButtonGlyph(isPlaying: true);
                AlbumArtBorder.Opacity = 1.0;

                BtnPrevTrack.Opacity = 1.0;
                BtnPrevTrack.IsEnabled = true;
                BtnPlayPause.Opacity = 1.0;
                BtnPlayPause.IsEnabled = true;
                BtnNextTrack.Opacity = 1.0;
                BtnNextTrack.IsEnabled = true;

                UpdateMediaProgress();
            }

            // --- 2. IF-ELSE STATUS BATERAI ---
            bool isCharging = _viewModel.Telemetry.IsCharging;
            int batteryPercent = _viewModel.Telemetry.BatteryPercent;

            if (isCharging)
            {
                CompactChargingBoltPath.Visibility = Visibility.Visible;
                ExpandedChargingBoltPath.Visibility = Visibility.Visible;

                var greenBrush = (Brush)new BrushConverter().ConvertFromString("#30D158")!;
                var greenBg = (Brush)new BrushConverter().ConvertFromString("#1F30D158")!;

                CompactChargingBoltPath.Stroke = greenBrush;
                CompactChargingBoltPath.Fill = greenBrush;
                ExpandedChargingBoltPath.Stroke = greenBrush;
                ExpandedChargingBoltPath.Fill = greenBrush;

                CompactBatteryText.Foreground = greenBrush;
                ExpandedBatteryText.Foreground = greenBrush;

                CompactBatteryBadge.Background = greenBg;
                ExpandedBatteryBadge.Background = greenBg;
            }
            else if (batteryPercent <= 20)
            {
                CompactChargingBoltPath.Visibility = Visibility.Collapsed;
                ExpandedChargingBoltPath.Visibility = Visibility.Collapsed;

                var redBrush = (Brush)new BrushConverter().ConvertFromString("#FF453A")!;
                var redBg = (Brush)new BrushConverter().ConvertFromString("#1FFF453A")!;

                CompactBatteryText.Foreground = redBrush;
                ExpandedBatteryText.Foreground = redBrush;

                CompactBatteryBadge.Background = redBg;
                ExpandedBatteryBadge.Background = redBg;
            }
            else
            {
                CompactChargingBoltPath.Visibility = Visibility.Collapsed;
                ExpandedChargingBoltPath.Visibility = Visibility.Collapsed;

                var neutralBrush = (Brush)new BrushConverter().ConvertFromString("#EBEBF5")!;
                var neutralBg = (Brush)new BrushConverter().ConvertFromString("#12FFFFFF")!;

                CompactBatteryText.Foreground = neutralBrush;
                ExpandedBatteryText.Foreground = neutralBrush;

                CompactBatteryBadge.Background = neutralBg;
                ExpandedBatteryBadge.Background = neutralBg;
            }

            // --- 3. IF-ELSE TAB FILES ---
            int filesCount = FileShelfService.Instance.Items.Count;
            if (filesCount == 0)
            {
                ClearFilesButton.Visibility = Visibility.Collapsed;
                EmptyFilesText.Text = "Drop files here to hold";
                EmptyFilesView.Visibility = Visibility.Visible;
                FilesListScrollViewer.Visibility = Visibility.Collapsed;
                FilesTabRadio.Content = "Files";
            }
            else
            {
                ClearFilesButton.Visibility = Visibility.Visible;
                FilesTabRadio.Content = $"Files ({filesCount})";
                EmptyFilesView.Visibility = Visibility.Collapsed;
                FilesListScrollViewer.Visibility = Visibility.Visible;
            }

            // --- 4. IF-ELSE TAB TOOLS (POMODORO & CLIPBOARD) ---
            bool isPomodoroRunning = _viewModel.IsPomoRunning;
            if (isPomodoroRunning)
            {
                PomodoroPresetPanel.Visibility = Visibility.Collapsed;
                CompactPomodoroBadge.Visibility = Visibility.Visible;
                PomodoroPlayGlyph.Data = Geometry.Parse("M 4,3.5 H 6 V 11.5 H 4 Z M 8,3.5 H 10 V 11.5 H 8 Z");
            }
            else
            {
                PomodoroPresetPanel.Visibility = Visibility.Visible;
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
            EqBar6.Height = 2;
            EqBar1.Background = grayBrush;
            EqBar2.Background = grayBrush;
            EqBar3.Background = grayBrush;
            EqBar4.Background = grayBrush;
            EqBar5.Background = grayBrush;
            EqBar6.Background = grayBrush;
        }

        private void SetPlayButtonGlyph(bool isPlaying)
        {
            if (PlayPauseGlyph == null) return;
            if (isPlaying)
            {
                // Pause glyph: two vertical bars (⏸)
                PlayPauseGlyph.Data = Geometry.Parse("M 4,3.5 H 6 V 11.5 H 4 Z M 8,3.5 H 10 V 11.5 H 8 Z");
            }
            else
            {
                // Play glyph: right pointing triangle (▶)
                PlayPauseGlyph.Data = Geometry.Parse("M 5,3.5 L 11.5,7.5 L 5,11.5 Z");
            }
        }

        #endregion

        #region Tab Navigation

        private void OnTabSelected(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton btn && btn.Tag is string tag)
            {
                SelectTab(tag);
            }
        }

        private void SelectTab(string tag)
        {
            MediaTabContent.Visibility = Visibility.Collapsed;
            FileShelfTabContent.Visibility = Visibility.Collapsed;
            QuickToolsTabContent.Visibility = Visibility.Collapsed;
            SettingsTabContent.Visibility = Visibility.Collapsed;

            switch (tag)
            {
                case "Media":
                    MediaTabContent.Visibility = Visibility.Visible;
                    _viewModel.CurrentTab = ActiveTab.Media;
                    MusicTabRadio.IsChecked = true;
                    break;
                case "FileShelf":
                    FileShelfTabContent.Visibility = Visibility.Visible;
                    _viewModel.CurrentTab = ActiveTab.FileShelf;
                    FilesTabRadio.IsChecked = true;
                    break;
                case "QuickTools":
                    QuickToolsTabContent.Visibility = Visibility.Visible;
                    _viewModel.CurrentTab = ActiveTab.QuickTools;
                    ToolsTabRadio.IsChecked = true;
                    break;
                case "Settings":
                    SettingsTabContent.Visibility = Visibility.Visible;
                    _viewModel.CurrentTab = ActiveTab.Settings;
                    SettingsTabRadio.IsChecked = true;
                    break;
            }
            UpdateUiStates();
        }

        #endregion

        #region Visualizers & Pet Animation

        private void StartPetWalkingAnimation()
        {
            var walkAnim = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(9),
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

            var whiteBrush = (Brush)new BrushConverter().ConvertFromString("#FFFFFF")!;
            var greenBrush = (Brush)new BrushConverter().ConvertFromString("#30D158")!;

            MiniBar1.Background = whiteBrush;
            MiniBar2.Background = greenBrush;
            MiniBar3.Background = whiteBrush;
            MiniBar4.Background = greenBrush;

            EqBar1.Height = Math.Max(2, (4 + _random.NextDouble() * 8) * dynamicFactor);
            EqBar2.Height = Math.Max(2, (6 + _random.NextDouble() * 10) * dynamicFactor);
            EqBar3.Height = Math.Max(2, (8 + _random.NextDouble() * 10) * dynamicFactor);
            EqBar4.Height = Math.Max(2, (6 + _random.NextDouble() * 10) * dynamicFactor);
            EqBar5.Height = Math.Max(2, (5 + _random.NextDouble() * 8) * dynamicFactor);
            EqBar6.Height = Math.Max(2, (3 + _random.NextDouble() * 7) * dynamicFactor);

            EqBar1.Background = whiteBrush;
            EqBar2.Background = whiteBrush;
            EqBar3.Background = greenBrush;
            EqBar4.Background = greenBrush;
            EqBar5.Background = whiteBrush;
            EqBar6.Background = whiteBrush;
        }

        #endregion

        #region Interactive Scrubber Logic

        private bool _isScrubbing;

        private void OnScrubberMouseEnter(object sender, MouseEventArgs e)
        {
            ScrubberTrackBackground.Height = 5;
            ScrubberFill.Background = (Brush)new BrushConverter().ConvertFromString("#FFFFFF")!;
        }

        private void OnScrubberMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isScrubbing)
            {
                ScrubberTrackBackground.Height = 4;
                ScrubberFill.Background = (Brush)new BrushConverter().ConvertFromString("#E5E5EA")!;
            }
        }

        private void OnScrubberMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_viewModel.MediaInfo.HasActiveSession || _viewModel.MediaInfo.Duration <= TimeSpan.Zero)
                return;

            _isScrubbing = true;
            ScrubberTrackContainer.CaptureMouse();
            ScrubberTrackBackground.Height = 5;
            ScrubberFill.Background = (Brush)new BrushConverter().ConvertFromString("#FFFFFF")!;

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
                    ScrubberFill.Background = (Brush)new BrushConverter().ConvertFromString("#E5E5EA")!;
                }

                double trackWidth = ScrubberTrackContainer.ActualWidth;
                if (trackWidth > 0 && _viewModel.MediaInfo.Duration > TimeSpan.Zero)
                {
                    double mouseX = e.GetPosition(ScrubberTrackContainer).X;
                    double ratio = Math.Clamp(mouseX / trackWidth, 0.0, 1.0);
                    var targetTime = _viewModel.MediaInfo.StartTime + TimeSpan.FromMilliseconds(_viewModel.MediaInfo.Duration.TotalMilliseconds * ratio);
                    _ = MediaControlService.Instance.TrySeekAsync(targetTime);
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
                if (TxtRemainingTime.Text != "--:--") TxtRemainingTime.Text = "--:--";
                ScrubberFill.Width = 0;
            }
        }

        #endregion

        #region Minimalist Volume & Brightness Controls

        private void InitBrightnessAndVolume()
        {
            // Volume
            float vol = AudioHudService.Instance.CurrentVolume;
            if (VolumeSlider != null) VolumeSlider.Value = vol * 100;
            if (TxtVolumePercent != null) TxtVolumePercent.Text = $"{(int)(vol * 100)}%";

            // Brightness (Read from hardware WMI / DDC/CI)
            int br = BrightnessService.Instance.CurrentBrightness;
            if (BrightnessSlider != null) BrightnessSlider.Value = br;
            if (SettingsBrightnessSlider != null) SettingsBrightnessSlider.Value = br;
            if (TxtBrightnessPercent != null) TxtBrightnessPercent.Text = $"{br}%";
            if (TxtSettingsBrightnessPercent != null) TxtSettingsBrightnessPercent.Text = $"{br}%";
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
            if (TxtSettingsBrightnessPercent != null) TxtSettingsBrightnessPercent.Text = $"{val}%";

            // Keep both sliders in sync
            if (sender == BrightnessSlider && SettingsBrightnessSlider != null && Math.Abs(SettingsBrightnessSlider.Value - val) > 1)
            {
                SettingsBrightnessSlider.Value = val;
            }
            else if (sender == SettingsBrightnessSlider && BrightnessSlider != null && Math.Abs(BrightnessSlider.Value - val) > 1)
            {
                BrightnessSlider.Value = val;
            }

            BrightnessService.Instance.SetBrightness(val);
        }

        private void OnMainIslandMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // Shift + Wheel -> Real-time Hardware Brightness adjustment
                double delta = e.Delta > 0 ? 5 : -5;
                int newBr = Math.Clamp(BrightnessService.Instance.CurrentBrightness + (int)delta, 0, 100);
                BrightnessService.Instance.SetBrightness(newBr);
                if (BrightnessSlider != null) BrightnessSlider.Value = newBr;
                if (SettingsBrightnessSlider != null) SettingsBrightnessSlider.Value = newBr;
                if (TxtBrightnessPercent != null) TxtBrightnessPercent.Text = $"{newBr}%";
                if (TxtSettingsBrightnessPercent != null) TxtSettingsBrightnessPercent.Text = $"{newBr}%";
            }
            else
            {
                // Wheel -> Volume adjustment
                float delta = e.Delta > 0 ? 0.02f : -0.02f;
                AudioHudService.Instance.ChangeVolume(delta);
            }
            e.Handled = true;
        }

        #endregion

        #region User Interactions (Drag & Drop File Shelf, Tools, Settings)

        private void OnMainIslandDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                _isDraggingFile = true;
                e.Effects = DragDropEffects.Copy;
                SelectTab("FileShelf");
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
                    SelectTab("FileShelf");
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

        private void OnPomodoroPlayClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.TogglePomoCommand.Execute(null);
            UpdateUiStates();
        }

        private void OnPomodoroResetClicked(object sender, RoutedEventArgs e)
        {
            _viewModel.ResetPomoCommand.Execute(null);
            UpdateUiStates();
        }

        private void OnClipboardItemClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item)
            {
                _viewModel.CopyClipboardItemCommand.Execute(item);
            }
        }

        private void LoadCurrentSettings()
        {
            var s = SettingsManager.Instance.Current;
            ChkAutoHide.IsChecked = s.EnableAutoHideFullScreen;
            ChkRequireAlt.IsChecked = s.RequireAltKey;
            SliderHoverDelay.Value = s.HoverDelayMs;
            TxtDelayValue.Text = $"{s.HoverDelayMs}ms";
            ChkStartup.IsChecked = s.RunOnStartup;
            _isInitializingSettings = false;
        }

        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializingSettings) return;

            var s = SettingsManager.Instance.Current;
            s.EnableAutoHideFullScreen = ChkAutoHide.IsChecked == true;
            s.RequireAltKey = ChkRequireAlt.IsChecked == true;

            SettingsManager.Instance.SaveSettings(s);
        }

        private void OnSliderDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtDelayValue != null)
            {
                int val = (int)e.NewValue;
                TxtDelayValue.Text = $"{val}ms";
                if (!_isInitializingSettings)
                {
                    var s = SettingsManager.Instance.Current;
                    s.HoverDelayMs = val;
                    SettingsManager.Instance.SaveSettings(s);
                }
            }
        }

        private void OnStartupToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializingSettings) return;

            bool enable = ChkStartup.IsChecked == true;
            var s = SettingsManager.Instance.Current;
            s.RunOnStartup = enable;
            SettingsManager.Instance.SaveSettings(s);

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (key != null)
                {
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
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] Startup registry error: {ex.Message}");
            }
        }

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
            _trayService?.Dispose();
            _viewModel.HoverController.Dispose();
            AudioHudService.Instance.Dispose();
            ClipboardService.Instance.Dispose();
            TelemetryService.Instance.Dispose();
            base.OnClosed(e);
        }
    }
}