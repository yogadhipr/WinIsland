using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinIsland.App.Services;
using WinIsland.App.ViewModels;

namespace WinIsland.App.Views.Modules
{
    public partial class MediaWidget : UserControl
    {
        private readonly DispatcherTimer _animTimer;
        private readonly Random _rand = new Random();
        private double _phase;
        private bool _isScrubbing;

        public MediaWidget()
        {
            InitializeComponent();

            ScrubberTrackContainer.SizeChanged += (s, e) => UpdateMediaProgress();

            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _animTimer.Tick += OnAnimTick;
            _animTimer.Start();
        }

        private void OnAnimTick(object? sender, EventArgs e)
        {
            UpdateMediaProgress();

            if (DataContext is MainIslandViewModel vm && vm.MediaInfo.IsPlaying)
            {
                _phase += 0.3;
                float peak = Math.Max(vm.AudioPeak, 0.25f);

                Bar1.Height = Math.Clamp(4 + Math.Sin(_phase) * 6 * peak + _rand.Next(0, 3), 3, 14);
                Bar2.Height = Math.Clamp(6 + Math.Cos(_phase * 1.2) * 8 * peak + _rand.Next(0, 3), 3, 14);
                Bar3.Height = Math.Clamp(8 + Math.Sin(_phase * 0.8) * 9 * peak + _rand.Next(0, 4), 3, 14);
                Bar4.Height = Math.Clamp(5 + Math.Cos(_phase * 1.5) * 7 * peak + _rand.Next(0, 3), 3, 14);
                Bar5.Height = Math.Clamp(7 + Math.Sin(_phase * 1.1) * 8 * peak + _rand.Next(0, 4), 3, 14);
                Bar6.Height = Math.Clamp(9 + Math.Cos(_phase * 0.9) * 9 * peak + _rand.Next(0, 4), 3, 14);
            }
            else
            {
                // Idle bars
                Bar1.Height = 3;
                Bar2.Height = 5;
                Bar3.Height = 7;
                Bar4.Height = 5;
                Bar5.Height = 4;
                Bar6.Height = 3;
            }
        }

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
            if (DataContext is not MainIslandViewModel vm || !vm.MediaInfo.HasActiveSession || vm.MediaInfo.Duration <= TimeSpan.Zero)
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
                if (trackWidth > 0 && DataContext is MainIslandViewModel vm && vm.MediaInfo.Duration > TimeSpan.Zero)
                {
                    double mouseX = e.GetPosition(ScrubberTrackContainer).X;
                    double ratio = Math.Clamp(mouseX / trackWidth, 0.0, 1.0);
                    var targetTime = vm.MediaInfo.StartTime + TimeSpan.FromMilliseconds(vm.MediaInfo.Duration.TotalMilliseconds * ratio);
                    _ = MediaControlService.Instance.TrySeekAsync(targetTime);
                }
            }
        }

        private void UpdateScrubPosition(double mouseX)
        {
            if (DataContext is not MainIslandViewModel vm) return;
            double trackWidth = ScrubberTrackContainer.ActualWidth;
            if (trackWidth <= 0 || vm.MediaInfo.Duration <= TimeSpan.Zero) return;

            double ratio = Math.Clamp(mouseX / trackWidth, 0.0, 1.0);
            ScrubberFill.Width = trackWidth * ratio;

            var previewPos = vm.MediaInfo.StartTime + TimeSpan.FromMilliseconds(vm.MediaInfo.Duration.TotalMilliseconds * ratio);
            var previewRemaining = vm.MediaInfo.Duration - (previewPos - vm.MediaInfo.StartTime);
            if (previewRemaining < TimeSpan.Zero) previewRemaining = TimeSpan.Zero;

            TxtCurrentTime.Text = vm.MediaInfo.Duration.TotalHours >= 1
                ? previewPos.ToString(@"hh\:mm\:ss")
                : previewPos.ToString(@"mm\:ss");

            TxtRemainingTime.Text = "-" + (vm.MediaInfo.Duration.TotalHours >= 1
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
    }
}
