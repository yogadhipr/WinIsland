using System.Text.Json;
using WinIsland.App.Core;
using Xunit;

namespace WinIsland.Tests
{
    public class SettingsTests
    {
        [Fact]
        public void AppSettings_DefaultValues_AreCorrect()
        {
            var settings = new AppSettings();

            Assert.Equal(250, settings.HoverDelayMs);
            Assert.Equal(300, settings.CollapseDelayMs);
            Assert.False(settings.RequireAltKey);
            Assert.True(settings.EnableAutoHideFullScreen);
            Assert.True(settings.EnableMediaModule);
            Assert.True(settings.EnableFileShelfModule);
            Assert.True(settings.EnableTelemetryModule);
            Assert.True(settings.EnablePomodoroModule);
            Assert.True(settings.EnablePixelPet);
            Assert.False(settings.RunOnStartup);
        }

        [Fact]
        public void AppSettings_SerializesAndDeserializes_Correctly()
        {
            var original = new AppSettings
            {
                HoverDelayMs = 400,
                RequireAltKey = true,
                RunOnStartup = true
            };

            string json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(400, deserialized!.HoverDelayMs);
            Assert.True(deserialized.RequireAltKey);
            Assert.True(deserialized.RunOnStartup);
        }
    }
}
