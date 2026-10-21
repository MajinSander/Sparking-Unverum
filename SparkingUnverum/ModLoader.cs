using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SearchOption = System.IO.SearchOption;

namespace SparkingUnverum
{
    public class BuildResult
    {
        public bool Success;
        // SparkingZERO Mod Loader (SZModLib/ZeroSpark) is needed by some enabled mods but isn't part of the loadout
        public bool MissingModLoader;
        public int Conflicts;
    }

    public static class ModLoader
    {
        private static string ManifestPath => Path.Combine(Global.assemblyLocation, "deployed.json");
        // Priority folders created by Sparking Unverum and the classic Unverum inside ~mods
        public static readonly Regex PriorityFolder = new(@"^(~*[a-z]|--Base--)$");
        private static readonly string[] Ue4ssFiles = { "opengl32.dll", "patternsleuth_bind.dll", "ue4ss.dll", "UE4SS-settings.ini", "dwmapi.dll" };
        private static readonly string[] DefaultUe4ssMods =
        {
            "CheatManagerEnablerMod", "ActorDumperMod", "ConsoleCommandsMod", "ConsoleEnablerMod", "SplitScreenMod",
            "LineTraceMod", "BPModLoaderMod", "BPML_GenericFunctions", "jsbLuaProfilerMod", "shared", "Keybinds"
        };

