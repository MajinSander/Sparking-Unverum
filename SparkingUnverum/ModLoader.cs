using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        public int Linked;
        public int Copied;
        public int Unchanged;
        public int Removed;
        // Old copies turned into hard links and the disk space they freed
        public int Relinked;
        public long FreedBytes;
        // Plugin folders linked as a whole and files the game wrote that were saved into their mods
        public int LinkedFolders;
        public int SavedBack;
        // Bytes that didn't have to be written thanks to hard links and unchanged files
        public long SavedBytes;
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
        // Big files are hard linked into the game instead of copied, small ones (configs the game may edit) are copied
        private static readonly string[] LinkExtensions = { ".pak", ".utoc", ".ucas", ".sig", ".bk2", ".usm", ".awb" };
        private const long LinkMinimumSize = 4 * 1024 * 1024;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct FileInformation
        {
            public uint Attributes;
            public long Created, Accessed, Written;
            public uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation information);

        // Enumerations that never go inside folder links (junctions), so cleaning the game can't touch the mods themselves
        private static readonly EnumerationOptions AllNoLinks = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        private static readonly EnumerationOptions TopNoLinks = new() { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };

        private static bool IsJunction(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return attributes.HasFlag(FileAttributes.Directory) && attributes.HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return false;
            }
        }

        // True if the path goes through a folder link: deleting it would delete the mod's own file
        private static bool ThroughJunction(string path, string root)
        {
            var dir = Path.GetDirectoryName(path);
            while (dir != null && dir.Length > root.Length)
            {
                if (IsJunction(dir))
                    return true;
                dir = Path.GetDirectoryName(dir);
            }
            return false;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            foreach (var prefix in new[] { @"\\?\", @"\??\" })
                if (path.StartsWith(prefix))
                    path = path[prefix.Length..];
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }

        private static bool PointsTo(string junction, string target)
        {
            try
            {
                return IsJunction(junction) && string.Equals(NormalizePath(new DirectoryInfo(junction).LinkTarget), NormalizePath(target), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // Removes only the link, the mod folder it points to is untouched
        private static void RemoveJunction(string path)
        {
            if (IsJunction(path))
                Directory.Delete(path, false);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, byte[] inBuffer, int inSize, IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

        // Creates a directory junction (no administrator rights needed, works between local drives)
        private static bool CreateJunction(string link, string target)
        {
            try
            {
                target = NormalizePath(target);
                Directory.CreateDirectory(Path.GetDirectoryName(link));
                Directory.CreateDirectory(link);
                var substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target);
                var print = System.Text.Encoding.Unicode.GetBytes(target);
                var dataLength = 8 + substitute.Length + 2 + print.Length + 2;
                var buffer = new byte[8 + dataLength];
                BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);             // IO_REPARSE_TAG_MOUNT_POINT
                BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
                BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);                // substitute name offset
                BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
                BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12); // print name offset
                BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
                substitute.CopyTo(buffer, 16);
                print.CopyTo(buffer, 16 + substitute.Length + 2);
                using (var handle = CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero))
                {
                    if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                    {
                        handle.Dispose();
                        Directory.Delete(link, false);
                        return false;
                    }
                }
                return PointsTo(link, target);
            }
            catch
            {
                try
                {
                    if (Directory.Exists(link) && !IsJunction(link) && !Directory.EnumerateFileSystemEntries(link).Any())
                        Directory.Delete(link);
                }
                catch { }
                return false;
            }
        }

        // Copies into the mod what the game wrote in a plugin folder that was installed as copies:
        // new files and files changed after they were installed. Returns how many were saved
        private static int SaveBack(string gameFolder, string modFolder, HashSet<string> installed, HashSet<string> generated)
        {
            if (!Directory.Exists(gameFolder) || IsJunction(gameFolder) || !Directory.Exists(modFolder))
                return 0;
            var saved = 0;
            foreach (var file in Directory.EnumerateFiles(gameFolder, "*", AllNoLinks))
            {
                if (generated.Contains(file))
                    continue;
                var source = Path.Combine(modFolder, Path.GetRelativePath(gameFolder, file));
                var game = new FileInfo(file);
                bool save;
                if (!File.Exists(source))
                    // Created by the game (a file installed from another mod is in the installed list)
                    save = !installed.Contains(file);
                else
                    // Edited by the game after it was installed (hard links share the date and are skipped)
                    save = game.LastWriteTimeUtc > new FileInfo(source).LastWriteTimeUtc;
                if (!save)
                    continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(source));
                    File.Copy(file, source, true);
                    saved++;
                    Global.logger.WriteLine(Loc.T("Build.SavedBack", Path.GetFileName(file), Path.GetFileName(modFolder)), LoggerType.Info);
                }
                catch (Exception e)
                {
                    Global.logger.WriteLine(Loc.T("Build.SaveBackError", Path.GetFileName(file), e.Message), LoggerType.Warning);
                }
            }
            return saved;
        }

        // How many names a file has, 1 means it's a separate copy
        private static uint LinkCount(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return GetFileInformationByHandle(stream.SafeFileHandle, out var information) ? information.NumberOfLinks : 0;
            }
            catch
            {
                return 0;
            }
        }

        // Turns an identical copy into a hard link to free the duplicated space, without writing the data again
        private static bool Relink(FileInfo source, string target)
        {
            var temp = target + ".sulink";
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
                if (!CreateHardLink(temp, source.FullName, IntPtr.Zero))
                    return false;
                File.Move(temp, target, true);
                return true;
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                return false;
            }
        }

        private static DeployManifest LoadManifest(GamePaths paths)
        {
            try
            {
                if (File.Exists(ManifestPath))
                {
                    var manifest = JsonSerializer.Deserialize<DeployManifest>(File.ReadAllText(ManifestPath));
                    if (manifest != null && string.Equals(manifest.GameRoot, paths.Root, StringComparison.OrdinalIgnoreCase))
                        return manifest;
                }
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Build.ManifestError", e.Message), LoggerType.Warning);
            }
            return new DeployManifest { GameRoot = paths.Root };
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

        // Removes every mod from the game. Anything Sparking Unverum didn't put there is moved to Backups instead of deleted
        public static bool Restart(GamePaths paths, string movies, string splash, string sound)
        {
            try
            {
                var manifest = LoadManifest(paths);
                var old = manifest.FileSet;
                var generated = new HashSet<string>(manifest.Generated, StringComparer.OrdinalIgnoreCase);
                foreach (var (gameFolder, modFolder) in manifest.Folders)
                    SaveBack(gameFolder, modFolder, old, generated);
                foreach (var junction in manifest.Junctions)
                    RemoveJunction(junction);
                var removed = Clean(paths, new HashSet<string>(StringComparer.OrdinalIgnoreCase), old, movies, splash, sound);
                SaveManifest(new DeployManifest { GameRoot = paths.Root });
                Global.logger.WriteLine(Loc.T("Build.Restored"), LoggerType.Info);
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(e.Message, LoggerType.Error);
                return false;
            }
            return true;
        }

        // Deletes what the previous build installed and isn't needed anymore (keep), backs up files Sparking Unverum didn't install.
        // Returns how many files were deleted
        private static int Clean(GamePaths paths, HashSet<string> keep, HashSet<string> old, string movies, string splash, string sound)
        {
            var backupRoot = Path.Combine(Global.BackupsPath, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            var backedUp = 0;
            var removed = 0;
            foreach (var file in old)
                if (!keep.Contains(file) && !ThroughJunction(file, paths.Root) && File.Exists(file))
                {
                    File.Delete(file);
                    removed++;
                }

            // ~mods: the priority folders belong to Sparking Unverum (or the classic Unverum), other things were put there by hand
            if (Directory.Exists(paths.TildeMods))
            {
                foreach (var dir in Directory.GetDirectories(paths.TildeMods, "*", TopNoLinks))
                {
                    if (PriorityFolder.IsMatch(Path.GetFileName(dir)))
                    {
                        foreach (var file in Directory.GetFiles(dir, "*", AllNoLinks))
                            if (!keep.Contains(file))
                            {
                                File.Delete(file);
                                removed++;
                            }
                    }
                    else
                        backedUp += MoveToBackup(dir, paths, backupRoot);
                }
                foreach (var file in Directory.GetFiles(paths.TildeMods))
                    backedUp += MoveToBackup(file, paths, backupRoot);
                DeleteEmptyFolders(paths.TildeMods, false);
            }
            Directory.CreateDirectory(paths.TildeMods);

            // Folders that only exist because of mods
            foreach (var folder in new[] { paths.LogicMods, paths.SZModLib, paths.Ue4ssMods })
            {
                if (!Directory.Exists(folder))
                    continue;
                foreach (var file in Directory.GetFiles(folder, "*", AllNoLinks))
                    if (!keep.Contains(file))
                        backedUp += MoveToBackup(file, paths, backupRoot);
                DeleteEmptyFolders(folder, true);
            }
            foreach (var ue4ssFile in Ue4ssFiles)
            {
                var file = Path.Combine(paths.Win64, ue4ssFile);
                if (File.Exists(file) && !keep.Contains(file))
                    backedUp += MoveToBackup(file, paths, backupRoot);
            }

            // Reset movies, splash and sound files replaced by loose file mods
            foreach (var folder in new[] { movies, splash, sound })
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    RestoreDirectory(folder);

            if (backedUp > 0)
                Global.logger.WriteLine(Loc.T("Build.BackedUp", backedUp, backupRoot), LoggerType.Warning);
            return removed;
        }

        private static int MoveToBackup(string path, GamePaths paths, string backupRoot)
        {
            var count = 0;
            var target = Path.Combine(backupRoot, Path.GetRelativePath(paths.Root, path));
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.GetFiles(path, "*", AllNoLinks))
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

        private static void DeleteEmptyFolders(string folder, bool deleteRoot)
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var dir in Directory.GetDirectories(folder, "*", TopNoLinks))
                DeleteEmptyFolders(dir, true);
            if (deleteRoot && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }

        private static void RestoreDirectory(string path)
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                if (File.Exists($"{file}.bak"))
                    File.Move($"{file}.bak", file, true);
        }

        // What goes where, decided before touching the game
        private class Deployment
        {
            // target -> (source, mod), a later (higher priority) mod replaces an earlier one
            public Dictionary<string, (string Source, string Mod)> Plan = new(StringComparer.OrdinalIgnoreCase);
            // Plugin folders that can be linked as a whole: game folder -> (mod folder, mod)
            public Dictionary<string, (string Source, string Mod)> Folders = new(StringComparer.OrdinalIgnoreCase);
            // Every plugin folder installed in this build, linked or not
            public Dictionary<string, string> AllFolders = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> shared = new(StringComparer.OrdinalIgnoreCase);
            public int Conflicts;

            // A plugin folder: linked as a whole unless another mod also puts files in it
            public void Folder(string source, string target, string mod)
            {
                AllFolders[target] = source;
                if (shared.Contains(target) || Folders.ContainsKey(target))
                {
                    if (Folders.Remove(target, out var previous))
                        CopyDirectory(previous.Source, target, previous.Mod);
                    shared.Add(target);
                    CopyDirectory(source, target, mod);
                    return;
                }
                Folders[target] = (source, mod);
            }

            // Folders that also receive files from other mods (like ZeroSpark/Json) are installed file by file
            public void Finish()
            {
                foreach (var (target, item) in Folders.ToList())
                    if (Plan.Keys.Any(x => x.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    {
                        Folders.Remove(target);
                        CopyDirectory(item.Source, target, item.Mod);
                    }
            }

            public void Copy(string source, string target, string mod)
            {
                if (Plan.TryGetValue(target, out var previous) && previous.Mod != mod)
                {
                    Conflicts++;
                    Global.logger.WriteLine(Loc.T("Build.Conflict", mod, Path.GetFileName(target), previous.Mod), LoggerType.Warning);
                }
                Plan[target] = (source, mod);
            }
            public void CopyDirectory(string source, string target, string mod, Func<string, bool> skip = null)
            {
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                    if (skip == null || !skip(file))
                        Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), mod);
            }
        }

        // Same file already in place (hard links share it, copies keep the date of the original)
        private static bool IsUnchanged(FileInfo source, string target)
        {
            var existing = new FileInfo(target);
            return existing.Exists && existing.Length == source.Length && existing.LastWriteTimeUtc == source.LastWriteTimeUtc;
        }

        private static bool ShouldLink(FileInfo source, string target)
        {
            return LinkExtensions.Contains(Path.GetExtension(target).ToLowerInvariant()) || source.Length >= LinkMinimumSize;
        }

        // Puts the planned files in the game, hard links when possible (same drive and NTFS), copies otherwise
        private static void Execute(Deployment deployment, BuildResult result)
        {
            foreach (var (target, item) in deployment.Plan)
            {
                var source = new FileInfo(item.Source);
                if (IsUnchanged(source, target))
                {
                    // Installed as a copy by an older version: link it instead to free the space
                    if (ShouldLink(source, target) && LinkCount(target) == 1 && LinkCount(source.FullName) == 1 && Relink(source, target))
                    {
                        result.Relinked++;
                        result.FreedBytes += source.Length;
                    }
                    else
                        result.Unchanged++;
                    result.SavedBytes += source.Length;
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (File.Exists(target))
                    File.Delete(target);
                if (ShouldLink(source, target) && CreateHardLink(target, source.FullName, IntPtr.Zero))
                {
                    result.Linked++;
                    result.SavedBytes += source.Length;
                }
                else
                {
                    File.Copy(source.FullName, target, true);
                    result.Copied++;
                }
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

        // Installs the mods into the game, mods must be ordered from lowest to highest priority.
        // Only what changed since the last build is written
        public static BuildResult Build(GamePaths paths, List<Mod> mods, string movies, string splash, string sound)
        {
            var result = new BuildResult();
            var deployment = new Deployment();
            var previous = LoadManifest(paths);
            var old = previous.FileSet;
            var manifest = new DeployManifest { GameRoot = paths.Root };
            string sig = Directory.Exists(paths.Paks) ? Directory.GetFiles(paths.Paks, "*.sig", SearchOption.TopDirectoryOnly).FirstOrDefault() : null;
            var priority = 0;
            var ue4ss = false;
            var usesModLoader = false;
            List<string> zeroSparkJsons = null;
            var looseAssets = new List<(string File, string Folder)>();
            try
            {
                foreach (var mod in mods)
                {
                    var modPath = Path.Combine(Global.ModsPath, mod.name);
                    if (!Directory.Exists(modPath))
                        continue;

                    // Folders with a .uplugin are SZModLib plugins, they go as they are to SparkingZERO/Mods
                    var plugins = Directory.EnumerateFiles(modPath, "*.uplugin", SearchOption.AllDirectories)
                        .Select(Path.GetDirectoryName).Distinct().ToList();
                    foreach (var plugin in plugins)
                    {
                        Global.logger.WriteLine(Loc.T("Build.AddingPlugin", Path.GetFileName(plugin)), LoggerType.Info);
                        deployment.Folder(plugin, Path.Combine(paths.SZModLib, Path.GetFileName(plugin)), mod.name);
                        usesModLoader = true;
                    }
                    bool InPlugin(string file) => plugins.Any(p => file.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

                    // Paks (with their utoc/ucas) go to ~mods in priority order
                    var folder = Path.Combine(paths.TildeMods, PriorityName(priority), mod.name);
                    var paks = 0;
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
                        paks++;
                    }
                    if (paks > 0)
                    {
                        Global.logger.WriteLine(Loc.T("Build.CopiedPaks", paks, mod.name, PriorityName(priority)), LoggerType.Info);
                        priority++;
                    }

                    // UE4SS lua mods and blueprint LogicMods
                    foreach (var ue4ssModDirectory in Directory.GetDirectories(modPath, "*ue4ss", SearchOption.AllDirectories))
                    {
                        // Each lua mod folder is linked as a whole so its settings are kept
                        foreach (var luaMod in Directory.GetDirectories(ue4ssModDirectory))
                            deployment.Folder(luaMod, Path.Combine(paths.Ue4ssMods, Path.GetFileName(luaMod)), mod.name);
                        foreach (var file in Directory.GetFiles(ue4ssModDirectory))
                            if (!Path.GetFileName(file).Equals("mods.txt", StringComparison.OrdinalIgnoreCase))
                                deployment.Copy(file, Path.Combine(paths.Ue4ssMods, Path.GetFileName(file)), mod.name);
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
                            if (File.Exists(target) && !old.Contains(target) && !deployment.Plan.ContainsKey(target))
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
                                looseAssets.Add((file, movies));
                                break;
                            case ".bmp":
                                looseAssets.Add((file, splash));
                                break;
                            case ".awb":
                                looseAssets.Add((file, sound));
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
                var ue4ssSource = Path.Combine(Global.assemblyLocation, "Dependencies", "ue4ss");
                if (ue4ss)
                    deployment.CopyDirectory(ue4ssSource, paths.Win64, "UE4SS",
                        file => Path.GetFileName(file).Equals("mods.txt", StringComparison.OrdinalIgnoreCase));

                deployment.Finish();

                // Keep what the game wrote in plugin folders installed as copies (configs, caches...)
                foreach (var (gameFolder, modFolder) in previous.Folders.Concat(deployment.AllFolders)
                    .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Last()))
                    result.SavedBack += SaveBack(gameFolder, modFolder, old, new HashSet<string>(previous.Generated, StringComparer.OrdinalIgnoreCase));
                // Folders that become links: their copies were saved above, remove them
                foreach (var target in deployment.Folders.Keys)
                    if (Directory.Exists(target) && !IsJunction(target))
                        Directory.Delete(target, true);
                // Links that aren't needed anymore or point somewhere else: remove only the link
                foreach (var junction in previous.Junctions.Concat(deployment.Folders.Keys))
                    if (IsJunction(junction) && !(deployment.Folders.TryGetValue(junction, out var wanted) && PointsTo(junction, wanted.Source)))
                    {
                        RemoveJunction(junction);
                        if (!deployment.Folders.ContainsKey(junction))
                            result.Removed++;
                    }

                // Remove what isn't needed anymore, then write only what changed
                var keep = new HashSet<string>(deployment.Plan.Keys, StringComparer.OrdinalIgnoreCase);
                // Rewritten below, no need to delete it first
                if (ue4ss)
                    keep.Add(Path.Combine(paths.Ue4ssMods, "mods.txt"));
                result.Removed += Clean(paths, keep, old, movies, splash, sound);
                Execute(deployment, result);
                manifest.Files.AddRange(deployment.Plan.Keys);

                // Link the plugin folders, if Windows can't (FAT/exFAT drives) install them file by file
                var fallback = new Deployment();
                foreach (var (target, item) in deployment.Folders)
                {
                    if (PointsTo(target, item.Source) || CreateJunction(target, item.Source))
                    {
                        result.LinkedFolders++;
                        manifest.Junctions.Add(target);
                    }
                    else
                        fallback.CopyDirectory(item.Source, target, item.Mod);
                }
                if (fallback.Plan.Count > 0)
                {
                    Execute(fallback, result);
                    manifest.Files.AddRange(fallback.Plan.Keys);
                }
                foreach (var (gameFolder, modFolder) in deployment.AllFolders)
                    manifest.Folders[gameFolder] = modFolder;

                foreach (var (file, folder) in looseAssets)
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                        ReplaceAsset(file, folder);

                if (ue4ss)
                {
                    // Enable every mod folder that isn't one of the defaults
                    var lines = File.ReadAllLines(Path.Combine(ue4ssSource, "Mods", "mods.txt")).ToList();
                    var modFolders = deployment.Plan.Keys.Concat(deployment.Folders.Keys).Where(x => x.StartsWith(paths.Ue4ssMods + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        .Select(x => Path.GetRelativePath(paths.Ue4ssMods, x).Split(Path.DirectorySeparatorChar)[0])
                        .Where(x => !x.Contains('.')).Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in modFolders)
                        if (!DefaultUe4ssMods.Contains(name))
                            lines.Insert(Math.Max(0, lines.Count - 1), $"{name} : 1");
                    var modsFile = Path.Combine(paths.Ue4ssMods, "mods.txt");
                    WriteFresh(modsFile, string.Join(Environment.NewLine, lines) + Environment.NewLine);
                    manifest.Files.Add(modsFile);
                    manifest.Generated.Add(modsFile);
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
                            WriteFresh(jsonFiles, jsonObject.ToJsonString());
                            manifest.Generated.Add(jsonFiles);
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
                Global.logger.WriteLine(Loc.T("Build.Summary", result.Linked, result.Copied, result.Unchanged, result.Removed,
                    StringConverters.FormatSize(result.SavedBytes)), LoggerType.Info);
                if (result.LinkedFolders > 0 || result.SavedBack > 0)
                    Global.logger.WriteLine(Loc.T("Build.FolderSummary", result.LinkedFolders, result.SavedBack), LoggerType.Info);
                if (result.Relinked > 0)
                    Global.logger.WriteLine(Loc.T("Build.Relinked", result.Relinked, StringConverters.FormatSize(result.FreedBytes)), LoggerType.Info);
                Global.logger.WriteLine(Loc.T("Build.Finished"), LoggerType.Info);
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Build.Error", e.Message), LoggerType.Error);
                // Whatever is in the game now has to be cleaned next time
                manifest.Files.AddRange(deployment.Plan.Keys.Where(File.Exists));
                manifest.Files.AddRange(old.Where(File.Exists));
                manifest.Generated.AddRange(previous.Generated);
                manifest.Junctions.AddRange(previous.Junctions.Concat(deployment.Folders.Keys).Where(IsJunction).Distinct(StringComparer.OrdinalIgnoreCase));
                foreach (var (gameFolder, modFolder) in previous.Folders.Concat(deployment.AllFolders))
                    manifest.Folders[gameFolder] = modFolder;
            }
            manifest.Files = manifest.Files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SaveManifest(manifest);
            return result;
        }

        // Writes a new file instead of editing the existing one, which could be a hard link to the mod's own file
        private static void WriteFresh(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path))
                File.Delete(path);
            File.WriteAllText(path, content);
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
