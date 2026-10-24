using System;
using WinIsland.App.Core;
using Xunit;

namespace WinIsland.Tests
{
    public class SpringAnimatorTests
    {
        [Fact]
        public void SpringValue_ConvergesToTarget_WithinTolerance()
        {
            // Arrange: start at 180, target 420
            var spring = new SpringValue(180, stiffness: 320.0, damping: 26.0, mass: 1.0);
            spring.SetTarget(420);

            // Act: simulate 60fps (dt ~ 0.01667s) for up to 35 steps (~0.58s)
            double dt = 1.0 / 60.0;
            int steps = 0;
            while (!spring.IsAtRest && steps < 60)
            {
                spring.Step(dt);
                steps++;
            }

            // Assert
            Assert.True(spring.IsAtRest, "Spring did not reach rest state within expected frames.");
            Assert.True(Math.Abs(spring.CurrentValue - 420.0) < 0.01, $"Current value {spring.CurrentValue} did not reach target 420 with tolerance 0.01.");
            Assert.Equal(0.0, spring.Velocity);
        }

        [Fact]
        public void SpringAnimator_TransitionsFromCompactToExpanded_Under400ms()
        {
            // Compact: 180 x 34, Expanded: 420 x 150
            var animator = new SpringAnimator(180, 34, 17, 1.0, 0);
            animator.AnimateTo(420, 150, 24);

            // 60 fps simulation: 24 steps = 400ms
            double dt = 1.0 / 60.0;
            for (int i = 0; i < 24; i++)
            {
                animator.Step(dt);
            }

            // At 400ms, it has reached over 99% of target
            Assert.True(Math.Abs(animator.Width.CurrentValue - 420.0) < 3.0, $"Width is {animator.Width.CurrentValue} at 400ms");
            Assert.True(Math.Abs(animator.Height.CurrentValue - 150.0) < 2.0, $"Height is {animator.Height.CurrentValue} at 400ms");
            Assert.True(Math.Abs(animator.CornerRadius.CurrentValue - 24.0) < 1.0, $"CornerRadius is {animator.CornerRadius.CurrentValue} at 400ms");

            // Verify full rest convergence occurs cleanly
            int totalSteps = 24;
            while (animator.IsRunning && totalSteps < 60)
            {
                animator.Step(dt);
                totalSteps++;
            }

            Assert.True(animator.Width.IsAtRest);
            Assert.True(Math.Abs(animator.Width.CurrentValue - 420.0) < 0.01);
        }
    }
}
