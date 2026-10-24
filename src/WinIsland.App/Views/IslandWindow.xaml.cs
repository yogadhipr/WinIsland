using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinIsland.App.Core;
using WinIsland.App.Services;
using WinIsland.App.ViewModels;

namespace WinIsland.App.Views
{
    public partial class IslandWindow : Window
    {
        private readonly MainIslandViewModel _viewModel;
        private readonly DispatcherTimer _hoverTimer;
        private readonly DispatcherTimer _fullscreenCheckTimer;
        private readonly DispatcherTimer _visualizerTimer;
        private SystemTrayService? _trayService;

        private bool _isExpanded;
        private bool _isTransitioning;
        private bool _isHiddenByFullscreen;
        private readonly Random _random = new();

        public static readonly DependencyProperty CapsuleCornerRadiusProperty =
            DependencyProperty.Register(nameof(CapsuleCornerRadius), typeof(double), typeof(IslandWindow),
                new PropertyMetadata(19.0, (d, e) =>
                {
                    if (d is IslandWindow w && w.MainIslandBorder != null)
                    {
                        w.MainIslandBorder.CornerRadius = new CornerRadius((double)e.NewValue);
                    }
                }));

        public double CapsuleCornerRadius
        {
            get => (double)GetValue(CapsuleCornerRadiusProperty);
            set => SetValue(CapsuleCornerRadiusProperty, value);
        }

        public IslandWindow()
        {
            InitializeComponent();

            _viewModel = new MainIslandViewModel();
            DataContext = _viewModel;

            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _hoverTimer.Tick += OnHoverTimerTick;

            _fullscreenCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _fullscreenCheckTimer.Tick += OnFullscreenCheck;
            _fullscreenCheckTimer.Start();

            _visualizerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _visualizerTimer.Tick += OnVisualizerTick;
            _visualizerTimer.Start();

            _viewModel.HoverController.ExpandRequested += () => Dispatcher.Invoke(ExpandIsland);
            _viewModel.HoverController.CollapseRequested += () => Dispatcher.Invoke(CollapseIsland);

            Loaded += OnWindowLoaded;
        }

        private void OnWindowLoaded(object sender, RoutedEventArgs e)
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

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            double screenW = SystemParameters.PrimaryScreenWidth;
            Left = (screenW - Width) / 2.0;
            Top = 8.0;

            Win32Interop.MakeOverlayWindow(this);
            ClipboardService.Instance.Hook(this);

            var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            hwndSource?.AddHook(WndProc);

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

        private void OnCapsuleMouseEnter(object sender, MouseEventArgs e)
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

        private void OnCapsuleMouseLeave(object sender, MouseEventArgs e)
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

        private void OnMinimizeClicked(object sender, RoutedEventArgs e)
        {
            CollapseIsland();
        }

        public void ExpandIsland()
        {
            if (_isExpanded || _isTransitioning) return;
            _isTransitioning = true;
            _hoverTimer.Stop();

            var fadeOutCompact = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(80));
            fadeOutCompact.Completed += (s, e) =>
            {
                CompactView.Visibility = Visibility.Collapsed;

                var backEase = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
                var widthAnim = new DoubleAnimation(248, 430, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                var heightAnim = new DoubleAnimation(38, 190, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                var radiusAnim = new DoubleAnimation(19, 26, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };

                widthAnim.Completed += (s2, e2) =>
                {
                    ExpandedView.Visibility = Visibility.Visible;
                    var fadeInExpanded = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120));
                    fadeInExpanded.Completed += (s3, e3) =>
                    {
                        _isExpanded = true;
                        _isTransitioning = false;
                        _viewModel.CurrentState = IslandState.Expanded;
                    };
                    ExpandedView.BeginAnimation(UIElement.OpacityProperty, fadeInExpanded);
                };

                MainIslandBorder.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
                MainIslandBorder.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
                this.BeginAnimation(CapsuleCornerRadiusProperty, radiusAnim);
            };

            CompactView.BeginAnimation(UIElement.OpacityProperty, fadeOutCompact);
        }

        public void CollapseIsland()
        {
            if (!_isExpanded || _isTransitioning) return;
            _isTransitioning = true;
            _hoverTimer.Stop();

            var fadeOutExpanded = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(80));
            fadeOutExpanded.Completed += (s, e) =>
            {
                ExpandedView.Visibility = Visibility.Collapsed;

                var backEase = new BackEase { Amplitude = 0.2, EasingMode = EasingMode.EaseOut };
                var widthAnim = new DoubleAnimation(430, 248, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                var heightAnim = new DoubleAnimation(190, 38, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };
                var radiusAnim = new DoubleAnimation(26, 19, TimeSpan.FromMilliseconds(260)) { EasingFunction = backEase };

                widthAnim.Completed += (s2, e2) =>
                {
                    CompactView.Visibility = Visibility.Visible;
                    var fadeInCompact = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(120));
                    fadeInCompact.Completed += (s3, e3) =>
                    {
                        _isExpanded = false;
                        _isTransitioning = false;
                        _viewModel.CurrentState = IslandState.Compact;
                    };
                    CompactView.BeginAnimation(UIElement.OpacityProperty, fadeInCompact);
                };

                MainIslandBorder.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
                MainIslandBorder.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
                this.BeginAnimation(CapsuleCornerRadiusProperty, radiusAnim);
            };

            ExpandedView.BeginAnimation(UIElement.OpacityProperty, fadeOutExpanded);
        }

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
                    break;
                case "FileShelf":
                    FileShelfTabContent.Visibility = Visibility.Visible;
                    _viewModel.CurrentTab = ActiveTab.FileShelf;
                    break;
                case "QuickTools":
                    QuickToolsTabContent.Visibility = Visibility.Visible;
                    _viewModel.CurrentTab = ActiveTab.QuickTools;
                    break;
                case "Settings":
                    SettingsTabContent.Visibility = Visibility.Visible;
                    _viewModel.CurrentTab = ActiveTab.Settings;
                    break;
            }
        }

        private void OnVisualizerTick(object? sender, EventArgs e)
        {
            float peak = AudioHudService.Instance.ReadPeakLevel();
            bool isAudioActive = peak > 0.02f || _viewModel.MediaInfo.IsPlaying;

            if (isAudioActive)
            {
                MiniBar1.Height = 3 + _random.NextDouble() * 5;
                MiniBar2.Height = 4 + _random.NextDouble() * 7;
                MiniBar3.Height = 3 + _random.NextDouble() * 6;
                MiniBar4.Height = 2 + _random.NextDouble() * 5;
            }
            else
            {
                MiniBar1.Height = 3;
                MiniBar2.Height = 5;
                MiniBar3.Height = 4;
                MiniBar4.Height = 2;
            }
        }

        private void OnCapsuleMouseWheel(object sender, MouseWheelEventArgs e)
        {
            float delta = e.Delta > 0 ? 0.02f : -0.02f;
            AudioHudService.Instance.ChangeVolume(delta);
            e.Handled = true;
        }

        private void OnCapsuleDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                SelectTab("FileShelf");
                ExpandIsland();
            }
            e.Handled = true;
        }

        private void OnCapsuleDragLeave(object sender, DragEventArgs e)
        {
            e.Handled = true;
        }

        private void OnCapsuleDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    FileShelfService.Instance.AddFiles(files);
                    SelectTab("FileShelf");
                    ExpandIsland();
                }
            }
            e.Handled = true;
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

        protected override void OnClosed(EventArgs e)
        {
            _hoverTimer.Stop();
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