        private static DeployManifest LoadManifest()
        {
            try
            {
                if (File.Exists(ManifestPath))
                    return JsonSerializer.Deserialize<DeployManifest>(File.ReadAllText(ManifestPath));
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Build.ManifestError", e.Message), LoggerType.Warning);
            }
            return null;
        }
        private static void SaveManifest(DeployManifest manifest)
        {
            try
            {
                File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, Global.JsonOptions));
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Build.ManifestError", e.Message), LoggerType.Warning);
            }
        }

        // Removes what the previous build put in the game. Anything Sparking Unverum didn't put there is moved to Backups instead of deleted
        public static bool Restart(GamePaths paths, string movies, string splash, string sound)
        {
            var backupRoot = Path.Combine(Global.BackupsPath, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            var backedUp = 0;
            try
            {
                var manifest = LoadManifest();
                if (manifest != null && string.Equals(manifest.GameRoot, paths.Root, StringComparison.OrdinalIgnoreCase))
                    foreach (var file in manifest.Files)
                        if (File.Exists(file))
                            File.Delete(file);

                // ~mods, the priority folders can always be rebuilt
                if (Directory.Exists(paths.TildeMods))
                {
                    foreach (var dir in Directory.GetDirectories(paths.TildeMods))
                    {
                        if (PriorityFolder.IsMatch(Path.GetFileName(dir)))
                            Directory.Delete(dir, true);
                        else
                            backedUp += MoveToBackup(dir, paths, backupRoot);
                    }
                    foreach (var file in Directory.GetFiles(paths.TildeMods))
                        backedUp += MoveToBackup(file, paths, backupRoot);
                }
                Directory.CreateDirectory(paths.TildeMods);

                // Folders that only exist because of mods
                foreach (var folder in new[] { paths.LogicMods, paths.SZModLib, paths.Ue4ssMods })
                {
                    if (!Directory.Exists(folder))
                        continue;
                    foreach (var entry in Directory.GetFileSystemEntries(folder))
                        backedUp += MoveToBackup(entry, paths, backupRoot);
                    DeleteIfEmpty(folder);
                }
                foreach (var ue4ssFile in Ue4ssFiles)
                {
                    var file = Path.Combine(paths.Win64, ue4ssFile);
                    if (File.Exists(file))
                        backedUp += MoveToBackup(file, paths, backupRoot);
                }

                // Reset movies, splash and sound files replaced by loose file mods
                foreach (var folder in new[] { movies, splash, sound })
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                        RestoreDirectory(folder);

                SaveManifest(new DeployManifest { GameRoot = paths.Root });
                if (backedUp > 0)
                    Global.logger.WriteLine(Loc.T("Build.BackedUp", backedUp, backupRoot), LoggerType.Warning);
                Global.logger.WriteLine(Loc.T("Build.Restored"), LoggerType.Info);
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(e.Message, LoggerType.Error);
                return false;
            }
            return true;
        }

        private static int MoveToBackup(string path, GamePaths paths, string backupRoot)
        {
            var count = 0;
            var target = Path.Combine(backupRoot, Path.GetRelativePath(paths.Root, path));
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    var fileTarget = Path.Combine(target, Path.GetRelativePath(path, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(fileTarget));
                    File.Move(file, fileTarget, true);
                    count++;
                }
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Move(path, target, true);
                count++;
            }
            return count;
        }

        private static void DeleteIfEmpty(string folder)
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var dir in Directory.GetDirectories(folder))
                DeleteIfEmpty(dir);
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }

        private static void RestoreDirectory(string path)
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                if (File.Exists($"{file}.bak"))
                    File.Move($"{file}.bak", file, true);
        }

        private class Deployment
        {
            public DeployManifest Manifest;
            // Which mod wrote each file, to report mods overwriting each other
            public Dictionary<string, string> WrittenBy = new(StringComparer.OrdinalIgnoreCase);
            public int Conflicts;

            public void Copy(string source, string target, string mod)
            {
                if (WrittenBy.TryGetValue(target, out var previous) && previous != mod)
                {
                    Conflicts++;
                    Global.logger.WriteLine(Loc.T("Build.Conflict", mod, Path.GetFileName(target), previous), LoggerType.Warning);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, true);
                WrittenBy[target] = mod;
                Manifest.Files.Add(target);
            }
            public void CopyDirectory(string source, string target, string mod, Func<string, bool> skip = null)
            {
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                    if (skip == null || !skip(file))
                        Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), mod);
            }
        }

        private static string PriorityName(int index)
        {
            // a, b, ..., z, ~a, ~b, ..., ~z, ~~a...
            return new string('~', index / 26) + (char)('a' + index % 26);
        }

        private static string WithSuffix(string file, string suffix)
        {
            return Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + suffix + Path.GetExtension(file));
        }

        private static void ReplaceAsset(string file, string path)
        {
            var filesFound = 0;
            foreach (var oldFile in Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                            .Where(a => Path.GetFileName(a).Equals(Path.GetFileName(file), StringComparison.InvariantCultureIgnoreCase)))
            {
                if (!File.Exists($"{oldFile}.bak"))
                    File.Copy(oldFile, $"{oldFile}.bak", true);
                File.Copy(file, oldFile, true);
                Global.logger.WriteLine(Loc.T("Build.Replaced", oldFile, Path.GetFileName(file)), LoggerType.Info);
                filesFound++;
            }
            if (filesFound == 0)
                Global.logger.WriteLine(Loc.T("Build.ReplaceNotFound", Path.GetFileName(file), path), LoggerType.Warning);
        }

        // Copies the mods into the game, mods must be ordered from lowest to highest priority
        public static BuildResult Build(GamePaths paths, List<Mod> mods, string movies, string splash, string sound)
        {
            var result = new BuildResult();
            var deployment = new Deployment { Manifest = new DeployManifest { GameRoot = paths.Root } };
            string sig = Directory.Exists(paths.Paks) ? Directory.GetFiles(paths.Paks, "*.sig", SearchOption.TopDirectoryOnly).FirstOrDefault() : null;
            var priority = 0;
            var ue4ss = false;
            var usesModLoader = false;
            List<string> zeroSparkJsons = null;
            try
            {
                foreach (var mod in mods)
                {
                    var modPath = Path.Combine(Global.ModsPath, mod.name);
                    if (!Directory.Exists(modPath))
                        continue;

                    // Folders with a .uplugin are SZModLib plugins, they are copied as they are to SparkingZERO/Mods
                    var plugins = Directory.EnumerateFiles(modPath, "*.uplugin", SearchOption.AllDirectories)
                        .Select(Path.GetDirectoryName).Distinct().ToList();
                    foreach (var plugin in plugins)
                    {
                        Global.logger.WriteLine(Loc.T("Build.AddingPlugin", Path.GetFileName(plugin)), LoggerType.Info);
                        deployment.CopyDirectory(plugin, Path.Combine(paths.SZModLib, Path.GetFileName(plugin)), mod.name);
                        usesModLoader = true;
                    }
                    bool InPlugin(string file) => plugins.Any(p => file.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

                    // Paks (with their utoc/ucas) go to ~mods in priority order
                    var folder = Path.Combine(paths.TildeMods, PriorityName(priority), mod.name);
                    var copiedPaks = 0;
                    foreach (var pak in Directory.GetFiles(modPath, "*.pak", SearchOption.AllDirectories))
                    {
                        if (InPlugin(pak) || mod.paks == null || !mod.paks.TryGetValue(pak, out var pakEnabled) || !pakEnabled)
                            continue;
                        var target = WithSuffix(Path.Combine(folder, Path.GetRelativePath(modPath, pak)), "_9_P");
                        deployment.Copy(pak, target, mod.name);
                        foreach (var extension in new[] { ".utoc", ".ucas" })
                        {
                            var companion = Path.ChangeExtension(pak, extension);
                            if (File.Exists(companion))
                                deployment.Copy(companion, Path.ChangeExtension(target, extension), mod.name);
                        }
                        if (sig != null)
                        {
                            var modSig = Path.ChangeExtension(pak, ".sig");
                            deployment.Copy(File.Exists(modSig) ? modSig : sig, Path.ChangeExtension(target, ".sig"), mod.name);
                        }
                        copiedPaks++;
                    }
                    if (copiedPaks > 0)
                    {
                        Global.logger.WriteLine(Loc.T("Build.CopiedPaks", copiedPaks, mod.name, PriorityName(priority)), LoggerType.Info);
                        priority++;
                    }

                    // UE4SS lua mods and blueprint LogicMods
                    foreach (var ue4ssModDirectory in Directory.GetDirectories(modPath, "*ue4ss", SearchOption.AllDirectories))
                    {
                        deployment.CopyDirectory(ue4ssModDirectory, paths.Ue4ssMods, mod.name,
                            file => Path.GetFileName(file).Equals("mods.txt", StringComparison.OrdinalIgnoreCase));
                        ue4ss = true;
                    }
                    foreach (var logicModDirectory in Directory.GetDirectories(modPath, "*LogicMods", SearchOption.AllDirectories))
                    {
                        deployment.CopyDirectory(logicModDirectory, paths.LogicMods, mod.name);
                        ue4ss = true;
                    }

                    // ASI plugins are loaded by the ASI loader installed with the UTOC bypass, their .ini files go next to them
                    var asiFolders = Directory.GetFiles(modPath, "*.asi", SearchOption.AllDirectories).Where(x => !InPlugin(x))
                        .Select(Path.GetDirectoryName).Distinct().ToList();
                    foreach (var asiFolder in asiFolders)
                        foreach (var file in Directory.GetFiles(asiFolder, "*", SearchOption.TopDirectoryOnly)
                            .Where(x => Path.GetExtension(x).Equals(".asi", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(x).Equals(".ini", StringComparison.OrdinalIgnoreCase)))
                        {
                            var target = Path.Combine(paths.AsiPlugins, Path.GetFileName(file));
                            // Never replace files that were already in the game, like the UTOC bypass itself
                            if (File.Exists(target) && !deployment.WrittenBy.ContainsKey(target))
                            {
                                Global.logger.WriteLine(Loc.T("Build.AsiExists", Path.GetFileName(file)), LoggerType.Info);
                                continue;
                            }
                            Global.logger.WriteLine(Loc.T("Build.AddingAsi", Path.GetFileName(file)), LoggerType.Info);
                            deployment.Copy(file, target, mod.name);
                        }

                    // Loose files
                    foreach (var file in Directory.GetFiles(modPath, "*", SearchOption.AllDirectories).Where(x => !InPlugin(x)))
                    {
                        switch (Path.GetExtension(file).ToLowerInvariant())
                        {
                            case ".usm":
                            case ".mp4":
                                if (!string.IsNullOrEmpty(movies) && Directory.Exists(movies))
                                    ReplaceAsset(file, movies);
                                break;
                            case ".bmp":
                                if (!string.IsNullOrEmpty(splash) && Directory.Exists(splash))
                                    ReplaceAsset(file, splash);
                                break;
                            case ".awb":
                                if (!string.IsNullOrEmpty(sound) && Directory.Exists(sound))
                                    ReplaceAsset(file, sound);
                                break;
                            case ".json":
                                if (Path.GetFileName(file).Equals("mod.json", StringComparison.OrdinalIgnoreCase))
                                    break;
                                if (IsZeroSparkJson(file))
                                {
                                    Global.logger.WriteLine(Loc.T("Build.AddingZeroSpark", Path.GetFileName(file)), LoggerType.Info);
                                    deployment.Copy(file, Path.Combine(paths.ZeroSparkJson, Path.GetFileName(file)), mod.name);
                                    zeroSparkJsons ??= new();
                                    zeroSparkJsons.Add(Path.GetFileNameWithoutExtension(file));
                                    usesModLoader = true;
                                }
                                break;
                        }
                    }
                }

                // UE4SS itself is only installed when a mod needs it
                if (ue4ss)
                {
                    var ue4ssSource = Path.Combine(Global.assemblyLocation, "Dependencies", "ue4ss");
                    deployment.CopyDirectory(ue4ssSource, paths.Win64, "UE4SS",
                        file => Path.GetFileName(file).Equals("mods.txt", StringComparison.OrdinalIgnoreCase));
                    // Enable every mod folder that isn't one of the defaults
                    var lines = File.ReadAllLines(Path.Combine(ue4ssSource, "Mods", "mods.txt")).ToList();
                    foreach (var directory in Directory.GetDirectories(paths.Ue4ssMods, "*", SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileName(directory);
                        if (!DefaultUe4ssMods.Contains(name))
                            lines.Insert(Math.Max(0, lines.Count - 1), $"{name} : 1");
                    }
                    var modsFile = Path.Combine(paths.Ue4ssMods, "mods.txt");
                    File.WriteAllLines(modsFile, lines);
                    deployment.Manifest.Files.Add(modsFile);
                }

                // Register the ZeroSpark jsons
                if (zeroSparkJsons != null)
                {
                    var jsonFiles = Path.Combine(paths.ZeroSparkJson, "JsonFiles.json");
                    if (File.Exists(jsonFiles))
                    {
                        var jsonObject = JsonNode.Parse(File.ReadAllText(jsonFiles));
                        if (jsonObject != null)
                        {
                            var array = new JsonArray();
                            foreach (var json in zeroSparkJsons)
                                array.Add(json);
                            // Key name used by the classic Unverum, ZeroSpark reads every key
                            jsonObject["Unverum"] = array;
                            File.WriteAllText(jsonFiles, jsonObject.ToJsonString());
                        }
                    }
                }

                if (usesModLoader && (!File.Exists(Path.Combine(paths.SZModLib, "SZModLib", "SZModLib.uplugin"))
                    || (zeroSparkJsons != null && !File.Exists(Path.Combine(paths.ZeroSparkJson, "JsonFiles.json")))))
                {
                    result.MissingModLoader = true;
                    Global.logger.WriteLine(Loc.T("Build.MissingModLoader"), LoggerType.Warning);
                }
                result.Conflicts = deployment.Conflicts;
                result.Success = true;
                Global.logger.WriteLine(Loc.T("Build.Finished"), LoggerType.Info);
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Build.Error", e.Message), LoggerType.Error);
            }
            SaveManifest(deployment.Manifest);
            return result;
        }

        private static bool IsZeroSparkJson(string file)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("ZeroSparkVersion", out _);
            }
            catch
            {
                return false;
            }
        }
    }
}
