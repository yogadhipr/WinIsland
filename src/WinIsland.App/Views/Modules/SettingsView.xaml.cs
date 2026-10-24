using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinIsland.App.Core;

namespace WinIsland.App.Views.Modules
{
    public partial class SettingsView : UserControl
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "WinIsland";
        private bool _isInitializing = true;

        public SettingsView()
        {
            InitializeComponent();
            LoadCurrentSettings();
            _isInitializing = false;
        }

        private void LoadCurrentSettings()
        {
            var s = SettingsManager.Instance.Current;
            ChkAutoHide.IsChecked = s.EnableAutoHideFullScreen;
            ChkRequireAlt.IsChecked = s.RequireAltKey;
            SliderHoverDelay.Value = s.HoverDelayMs;
            TxtDelayValue.Text = $"{s.HoverDelayMs}ms";
            ChkStartup.IsChecked = s.RunOnStartup;
        }

        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            var s = SettingsManager.Instance.Current;
            s.EnableAutoHideFullScreen = ChkAutoHide.IsChecked == true;
            s.RequireAltKey = ChkRequireAlt.IsChecked == true;

            SettingsManager.Instance.SaveSettings(s);
        }

        private void OnSliderDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtDelayValue != null)
            {
                int val = (int)e.NewValue;
                TxtDelayValue.Text = $"{val}ms";
                if (!_isInitializing)
                {
                    var s = SettingsManager.Instance.Current;
                    s.HoverDelayMs = val;
                    SettingsManager.Instance.SaveSettings(s);
                }
            }
        }

        private void OnStartupToggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool enable = ChkStartup.IsChecked == true;
            var s = SettingsManager.Instance.Current;
            s.RunOnStartup = enable;
            SettingsManager.Instance.SaveSettings(s);

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (key != null)
                {
                    if (enable)
                    {
                        string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            key.SetValue(AppName, $"\"{exePath}\"");
                        }
                    }
                    else
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SettingsView] Startup registry error: {ex.Message}");
            }
        }
    }
}
