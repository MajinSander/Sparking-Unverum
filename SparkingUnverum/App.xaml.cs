using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using SparkingUnverum.UI;

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

            if (e.Args.Contains(Installer.UninstallArgument))
            {
                Uninstall();
                Shutdown();
                return;
            }

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

            var target = Installer.Default();
            if (Installer.ShouldOffer(target))
            {
                // The downloaded .exe: install it, or keep going from here if the user wants it portable
                Loc.Init(null);
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var setup = new InstallWindow(target);
                setup.ShowDialog();
                if (setup.Choice != InstallChoice.Portable)
                {
                    if (setup.Choice == InstallChoice.Installed)
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup.Target.Exe, RestartArgument)
                        {
                            UseShellExecute = false,
                            WorkingDirectory = setup.Target.Folder
                        });
                    Shutdown();
                    return;
                }
            }
            else if (!FolderAccepted())
            {
                Shutdown();
                return;
            }
            Global.LoadConfig();
            Loc.Init(Global.config.Language);
            if (Global.config.RegisterOneClick)
                RegistryConfig.InstallGBHandler();
            Installer.UpdateVersion(target);

            // Opened from a 1-click install button: install the mod and skip the self update check
            var window = new MainWindow(checkAppUpdate: link == null);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            SingleInstance.Listen(message => Dispatcher.InvokeAsync(() => window.HandleExternalMessage(message)));
            if (link != null)
                window.HandleExternalMessage(link);
        }

        // From the installed apps of Windows: takes the mods out of the game, then the program
        private static void Uninstall()
        {
            Global.LoadConfig();
            Loc.Init(Global.config.Language);
            // Only to the log file, there is no window
            Global.logger = new Logger(null);
            if (!SingleInstance.TryAcquire())
            {
                MessageBox.Show(Loc.T("Uninstall.Running"), "Majin Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(Loc.T("Uninstall.Confirm"), "Majin Unverum", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            var paths = GamePaths.Current;
            if (paths != null && Directory.Exists(paths.Content))
            {
                if (GameProcess.IsRunning())
                {
                    MessageBox.Show(Loc.T("Uninstall.GameRunning"), "Majin Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var sound = Directory.Exists(Path.Combine(paths.Content, "Sound")) ? Path.Combine(paths.Content, "Sound") : Path.Combine(paths.Content, "CriWareData");
                bool removed;
                try
                {
                    removed = ModLoader.Restart(paths, Path.Combine(paths.Content, "Movies"), Path.Combine(paths.Content, "Splash"), sound);
                }
                catch
                {
                    removed = false;
                }
                if (!removed)
                    MessageBox.Show(Loc.T("Uninstall.ModsFailed"), "Majin Unverum", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            var target = Installer.Default();
            var folder = Installer.InstalledFolder(target) ?? Global.assemblyLocation;
            var size = StringConverters.FormatSize(new DirectoryInfo(folder).GetDirectorySize());
            var deleteData = MessageBox.Show(Loc.T("Uninstall.DeleteData", folder, size), "Majin Unverum", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
            Installer.Uninstall(target, Environment.ProcessPath, Environment.ProcessId, deleteData);
            MessageBox.Show(Loc.T("Uninstall.Done"), "Majin Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
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
