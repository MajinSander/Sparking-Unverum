using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SparkingUnverum
{
    // Installs the single .exe for the current user: a folder of its own, shortcuts on the Desktop and the
    // Start menu and an entry in the installed apps of Windows, from where it can be uninstalled
    public static class Installer
    {
        public const string UninstallArgument = "--uninstall";
        public const string ExeName = "MajinUnverum.exe";
        private const string LinkName = "Majin Unverum.lnk";

        public class Target
        {
            public string Folder { get; set; }
            public string Desktop { get; set; }
            public string StartMenu { get; set; }
            public string RegistryName { get; set; }
            public string Exe => Path.Combine(Folder, ExeName);
            public string DesktopLink => Path.Combine(Desktop, LinkName);
            public string StartMenuLink => Path.Combine(StartMenu, LinkName);
            public string UninstallKey => $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{RegistryName}";
        }

        // C:\Users\<name>\Majin Unverum. The tests move everything into a folder of their own
        public static Target Default()
        {
            var test = Environment.GetEnvironmentVariable("MAJINUNVERUM_TEST_ROOT");
            if (!string.IsNullOrEmpty(test))
                return new Target
                {
                    Folder = Path.Combine(test, "Majin Unverum"),
                    Desktop = Path.Combine(test, "Desktop"),
                    StartMenu = Path.Combine(test, "StartMenu"),
                    RegistryName = "MajinUnverumTest"
                };
            return new Target
            {
                Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Majin Unverum"),
                Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                StartMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                RegistryName = "MajinUnverum"
            };
        }

        // The single .exe has no dll next to it
        public static bool IsSingleFile =>
            !File.Exists(Path.Combine(Global.assemblyLocation, Assembly.GetExecutingAssembly().GetName().Name + ".dll"));

        // Folder where Windows has it installed, null if it isn't
        public static string InstalledFolder(Target target)
        {
            using var key = Registry.CurrentUser.OpenSubKey(target.UninstallKey);
            return key?.GetValue("InstallLocation") as string;
        }

        private static bool SameFolder(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
            && Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

        // The single .exe opened somewhere without mods or settings of its own (a download) offers to install itself
        public static bool ShouldOffer(Target target)
        {
            var here = Global.assemblyLocation;
            if (!IsSingleFile || SameFolder(here, target.Folder) || SameFolder(here, InstalledFolder(target)))
                return false;
            return !File.Exists(Global.ConfigPath) && !File.Exists(Global.PreviousConfigPath) && !File.Exists(Global.ClassicConfigPath)
                && !Directory.Exists(Path.Combine(here, "Mods"));
        }

        // Copies the program, creates the shortcuts and the entry in Windows. Throws when the folder can't be used
        public static void Install(Target target, string source, bool desktop, bool startMenu)
        {
            Directory.CreateDirectory(target.Folder);
            if (!SameFolder(Path.GetDirectoryName(source), target.Folder) || !Path.GetFileName(source).Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                File.Copy(source, target.Exe, true);
            if (desktop)
                CreateShortcut(target.DesktopLink, target.Exe);
            if (startMenu)
                CreateShortcut(target.StartMenuLink, target.Exe);

            using var key = Registry.CurrentUser.CreateSubKey(target.UninstallKey);
            key.SetValue("DisplayName", "Majin Unverum");
            key.SetValue("DisplayVersion", App.Version);
            key.SetValue("Publisher", "Majin");
            key.SetValue("DisplayIcon", $"\"{target.Exe}\",0");
            key.SetValue("InstallLocation", target.Folder);
            key.SetValue("UninstallString", $"\"{target.Exe}\" {UninstallArgument}");
            key.SetValue("URLInfoAbout", SparkingGame.RepositoryUrl);
            key.SetValue("EstimatedSize", (int)(new FileInfo(target.Exe).Length / 1024), RegistryValueKind.DWord);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("DesktopShortcut", desktop ? target.DesktopLink : string.Empty);
            key.SetValue("StartMenuShortcut", startMenu ? target.StartMenuLink : string.Empty);
        }

        // After a self update the version shown by Windows follows the program
        public static void UpdateVersion(Target target)
        {
            try
            {
                if (!SameFolder(Global.assemblyLocation, InstalledFolder(target)))
                    return;
                using var key = Registry.CurrentUser.OpenSubKey(target.UninstallKey, true);
                if (key != null && key.GetValue("DisplayVersion") as string != App.Version)
                    key.SetValue("DisplayVersion", App.Version);
            }
            catch { }
        }

        // Removes shortcuts, the entry in Windows and the 1-click handler, then the program once it has closed.
        // The mods, loadouts and settings go to the Recycle Bin only when asked
        public static void Uninstall(Target target, string exe, int pid, bool deleteData)
        {
            var folder = InstalledFolder(target) ?? Path.GetDirectoryName(exe);
            using (var key = Registry.CurrentUser.OpenSubKey(target.UninstallKey))
            {
                foreach (var link in new[] { key?.GetValue("DesktopShortcut") as string, key?.GetValue("StartMenuShortcut") as string, target.DesktopLink, target.StartMenuLink })
                    if (!string.IsNullOrEmpty(link) && File.Exists(link))
                        File.Delete(link);
            }
            Registry.CurrentUser.DeleteSubKeyTree(target.UninstallKey, false);
            // The 1-click buttons stop opening it, unless they belong to another manager by now
            string handler;
            using (var command = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Unverum\shell\open\command"))
                handler = command?.GetValue("") as string;
            if (handler?.Contains(exe, StringComparison.OrdinalIgnoreCase) == true)
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Unverum", false);
            // The same for .majin lists
            using (var command = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{RegistryConfig.ListType}\shell\open\command"))
                handler = command?.GetValue("") as string;
            if (handler?.Contains(exe, StringComparison.OrdinalIgnoreCase) == true)
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{RegistryConfig.ListType}", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ModListShare.Extension, false);
            }

            static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
            var lines = new StringBuilder();
            lines.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            lines.AppendLine($"Wait-Process -Id {pid} -Timeout 120");
            lines.AppendLine("Start-Sleep -Milliseconds 500");
            if (deleteData)
            {
                lines.AppendLine("Add-Type -AssemblyName Microsoft.VisualBasic");
                lines.AppendLine($"[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory({Quote(folder)}, 'OnlyErrorDialogs', 'SendToRecycleBin')");
            }
            else
            {
                lines.AppendLine($"Remove-Item -LiteralPath {Quote(exe)} -Force");
                foreach (var leftover in new[] { "Dependencies", "Cache" })
                    lines.AppendLine($"Remove-Item -LiteralPath {Quote(Path.Combine(folder, leftover))} -Recurse -Force");
            }
            lines.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force");
            var script = Path.Combine(Path.GetTempPath(), "MajinUnverumUninstall.ps1");
            // Windows PowerShell needs the BOM to read accents in paths
            File.WriteAllText(script, lines.ToString(), new UTF8Encoding(true));
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script },
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            });
        }

        // .lnk through the Windows Script Host, available in every Windows
        public static void CreateShortcut(string link, string exe)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell");
            dynamic shell = Activator.CreateInstance(type);
            try
            {
                dynamic shortcut = shell.CreateShortcut(link);
                shortcut.TargetPath = exe;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
                shortcut.IconLocation = $"{exe},0";
                shortcut.Description = "Majin Unverum";
                shortcut.Save();
                Marshal.FinalReleaseComObject(shortcut);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        // Path a shortcut opens, for the tests
        public static string ShortcutTarget(string link)
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(type);
            try
            {
                dynamic shortcut = shell.CreateShortcut(link);
                string target = shortcut.TargetPath;
                Marshal.FinalReleaseComObject(shortcut);
                return target;
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
