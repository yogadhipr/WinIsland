using System;
using System.Windows.Controls;
using System.Windows.Threading;
using WinIsland.App.ViewModels;

namespace WinIsland.App.Views.Modules
{
    public partial class MediaWidget : UserControl
    {
        private readonly DispatcherTimer _animTimer;
        private readonly Random _rand = new Random();
        private double _phase;

        public MediaWidget()
        {
            InitializeComponent();

            _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _animTimer.Tick += OnAnimTick;
            _animTimer.Start();
        }

        private void OnAnimTick(object? sender, EventArgs e)
        {
            if (DataContext is MainIslandViewModel vm && vm.MediaInfo.IsPlaying)
            {
                _phase += 0.3;
                float peak = Math.Max(vm.AudioPeak, 0.25f);

                Bar1.Height = Math.Clamp(4 + Math.Sin(_phase) * 6 * peak + _rand.Next(0, 3), 3, 14);
                Bar2.Height = Math.Clamp(6 + Math.Cos(_phase * 1.2) * 8 * peak + _rand.Next(0, 3), 3, 14);
                Bar3.Height = Math.Clamp(8 + Math.Sin(_phase * 0.8) * 9 * peak + _rand.Next(0, 4), 3, 14);
                Bar4.Height = Math.Clamp(5 + Math.Cos(_phase * 1.5) * 7 * peak + _rand.Next(0, 3), 3, 14);
                Bar5.Height = Math.Clamp(7 + Math.Sin(_phase * 1.1) * 8 * peak + _rand.Next(0, 4), 3, 14);
                Bar6.Height = Math.Clamp(9 + Math.Cos(_phase * 0.9) * 9 * peak + _rand.Next(0, 4), 3, 14);
            }
            else
            {
                // Idle bars
                Bar1.Height = 3;
                Bar2.Height = 5;
                Bar3.Height = 7;
                Bar4.Height = 5;
                Bar5.Height = 4;
                Bar6.Height = 3;
            }
        }
    }
}
