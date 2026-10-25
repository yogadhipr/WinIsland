using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace WinIsland.App.ViewModels
{
    public class TodoTaskItem : INotifyPropertyChanged
    {
        private static readonly Regex TimeRangeRegex = new(@"(\d{1,2})[:.](\d{2})(?:\s*-\s*(\d{1,2})[:.](\d{2}))?", RegexOptions.Compiled);

        public string Id { get; set; } = Guid.NewGuid().ToString();

        private string _title = string.Empty;
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        private DateTime _taskDate = DateTime.Today;
        public DateTime TaskDate
        {
            get => _taskDate == default ? DateTime.Today : _taskDate.Date;
            set
            {
                _taskDate = (value == default ? DateTime.Today : value).Date;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedDate));
                OnPropertyChanged(nameof(DisplaySchedule));
            }
        }

        private string _timeRange = string.Empty;
        public string TimeRange
        {
            get => _timeRange;
            set
            {
                _timeRange = value;
                ParseTimeRange(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplaySchedule));
            }
        }

        public string FormattedDate => TaskDate.Date == DateTime.Today
            ? "Today"
            : TaskDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

        public string DisplaySchedule => string.IsNullOrWhiteSpace(TimeRange)
            ? FormattedDate
            : $"{FormattedDate} • {TimeRange}";

        private double _startHour = 8.0;
        public double StartHour
        {
            get => _startHour;
            set { _startHour = value; OnPropertyChanged(); }
        }

        private double _durationHours = 1.0;
        public double DurationHours
        {
            get => _durationHours;
            set { _durationHours = value; OnPropertyChanged(); }
        }

        private string _categoryColor = "#30D158";
        public string CategoryColor
        {
            get => _categoryColor;
            set { _categoryColor = value; OnPropertyChanged(); }
        }

        private bool _isCompleted;
        public bool IsCompleted
        {
            get => _isCompleted;
            set { _isCompleted = value; OnPropertyChanged(); }
        }

        public (int startH, int startM, int endH, int endM) GetParsedTimeParts()
        {
            var m = TimeRangeRegex.Match(_timeRange ?? string.Empty);
            if (m.Success &&
                int.TryParse(m.Groups[1].Value, out int h1) &&
                int.TryParse(m.Groups[2].Value, out int m1))
            {
                h1 = Math.Clamp(h1, 0, 23);
                m1 = Math.Clamp(m1, 0, 59);
                int h2 = Math.Clamp(h1 + 1, 0, 23);
                int m2 = m1;
                if (m.Groups[3].Success && m.Groups[4].Success &&
                    int.TryParse(m.Groups[3].Value, out int parsedH2) &&
                    int.TryParse(m.Groups[4].Value, out int parsedM2))
                {
                    h2 = Math.Clamp(parsedH2, 0, 23);
                    m2 = Math.Clamp(parsedM2, 0, 59);
                }
                return (h1, m1, h2, m2);
            }

            int defaultStartH = Math.Clamp((int)Math.Floor(_startHour), 0, 23);
            int defaultStartM = Math.Clamp((int)Math.Round((_startHour - defaultStartH) * 60), 0, 59);
            double endTotal = Math.Clamp(_startHour + _durationHours, 0, 23.98);
            int defaultEndH = Math.Clamp((int)Math.Floor(endTotal), 0, 23);
            int defaultEndM = Math.Clamp((int)Math.Round((endTotal - defaultEndH) * 60), 0, 59);
            return (defaultStartH, defaultStartM, defaultEndH, defaultEndM);
        }

        private void ParseTimeRange(string? range)
        {
            if (string.IsNullOrWhiteSpace(range)) return;
            var m = TimeRangeRegex.Match(range);
            if (!m.Success) return;

            if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h1) &&
                int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m1))
            {
                h1 = Math.Clamp(h1, 0, 23);
                m1 = Math.Clamp(m1, 0, 59);
                double start = Math.Clamp(h1 + m1 / 60.0, 0.0, 23.9);
                _startHour = start;
                OnPropertyChanged(nameof(StartHour));

                if (m.Groups[3].Success && m.Groups[4].Success &&
                    int.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int h2) &&
                    int.TryParse(m.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int m2))
                {
                    h2 = Math.Clamp(h2, 0, 23);
                    m2 = Math.Clamp(m2, 0, 59);
                    _timeRange = $"{h1:D2}:{m1:D2}-{h2:D2}:{m2:D2}";
                    double end = Math.Clamp(h2 + m2 / 60.0, 0.0, 24.0);
                    double dur = end > start ? (end - start) : 0.5;
                    _durationHours = Math.Clamp(dur, 0.25, 24.0 - start);
                    OnPropertyChanged(nameof(DurationHours));
                }
                else
                {
                    _timeRange = $"{h1:D2}:{m1:D2}";
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }

    public class NotificationItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        private string _appName = string.Empty;
        public string AppName
        {
            get => _appName;
            set { _appName = value; OnPropertyChanged(); }
        }

        private string _title = string.Empty;
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        private string _message = string.Empty;
        public string Message
        {
            get => _message;
            set { _message = value; OnPropertyChanged(); }
        }

        private string _accentHex = "#0A84FF";
        public string AccentHex
        {
            get => _accentHex;
            set { _accentHex = value; OnPropertyChanged(); }
        }

        private DateTime _timestamp = DateTime.Now;
        public DateTime Timestamp
        {
            get => _timestamp;
            set { _timestamp = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedTime)); }
        }

        public string FormattedTime => Timestamp.ToString("HH:mm");

        private bool _isRead;
        public bool IsRead
        {
            get => _isRead;
            set { _isRead = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }

    public class LyricLine
    {
        public TimeSpan Time { get; set; }
        public string Text { get; set; } = string.Empty;

        public LyricLine(TimeSpan time, string text)
        {
            Time = time;
            Text = text;
        }
    }
}
