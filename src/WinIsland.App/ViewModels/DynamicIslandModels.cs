using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinIsland.App.ViewModels
{
    public class TodoTaskItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        private string _title = string.Empty;
        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        private string _timeRange = string.Empty;
        public string TimeRange
        {
            get => _timeRange;
            set { _timeRange = value; OnPropertyChanged(); }
        }

        public double StartHour { get; set; } = 8.0;
        public double DurationHours { get; set; } = 1.0;
        public string CategoryColor { get; set; } = "#30D158";

        private bool _isCompleted;
        public bool IsCompleted
        {
            get => _isCompleted;
            set { _isCompleted = value; OnPropertyChanged(); }
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
            set { _timestamp = value; OnPropertyChanged(); }
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
