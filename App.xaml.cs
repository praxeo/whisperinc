using System;
using System.IO;
using System.Threading.Tasks;

namespace WhisperInk
{
    public partial class App : System.Windows.Application
    {
        // Must match MainWindow.ConfigFolder so all crash entries land in the
        // same debug.log the rest of the app writes to.
        private static readonly string CrashLog =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".WhisperInk",
                "debug.log");

        private static void WriteCrash(string source, Exception? ex)
        {
            try
            {
                string msg = ex == null
                    ? "(null exception)"
                    : $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}";
                File.AppendAllText(
                    CrashLog,
                    $"[{DateTime.Now:HH:mm:ss.fff}] !!! {source} !!! {msg}\n");
            }
            catch { }
        }

        public App()
        {
            // async void exceptions on the WPF UI thread surface here
            // because DispatcherSynchronizationContext is captured at the
            // first await. e.Handled=true keeps the app alive so later
            // [diag] log lines still flush instead of being lost to a
            // mid-write process exit.
            DispatcherUnhandledException += (s, e) =>
            {
                WriteCrash("DispatcherUnhandledException", e.Exception);
                e.Handled = true;
            };

            // Backstop for non-UI threads: the WH_KEYBOARD_LL hook thread
            // and the WaveIn callback thread don't route through the
            // dispatcher, so an exception there would otherwise vanish.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                WriteCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                WriteCrash("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }
    }
}
