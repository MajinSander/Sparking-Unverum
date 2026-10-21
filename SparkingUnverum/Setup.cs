using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;

namespace SparkingUnverum
{
    public static class Setup
    {
        // Looks for SparkingZERO.exe in every Steam library, returns null if it isn't found
        public static string FindGame()
        {
            foreach (var candidate in Candidates())
            {
                try
                {
                    if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }
            return null;
        }

        private static IEnumerable<string> Candidates()
        {
            // Uninstall entry written by Steam, it can be on either registry view
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                string location = null;
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {SparkingGame.SteamAppId}");
                    location = key?.GetValue("InstallLocation") as string;
                }
                catch { }
                if (!string.IsNullOrEmpty(location))
                    yield return Path.Combine(location, SparkingGame.Exe);
            }
            // Every library listed in libraryfolders.vdf
            foreach (var library in SteamLibraries())
            {
                var manifest = Path.Combine(library, "steamapps", $"appmanifest_{SparkingGame.SteamAppId}.acf");
                var installDir = SparkingGame.SteamFolderName;
                try
                {
                    if (File.Exists(manifest))
                    {
                        var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                        if (match.Success)
                            installDir = match.Groups[1].Value;
                    }
                }
                catch { }
                yield return Path.Combine(library, "steamapps", "common", installDir, SparkingGame.Exe);
            }
            yield return $@"C:\Program Files (x86)\Steam\steamapps\common\{SparkingGame.SteamFolderName}\{SparkingGame.Exe}";
        }

        private static List<string> SteamLibraries()
        {
            var libraries = new List<string>();
            string steamPath = null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                steamPath = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
            }
            catch { }
            if (string.IsNullOrEmpty(steamPath))
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam");
                    steamPath = key?.GetValue("InstallPath") as string;
                }
                catch { }
            }
            if (string.IsNullOrEmpty(steamPath))
                return libraries;
            libraries.Add(steamPath);
            try
            {
                var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    {
                        var path = match.Groups[1].Value.Replace(@"\\", @"\");
                        if (!libraries.Contains(path, StringComparer.OrdinalIgnoreCase))
                            libraries.Add(path);
                    }
            }
            catch { }
            return libraries;
        }

        // Asks for the game location if needed and saves it, returns true if the setup is complete
        public static bool Run()
        {
            var exe = FindGame();
            if (exe != null)
                Global.logger.WriteLine(Loc.T("Setup.Found", exe), LoggerType.Info);
            else
            {
                Global.logger.WriteLine(Loc.T("Setup.NotFound"), LoggerType.Warning);
                exe = AskForExe();
                if (exe == null)
                    return false;
            }
            return Apply(exe);
        }

        public static string AskForExe()
        {
            string result = null;
            Application.Current.Dispatcher.Invoke(() =>
            {
                var dialog = new OpenFileDialog
                {
                    DefaultExt = ".exe",
                    Filter = $"{SparkingGame.DisplayName} ({SparkingGame.Exe})|{SparkingGame.Exe}",
                    Title = Loc.T("Setup.SelectExe", SparkingGame.Exe),
                    Multiselect = false
                };
                if (dialog.ShowDialog() == true)
                {
                    if (Path.GetFileName(dialog.FileName).Equals(SparkingGame.Exe, StringComparison.OrdinalIgnoreCase))
                        result = dialog.FileName;
                    else
                        Global.logger.WriteLine(Loc.T("Setup.InvalidExe"), LoggerType.Error);
                }
            });
            return result;
        }

        public static bool Apply(string exe)
        {
            var paths = new GamePaths(exe);
            if (!Directory.Exists(paths.Project))
            {
                Global.logger.WriteLine(Loc.T("Setup.InvalidFolder", paths.Root), LoggerType.Error);
                return false;
            }
            Directory.CreateDirectory(paths.TildeMods);
            Global.Game.ModsFolder = paths.TildeMods;
            Global.Game.Launcher = exe;
            InstallBypass(paths);
            Global.UpdateConfig();
            Global.logger.WriteLine(Loc.T("Setup.Done"), LoggerType.Info);
            return true;
        }

        // Signature/UTOC bypass needed so the game loads modded paks
        public static void InstallBypass(GamePaths paths)
        {
            var asi = Path.Combine(paths.Win64, "plugins", "DBSparkingZeroUTOCBypass.asi");
            if (File.Exists(asi))
                return;
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                foreach (var resourceName in assembly.GetManifestResourceNames().Where(x => x.StartsWith("Patch/")))
                {
                    var target = Path.Combine(paths.Win64, resourceName["Patch/".Length..].Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using var resource = assembly.GetManifestResourceStream(resourceName);
                    using var stream = new FileStream(target, FileMode.Create, FileAccess.Write);
                    resource.CopyTo(stream);
                }
                Global.logger.WriteLine(Loc.T("Setup.BypassInstalled"), LoggerType.Info);
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Setup.BypassError", e.Message), LoggerType.Error);
            }
        }

        public static bool IsComplete => !string.IsNullOrEmpty(Global.Game.Launcher) && File.Exists(Global.Game.Launcher)
            && !string.IsNullOrEmpty(Global.Game.ModsFolder);
    }
}
