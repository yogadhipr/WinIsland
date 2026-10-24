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
                }

                _currentSession = _sessionManager?.GetCurrentSession();

                if (_currentSession != null)
                {
                    _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
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
                        Thumbnail = null
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

        public async Task RefreshMediaInfoAsync()
        {
            if (_currentSession == null) return;

            try
            {
                var mediaProps = await _currentSession.TryGetMediaPropertiesAsync();
                var playbackInfo = _currentSession.GetPlaybackInfo();

                string title = !string.IsNullOrWhiteSpace(mediaProps?.Title) ? mediaProps.Title : "Unknown Title";
                string artist = !string.IsNullOrWhiteSpace(mediaProps?.Artist) ? mediaProps.Artist : "Unknown Artist";
                string album = mediaProps?.AlbumTitle ?? "";

                bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                ImageSource? thumbSource = null;
                if (mediaProps?.Thumbnail != null)
                {
                    thumbSource = await LoadThumbnailAsync(mediaProps.Thumbnail);
                }

                CurrentTrack = new MediaTrackInfo
                {
                    Title = title,
                    Artist = artist,
                    Album = album,
                    IsPlaying = isPlaying,
                    Thumbnail = thumbSource,
                    HasActiveSession = true
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
            if (_currentSession == null) return false;
            try
            {
                return await _currentSession.TryTogglePlayPauseAsync();
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> TrySkipNextAsync()
        {
            if (_currentSession == null) return false;
            try
            {
                return await _currentSession.TrySkipNextAsync();
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> TrySkipPreviousAsync()
        {
            if (_currentSession == null) return false;
            try
            {
                return await _currentSession.TrySkipPreviousAsync();
            }
            catch
            {
                return false;
            }
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
