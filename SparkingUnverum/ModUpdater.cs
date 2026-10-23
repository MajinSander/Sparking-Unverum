using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SparkingUnverum.UI;

namespace SparkingUnverum
{
    public enum ModState
    {
        None,
        UpdateAvailable,
        Unavailable
    }

    // An update found for an installed mod, shown in the updates window
    public class PendingUpdate : INotifyPropertyChanged
    {
        public string Folder { get; init; }
        public Metadata Metadata { get; init; }
        public GameBananaSubmission Submission { get; init; }
        public GameBananaItemUpdate Update => Submission.Updates[0];
        public string Name => Path.GetFileName(Folder);
        public Uri Image => Submission.Image ?? Metadata.preview;
        public string Title => $"{Update.Title} {Update.Version}".Trim();
        public string When => StringConverters.FormatTimeAgo(DateTime.UtcNow - Update.DateAdded);
        public string Changes
        {
            get
            {
                var lines = (Update.Changes ?? new()).Where(x => !string.IsNullOrWhiteSpace(x.Text)).Select(x => $"• [{x.Category}] {x.Text}").ToList();
                var text = StringConverters.ConvertHtmlToText(Update.Text ?? string.Empty);
                if (text.Length > 0)
                    lines.Add(text);
                return string.Join("\n", lines);
            }
        }
        public bool HasChanges => Changes.Length > 0;
        // Updates hosted outside GameBanana can only be downloaded by hand
        public bool ManualOnly => !Submission.HasFiles;
        private bool isSelected = true;
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class UpdateCheckResult
    {
        public List<PendingUpdate> Pending = new();
        public List<string> Unavailable = new();
        public bool Failed;
    }

    public static class ModUpdater
    {
        // Mod folder name -> state shown with an icon in the mod list
        private static Dictionary<string, ModState> states = new(StringComparer.OrdinalIgnoreCase);
        public static ModState GetState(string name) => name != null && states.TryGetValue(name, out var state) ? state : ModState.None;

        // Looks for updates of every mod downloaded from GameBanana, without asking anything
        public static async Task<UpdateCheckResult> CheckForUpdates()
        {
            var result = new UpdateCheckResult();
            if (!Directory.Exists(Global.ModsPath))
                return result;
            // Mod folder -> (model, id) taken from the homepage in mod.json
            var installed = new List<(string Folder, Metadata Metadata, string Model, long Id)>();
            foreach (var folder in Directory.GetDirectories(Global.ModsPath))
            {
                var metadata = ModDownloader.ReadMetadata(folder);
                if (metadata?.homepage != null && GameBananaApi.TryParseProfileUrl(metadata.homepage.ToString(), out var model, out var id))
                    installed.Add((folder, metadata, model, id));
            }
            var submissions = new Dictionary<(string, long), GameBananaSubmission>();
            var errors = new List<Exception>();
            foreach (var group in installed.GroupBy(x => x.Model))
                foreach (var submission in await GameBananaApi.GetSubmissions(group.Key, group.Select(x => x.Id), errors))
                    submissions[(group.Key, submission.Id)] = submission;
            // Some requests failed, check the mods that did arrive
            foreach (var error in errors.Select(x => x.Message).Distinct())
                Global.logger.WriteLine(Loc.T("Update.CheckError", error), LoggerType.Error);
            if (submissions.Count == 0 && errors.Count > 0)
            {
                result.Failed = true;
                return result;
            }

            var newStates = new Dictionary<string, ModState>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in installed)
            {
                if (!submissions.TryGetValue((mod.Model, mod.Id), out var submission))
                    continue;
                var name = Path.GetFileName(mod.Folder);
                // Deleted by its author or hidden by the moderators, nothing to update
                if (submission.IsUnavailable)
                {
                    result.Unavailable.Add(name);
                    newStates[name] = ModState.Unavailable;
                    continue;
                }
                var latest = submission.LatestUpdateDate;
                // Mods without a date yet, remember the current state and check next time
                if (mod.Metadata.lastupdate == null)
                {
                    mod.Metadata.lastupdate = latest ?? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    ModDownloader.SaveMetadata(mod.Folder, mod.Metadata);
                    continue;
                }
                if (latest != null && DateTime.Compare(AsUtc(mod.Metadata.lastupdate.Value), latest.Value) < 0)
                {
                    result.Pending.Add(new PendingUpdate { Folder = mod.Folder, Metadata = mod.Metadata, Submission = submission });
                    newStates[name] = ModState.UpdateAvailable;
                }
            }
            states = newStates;
            if (result.Unavailable.Count > 0)
                Global.logger.WriteLine(Loc.T("Update.Unavailable", result.Unavailable.Count, string.Join(", ", result.Unavailable)), LoggerType.Info);
            Global.logger.WriteLine(result.Pending.Count == 0 ? Loc.T("Update.None") : Loc.T("Update.Found", result.Pending.Count), LoggerType.Info);
            return result;
        }

