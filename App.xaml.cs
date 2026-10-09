using Microsoft.UI.Xaml;
using MusicPower3.Services;
using System;

namespace MusicPower3
{
    public partial class App : Application
    {
        public static AudioEngine? MusicEngine { get; private set; }

        // File passed on the command line by Explorer ("Open with" / double-click), if any.
        public static string? LaunchFile { get; set; }

        // Raised when a second launch is forwarded to this instance. Null path means "just bring to front".
        public static event Action<string?>? FileActivated;
        public static void RaiseFileActivated(string? path) => FileActivated?.Invoke(path);

        private Window? m_window;

        public App()
        {
            this.UnhandledException += (sender, e) =>
            {
                try
                {
                    string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicPower3");
                    System.IO.Directory.CreateDirectory(dir);
                    string logFile = System.IO.Path.Combine(dir, "crash.log");
                    System.IO.File.AppendAllText(logFile, $"[{DateTime.UtcNow:O}] {e.Message}\n{e.Exception}\n\n");
                }
                catch { }
            };
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