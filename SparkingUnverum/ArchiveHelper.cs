using System;
using System.IO;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace SparkingUnverum
{
    public static class ArchiveHelper
    {
        // Files GameBanana sometimes serves without an archive around them
        private static readonly string[] LooseExtensions = { ".pak", ".utoc", ".ucas", ".sig", ".json", ".lua", ".dll", ".txt" };

        // Extracts zip, rar and 7z archives into destination, returns false if nothing was extracted
        public static bool Extract(string archivePath, string destination)
        {
            Directory.CreateDirectory(destination);
            if (LooseExtensions.Contains(Path.GetExtension(archivePath).ToLowerInvariant()))
            {
                File.Copy(archivePath, Path.Combine(destination, Path.GetFileName(archivePath)), true);
                return true;
            }
            var count = 0;
            using (var archive = ArchiveFactory.OpenArchive(archivePath))
            {
                foreach (var entry in archive.Entries.Where(x => !x.IsDirectory))
                {
                    entry.WriteToDirectory(destination, new ExtractionOptions()
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                    count++;
                }
            }
            return count > 0;
        }

        // Mod content, used to tell it apart from settings the game wrote when the install date is unknown
        private static readonly string[] ContentExtensions = { ".pak", ".utoc", ".ucas", ".sig", ".uasset", ".uexp", ".ubulk", ".dll", ".asi", ".exe",
            ".uplugin", ".usm", ".awb", ".bk2", ".bmp", ".mp4", ".lua", ".png", ".jpg", ".jpeg" };

        // Moves the files of a mod aside before replacing it with a new version, returns where they are
        public static string SetAside(string modFolder)
        {
            var kept = Path.Combine(Global.DownloadsPath, "kept_" + Guid.NewGuid().ToString("N"));
            if (!Directory.Exists(modFolder))
                return kept;
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            foreach (var file in Directory.GetFiles(modFolder, "*", options))
            {
                if (Path.GetFileName(file).Equals("mod.json", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(file) == modFolder)
                    continue;
                var target = Path.Combine(kept, Path.GetRelativePath(modFolder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Move(file, target);
            }
            foreach (var dir in Directory.GetDirectories(modFolder))
                Directory.Delete(dir, true);
            return kept;
        }

        // After installing the new version, puts back the files the game or the user created or edited after the
        // previous install (settings, caches...). If the new version has its own copy, the old one goes to Backups.
        // Old mod files are discarded. Returns how many files were kept
        public static int RestoreUserFiles(string modFolder, string kept, DateTime? installed)
        {
            if (!Directory.Exists(kept))
                return 0;
            var restored = 0;
            var backupRoot = Path.Combine(Global.BackupsPath, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"), Path.GetFileName(modFolder));
            foreach (var file in Directory.GetFiles(kept, "*", SearchOption.AllDirectories))
            {
                var userFile = installed != null
                    ? File.GetLastWriteTimeUtc(file) > DateTime.SpecifyKind(installed.Value, DateTimeKind.Utc).AddMinutes(1)
                    : !ContentExtensions.Contains(Path.GetExtension(file).ToLowerInvariant());
                if (!userFile)
                    continue;
                var relative = Path.GetRelativePath(kept, file);
                var target = Path.Combine(modFolder, relative);
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Move(file, target);
                    restored++;
                }
                else
                {
                    var backup = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    File.Move(file, backup, true);
                    Global.logger.WriteLine(Loc.T("Update.UserFileBackedUp", relative, backup), LoggerType.Info);
                }
            }
            Directory.Delete(kept, true);
            if (restored > 0)
                Global.logger.WriteLine(Loc.T("Update.UserFilesKept", restored, Path.GetFileName(modFolder)), LoggerType.Info);
            return restored;
        }

        public static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        public static string SafeFolderName(string name)
        {
            var clean = string.Concat((name ?? string.Empty).Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(clean) ? "Mod" : clean;
        }
    }
}