        // The classic Unverum saved UTC dates without a time zone
        private static DateTime AsUtc(DateTime date) =>
            date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : DateTime.SpecifyKind(date, DateTimeKind.Utc);

        // Marks the updates as seen without downloading them
        public static void Skip(IEnumerable<PendingUpdate> updates)
        {
            foreach (var pending in updates)
            {
                pending.Metadata.lastupdate = pending.Update.DateAdded;
                ModDownloader.SaveMetadata(pending.Folder, pending.Metadata);
                states.Remove(pending.Name);
                Global.logger.WriteLine(Loc.T("Update.Skipped", pending.Name), LoggerType.Info);
            }
        }

        // Downloads and installs the updates one after another, returns how many were installed
        public static async Task<int> Apply(IEnumerable<PendingUpdate> updates)
        {
            var installed = 0;
            foreach (var pending in updates)
            {
                if (pending.ManualOnly)
                {
                    if (pending.Submission.HasAltLinks)
                        new AltLinkWindow(pending.Submission.AlternateFileSources, pending.Name, Global.ModsPath, pending.Submission.ProfileUrl, true).ShowDialog();
                    else
                        Global.logger.WriteLine(Loc.T("Update.NoFiles", pending.Name), LoggerType.Warning);
                    continue;
                }
                var files = ChooseFiles(pending);
                if (files == null)
                {
                    Global.logger.WriteLine(Loc.T("Update.Cancelled", pending.Name), LoggerType.Info);
                    continue;
                }
                var result = await Install(pending, files);
                if (result == null)
                    break;
                if (result == true)
                    installed++;
            }
            return installed;
        }

        // Same file name without versions: "szmodlib126_release.zip" and "szmodlib127_release.zip" are the same file
        public static string BaseName(string fileName) =>
            Regex.Replace(Path.GetFileNameWithoutExtension(fileName ?? string.Empty).ToLowerInvariant(), @"(v(?=\d)|[\d\s._\-()\[\]])+", string.Empty);

        // Picks the files to download: the only one, the ones that match what was downloaded last time, or asks
        private static List<GameBananaItemFile> ChooseFiles(PendingUpdate pending)
        {
            var files = pending.Submission.AllFiles;
            if (files.Count == 1)
                return files;
            var previous = pending.Metadata.files;
            if (previous != null && previous.Count > 0)
            {
                var matches = previous.Select(p => files.Where(f => BaseName(f.FileName) == BaseName(p)).ToList()).ToList();
                if (matches.All(m => m.Count == 1))
                    return matches.Select(m => m[0]).Distinct().ToList();
            }
            // Mods installed by the classic Unverum only remember the description of the downloaded file
            else if (ModMerger.MatchFile(pending.Metadata.filedescription, files) is GameBananaItemFile byDescription)
                return new List<GameBananaItemFile> { byDescription };
            var fileBox = new UpdateFileBox(pending.Submission, files, 0, false);
            return fileBox.ShowDialog() == true && fileBox.SelectedFiles.Count > 0 ? fileBox.SelectedFiles : null;
        }

        // true = installed, false = failed, null = cancelled by the user (stop the batch)
        private static async Task<bool?> Install(PendingUpdate pending, List<GameBananaItemFile> files)
        {
            var downloader = new ModDownloader();
            var cancellation = new CancellationTokenSource();
            var archives = new List<string>();
            try
            {
                // Download everything first, the mod folder is only touched when all files arrived
                foreach (var file in files)
                {
                    var archive = await downloader.DownloadFile(file.DownloadUrl, file.FileName, cancellation);
                    if (archive == null)
                        return cancellation.IsCancellationRequested ? null : false;
                    archives.Add(archive);
                }
                await Task.Run(() =>
                {
                    // Settings the game saved in the mod are put back after extracting
                    var kept = ArchiveHelper.SetAside(pending.Folder);
                    try
                    {
                        foreach (var archive in archives)
                            ArchiveHelper.Extract(archive, pending.Folder);
                        ArchiveHelper.RestoreUserFiles(pending.Folder, kept, pending.Metadata.installed);
                    }
                    catch
                    {
                        // Extraction failed: everything goes back as it was
                        ArchiveHelper.RestoreUserFiles(pending.Folder, kept, DateTime.MinValue);
                        throw;
                    }
                });
                ModDownloader.WriteMetadata(pending.Folder, pending.Submission,
                    string.Join("\n", files.Select(x => x.Description).Where(x => !string.IsNullOrWhiteSpace(x))), files.Select(x => x.FileName).ToList());
                states.Remove(pending.Name);
                Global.logger.WriteLine(Loc.T("Update.Updated", pending.Name), LoggerType.Info);
                return true;
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Download.ExtractError", pending.Name, e.Message), LoggerType.Error);
                return false;
            }
            finally
            {
                foreach (var archive in archives)
                    try { File.Delete(archive); } catch { }
            }
        }
    }
}
