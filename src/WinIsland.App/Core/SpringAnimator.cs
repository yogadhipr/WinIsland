using System;
using System.Diagnostics;
using System.Windows.Media;

namespace WinIsland.App.Core
{
    public class SpringValue
    {
        public double CurrentValue { get; set; }
        public double TargetValue { get; set; }
        public double Velocity { get; set; }

        public double Stiffness { get; set; } = 320.0;
        public double Damping { get; set; } = 26.0;
        public double Mass { get; set; } = 1.0;
        public double Epsilon { get; set; } = 0.01;
        public double VelocityEpsilon { get; set; } = 0.02;

        public bool IsAtRest { get; private set; } = true;

        public SpringValue(double initialValue, double stiffness = 320.0, double damping = 26.0, double mass = 1.0)
        {
            CurrentValue = initialValue;
            TargetValue = initialValue;
            Velocity = 0.0;
            Stiffness = stiffness;
            Damping = damping;
            Mass = mass;
            IsAtRest = true;
        }

        public void SetTarget(double target)
        {
            TargetValue = target;
            IsAtRest = false;
        }

        public void SnapTo(double value)
        {
            CurrentValue = value;
            TargetValue = value;
            Velocity = 0.0;
            IsAtRest = true;
        }

        public void Step(double dt)
        {
            if (IsAtRest) return;

            // F_spring = -k * (x - x_target) - c * v
            double displacement = CurrentValue - TargetValue;
            double springForce = -Stiffness * displacement;
            double dampingForce = -Damping * Velocity;
            double totalForce = springForce + dampingForce;
            double acceleration = totalForce / Mass;

            Velocity += acceleration * dt;
            CurrentValue += Velocity * dt;

            if (Math.Abs(displacement) <= Epsilon && Math.Abs(Velocity) <= VelocityEpsilon)
            {
                CurrentValue = TargetValue;
                Velocity = 0.0;
                IsAtRest = true;
            }
        }
    }

    public class SpringAnimator
    {
        public SpringValue Width { get; }
        public SpringValue Height { get; }
        public SpringValue CornerRadius { get; }
        public SpringValue Opacity { get; }
        public SpringValue OffsetY { get; }

        private readonly Stopwatch _stopwatch = new Stopwatch();
        private double _lastTimeSeconds;
        private bool _isHooked;

        public event Action<SpringAnimator>? Updated;
        public event Action? RestReached;

        public bool IsRunning => !Width.IsAtRest || !Height.IsAtRest || !CornerRadius.IsAtRest || !Opacity.IsAtRest || !OffsetY.IsAtRest;

        public SpringAnimator(double initialWidth = 180, double initialHeight = 34, double initialRadius = 17, double initialOpacity = 1.0, double initialOffsetY = 0)
        {
            Width = new SpringValue(initialWidth);
            Height = new SpringValue(initialHeight);
            CornerRadius = new SpringValue(initialRadius);
            Opacity = new SpringValue(initialOpacity);
            OffsetY = new SpringValue(initialOffsetY);
        }

        public void Start()
        {
            if (!_isHooked)
            {
                _stopwatch.Restart();
                _lastTimeSeconds = 0;
                CompositionTarget.Rendering += OnRendering;
                _isHooked = true;
            }
        }

        public void Stop()
        {
            if (_isHooked)
            {
                CompositionTarget.Rendering -= OnRendering;
                _isHooked = false;
                _stopwatch.Stop();
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            double currentTime = _stopwatch.Elapsed.TotalSeconds;
            double dt = currentTime - _lastTimeSeconds;
            _lastTimeSeconds = currentTime;

            // Clamp dt to avoid physics explosion on sudden delay
            if (dt <= 0) dt = 1.0 / 60.0;
            if (dt > 0.05) dt = 0.05;

            Step(dt);

            Updated?.Invoke(this);

            if (!IsRunning)
            {
                Stop();
                RestReached?.Invoke();
            }
        }

        public void Step(double dt)
        {
            Width.Step(dt);
            Height.Step(dt);
            CornerRadius.Step(dt);
            Opacity.Step(dt);
            OffsetY.Step(dt);
        }

        /// <summary>
        /// Deterministic simulation runner useful for unit testing and convergence verification.
        /// </summary>
        public int SimulateUntilRest(double stepDt = 1.0 / 60.0, int maxSteps = 300)
        {
            int steps = 0;
            while (IsRunning && steps < maxSteps)
            {
                Step(stepDt);
                steps++;
            }
            return steps;
        }

        public void AnimateTo(double targetWidth, double targetHeight, double targetRadius, double targetOpacity = 1.0, double targetOffsetY = 0)
        {
            Width.SetTarget(targetWidth);
            Height.SetTarget(targetHeight);
            CornerRadius.SetTarget(targetRadius);
            Opacity.SetTarget(targetOpacity);
            OffsetY.SetTarget(targetOffsetY);

            Start();
        }
    }
}
