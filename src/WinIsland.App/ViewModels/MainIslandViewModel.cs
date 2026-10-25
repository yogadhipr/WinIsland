using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
        private readonly string _todosFilePath;
        private readonly string _notificationsFilePath;

        public SpringAnimator Animator { get; }
        public HoverIntentController HoverController { get; }

        public event Action<string, string, string, string>? NotificationRequested;
        public event Action? TodoTasksUpdated;

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

        // Media Info & Synced Lyrics
        private MediaTrackInfo _mediaInfo = new();
        private string _lyricsKey = string.Empty;
        private bool _lyricsLoadedWithZeroDuration;

        public MediaTrackInfo MediaInfo
        {
            get => _mediaInfo;
            set
            {
                _mediaInfo = value;
                OnPropertyChanged();
                _ = LoadLyricsForTrackAsync(value);
            }
        }

        public Task EnsureLyricsLoadedAsync() => LoadLyricsForTrackAsync(_mediaInfo);

        private async Task LoadLyricsForTrackAsync(MediaTrackInfo track)
        {
            if (!track.HasActiveSession || string.IsNullOrWhiteSpace(track.Title) || track.Title == "No Media Playing")
            {
                _lyricsKey = string.Empty;
                await (Application.Current?.Dispatcher?.InvokeAsync(() =>
                {
                    _lyrics.Clear();
                    ActiveLyricLine = "Belum ada musik diputar";
                    NextLyricLine = "Putar lagu untuk menampilkan lirik otomatis";
                    LaterLyricLine = string.Empty;
                }).Task ?? Task.CompletedTask);
                return;
            }

            string key = $"{track.Title.Trim()}|{track.Artist.Trim()}";
            bool retryWithDuration = key == _lyricsKey && _lyricsLoadedWithZeroDuration && track.Duration > TimeSpan.Zero;

            if (key == _lyricsKey && !retryWithDuration && _lyrics.Count > 0) return;
            _lyricsKey = key;
            _lyricsLoadedWithZeroDuration = track.Duration <= TimeSpan.Zero;

            await (Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                ActiveLyricLine = "⏳ Memuat lirik...";
                NextLyricLine = $"{track.Title} • {track.Artist}";
                LaterLyricLine = string.Empty;
            }).Task ?? Task.CompletedTask);

            var lines = await LyricsService.FetchAsync(track.Title, track.Artist, track.Duration);
            if (key != _lyricsKey) return;

            await (Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                if (lines is { Count: > 0 })
                {
                    _lyrics = lines;
                    var (pos, _, _) = MediaControlService.Instance.GetCurrentProgress();
                    UpdateLyricsForPosition(pos);
                }
                else
                {
                    _lyrics = new List<LyricLine>
                    {
                        new(TimeSpan.Zero, $"♪ {track.Title}"),
                        new(TimeSpan.FromSeconds(5), "Lirik tersinkronisasi belum tersedia untuk lagu ini")
                    };
                    ActiveLyricLine = _lyrics[0].Text;
                    NextLyricLine = _lyrics[1].Text;
                    LaterLyricLine = string.Empty;
                }
            }).Task ?? Task.CompletedTask);
        }

        // Live Synced Lyrics (dynamic — populated from active media session)
        private List<LyricLine> _lyrics = new();

        private string _activeLyricLine = "Belum ada musik diputar";
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

        private string _nextLyricLine = "Putar lagu untuk menampilkan lirik otomatis";
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

        private string _laterLyricLine = string.Empty;
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
        public string VolumeText => $"{(int)Math.Round(VolumePercent * 100)}%";

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

        /// <summary>
        /// Remaining fraction (1.0 at full time -> 0.0 when finished) so the circular ring starts full and drains smoothly.
        /// </summary>
        public double PomodoroProgressFraction => Math.Clamp((double)_pomoSecondsRemaining / Math.Max(1, _pomoTotalSeconds), 0.0, 1.0);

        public void SetPomoDuration(int minutes, bool isFocus = true)
        {
            minutes = Math.Clamp(minutes, 1, 180);
            _pomoTotalSeconds = minutes * 60;
            _pomoSecondsRemaining = _pomoTotalSeconds;
            PomoModeName = isFocus ? "Focus time" : "Short Break";
            IsPomoRunning = false;
            _pomoTimer?.Stop();
            OnPropertyChanged(nameof(PomoTimeRemaining));
            OnPropertyChanged(nameof(PomodoroProgressFraction));
        }

        public void AdjustPomoMinutes(int deltaMinutes)
        {
            int currentMinutes = (int)Math.Ceiling(_pomoSecondsRemaining / 60.0);
            int newMinutes = Math.Clamp(currentMinutes + deltaMinutes, 1, 180);
            SetPomoDuration(newMinutes, PomoModeName == "Focus time");
        }

        // TodoList & Timeline (with JSON Persistence & Per-Date Filtering)
        public ObservableCollection<TodoTaskItem> TodoTasks { get; } = new();
        public ObservableCollection<TodoTaskItem> FilteredTodoTasks { get; } = new();

        private DateTime _selectedTaskDate = DateTime.Today;
        public DateTime SelectedTaskDate
        {
            get => _selectedTaskDate.Date;
            set
            {
                var normalized = (value == default ? DateTime.Today : value).Date;
                if (_selectedTaskDate.Date == normalized) return;
                _selectedTaskDate = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TodoDateSubtitleText));
                SyncFilteredTodoTasks();
            }
        }

        public string TodoDateSubtitleText => _selectedTaskDate.Date == DateTime.Today
            ? "Task Done Today"
            : $"Tasks • {_selectedTaskDate:dd MMM yyyy}";

        public string TodoProgressText
        {
            get
            {
                int completed = FilteredTodoTasks.Count(t => t.IsCompleted);
                int total = FilteredTodoTasks.Count;
                return $"{completed}/{total}";
            }
        }

        public bool HasNoTasksForSelectedDate => FilteredTodoTasks.Count == 0;

        public bool HasTasksOnDate(DateTime date) => TodoTasks.Any(t => t.TaskDate.Date == date.Date);

        public void SyncFilteredTodoTasks()
        {
            FilteredTodoTasks.Clear();
            foreach (var t in TodoTasks.Where(x => x.TaskDate.Date == _selectedTaskDate.Date).OrderBy(x => x.StartHour))
            {
                FilteredTodoTasks.Add(t);
            }
            OnPropertyChanged(nameof(TodoProgressText));
            OnPropertyChanged(nameof(HasNoTasksForSelectedDate));
            OnPropertyChanged(nameof(TodoDateSubtitleText));
            TodoTasksUpdated?.Invoke();
        }

        public void RefreshTodoProgress()
        {
            SaveTodos();
            SyncFilteredTodoTasks();
        }

        public void AddCustomTask(string title, DateTime taskDate, string timeRange, string categoryColor = "#30D158")
        {
            if (string.IsNullOrWhiteSpace(title)) return;
            if (string.IsNullOrWhiteSpace(timeRange))
            {
                timeRange = $"{DateTime.Now:HH:mm}-{DateTime.Now.AddHours(1):HH:mm}";
            }

            var normalizedDate = (taskDate == default ? DateTime.Today : taskDate).Date;
            var task = new TodoTaskItem
            {
                Title = title.Trim(),
                TaskDate = normalizedDate,
                StartHour = DateTime.Now.Hour + DateTime.Now.Minute / 60.0,
                DurationHours = 1.0,
                TimeRange = timeRange.Trim(),
                CategoryColor = categoryColor,
                IsCompleted = false
            };
            TodoTasks.Add(task);
            _selectedTaskDate = normalizedDate;
            OnPropertyChanged(nameof(SelectedTaskDate));
            RefreshTodoProgress();
        }

        public void UpdateTask(TodoTaskItem task, string title, DateTime taskDate, string timeRange, string categoryColor)
        {
            if (task == null || string.IsNullOrWhiteSpace(title)) return;
            var normalizedDate = (taskDate == default ? DateTime.Today : taskDate).Date;
            task.Title = title.Trim();
            task.TaskDate = normalizedDate;
            if (!string.IsNullOrWhiteSpace(timeRange))
            {
                task.TimeRange = timeRange.Trim();
            }
            if (!string.IsNullOrWhiteSpace(categoryColor))
            {
                task.CategoryColor = categoryColor;
            }
            _selectedTaskDate = normalizedDate;
            OnPropertyChanged(nameof(SelectedTaskDate));
            RefreshTodoProgress();
        }

        public void DeleteTask(TodoTaskItem task)
        {
            if (task != null && TodoTasks.Remove(task))
            {
                RefreshTodoProgress();
            }
        }

        public int ClearCompletedTasks()
        {
            var completedOnDate = TodoTasks
                .Where(t => t.TaskDate.Date == _selectedTaskDate.Date && t.IsCompleted)
                .ToList();

            if (completedOnDate.Count == 0)
            {
                completedOnDate = TodoTasks.Where(t => t.IsCompleted).ToList();
            }

            foreach (var item in completedOnDate)
            {
                TodoTasks.Remove(item);
            }

            if (completedOnDate.Count > 0)
            {
                RefreshTodoProgress();
            }
            return completedOnDate.Count;
        }

        private void LoadTodos()
        {
            try
            {
                if (File.Exists(_todosFilePath))
                {
                    string json = File.ReadAllText(_todosFilePath);
                    var items = JsonSerializer.Deserialize<List<TodoTaskItem>>(json);
                    if (items != null)
                    {
                        var legacyFakeTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            "Morning Standup & Sync",
                            "Code Review & PR Merge",
                            "Lunch & Wellness Walk",
                            "WPF Dynamic Island Polish"
                        };

                        TodoTasks.Clear();
                        foreach (var item in items)
                        {
                            if (legacyFakeTitles.Contains(item.Title)) continue;
                            if (item.TaskDate == default)
                            {
                                item.TaskDate = DateTime.Today;
                            }
                            // Re-trigger TimeRange normalization (e.g. "08:00-17.00" -> "08:00-17:00")
                            item.TimeRange = item.TimeRange;
                            TodoTasks.Add(item);
                        }
                        SaveTodos();
                        SyncFilteredTodoTasks();
                        return;
                    }
                }
            }
            catch { }

            TodoTasks.Clear();
            SyncFilteredTodoTasks();
        }

        public void SaveTodos()
        {
            try
            {
                string json = JsonSerializer.Serialize(TodoTasks.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_todosFilePath, json);
            }
            catch { }
        }

        // Notification Center (Dynamic & Persisted — No Hardcoded Fake Items)
        public ObservableCollection<NotificationItem> Notifications { get; } = new();

        private NotificationItem? _selectedNotification;
        public NotificationItem? SelectedNotification
        {
            get => _selectedNotification;
            set
            {
                _selectedNotification = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedNotification));
            }
        }

        public bool HasSelectedNotification => _selectedNotification != null;

        public int UnreadNotificationCount => Notifications.Count(n => !n.IsRead);
        public bool HasUnreadNotifications => UnreadNotificationCount > 0;
        public bool HasNoNotifications => Notifications.Count == 0;

        private void LoadNotifications()
        {
            try
            {
                if (File.Exists(_notificationsFilePath))
                {
                    string json = File.ReadAllText(_notificationsFilePath);
                    var items = JsonSerializer.Deserialize<List<NotificationItem>>(json);
                    if (items != null)
                    {
                        Notifications.Clear();
                        foreach (var item in items)
                        {
                            item.IsExpanded = false;
                            Notifications.Add(item);
                        }
                        NotifyNotificationCountChanged();
                    }
                }
            }
            catch { }
        }

        private void SaveNotifications()
        {
            try
            {
                string json = JsonSerializer.Serialize(Notifications.ToList(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_notificationsFilePath, json);
            }
            catch { }
        }

        private void NotifyNotificationCountChanged()
        {
            OnPropertyChanged(nameof(UnreadNotificationCount));
            OnPropertyChanged(nameof(HasUnreadNotifications));
            OnPropertyChanged(nameof(HasNoNotifications));
        }

        public void MarkNotificationRead(NotificationItem item)
        {
            if (item == null || item.IsRead) return;
            item.IsRead = true;
            SaveNotifications();
            NotifyNotificationCountChanged();
        }

        public void AddNotification(string appName, string title, string message, string accentHex)
        {
            var item = new NotificationItem
            {
                AppName = appName,
                Title = title,
                Message = message,
                AccentHex = accentHex,
                Timestamp = DateTime.Now,
                IsRead = false,
                IsExpanded = false
            };
            Notifications.Insert(0, item);
            while (Notifications.Count > 30)
            {
                Notifications.RemoveAt(Notifications.Count - 1);
            }
            SaveNotifications();
            NotifyNotificationCountChanged();
        }

        public void ReadAllNotifications()
        {
            foreach (var n in Notifications)
            {
                n.IsRead = true;
            }
            SaveNotifications();
            NotifyNotificationCountChanged();
        }

        public void ClearAllNotifications()
        {
            Notifications.Clear();
            SelectedNotification = null;
            SaveNotifications();
            NotifyNotificationCountChanged();
        }

        public void DismissNotification(NotificationItem item)
        {
            if (Notifications.Contains(item))
            {
                if (SelectedNotification == item)
                {
                    SelectedNotification = null;
                }
                Notifications.Remove(item);
                SaveNotifications();
                NotifyNotificationCountChanged();
            }
        }

        // Jadwal Sholat (Location-Based Prayer Times)
        public ObservableCollection<PrayerTimeEntry> PrayerTimes { get; } = new();

        private PrayerLocationSettings _prayerLocation = new() { City = "Mendeteksi lokasi...", Country = "Indonesia" };
        private DateTime _lastPrayerFetchedDate = DateTime.MinValue;
        private string _lastNotifiedPrayerKey = string.Empty;

        public string CurrentPrayerCityName => _prayerLocation.City;

        private string _prayerLocationText = "Mendeteksi lokasi...";
        public string PrayerLocationText
        {
            get => _prayerLocationText;
            set { _prayerLocationText = value; OnPropertyChanged(); }
        }

        private string _prayerHijriDateText = PrayerTimeService.FormatLocalHijriDate(DateTime.Today);
        public string PrayerHijriDateText
        {
            get => _prayerHijriDateText;
            set { _prayerHijriDateText = value; OnPropertyChanged(); }
        }

        private string _nextPrayerName = "Memuat...";
        public string NextPrayerName
        {
            get => _nextPrayerName;
            set { _nextPrayerName = value; OnPropertyChanged(); }
        }

        private string _nextPrayerTimeText = "--:--";
        public string NextPrayerTimeText
        {
            get => _nextPrayerTimeText;
            set { _nextPrayerTimeText = value; OnPropertyChanged(); }
        }

        private string _nextPrayerCountdownText = "Menghitung waktu...";
        public string NextPrayerCountdownText
        {
            get => _nextPrayerCountdownText;
            set { _nextPrayerCountdownText = value; OnPropertyChanged(); }
        }

        public async Task InitializePrayerTimesAsync()
        {
            var saved = PrayerTimeService.LoadSavedLocation();
            if (saved != null && !string.IsNullOrWhiteSpace(saved.City))
            {
                _prayerLocation = saved;
            }
            else
            {
                _prayerLocation = await PrayerTimeService.DetectLocationAsync();
            }
            await RefreshPrayerTimesAsync(_prayerLocation);
        }

        public async Task AutoDetectPrayerLocationAsync()
        {
            PrayerLocationText = "Mendeteksi lokasi...";
            var detected = await PrayerTimeService.DetectLocationAsync();
            await RefreshPrayerTimesAsync(detected);
        }

        public async Task<bool> SearchAndSetPrayerCityAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return false;
            PrayerLocationText = $"Mencari {query.Trim()}...";
            var found = await PrayerTimeService.SearchCityAsync(query);
            if (found == null) return false;
            await RefreshPrayerTimesAsync(found);
            return true;
        }

        public async Task RefreshPrayerTimesAsync(PrayerLocationSettings? location = null)
        {
            if (location != null)
            {
                _prayerLocation = location;
            }

            _lastPrayerFetchedDate = DateTime.Today;
            var result = await PrayerTimeService.FetchPrayerTimesAsync(_prayerLocation, DateTime.Today);

            await (Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                PrayerLocationText = string.IsNullOrWhiteSpace(result.Country)
                    ? result.City
                    : $"{result.City}, {result.Country}";
                PrayerHijriDateText = result.HijriDate;

                PrayerTimes.Clear();
                foreach (var item in result.Items)
                {
                    PrayerTimes.Add(item);
                }

                UpdateNextPrayerCountdown(DateTime.Now);
            }).Task ?? Task.CompletedTask);
        }

        public void UpdateNextPrayerCountdown(DateTime now)
        {
            if (PrayerTimes.Count == 0) return;

            if (_lastPrayerFetchedDate != DateTime.MinValue && _lastPrayerFetchedDate.Date != now.Date)
            {
                _lastPrayerFetchedDate = now.Date;
                _ = RefreshPrayerTimesAsync(_prayerLocation);
                return;
            }

            var obligatory = PrayerTimes.Where(p => p.IsObligatory).ToList();
            if (obligatory.Count == 0) obligatory = PrayerTimes.ToList();

            PrayerTimeEntry? nextEntry = obligatory.FirstOrDefault(p => p.TimeOfDay > now.TimeOfDay);
            TimeSpan remaining;

            if (nextEntry != null)
            {
                remaining = nextEntry.TimeOfDay - now.TimeOfDay;
            }
            else
            {
                nextEntry = obligatory.FirstOrDefault(p => p.Name == "Subuh") ?? obligatory.First();
                remaining = (TimeSpan.FromHours(24) - now.TimeOfDay) + nextEntry.TimeOfDay;
            }

            PrayerTimeEntry? currentEntry = PrayerTimes.LastOrDefault(p => p.TimeOfDay <= now.TimeOfDay)
                                            ?? PrayerTimes.LastOrDefault(p => p.Name == "Isya")
                                            ?? PrayerTimes.LastOrDefault();

            foreach (var entry in PrayerTimes)
            {
                entry.IsCurrent = ReferenceEquals(entry, currentEntry);
                entry.IsNext = ReferenceEquals(entry, nextEntry);
            }

            NextPrayerName = nextEntry.Name;
            NextPrayerTimeText = nextEntry.TimeString;
            NextPrayerCountdownText = $"-{(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2} lagi";

            // Trigger Adzan/Prayer notification once when exact minute starts
            foreach (var p in obligatory)
            {
                if (now.Hour == p.TimeOfDay.Hours && now.Minute == p.TimeOfDay.Minutes && now.Second <= 2)
                {
                    string key = $"{now:yyyyMMdd}_{p.Name}";
                    if (_lastNotifiedPrayerKey != key)
                    {
                        _lastNotifiedPrayerKey = key;
                        NotificationRequested?.Invoke(
                            "Jadwal Sholat",
                            $"Waktu {p.Name} • {p.TimeString}",
                            $"Telah masuk waktu sholat {p.Name} ({p.TimeString}) untuk wilayah {_prayerLocation.City}.",
                            "#30D158");
                    }
                }
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
        public ICommand DeleteTaskCommand { get; }
        public ICommand ReadAllNotificationsCommand { get; }
        public ICommand ClearAllNotificationsCommand { get; }
        public ICommand DismissNotificationCommand { get; }
        public ICommand LogHydrationCommand { get; }

        public MainIslandViewModel()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "WinIsland");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            _todosFilePath = Path.Combine(folder, "todos.json");
            _notificationsFilePath = Path.Combine(folder, "notifications.json");
            LoadTodos();
            LoadNotifications();

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
                UpdateNextPrayerCountdown(now);
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
                string[] colors = { "#30D158", "#0A84FF", "#FF9F0A", "#BF5AF2", "#FF2D55" };
                string color = colors[TodoTasks.Count % colors.Length];
                AddCustomTask($"Task #{TodoTasks.Count + 1}", SelectedTaskDate, $"{DateTime.Now:HH:mm}-{DateTime.Now.AddHours(1):HH:mm}", color);
            });

            DeleteTaskCommand = new RelayCommand(param =>
            {
                if (param is TodoTaskItem task)
                {
                    DeleteTask(task);
                }
            });

            ReadAllNotificationsCommand = new RelayCommand(ReadAllNotifications);
            ClearAllNotificationsCommand = new RelayCommand(ClearAllNotifications);
            DismissNotificationCommand = new RelayCommand(param =>
            {
                if (param is NotificationItem n) DismissNotification(n);
            });

            LogHydrationCommand = new RelayCommand(LogHydrationGlass);

            // Connect Services
            InitializeServices();
            _ = InitializePrayerTimesAsync();
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
                    OnPropertyChanged(nameof(PomodoroProgressFraction));
                    try { System.Media.SystemSounds.Beep.Play(); } catch { }
                    NotificationRequested?.Invoke("Pomodoro", "Session Finished", $"{PomoModeName} has ended! Great focus.", "#FF3B30");
                }
            };
        }

        public void ExpandToDefault()
        {
            CurrentState = IslandState.Expanded;
            Animator.AnimateTo(460, 215, 30, 1.0, 0);
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
                if (_pomoSecondsRemaining <= 0)
                {
                    _pomoSecondsRemaining = _pomoTotalSeconds;
                }
                _pomoTimer?.Start();
                IsPomoRunning = true;
            }
            OnPropertyChanged(nameof(PomoTimeRemaining));
            OnPropertyChanged(nameof(PomodoroProgressFraction));
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
