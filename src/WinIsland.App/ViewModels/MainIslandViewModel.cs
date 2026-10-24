using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinIsland.App.Core;
using WinIsland.App.Services;

namespace WinIsland.App.ViewModels
{
    public enum IslandState
    {
        Compact,
        Expanded,
        Hidden
    }

    public enum ActiveTab
    {
        Media,
        FileShelf,
        QuickTools,
        Settings
    }

    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute) : this(_ => execute(), null) { }

        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object? parameter) => _execute(parameter);

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }

    public class MainIslandViewModel : INotifyPropertyChanged
    {
        public SpringAnimator Animator { get; }
        public HoverIntentController HoverController { get; }

        private IslandState _currentState = IslandState.Compact;
        public IslandState CurrentState
        {
            get => _currentState;
            set { _currentState = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsExpanded)); OnPropertyChanged(nameof(IsCompact)); }
        }

        public bool IsExpanded => CurrentState == IslandState.Expanded;
        public bool IsCompact => CurrentState == IslandState.Compact;

        private ActiveTab _currentTab = ActiveTab.Media;
        public ActiveTab CurrentTab
        {
            get => _currentTab;
            set { _currentTab = value; OnPropertyChanged(); }
        }

        // Animated Dimensions
        public double IslandWidth => Animator.Width.CurrentValue;
        public double IslandHeight => Animator.Height.CurrentValue;
        public double IslandCornerRadius => Animator.CornerRadius.CurrentValue;
        public double IslandOpacity => Animator.Opacity.CurrentValue;
        public double IslandOffsetY => Animator.OffsetY.CurrentValue;

        // Current Clock
        private string _currentTimeString = DateTime.Now.ToString("HH:mm");
        public string CurrentTimeString
        {
            get => _currentTimeString;
            set { _currentTimeString = value; OnPropertyChanged(); }
        }

        // Media Info
        private MediaTrackInfo _mediaInfo = new();
        public MediaTrackInfo MediaInfo
        {
            get => _mediaInfo;
            set { _mediaInfo = value; OnPropertyChanged(); }
        }

        // Audio HUD
        private float _volumePercent = 0.5f;
        public float VolumePercent
        {
            get => _volumePercent;
            set
            {
                _volumePercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VolumeText));
            }
        }
        public string VolumeText => $"{(int)(VolumePercent * 100)}%";

        private bool _isMuted;
        public bool IsMuted
        {
            get => _isMuted;
            set { _isMuted = value; OnPropertyChanged(); }
        }

        private float _audioPeak;
        public float AudioPeak
        {
            get => _audioPeak;
            set { _audioPeak = value; OnPropertyChanged(); }
        }

        // Telemetry
        private TelemetryData _telemetry = new();
        public TelemetryData Telemetry
        {
            get => _telemetry;
            set
            {
                _telemetry = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CpuText));
                OnPropertyChanged(nameof(RamText));
                OnPropertyChanged(nameof(BatteryText));
                OnPropertyChanged(nameof(IsChargingGlowVisible));
            }
        }

        public string CpuText => $"{(int)Telemetry.CpuUsagePercent}%";
        public string RamText => $"{(int)Telemetry.RamUsagePercent}%";
        public string BatteryText => $"{Telemetry.BatteryPercent}%";
        public bool IsChargingGlowVisible => Telemetry.IsCharging;

        // Pomodoro Timer
        private DispatcherTimer? _pomoTimer;
        private int _pomoSecondsRemaining = 25 * 60;
        private bool _isPomoRunning;
        public bool IsPomoRunning
        {
            get => _isPomoRunning;
            set { _isPomoRunning = value; OnPropertyChanged(); }
        }
        public string PomoTimeRemaining => $"{_pomoSecondsRemaining / 60:D2}:{_pomoSecondsRemaining % 60:D2}";

        // File Shelf
        public ObservableCollection<FileShelfItem> FileItems => FileShelfService.Instance.Items;

        // Clipboard
        public ObservableCollection<ClipboardItem> ClipboardItems => ClipboardService.Instance.History;

        // Settings Reference
        public AppSettings Settings => SettingsManager.Instance.Current;

        // Commands
        public ICommand TogglePlayPauseCommand { get; }
        public ICommand NextTrackCommand { get; }
        public ICommand PrevTrackCommand { get; }
        public ICommand ToggleMuteCommand { get; }
        public ICommand SwitchTabCommand { get; }
        public ICommand TogglePomoCommand { get; }
        public ICommand ResetPomoCommand { get; }
        public ICommand SetPomoPresetCommand { get; }
        public ICommand CopyClipboardItemCommand { get; }
        public ICommand RemoveFileItemCommand { get; }
        public ICommand ClearFilesCommand { get; }
        public ICommand ExpandCommand { get; }
        public ICommand CollapseCommand { get; }

        public MainIslandViewModel()
        {
            Animator = new SpringAnimator(180, 34, 17, 1.0, 0);
            Animator.Updated += _ =>
            {
                OnPropertyChanged(nameof(IslandWidth));
                OnPropertyChanged(nameof(IslandHeight));
                OnPropertyChanged(nameof(IslandCornerRadius));
                OnPropertyChanged(nameof(IslandOpacity));
                OnPropertyChanged(nameof(IslandOffsetY));
            };

            HoverController = new HoverIntentController
            {
                HoverDelayMs = Settings.HoverDelayMs,
                CollapseDelayMs = Settings.CollapseDelayMs,
                RequireAltKey = Settings.RequireAltKey
            };

            HoverController.ExpandRequested += () => Application.Current?.Dispatcher?.Invoke(ExpandToDefault);
            HoverController.CollapseRequested += () => Application.Current?.Dispatcher?.Invoke(CollapseToCompact);
            HoverController.FileDropExpandRequested += () => Application.Current?.Dispatcher?.Invoke(() =>
            {
                CurrentTab = ActiveTab.FileShelf;
                ExpandToDefault();
            });

            // Clock Timer
            var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clockTimer.Tick += (s, e) => CurrentTimeString = DateTime.Now.ToString("HH:mm");
            clockTimer.Start();

            // Commands
            TogglePlayPauseCommand = new RelayCommand(async () => await MediaControlService.Instance.TryTogglePlayPauseAsync());
            NextTrackCommand = new RelayCommand(async () => await MediaControlService.Instance.TrySkipNextAsync());
            PrevTrackCommand = new RelayCommand(async () => await MediaControlService.Instance.TrySkipPreviousAsync());
            ToggleMuteCommand = new RelayCommand(() => AudioHudService.Instance.ToggleMute());
            SwitchTabCommand = new RelayCommand(param =>
            {
                if (param is ActiveTab tab) CurrentTab = tab;
                else if (param is string str && Enum.TryParse<ActiveTab>(str, out var parsedTab)) CurrentTab = parsedTab;
            });

            TogglePomoCommand = new RelayCommand(TogglePomodoro);
            ResetPomoCommand = new RelayCommand(ResetPomodoro);
            SetPomoPresetCommand = new RelayCommand(param =>
            {
                if (int.TryParse(param?.ToString(), out int mins))
                {
                    _pomoSecondsRemaining = mins * 60;
                    IsPomoRunning = false;
                    _pomoTimer?.Stop();
                    OnPropertyChanged(nameof(PomoTimeRemaining));
                }
            });

            CopyClipboardItemCommand = new RelayCommand(param =>
            {
                if (param is ClipboardItem item)
                {
                    ClipboardService.Instance.CopyToClipboard(item);
                }
            });

            RemoveFileItemCommand = new RelayCommand(param =>
            {
                if (param is FileShelfItem item)
                {
                    FileShelfService.Instance.RemoveItem(item);
                }
            });

            ClearFilesCommand = new RelayCommand(() => FileShelfService.Instance.Clear());
            ExpandCommand = new RelayCommand(ExpandToDefault);
            CollapseCommand = new RelayCommand(CollapseToCompact);

            // Connect Services
            InitializeServices();
        }

        public async Task SeekToProgressAsync(double ratio)
        {
            var track = MediaInfo;
            if (track.Duration > TimeSpan.Zero)
            {
                var target = track.StartTime + TimeSpan.FromMilliseconds(track.Duration.TotalMilliseconds * Math.Clamp(ratio, 0.0, 1.0));
                await MediaControlService.Instance.TrySeekAsync(target);
            }
        }

        private void InitializeServices()
        {
            // Media
            MediaControlService.Instance.TrackInfoChanged += track =>
            {
                MediaInfo = track;
            };
            _ = MediaControlService.Instance.InitializeAsync();

            // Audio
            AudioHudService.Instance.VolumeChanged += (vol, muted) =>
            {
                VolumePercent = vol;
                IsMuted = muted;
            };
            VolumePercent = AudioHudService.Instance.CurrentVolume;
            IsMuted = AudioHudService.Instance.IsMuted;

            // Audio Peak Meter animation ticker
            var peakTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            peakTimer.Tick += (s, e) =>
            {
                AudioPeak = AudioHudService.Instance.ReadPeakLevel();
            };
            peakTimer.Start();

            // Telemetry
            TelemetryService.Instance.TelemetryUpdated += data =>
            {
                Telemetry = data;
            };

            // Pomodoro Timer
            _pomoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _pomoTimer.Tick += (s, e) =>
            {
                if (_pomoSecondsRemaining > 0)
                {
                    _pomoSecondsRemaining--;
                    OnPropertyChanged(nameof(PomoTimeRemaining));
                }
                else
                {
                    _pomoTimer.Stop();
                    IsPomoRunning = false;
                    // Play notification beep or sound
                    System.Media.SystemSounds.Beep.Play();
                }
            };
        }

        public void ExpandToDefault()
        {
            CurrentState = IslandState.Expanded;
            // Mode Expanded: 420 x 150, corner radius 24
            Animator.AnimateTo(420, 150, 24, 1.0, 0);
        }

        public void CollapseToCompact()
        {
            CurrentState = IslandState.Compact;
            // Mode Compact: 180 x 34, corner radius 17 (half of height for full pill)
            Animator.AnimateTo(180, 34, 17, 1.0, 0);
        }

        public void HideIsland()
        {
            CurrentState = IslandState.Hidden;
            Animator.AnimateTo(180, 34, 17, 0.0, -40);
        }

        public void RestoreFromHidden()
        {
            CollapseToCompact();
        }

        private void TogglePomodoro()
        {
            if (IsPomoRunning)
            {
                _pomoTimer?.Stop();
                IsPomoRunning = false;
            }
            else
            {
                _pomoTimer?.Start();
                IsPomoRunning = true;
            }
        }

        private void ResetPomodoro()
        {
            _pomoTimer?.Stop();
            IsPomoRunning = false;
            _pomoSecondsRemaining = 25 * 60;
            OnPropertyChanged(nameof(PomoTimeRemaining));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}
