using System;
using System.Windows;
using NAudio.CoreAudioApi;

namespace WinIsland.App.Services
{
    public class AudioHudService : IDisposable
    {
        private static readonly Lazy<AudioHudService> _instance = new(() => new AudioHudService());
        public static AudioHudService Instance => _instance.Value;

        private MMDeviceEnumerator? _deviceEnumerator;
        private MMDevice? _defaultPlaybackDevice;

        public event Action<float, bool>? VolumeChanged;
        public event Action<float>? PeakMeterUpdated;

        public float CurrentVolume { get; private set; }
        public bool IsMuted { get; private set; }
        public float MasterPeak { get; private set; }

        public AudioHudService()
        {
            Initialize();
        }

        private void Initialize()
        {
            try
            {
                _deviceEnumerator = new MMDeviceEnumerator();
                _defaultPlaybackDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                if (_defaultPlaybackDevice != null)
                {
                    CurrentVolume = _defaultPlaybackDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                    IsMuted = _defaultPlaybackDevice.AudioEndpointVolume.Mute;

                    _defaultPlaybackDevice.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioHudService] Init error: {ex.Message}");
            }
        }

        private void OnVolumeNotification(AudioVolumeNotificationData data)
        {
            CurrentVolume = data.MasterVolume;
            IsMuted = data.Muted;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                VolumeChanged?.Invoke(CurrentVolume, IsMuted);
            });
        }

        public void ChangeVolume(float delta)
        {
            if (_defaultPlaybackDevice == null) return;
            try
            {
                float newVol = Math.Clamp(CurrentVolume + delta, 0.0f, 1.0f);
                _defaultPlaybackDevice.AudioEndpointVolume.MasterVolumeLevelScalar = newVol;
                CurrentVolume = newVol;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioHudService] ChangeVolume error: {ex.Message}");
            }
        }

        public void SetVolume(float volume)
        {
            if (_defaultPlaybackDevice == null) return;
            try
            {
                float clamped = Math.Clamp(volume, 0.0f, 1.0f);
                _defaultPlaybackDevice.AudioEndpointVolume.MasterVolumeLevelScalar = clamped;
                CurrentVolume = clamped;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioHudService] SetVolume error: {ex.Message}");
            }
        }

        public void ToggleMute()
        {
            if (_defaultPlaybackDevice == null) return;
            try
            {
                bool newMute = !_defaultPlaybackDevice.AudioEndpointVolume.Mute;
                _defaultPlaybackDevice.AudioEndpointVolume.Mute = newMute;
                IsMuted = newMute;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioHudService] ToggleMute error: {ex.Message}");
            }
        }

        public float ReadPeakLevel()
        {
            if (_defaultPlaybackDevice == null) return 0f;
            try
            {
                MasterPeak = _defaultPlaybackDevice.AudioMeterInformation.MasterPeakValue;
                PeakMeterUpdated?.Invoke(MasterPeak);
                return MasterPeak;
            }
            catch
            {
                return 0f;
            }
        }

        public void Dispose()
        {
            if (_defaultPlaybackDevice != null)
            {
                _defaultPlaybackDevice.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
                _defaultPlaybackDevice.Dispose();
                _defaultPlaybackDevice = null;
            }
            _deviceEnumerator?.Dispose();
            _deviceEnumerator = null;
        }
    }
}
