using Microsoft.UI.Xaml;
using MusicPower3.Services; // <-- Added to resolve AudioEngine globally
using System;

namespace MusicPower3
{
    public partial class App : Application
    {
        public static AudioEngine? MusicEngine { get; private set; }
        private Window? m_window;

        public App()
        {
            this.InitializeComponent();
            MusicEngine = new AudioEngine();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();
            m_window.Activate();
        }
    }
}