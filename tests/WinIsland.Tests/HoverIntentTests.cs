using System;
using System.Threading.Tasks;
using WinIsland.App.Core;
using Xunit;

namespace WinIsland.Tests
{
    public class HoverIntentTests
    {
        [Fact]
        public async Task Hover_CancelledBefore250ms_DoesNotTriggerExpand()
        {
            using var controller = new HoverIntentController
            {
                HoverDelayMs = 250
            };

            bool expandedTriggered = false;
            controller.ExpandRequested += () => expandedTriggered = true;

            // Start hover
            var hoverTask = controller.TriggerMouseEnterAsync(isAltDown: false);

            // Cancel after 100ms (< 250ms)
            await Task.Delay(100);
            controller.TriggerMouseLeave();

            // Wait until after 300ms total
            await Task.Delay(200);

            Assert.False(expandedTriggered, "Expand was triggered even though hover was cancelled early!");
            Assert.False(controller.IsExpanded, "Controller state is expanded when it should have remained compact.");
        }

        [Fact]
        public async Task Hover_Exceeds250ms_TriggersExpand()
        {
            using var controller = new HoverIntentController
            {
                HoverDelayMs = 150 // Use 150ms for reliable quick test execution
            };

            bool expandedTriggered = false;
            controller.ExpandRequested += () => expandedTriggered = true;

            await controller.TriggerMouseEnterAsync();

            Assert.True(expandedTriggered, "Expand was not triggered after hover delay completed.");
            Assert.True(controller.IsExpanded, "Controller state should be expanded.");
        }

        [Fact]
        public void DragEnter_ImmediatelyExpands_WithoutDelay()
        {
            using var controller = new HoverIntentController
            {
                HoverDelayMs = 5000 // Long delay to prove drag bypasses it
            };

            bool fileDropTriggered = false;
            bool expandTriggered = false;

            controller.FileDropExpandRequested += () => fileDropTriggered = true;
            controller.ExpandRequested += () => expandTriggered = true;

            controller.TriggerDragEnter();

            Assert.True(fileDropTriggered, "File drop expand was not triggered immediately.");
            Assert.True(expandTriggered, "Expand was not triggered immediately on drag.");
            Assert.True(controller.IsExpanded, "Controller state should be expanded on drag.");
        }

        [Fact]
        public async Task RequireAltKey_BlocksExpansion_WhenAltNotPressed()
        {
            using var controller = new HoverIntentController
            {
                HoverDelayMs = 100,
                RequireAltKey = true
            };

            bool expandedTriggered = false;
            controller.ExpandRequested += () => expandedTriggered = true;

            await controller.TriggerMouseEnterAsync(isAltDown: false);

            Assert.False(expandedTriggered, "Expand should not trigger when Alt key is required but not pressed.");
            Assert.False(controller.IsExpanded);

            // Now press Alt
            await controller.TriggerMouseEnterAsync(isAltDown: true);
            Assert.True(expandedTriggered, "Expand should trigger when Alt key is pressed.");
            Assert.True(controller.IsExpanded);
        }
    }
}
