using System;
using System.Windows;

namespace WinIsland.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Ensure MainWindow is the sole primary entry point window
            StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
            base.OnStartup(e);
        }
    }
}

