using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SparkingUnverum
{
    // One folder of a mod installed in several folders
    public class DuplicateFolder
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public Metadata Metadata { get; set; }
        public string Size { get; set; }
        public string Description => string.IsNullOrWhiteSpace(Metadata?.filedescription) ? "-" : Metadata.filedescription.Trim();
        // GameBanana download this folder came from
        public List<GameBananaItemFile> Choices { get; set; } = new();
        public GameBananaItemFile SelectedFile { get; set; }
    }

    // Folders installed from the same GameBanana page, usually a mod with several downloads
    // that the classic Unverum extracted into "Name", "Name (2)", "Name (3)"...
    public class DuplicateGroup
    {
        public string Model { get; set; }
        public long Id { get; set; }
        public string Key => $"{Model}:{Id}";
        public string Title { get; set; }
        public List<DuplicateFolder> Folders { get; set; } = new();
        public string MergedName { get; set; }
        public bool KeptSeparate { get; set; }
        public string Summary => Loc.T("Merge.FolderCount", Folders.Count);
    }

    public static class ModMerger
    {
        private static readonly Regex CopySuffix = new(@"\s\(\d+\)$");

        // Groups of mod folders that come from the same GameBanana page
        public static List<DuplicateGroup> Find(bool includeKeptSeparate)
        {
            var groups = new Dictionary<string, DuplicateGroup>();
            if (!Directory.Exists(Global.ModsPath))
                return new();
            foreach (var folder in Directory.GetDirectories(Global.ModsPath))
            {
                var metadata = ModDownloader.ReadMetadata(folder);
                if (metadata?.homepage == null || !GameBananaApi.TryParseProfileUrl(metadata.homepage.ToString(), out var model, out var id))
                    continue;
                var key = $"{model}:{id}";
                if (!groups.TryGetValue(key, out var group))
                    groups[key] = group = new DuplicateGroup { Model = model, Id = id };
                group.Folders.Add(new DuplicateFolder { Name = System.IO.Path.GetFileName(folder), Path = folder, Metadata = metadata });
            }
            var kept = new HashSet<string>(Global.config.KeepSeparate ?? new());
            // Highest priority first, like in the mod list
            int Position(DuplicateFolder f)
            {
                var index = Global.ModList.ToList().FindIndex(m => m.name == f.Name);
                return index < 0 ? int.MaxValue : index;
            }
            return groups.Values.Where(g => g.Folders.Count > 1).Select(g =>
            {
                g.KeptSeparate = kept.Contains(g.Key);
                g.Folders = g.Folders.OrderBy(Position).ThenBy(f => f.Name, new NaturalSort()).ToList();
                g.Title = CopySuffix.Replace(g.Folders[0].Name, string.Empty);
                g.MergedName = g.Title;
                return g;
            }).Where(g => includeKeptSeparate || !g.KeptSeparate).OrderBy(g => g.Title, new NaturalSort()).ToList();
        }

        // Gets the downloads of each GameBanana page and guesses which one each folder is
        public static async Task LoadDetails(List<DuplicateGroup> groups)
        {
            foreach (var group in groups)
                foreach (var folder in group.Folders)
                    folder.Size = StringConverters.FormatSize(await Task.Run(() => new DirectoryInfo(folder.Path).GetDirectorySize()));
            var errors = new List<Exception>();
            foreach (var byModel in groups.GroupBy(g => g.Model))
            {
                var submissions = await GameBananaApi.GetSubmissions(byModel.Key, byModel.Select(g => g.Id), errors);
                foreach (var group in byModel)
                {
                    var submission = submissions.FirstOrDefault(s => s.Id == group.Id);
                    if (submission == null)
                        continue;
                    if (!string.IsNullOrWhiteSpace(submission.Title))
                        group.Title = submission.Title;
                    foreach (var folder in group.Folders)
                    {
                        folder.Choices = submission.AllFiles;
                        folder.SelectedFile = folder.Metadata.files?.Count > 0
                            ? submission.AllFiles.FirstOrDefault(f => folder.Metadata.files.Contains(f.FileName))
                            : MatchFile(folder.Metadata.filedescription, submission.AllFiles);
                    }
                }
            }
            foreach (var error in errors.Select(e => e.Message).Distinct())
                Global.logger.WriteLine(Loc.T("Update.CheckError", error), LoggerType.Warning);
        }

        // The GameBanana download whose description is the one saved when it was installed
        public static GameBananaItemFile MatchFile(string description, List<GameBananaItemFile> files)
        {
            if (string.IsNullOrWhiteSpace(description) || files == null)
                return null;
            description = description.Trim();
            var exact = files.Where(f => string.Equals(f.Description?.Trim(), description, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1)
                return exact[0];
            if (exact.Count > 1 || description.Length < 4)
                return null;
            var partial = files.Where(f => !string.IsNullOrWhiteSpace(f.Description) && f.Description.Trim().Length >= 4
                && (f.Description.Trim().StartsWith(description, StringComparison.OrdinalIgnoreCase) || description.StartsWith(f.Description.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
            return partial.Count == 1 ? partial[0] : null;
        }

        // Each folder remembers its own download so updates only bring that part
        public static void KeepSeparate(DuplicateGroup group)
        {
            foreach (var folder in group.Folders.Where(f => f.SelectedFile != null))
            {
                folder.Metadata.files = new List<string> { folder.SelectedFile.FileName };
                ModDownloader.SaveMetadata(folder.Path, folder.Metadata);
            }
            Global.config.KeepSeparate ??= new();
            if (!Global.config.KeepSeparate.Contains(group.Key))
                Global.config.KeepSeparate.Add(group.Key);
            group.KeptSeparate = true;
            Global.UpdateConfig();
            Global.logger.WriteLine(Loc.T("Merge.KeptSeparate", group.Title), LoggerType.Info);
        }

        // Moves every folder of the group into the first one (files only, run in the background).
        // Returns the name of the merged folder
        public static string MergeFiles(DuplicateGroup group)
        {
            var name = ArchiveHelper.SafeFolderName(string.IsNullOrWhiteSpace(group.MergedName) ? group.Title : group.MergedName.Trim());
            var destination = group.Folders[0].Path;
            var backupRoot = System.IO.Path.Combine(Global.BackupsPath, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"), "merge");
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
            var differing = 0;
            foreach (var other in group.Folders.Skip(1))
            {
                foreach (var file in Directory.GetFiles(other.Path, "*", options))
                {
                    var relative = System.IO.Path.GetRelativePath(other.Path, file);
                    if (relative.Equals("mod.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var target = System.IO.Path.Combine(destination, relative);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    if (!File.Exists(target))
                        File.Move(file, target);
                    else if (ArchiveHelper.SameContent(file, target))
                        File.Delete(file);
                    else
                    {
                        // Same file name with different content: nothing is lost, the other one goes to Backups
                        var backup = System.IO.Path.Combine(backupRoot, other.Name, relative);
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup));
                        File.Move(file, backup, true);
                        differing++;
                    }
                }
                Directory.Delete(other.Path, true);
            }
            if (differing > 0)
                Global.logger.WriteLine(Loc.T("Merge.Differing", differing, backupRoot), LoggerType.Warning);

            // One mod.json that remembers every download, the oldest dates so no update is skipped
            var metadata = group.Folders[0].Metadata;
            var downloads = group.Folders.Select(f => f.SelectedFile?.FileName).Where(f => f != null).Distinct().ToList();
            metadata.files = downloads.Count > 0 ? downloads : null;
            metadata.filedescription = string.Join("\n", group.Folders.Select(f => f.Metadata.filedescription).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct());
            metadata.installed = group.Folders.Select(f => f.Metadata.installed).Where(d => d != null).DefaultIfEmpty(null).Min();
            metadata.lastupdate = group.Folders.Select(f => f.Metadata.lastupdate).Where(d => d != null).DefaultIfEmpty(null).Min();
            ModDownloader.SaveMetadata(destination, metadata);

            // Rename to the chosen name
            if (!name.Equals(group.Folders[0].Name, StringComparison.Ordinal))
            {
                var renamed = System.IO.Path.Combine(Global.ModsPath, name);
                if (name.Equals(group.Folders[0].Name, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(destination, destination + "_merge_tmp");
                    Directory.Move(destination + "_merge_tmp", renamed);
                }
                else if (!Directory.Exists(renamed))
                    Directory.Move(destination, renamed);
                else
                    name = group.Folders[0].Name;
            }
            return name;
        }

        // In every loadout the parts become one entry: the place of the highest one, enabled if any part was (UI thread)
        public static void MergeLoadouts(DuplicateGroup group, string mergedName)
        {
            var names = group.Folders.Select(f => f.Name).ToHashSet();
            var mergedPath = System.IO.Path.Combine(Global.ModsPath, mergedName);
            foreach (var loadout in Global.Game.Loadouts.Values)
            {
                var entries = loadout.Where(m => names.Contains(m.name)).ToList();
                if (entries.Count == 0)
                    continue;
                var first = entries[0];
                var paks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                    if (entry.paks != null)
                    {
                        var oldPath = System.IO.Path.Combine(Global.ModsPath, entry.name);
                        foreach (var (pak, enabled) in entry.paks)
                            paks[pak.StartsWith(oldPath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                ? mergedPath + pak[oldPath.Length..] : pak] = enabled;
                    }
                first.enabled = entries.Any(e => e.enabled);
                first.name = mergedName;
                first.paks = paks;
                foreach (var entry in entries.Skip(1))
                    loadout.Remove(entry);
            }
            Global.UpdateConfig();
            Categories.MergeMods(names, mergedName);
            Global.logger.WriteLine(Loc.T("Merge.Done", group.Folders.Count, mergedName), LoggerType.Info);
        }
    }
}
