using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WinIsland.App.Services
{
    public class MediaTrackInfo
    {
        public string Title { get; set; } = "No Media Playing";
        public string Artist { get; set; } = "WinIsland Audio";
        public string Album { get; set; } = "";
        public bool IsPlaying { get; set; }
        public ImageSource? Thumbnail { get; set; }
        public bool HasActiveSession { get; set; }
        public TimeSpan Position { get; set; } = TimeSpan.Zero;
        public TimeSpan StartTime { get; set; } = TimeSpan.Zero;
        public TimeSpan EndTime { get; set; } = TimeSpan.Zero;
        public TimeSpan Duration { get; set; } = TimeSpan.Zero;
        public DateTimeOffset LastUpdatedTime { get; set; } = DateTimeOffset.MinValue;
        public bool CanSeek { get; set; }
    }

    public class MediaControlService
    {
        private static readonly Lazy<MediaControlService> _instance = new(() => new MediaControlService());
        public static MediaControlService Instance => _instance.Value;

        private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;

        public event Action<MediaTrackInfo>? TrackInfoChanged;
        public MediaTrackInfo CurrentTrack { get; private set; } = new MediaTrackInfo();

        public async Task InitializeAsync()
        {
            try
            {
                _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_sessionManager != null)
                {
                    _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                    UpdateCurrentSession();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaControlService] Init error: {ex.Message}");
            }
        }

        private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        {
            UpdateCurrentSession();
        }

        private void UpdateCurrentSession()
        {
            try
            {
                if (_currentSession != null)
                {
                    _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
                }

                _currentSession = _sessionManager?.GetCurrentSession();

                if (_currentSession != null)
                {
                    _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                    _ = RefreshMediaInfoAsync();
                }
                else
                {
                    CurrentTrack = new MediaTrackInfo
                    {
                        Title = "No Media Playing",
                        Artist = "Ready for playback",
                        IsPlaying = false,
                        HasActiveSession = false,
                        Thumbnail = null,
                        Position = TimeSpan.Zero,
                        StartTime = TimeSpan.Zero,
                        EndTime = TimeSpan.Zero,
                        Duration = TimeSpan.Zero,
                        LastUpdatedTime = DateTimeOffset.MinValue,
                        CanSeek = false
                    };
                    NotifyTrackChanged();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaControlService] Update session error: {ex.Message}");
            }
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            _ = RefreshMediaInfoAsync();
        }

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            _ = RefreshMediaInfoAsync();
        }

        private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            _ = RefreshTimelineAsync();
        }

        public Task RefreshTimelineAsync()
        {
            if (_currentSession == null) return Task.CompletedTask;

            try
            {
                var timeline = _currentSession.GetTimelineProperties();
                if (timeline != null)
                {
                    CurrentTrack.Position = timeline.Position;
                    CurrentTrack.StartTime = timeline.StartTime;
                    CurrentTrack.EndTime = timeline.EndTime;
                    CurrentTrack.Duration = timeline.EndTime >= timeline.StartTime
                        ? (timeline.EndTime - timeline.StartTime)
                        : TimeSpan.Zero;
                    CurrentTrack.LastUpdatedTime = timeline.LastUpdatedTime != DateTimeOffset.MinValue
                        ? timeline.LastUpdatedTime
                        : DateTimeOffset.UtcNow;
                    CurrentTrack.CanSeek = CurrentTrack.Duration > TimeSpan.Zero;

                    NotifyTrackChanged();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaControlService] Timeline error: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public async Task RefreshMediaInfoAsync()
        {
            if (_currentSession == null) return;

            try
            {
                var mediaProps = await _currentSession.TryGetMediaPropertiesAsync();
                var playbackInfo = _currentSession.GetPlaybackInfo();
                var timeline = _currentSession.GetTimelineProperties();

                string title = !string.IsNullOrWhiteSpace(mediaProps?.Title) ? mediaProps.Title : "Unknown Title";
                string artist = !string.IsNullOrWhiteSpace(mediaProps?.Artist) ? mediaProps.Artist : "Unknown Artist";
                string album = mediaProps?.AlbumTitle ?? "";

                bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                ImageSource? thumbSource = null;
                if (mediaProps?.Thumbnail != null)
                {
                    thumbSource = await LoadThumbnailAsync(mediaProps.Thumbnail);
                }

                TimeSpan position = TimeSpan.Zero;
                TimeSpan startTime = TimeSpan.Zero;
                TimeSpan endTime = TimeSpan.Zero;
                TimeSpan duration = TimeSpan.Zero;
                DateTimeOffset lastUpdated = DateTimeOffset.UtcNow;
                bool canSeek = false;

                if (timeline != null)
                {
                    position = timeline.Position;
                    startTime = timeline.StartTime;
                    endTime = timeline.EndTime;
                    duration = timeline.EndTime >= timeline.StartTime ? (timeline.EndTime - timeline.StartTime) : TimeSpan.Zero;
                    lastUpdated = timeline.LastUpdatedTime != DateTimeOffset.MinValue ? timeline.LastUpdatedTime : DateTimeOffset.UtcNow;
                    canSeek = duration > TimeSpan.Zero;
                }

                CurrentTrack = new MediaTrackInfo
                {
                    Title = title,
                    Artist = artist,
                    Album = album,
                    IsPlaying = isPlaying,
                    Thumbnail = thumbSource,
                    HasActiveSession = true,
                    Position = position,
                    StartTime = startTime,
                    EndTime = endTime,
                    Duration = duration,
                    LastUpdatedTime = lastUpdated,
                    CanSeek = canSeek
                };

                NotifyTrackChanged();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaControlService] Refresh error: {ex.Message}");
            }
        }

        private async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference thumbRef)
        {
            try
            {
                using var stream = await thumbRef.OpenReadAsync();
                using var netStream = stream.AsStreamForRead();
                var memStream = new MemoryStream();
                await netStream.CopyToAsync(memStream);
                memStream.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = memStream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        public async Task<bool> TryTogglePlayPauseAsync()
        {
            if (_currentSession == null)
            {
                UpdateCurrentSession();
            }
            if (_currentSession != null)
            {
                try
                {
                    if (await _currentSession.TryTogglePlayPauseAsync())
                        return true;
                }
                catch { }
            }
            Core.Win32Interop.SendMediaKey(Core.Win32Interop.VK_MEDIA_PLAY_PAUSE);
            return true;
        }

        public async Task<bool> TrySkipNextAsync()
        {
            if (_currentSession == null)
            {
                UpdateCurrentSession();
            }
            if (_currentSession != null)
            {
                try
                {
                    if (await _currentSession.TrySkipNextAsync())
                        return true;
                }
                catch { }
            }
            Core.Win32Interop.SendMediaKey(Core.Win32Interop.VK_MEDIA_NEXT_TRACK);
            return true;
        }

        public async Task<bool> TrySkipPreviousAsync()
        {
            if (_currentSession == null)
            {
                UpdateCurrentSession();
            }
            if (_currentSession != null)
            {
                try
                {
                    if (await _currentSession.TrySkipPreviousAsync())
                        return true;
                }
                catch { }
            }
            Core.Win32Interop.SendMediaKey(Core.Win32Interop.VK_MEDIA_PREV_TRACK);
            return true;
        }

        public async Task<bool> TrySeekAsync(TimeSpan targetPosition)
        {
            if (_currentSession == null) return false;
            try
            {
                CurrentTrack.Position = targetPosition;
                CurrentTrack.LastUpdatedTime = DateTimeOffset.UtcNow;
                NotifyTrackChanged();

                return await _currentSession.TryChangePlaybackPositionAsync(targetPosition.Ticks);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MediaControlService] Seek error: {ex.Message}");
                return false;
            }
        }

        public (TimeSpan position, TimeSpan duration, double progress) GetCurrentProgress()
        {
            var track = CurrentTrack;
            if (!track.HasActiveSession || track.Duration <= TimeSpan.Zero)
            {
                return (track.Position, track.Duration, 0.0);
            }

            TimeSpan currentPos = track.Position;
            if (track.IsPlaying && track.LastUpdatedTime > DateTimeOffset.MinValue)
            {
                var elapsed = DateTimeOffset.UtcNow - track.LastUpdatedTime;
                if (elapsed > TimeSpan.Zero)
                {
                    currentPos += elapsed;
                }
            }

            if (currentPos > track.EndTime && track.EndTime > TimeSpan.Zero)
            {
                currentPos = track.EndTime;
            }
            if (currentPos < track.StartTime)
            {
                currentPos = track.StartTime;
            }

            double totalMs = track.Duration.TotalMilliseconds;
            double currentMs = (currentPos - track.StartTime).TotalMilliseconds;
            double progress = totalMs > 0 ? Math.Clamp(currentMs / totalMs, 0.0, 1.0) : 0.0;

            return (currentPos, track.Duration, progress);
        }

        private void NotifyTrackChanged()
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                TrackInfoChanged?.Invoke(CurrentTrack);
            });
        }
    }
}
