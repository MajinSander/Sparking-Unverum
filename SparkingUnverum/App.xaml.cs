using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace SparkingUnverum
{
    public partial class App : Application
    {
        public static string Version
        {
            get
            {
                var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
                var plus = version.IndexOf('+');
                return plus >= 0 ? version[..plus] : version;
            }
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            var link = e.Args.Length > 1 && e.Args[0] == "-download" ? e.Args[1] : null;
            if (!SingleInstance.TryAcquire(e.Args.Contains(RestartArgument)))
            {
                // Already open, hand over the link (or just bring the window to the front)
                if (!SingleInstance.Send(link ?? SingleInstance.ActivateMessage))
                {
                    Loc.Init(null);
                    MessageBox.Show(Loc.T("App.AlreadyRunning"), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                }
                Shutdown();
                return;
            }

            Global.LoadConfig();
            Loc.Init(Global.config.Language);
            RegistryConfig.InstallGBHandler();

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            SingleInstance.Listen(message => Dispatcher.InvokeAsync(() => window.HandleExternalMessage(message)));

            // Self update check, skipped when opened from a 1-click install button
            if (link == null && Global.config.CheckAppUpdates && await AutoUpdater.CheckForUpdate(true))
            {
                window.Close();
                return;
            }
            if (link != null)
                window.HandleExternalMessage(link);
        }

        private const string RestartArgument = "--restart";
        // Opens a new Sparking Unverum and closes this one, used after changing the language
        public static void Restart()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath, RestartArgument) { UseShellExecute = false });
            }
            catch { }
            Current.MainWindow?.Close();
        }

        private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Global.logger?.WriteLine($"{e.Exception.Message}\n{e.Exception.StackTrace}", LoggerType.Error);
            MessageBox.Show(Loc.T("App.UnhandledException", e.Exception.Message, e.Exception.InnerException?.Message, e.Exception.StackTrace),
                "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            (Current?.MainWindow as MainWindow)?.SetBusy(false);
        }
    }
}
