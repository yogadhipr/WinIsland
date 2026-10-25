using System;
using System.IO;
using System.Text.Json;

namespace WinIsland.App.Core
{
    public class AppSettings
    {
        public bool EnableDynamicIsland { get; set; } = true;
        public bool EnableAutoRelaunch { get; set; } = true;

        public int HoverDelayMs { get; set; } = 250;
        public int CollapseDelayMs { get; set; } = 300;
        public bool RequireAltKey { get; set; } = false;
        public bool EnableAutoHideFullScreen { get; set; } = true;

        public bool EnableMediaModule { get; set; } = true;
        public bool EnableFileShelfModule { get; set; } = true;
        public bool EnableTelemetryModule { get; set; } = true;
        public bool EnablePomodoroModule { get; set; } = true;
        public bool EnablePixelPet { get; set; } = true;

        public bool RunOnStartup { get; set; } = true;

        public int GhostOpacityPercent { get; set; } = 15;
        public string DisplayTarget { get; set; } = "Primary"; // "Primary" or "ActiveCursor"
    }

    public class SettingsManager
    {
        private static readonly Lazy<SettingsManager> _instance = new(() => new SettingsManager());
        public static SettingsManager Instance => _instance.Value;

        private readonly string _settingsFilePath;
        public AppSettings Current { get; private set; }

        public SettingsManager()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "WinIsland");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            _settingsFilePath = Path.Combine(folder, "settings.json");
            Current = LoadSettings();
        }

        public AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null) return settings;
                }
            }
            catch
            {
                // Fallback to default on parse error
            }
            return new AppSettings();
        }

        public void SaveSettings(AppSettings? settings = null)
        {
            if (settings != null) Current = settings;
            try
            {
                string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch
            {
                // Suppress I/O errors during save
            }
        }
    }
}
