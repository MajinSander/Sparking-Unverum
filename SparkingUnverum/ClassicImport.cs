using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace SparkingUnverum
{
    // Brings the Sparking! ZERO mods and loadouts from a classic Unverum folder
    public static class ClassicImport
    {
        public static bool Run(Window owner)
        {
            var dialog = new OpenFolderDialog { Title = Loc.T("Import.SelectFolder"), Multiselect = false };
            if (dialog.ShowDialog(owner) != true)
                return false;
            var folder = dialog.FolderName;
            var classicMods = Path.Combine(folder, "Mods", SparkingGame.ConfigKey);
            var classicConfig = Path.Combine(folder, "Config.json");
            if (!Directory.Exists(classicMods) && !File.Exists(classicConfig))
            {
                MessageBox.Show(Loc.T("Import.NotFound", folder), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (Path.GetFullPath(classicMods).TrimEnd('\\').Equals(Path.GetFullPath(Global.ModsPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(Loc.T("Import.SameFolder"), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            var copied = 0;
            try
            {
                if (Directory.Exists(classicMods))
                    foreach (var mod in Directory.GetDirectories(classicMods))
                    {
                        var target = Path.Combine(Global.ModsPath, Path.GetFileName(mod));
                        if (Directory.Exists(target))
                            continue;
                        ArchiveHelper.CopyDirectory(mod, target);
                        copied++;
                    }
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Import.Error", e.Message), LoggerType.Error);
            }

            var loadouts = 0;
            if (File.Exists(classicConfig))
            {
                try
                {
                    var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(classicConfig));
                    if (config?.Configs != null && config.Configs.TryGetValue(SparkingGame.ConfigKey, out var classic) && classic != null)
                    {
                        var game = Global.Game;
                        if (string.IsNullOrEmpty(game.Launcher) && !string.IsNullOrEmpty(classic.Launcher) && File.Exists(classic.Launcher))
                        {
                            game.Launcher = classic.Launcher;
                            game.ModsFolder = classic.ModsFolder;
                            game.LauncherOptionIndex = classic.LauncherOptionIndex;
                        }
                        var classicLoadouts = classic.Loadouts ?? new();
                        if (classic.ModList != null && !classicLoadouts.ContainsKey("Default"))
                            classicLoadouts["Default"] = classic.ModList;
                        foreach (var loadout in classicLoadouts.Where(x => x.Value != null))
                        {
                            var name = game.Loadouts.ContainsKey(loadout.Key) ? $"{loadout.Key} (Unverum)" : loadout.Key;
                            if (game.Loadouts.ContainsKey(name))
                                continue;
                            game.Loadouts[name] = new ObservableCollection<Mod>(loadout.Value.Select(m => new Mod
                            {
                                name = m.name,
                                enabled = m.enabled,
                                // Pak paths pointed to the classic folder
                                paks = m.paks?.ToDictionary(p => p.Key.Replace(classicMods, Global.ModsPath, StringComparison.OrdinalIgnoreCase), p => p.Value)
                            }));
                            Global.LoadoutItems.Add(name);
                            loadouts++;
                        }
                    }
                }
                catch (Exception e)
                {
                    Global.logger.WriteLine(Loc.T("Import.Error", e.Message), LoggerType.Error);
                }
            }
            Global.UpdateConfig();
            Global.logger.WriteLine(Loc.T("Import.Done", copied, loadouts), LoggerType.Info);
            MessageBox.Show(Loc.T("Import.Done", copied, loadouts), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
            return copied > 0 || loadouts > 0;
        }
    }
}
