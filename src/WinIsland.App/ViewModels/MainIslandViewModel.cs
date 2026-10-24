using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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

        public event Action<string, string, string, string>? NotificationRequested;

        private IslandState _currentState = IslandState.Compact;
        public IslandState CurrentState
        {
            get => _currentState;
            set
            {
                _currentState = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsExpanded));
                OnPropertyChanged(nameof(IsCompact));
            }
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

        // Current Clock & Dynamic Top Header
        private string _currentTimeString = DateTime.Now.ToString("HH:mm");
        public string CurrentTimeString
        {
            get => _currentTimeString;
            set
            {
                if (_currentTimeString == value) return;
                _currentTimeString = value;
                OnPropertyChanged();
            }
        }

        private string _topHeaderDateText = DateTime.Now.ToString("ddd d MMM yyyy ");
        public string TopHeaderDateText
        {
            get => _topHeaderDateText;
            set
            {
                if (_topHeaderDateText == value) return;
                _topHeaderDateText = value;
                OnPropertyChanged();
            }
        }

        private string _topHeaderTimeText = DateTime.Now.ToString("HH:mm");
        public string TopHeaderTimeText
        {
            get => _topHeaderTimeText;
            set
            {
                if (_topHeaderTimeText == value) return;
                _topHeaderTimeText = value;
                OnPropertyChanged();
            }
        }

        // Media Info
        private MediaTrackInfo _mediaInfo = new();
        public MediaTrackInfo MediaInfo
        {
            get => _mediaInfo;
            set { _mediaInfo = value; OnPropertyChanged(); }
        }

        // Live Synced Lyrics
        private readonly List<LyricLine> _lyrics = new()
        {
            new(TimeSpan.FromSeconds(0), "(Instrumental Intro)"),
            new(TimeSpan.FromSeconds(12), "Midnight city, windows glowing bright"),
            new(TimeSpan.FromSeconds(26), "Speed of sound beneath the neon light"),
            new(TimeSpan.FromSeconds(45), "Caught inside the harmony we know"),
            new(TimeSpan.FromSeconds(65), "Watching every shadow gently flow"),
            new(TimeSpan.FromSeconds(88), "Let the music carry all the weight"),
            new(TimeSpan.FromSeconds(112), "In the heartbeat where the visions wait"),
            new(TimeSpan.FromSeconds(138), "Echoes fading through the silent air"),
            new(TimeSpan.FromSeconds(165), "Found the rhythm waiting everywhere"),
            new(TimeSpan.FromSeconds(190), "And time just slips away into the groove...")
        };

        private string _activeLyricLine = "Midnight city, windows glowing bright";
        public string ActiveLyricLine
        {
            get => _activeLyricLine;
            set
            {
                if (_activeLyricLine == value) return;
                _activeLyricLine = value;
                OnPropertyChanged();
            }
        }

        private string _nextLyricLine = "Speed of sound beneath the neon light";
        public string NextLyricLine
        {
            get => _nextLyricLine;
            set
            {
                if (_nextLyricLine == value) return;
                _nextLyricLine = value;
                OnPropertyChanged();
            }
        }

        private string _laterLyricLine = "Caught inside the harmony we know";
        public string LaterLyricLine
        {
            get => _laterLyricLine;
            set
            {
                if (_laterLyricLine == value) return;
                _laterLyricLine = value;
                OnPropertyChanged();
            }
        }

        public void UpdateLyricsForPosition(TimeSpan position)
        {
            if (_lyrics.Count == 0) return;

            int activeIdx = 0;
            for (int i = 0; i < _lyrics.Count; i++)
            {
                if (position >= _lyrics[i].Time)
                {
                    activeIdx = i;
                }
                else
                {
                    break;
                }
            }

            ActiveLyricLine = _lyrics[activeIdx].Text;
            NextLyricLine = activeIdx + 1 < _lyrics.Count ? _lyrics[activeIdx + 1].Text : string.Empty;
            LaterLyricLine = activeIdx + 2 < _lyrics.Count ? _lyrics[activeIdx + 2].Text : string.Empty;
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
        private int _pomoTotalSeconds = 25 * 60;
        private bool _isPomoRunning;
        private string _pomoModeName = "Focus time";

        public bool IsPomoRunning
        {
            get => _isPomoRunning;
            set { _isPomoRunning = value; OnPropertyChanged(); }
        }

        public string PomoModeName
        {
            get => _pomoModeName;
            set { _pomoModeName = value; OnPropertyChanged(); }
        }

        public string PomoTimeRemaining => $"{_pomoSecondsRemaining / 60:D2}:{_pomoSecondsRemaining % 60:D2}";

        public double PomodoroProgressFraction => Math.Clamp(1.0 - (double)_pomoSecondsRemaining / Math.Max(1, _pomoTotalSeconds), 0.0, 1.0);

        public void SetPomoDuration(int minutes, bool isFocus = true)
        {
            _pomoTotalSeconds = Math.Max(1, minutes * 60);
            _pomoSecondsRemaining = _pomoTotalSeconds;
            PomoModeName = isFocus ? "Focus time" : "Short Break";
            IsPomoRunning = false;
            _pomoTimer?.Stop();
            OnPropertyChanged(nameof(PomoTimeRemaining));
            OnPropertyChanged(nameof(PomodoroProgressFraction));
        }

        public void AdjustPomoMinutes(int deltaMinutes)
        {
            int currentMinutes = _pomoSecondsRemaining / 60;
            int newMinutes = Math.Clamp(currentMinutes + deltaMinutes, 1, 120);
            SetPomoDuration(newMinutes, PomoModeName == "Focus time");
        }

        // TodoList & Timeline
        public ObservableCollection<TodoTaskItem> TodoTasks { get; } = new()
        {
            new TodoTaskItem { Title = "Morning Standup & Sync", TimeRange = "07:00-08:00", StartHour = 7.0, DurationHours = 1.0, CategoryColor = "#30D158", IsCompleted = true },
            new TodoTaskItem { Title = "Code Review & PR Merge", TimeRange = "10:30-11:15", StartHour = 10.5, DurationHours = 0.75, CategoryColor = "#0A84FF", IsCompleted = false },
            new TodoTaskItem { Title = "Lunch & Wellness Walk", TimeRange = "12:06-12:07", StartHour = 12.1, DurationHours = 0.5, CategoryColor = "#FF9F0A", IsCompleted = false },
            new TodoTaskItem { Title = "WPF Dynamic Island Polish", TimeRange = "14:00-16:00", StartHour = 14.0, DurationHours = 2.0, CategoryColor = "#BF5AF2", IsCompleted = false }
        };

        public string TodoProgressText
        {
            get
            {
                int completed = TodoTasks.Count(t => t.IsCompleted);
                int total = TodoTasks.Count;
                return $"{completed}/{total}";
            }
        }

        public void RefreshTodoProgress()
        {
            OnPropertyChanged(nameof(TodoProgressText));
        }

        // Notification Center
        public ObservableCollection<NotificationItem> Notifications { get; } = new()
        {
            new NotificationItem { AppName = "WhatsApp", Title = "Sarah Miller", Message = "Hey! Did you check the new Dynamic Island update?", AccentHex = "#30D158", Timestamp = DateTime.Now.AddMinutes(-2), IsRead = false },
            new NotificationItem { AppName = "Discord", Title = "Dev Channel", Message = "PR #42 merged into main with 0 build errors", AccentHex = "#5865F2", Timestamp = DateTime.Now.AddMinutes(-8), IsRead = false },
            new NotificationItem { AppName = "Pomodoro", Title = "Focus Session", Message = "Time for a 5-minute coffee break! Great work.", AccentHex = "#FF3B30", Timestamp = DateTime.Now.AddMinutes(-26), IsRead = false },
            new NotificationItem { AppName = "Telegram", Title = "Design Team", Message = "New macOS Figma tokens uploaded for review", AccentHex = "#2AABEE", Timestamp = DateTime.Now.AddHours(-1), IsRead = true }
        };

        public int UnreadNotificationCount => Notifications.Count(n => !n.IsRead);
        public bool HasUnreadNotifications => UnreadNotificationCount > 0;

        public void AddNotification(string appName, string title, string message, string accentHex)
        {
            var item = new NotificationItem
            {
                AppName = appName,
                Title = title,
                Message = message,
                AccentHex = accentHex,
                Timestamp = DateTime.Now,
                IsRead = false
            };
            Notifications.Insert(0, item);
            OnPropertyChanged(nameof(UnreadNotificationCount));
            OnPropertyChanged(nameof(HasUnreadNotifications));
        }

        public void ReadAllNotifications()
        {
            foreach (var n in Notifications)
            {
                n.IsRead = true;
            }
            OnPropertyChanged(nameof(UnreadNotificationCount));
            OnPropertyChanged(nameof(HasUnreadNotifications));
        }

        public void DismissNotification(NotificationItem item)
        {
            if (Notifications.Contains(item))
            {
                Notifications.Remove(item);
                OnPropertyChanged(nameof(UnreadNotificationCount));
                OnPropertyChanged(nameof(HasUnreadNotifications));
            }
        }

        // Hydration Tracker
        private int _hydrationGlassesDrunk = 4;
        public int HydrationGlassesDrunk
        {
            get => _hydrationGlassesDrunk;
            set
            {
                _hydrationGlassesDrunk = Math.Clamp(value, 0, HydrationGoalGlasses);
                OnPropertyChanged();
                OnPropertyChanged(nameof(HydrationText));
                OnPropertyChanged(nameof(HydrationMlText));
            }
        }

        public int HydrationGoalGlasses { get; set; } = 8;
        public string HydrationText => $"{HydrationGlassesDrunk}/{HydrationGoalGlasses} Glasses";
        public string HydrationMlText => $"{HydrationGlassesDrunk * 250}/{HydrationGoalGlasses * 250} ml";

        public void LogHydrationGlass()
        {
            if (HydrationGlassesDrunk < HydrationGoalGlasses)
            {
                HydrationGlassesDrunk++;
            }
            else
            {
                HydrationGlassesDrunk = 1;
            }
        }

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
        public ICommand ToggleTaskCommand { get; }
        public ICommand AddTaskCommand { get; }
        public ICommand ReadAllNotificationsCommand { get; }
        public ICommand DismissNotificationCommand { get; }
        public ICommand LogHydrationCommand { get; }

        public MainIslandViewModel()
        {
            Animator = new SpringAnimator(220, 38, 22, 1.0, 0);
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

            // Clock & Dynamic Top Header Timer (1s)
            var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clockTimer.Tick += (s, e) =>
            {
                var now = DateTime.Now;
                CurrentTimeString = now.ToString("HH:mm");
                TopHeaderDateText = now.ToString("ddd d MMM yyyy ");
                TopHeaderTimeText = now.ToString("HH:mm");
            };
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
                    SetPomoDuration(mins, mins >= 15);
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

            ToggleTaskCommand = new RelayCommand(param =>
            {
                if (param is TodoTaskItem task)
                {
                    task.IsCompleted = !task.IsCompleted;
                    RefreshTodoProgress();
                }
            });

            AddTaskCommand = new RelayCommand(() =>
            {
                var newTask = new TodoTaskItem
                {
                    Title = $"New Focus Task #{TodoTasks.Count + 1}",
                    TimeRange = $"{DateTime.Now:HH:mm}-{DateTime.Now.AddHours(1):HH:mm}",
                    StartHour = DateTime.Now.Hour + DateTime.Now.Minute / 60.0,
                    DurationHours = 1.0,
                    CategoryColor = "#30D158",
                    IsCompleted = false
                };
                TodoTasks.Add(newTask);
                RefreshTodoProgress();
            });

            ReadAllNotificationsCommand = new RelayCommand(ReadAllNotifications);
            DismissNotificationCommand = new RelayCommand(param =>
            {
                if (param is NotificationItem n) DismissNotification(n);
            });

            LogHydrationCommand = new RelayCommand(LogHydrationGlass);

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
                UpdateLyricsForPosition(target);
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
                    OnPropertyChanged(nameof(PomodoroProgressFraction));
                }
                else
                {
                    _pomoTimer.Stop();
                    IsPomoRunning = false;
                    try { System.Media.SystemSounds.Beep.Play(); } catch { }
                    NotificationRequested?.Invoke("Pomodoro", "Session Finished", $"{PomoModeName} has ended! Great focus.", "#FF3B30");
                }
            };
        }

        public void ExpandToDefault()
        {
            CurrentState = IslandState.Expanded;
            Animator.AnimateTo(450, 215, 28, 1.0, 0);
        }

        public void CollapseToCompact()
        {
            CurrentState = IslandState.Compact;
            Animator.AnimateTo(220, 38, 22, 1.0, 0);
        }

        public void HideIsland()
        {
            CurrentState = IslandState.Hidden;
            Animator.AnimateTo(220, 38, 22, 0.0, -40);
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
            _pomoSecondsRemaining = _pomoTotalSeconds;
            OnPropertyChanged(nameof(PomoTimeRemaining));
            OnPropertyChanged(nameof(PomodoroProgressFraction));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}
