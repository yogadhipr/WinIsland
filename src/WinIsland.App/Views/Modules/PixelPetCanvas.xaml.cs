using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WinIsland.App.Views.Modules
{
    public partial class PixelPetCanvas : UserControl
    {
        private readonly DispatcherTimer _animTimer;
        private int _tickCount;
        private bool _isBlinking;

        public PixelPetCanvas()
        {
            InitializeComponent();

            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _animTimer.Tick += OnPetTick;
            _animTimer.Start();
        }

        private void OnPetTick(object? sender, EventArgs e)
        {
            _tickCount++;

            // Tail Wagging
            TailRotation.Angle = (_tickCount % 4 < 2) ? -15 : 15;

            // Blinking every ~4 seconds (20 ticks)
            if (_tickCount % 20 == 0)
            {
                _isBlinking = true;
                LeftEye.Height = 1;
                LeftEye.Margin = new Thickness(0, 1, 0, 0);
                LeftEyeShine.Visibility = Visibility.Collapsed;

                RightEye.Height = 1;
                RightEye.Margin = new Thickness(0, 1, 0, 0);
                RightEyeShine.Visibility = Visibility.Collapsed;
            }
            else if (_isBlinking)
            {
                _isBlinking = false;
                LeftEye.Height = 3;
                LeftEye.Margin = new Thickness(0);
                LeftEyeShine.Visibility = Visibility.Visible;

                RightEye.Height = 3;
                RightEye.Margin = new Thickness(0);
                RightEyeShine.Visibility = Visibility.Visible;
            }
        }

        private void OnPetClicked(object sender, MouseButtonEventArgs e)
        {
            // Trigger floating heart animation
            var heartOpacityAnim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(800));
            var heartYAnim = new DoubleAnimation(0, -14, TimeSpan.FromMilliseconds(800));

            TxtHeart.BeginAnimation(UIElement.OpacityProperty, heartOpacityAnim);
            HeartTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, heartYAnim);

            e.Handled = true;
        }
    }
}
