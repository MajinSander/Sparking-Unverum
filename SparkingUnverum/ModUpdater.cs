using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SparkingUnverum.UI;

namespace SparkingUnverum
{
    public static class ModUpdater
    {
        // Checks every mod downloaded from GameBanana for updates
        public static async Task CheckForUpdates()
        {
            if (!Directory.Exists(Global.ModsPath))
                return;
            // Mod folder -> (model, id) taken from the homepage in mod.json
            var installed = new List<(string Folder, Metadata Metadata, string Model, long Id)>();
            foreach (var folder in Directory.GetDirectories(Global.ModsPath))
            {
                var metadata = ModDownloader.ReadMetadata(folder);
                if (metadata?.homepage != null && GameBananaApi.TryParseProfileUrl(metadata.homepage.ToString(), out var model, out var id))
                    installed.Add((folder, metadata, model, id));
            }
            if (installed.Count == 0)
            {
                Global.logger.WriteLine(Loc.T("Update.None"), LoggerType.Info);
                return;
            }
            var submissions = new Dictionary<(string, long), GameBananaSubmission>();
            try
            {
                foreach (var group in installed.GroupBy(x => x.Model))
                    foreach (var submission in await GameBananaApi.GetSubmissions(group.Key, group.Select(x => x.Id)))
                        submissions[(group.Key, submission.Id)] = submission;
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Update.CheckError", e.Message), LoggerType.Error);
                return;
            }
            var updates = 0;
            foreach (var mod in installed)
            {
                if (!submissions.TryGetValue((mod.Model, mod.Id), out var submission))
                    continue;
                if (await UpdateMod(mod.Folder, mod.Metadata, submission))
                    updates++;
            }
            Global.logger.WriteLine(updates == 0 ? Loc.T("Update.None") : Loc.T("Update.Done"), LoggerType.Info);
        }

        // Returns true if an update was available
        private static async Task<bool> UpdateMod(string folder, Metadata metadata, GameBananaSubmission submission)
        {
            var name = Path.GetFileName(folder);
            var latest = submission.LatestUpdateDate;
            // Mods without a date yet, remember the current state and check next time
            if (metadata.lastupdate == null)
            {
                metadata.lastupdate = latest ?? new DateTime(1970, 1, 1);
                SaveMetadata(folder, metadata);
                return false;
            }
            // The classic Unverum saved UTC dates without a time zone
            var lastUpdate = metadata.lastupdate.Value.Kind == DateTimeKind.Local ? metadata.lastupdate.Value.ToUniversalTime()
                : DateTime.SpecifyKind(metadata.lastupdate.Value, DateTimeKind.Utc);
            if (latest == null || DateTime.Compare(lastUpdate, latest.Value) >= 0)
                return false;

            var update = submission.Updates[0];
            Global.logger.WriteLine(Loc.T("Update.Available", name), LoggerType.Info);
            var changelog = new ChangelogBox(update, name, Loc.T("Update.AvailableFor", name), submission.Image, true);
            changelog.ShowDialog();
            if (changelog.Skip)
            {
                Global.logger.WriteLine(Loc.T("Update.Skipped", name), LoggerType.Info);
                metadata.lastupdate = update.DateAdded;
                SaveMetadata(folder, metadata);
                return true;
            }
            if (!changelog.YesNo)
            {
                Global.logger.WriteLine(Loc.T("Update.Declined", name), LoggerType.Info);
                return true;
            }
            if (!submission.HasFiles)
            {
                if (submission.HasAltLinks)
                    new AltLinkWindow(submission.AlternateFileSources, name, Global.ModsPath, submission.ProfileUrl, true).ShowDialog();
                else
                    Global.logger.WriteLine(Loc.T("Update.NoFiles", name), LoggerType.Warning);
                return true;
            }
            var fileBox = new UpdateFileBox(submission, submission.AllFiles, 0, false);
            if (fileBox.ShowDialog() != true || fileBox.SelectedFiles.Count == 0)
            {
                Global.logger.WriteLine(Loc.T("Update.Cancelled", name), LoggerType.Info);
                return true;
            }
            var downloader = new ModDownloader();
            var cancellation = new CancellationTokenSource();
            var cleared = false;
            foreach (var file in fileBox.SelectedFiles)
            {
                var archive = await downloader.DownloadFile(file.DownloadUrl, file.FileName, cancellation);
                if (archive == null)
                {
                    if (cancellation.IsCancellationRequested)
                        break;
                    continue;
                }
                try
                {
                    await Task.Run(() =>
                    {
                        if (!cleared)
                        {
                            ArchiveHelper.ClearModFolder(folder);
                            cleared = true;
                        }
                        ArchiveHelper.Extract(archive, folder);
                    });
                }
                catch (Exception e)
                {
                    Global.logger.WriteLine(Loc.T("Download.ExtractError", file.FileName, e.Message), LoggerType.Error);
                }
                finally
                {
                    try { File.Delete(archive); } catch { }
                }
            }
            if (cleared)
            {
                ModDownloader.WriteMetadata(folder, submission, string.Join("\n", fileBox.SelectedFiles.Select(x => x.Description).Where(x => !string.IsNullOrWhiteSpace(x))));
                Global.logger.WriteLine(Loc.T("Update.Updated", name), LoggerType.Info);
            }
            return true;
        }

        private static void SaveMetadata(string folder, Metadata metadata)
        {
            try
            {
                File.WriteAllText(Path.Combine(folder, "mod.json"), System.Text.Json.JsonSerializer.Serialize(metadata, Global.JsonOptions));
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(e.Message, LoggerType.Error);
            }
        }
    }
}
