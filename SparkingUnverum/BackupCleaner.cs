using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace SparkingUnverum
{
    // A folder inside Backups and how much of it is already in the mods
    public class BackupFolder : INotifyPropertyChanged
    {
        public string Name { get; init; }
        public string Path { get; init; }
        public long Size { get; set; }
        public int Files { get; set; }
        public long RepeatedSize { get; private set; }
        public List<string> Repeated { get; private set; } = new();
        public bool Analyzed { get; private set; }
        private string status;
        public string Status
        {
            get => status;
            set
            {
                status = value;
                Changed(nameof(Status));
            }
        }
        public string Details => Loc.T("Backups.Details", StringConverters.FormatSize(Size), Files);
        public bool CanClean => Analyzed && Repeated.Count > 0;

        public void SetResult(List<string> repeated, long repeatedSize)
        {
            Repeated = repeated;
            RepeatedSize = repeatedSize;
            Analyzed = true;
            Status = repeated.Count == 0 ? Loc.T("Backups.NothingRepeated")
                : Loc.T("Backups.Repeated", StringConverters.FormatSize(repeatedSize), repeated.Count, Files);
            Changed(nameof(CanClean));
            Changed(nameof(Details));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    // Frees space in Backups: deletes only the files that are identical to a file in the mods
    public static class BackupCleaner
    {
        private static readonly EnumerationOptions AllNoLinks = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };

        public static List<BackupFolder> List()
        {
            if (!Directory.Exists(Global.BackupsPath))
                return new();
            return Directory.GetDirectories(Global.BackupsPath).OrderByDescending(x => x).Select(dir =>
            {
                var files = Directory.GetFiles(dir, "*", AllNoLinks);
                return new BackupFolder { Name = System.IO.Path.GetFileName(dir), Path = dir, Files = files.Length, Size = files.Sum(f => new FileInfo(f).Length) };
            }).ToList();
        }

        public static long TotalSize() => Directory.Exists(Global.BackupsPath)
            ? Directory.GetFiles(Global.BackupsPath, "*", AllNoLinks).Sum(f => new FileInfo(f).Length) : 0;

        // Mod files by size and name, to find candidates without reading everything
        public static Dictionary<(long, string), List<string>> IndexMods()
        {
            var index = new Dictionary<(long, string), List<string>>();
            if (!Directory.Exists(Global.ModsPath))
                return index;
            foreach (var file in Directory.GetFiles(Global.ModsPath, "*", AllNoLinks))
            {
                var key = (new FileInfo(file).Length, System.IO.Path.GetFileName(file).ToLowerInvariant());
                if (!index.TryGetValue(key, out var list))
                    index[key] = list = new List<string>();
                list.Add(file);
            }
            return index;
        }

        // Finds the files of the backup that have an identical copy in the mods (same name, size and content)
        public static void Analyze(BackupFolder backup, Dictionary<(long, string), List<string>> index, IProgress<double> progress = null)
        {
            var repeated = new List<string>();
            long repeatedSize = 0, done = 0;
            var files = Directory.GetFiles(backup.Path, "*", AllNoLinks);
            var total = Math.Max(1, files.Sum(f => new FileInfo(f).Length));
            foreach (var file in files)
            {
                var length = new FileInfo(file).Length;
                if (index.TryGetValue((length, System.IO.Path.GetFileName(file).ToLowerInvariant()), out var candidates)
                    && candidates.Any(c => ArchiveHelper.SameContent(file, c)))
                {
                    repeated.Add(file);
                    repeatedSize += length;
                }
                done += length;
                progress?.Report((double)done / total);
            }
            backup.SetResult(repeated, repeatedSize);
        }

        // Deletes the repeated files (they are still in the mods), returns the bytes freed
        public static long DeleteRepeated(BackupFolder backup)
        {
            long freed = 0;
            foreach (var file in backup.Repeated)
            {
                try
                {
                    var length = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += length;
                }
                catch { }
            }
            DeleteEmptyFolders(backup.Path);
            Global.logger.WriteLine(Loc.T("Backups.Freed", StringConverters.FormatSize(freed), backup.Name), LoggerType.Info);
            return freed;
        }

        private static void DeleteEmptyFolders(string folder)
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var dir in Directory.GetDirectories(folder, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }))
                DeleteEmptyFolders(dir);
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
    }
}
