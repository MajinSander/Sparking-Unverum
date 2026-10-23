using System;
using System.IO;
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

        protected override void OnStartup(StartupEventArgs e)
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
                    MessageBox.Show(Loc.T("App.AlreadyRunning"), "Majin Unverum", MessageBoxButton.OK, MessageBoxImage.Exclamation);
                }
                Shutdown();
                return;
            }

            if (!FolderAccepted())
            {
                Shutdown();
                return;
            }
            Global.LoadConfig();
            Loc.Init(Global.config.Language);
            RegistryConfig.InstallGBHandler();

            // Opened from a 1-click install button: install the mod and skip the self update check
            var window = new MainWindow(checkAppUpdate: link == null);
            MainWindow = window;
            window.Show();
            SingleInstance.Listen(message => Dispatcher.InvokeAsync(() => window.HandleExternalMessage(message)));
            if (link != null)
                window.HandleExternalMessage(link);
        }

        // Mods, loadouts and settings live next to the program: the first time, suggest its own folder
        // instead of Downloads, the Desktop or a temporary folder
        private static bool FolderAccepted()
        {
            if (File.Exists(Global.ConfigPath) || File.Exists(Global.PreviousConfigPath) || File.Exists(Global.ClassicConfigPath))
                return true;
            var folder = Path.GetFullPath(Global.assemblyLocation).TrimEnd(Path.DirectorySeparatorChar);
            var crowded = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            }.Where(f => !string.IsNullOrEmpty(f)).Select(f => Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar));
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!crowded.Any(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase))
                && !folder.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
                return true;
            Loc.Init(null);
            if (MessageBox.Show(Loc.T("App.FolderWarning", folder), "Majin Unverum", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                return true;
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Environment.ProcessPath}\"");
            }
            catch { }
            return false;
        }

        private const string RestartArgument = "--restart";
        // Opens a new Majin Unverum and closes this one, used after changing the language
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
                "Majin Unverum", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            (Current?.MainWindow as MainWindow)?.SetBusy(false);
        }
    }
}
