using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SparkingUnverum.UI;

namespace SparkingUnverum
{
    public class ModDownloader
    {
        private ProgressBox progressBox;

        // Download button of the mod browser
        public async Task<string> BrowserDownload(GameBananaRecord record)
        {
            var submission = await Fetch(record.Model ?? "Mod", record.Id);
            return submission == null ? null : await DownloadSubmission(submission);
        }

        // Used to install dependencies like the SparkingZERO Mod Loader
        public async Task<string> DownloadById(string model, long id)
        {
            var submission = await Fetch(model, id);
            return submission == null ? null : await DownloadSubmission(submission);
        }

        // 1-click install buttons on gamebanana.com: unverum:https://gamebanana.com/mmdl/<file id>,<Model>,<mod id>
        public async Task<string> ProtocolDownload(string line)
        {
            if (!TryParseProtocol(line, out var archiveUrl, out var model, out var id, out var fileId))
            {
                Global.logger.WriteLine(Loc.T("Download.InvalidLink", line), LoggerType.Error);
                return null;
            }
            var submission = await Fetch(model, id);
            if (submission == null)
                return null;
            if (submission.Game != null && submission.Game.Id != 0 && submission.Game.Id != SparkingGame.GameBananaId)
            {
                // Only Sparking! ZERO mods are supported
                Global.logger.WriteLine(Loc.T("Download.WrongGame", submission.Title, submission.Game.Name), LoggerType.Warning);
                MessageBox.Show(Loc.T("Download.WrongGame", submission.Title, submission.Game.Name), "Sparking Unverum",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return await DownloadSubmission(submission, fileId, archiveUrl);
        }

        public static bool TryParseProtocol(string line, out string archiveUrl, out string model, out long id, out long fileId)
        {
            archiveUrl = null;
            model = null;
            id = 0;
            fileId = 0;
            if (string.IsNullOrWhiteSpace(line))
                return false;
            line = Uri.UnescapeDataString(line.Trim().Trim('"'));
            line = Regex.Replace(line, "^unverum:(//)?", string.Empty, RegexOptions.IgnoreCase);
            var data = line.Split(',');
            if (data.Length < 3)
                return false;
            archiveUrl = data[0].Trim();
            model = data[1].Trim();
            if (model.Length > 0)
                model = char.ToUpperInvariant(model[0]) + model[1..].ToLowerInvariant();
            if (!long.TryParse(data[2].Trim().TrimEnd('/'), out id))
                return false;
            var match = Regex.Match(archiveUrl, @"(\d+)/?$");
            if (match.Success)
                fileId = long.Parse(match.Groups[1].Value);
            return archiveUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<GameBananaSubmission> Fetch(string model, long id)
        {
            try
            {
                return await GameBananaApi.GetSubmission(model, id);
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Download.FetchError", e.Message), LoggerType.Error);
                MessageBox.Show(Loc.T("Download.FetchError", e.Message), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }

        // Asks which files to download, downloads and extracts them, returns the name of the mod folder
        public async Task<string> DownloadSubmission(GameBananaSubmission submission, long preselectedFile = 0, string preselectedUrl = null)
        {
            if (submission.IsUnavailable)
            {
                Global.logger.WriteLine(Loc.T("Download.Unavailable", submission.Title), LoggerType.Warning);
                MessageBox.Show(Loc.T("Download.Unavailable", submission.Title), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            if (!submission.HasFiles)
            {
                if (submission.HasAltLinks)
                    new AltLinkWindow(submission.AlternateFileSources, submission.Title, Global.ModsPath, submission.ProfileUrl).ShowDialog();
                else
                    MessageBox.Show(Loc.T("Download.NoFiles", submission.Title), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            var fileBox = new UpdateFileBox(submission, submission.AllFiles, preselectedFile, true);
            if (fileBox.ShowDialog() != true || fileBox.SelectedFiles.Count == 0)
                return null;

            // Reuse the folder if this mod is already installed
            var existing = FindInstalled(submission);
            string folderName = null;
            var replace = false;
            if (existing != null)
            {
                var answer = MessageBox.Show(Loc.T("Download.AlreadyInstalled", submission.Title, existing), "Sparking Unverum",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel)
                    return null;
                if (answer == MessageBoxResult.Yes)
                {
                    if (GameProcess.IsRunning())
                    {
                        MessageBox.Show(Loc.T("Game.Running", Loc.T("Game.ActionUpdate")), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
                        return null;
                    }
                    folderName = existing;
                    replace = true;
                }
            }
            folderName ??= UniqueFolderName(ArchiveHelper.SafeFolderName(fileBox.FolderName ?? submission.Title));
            var destination = Path.Combine(Global.ModsPath, folderName);

            var extractedAny = false;
            var cancellation = new CancellationTokenSource();
            string kept = null;
            var installedBefore = replace ? ReadMetadata(destination)?.installed : null;
            foreach (var file in fileBox.SelectedFiles)
            {
                var url = file.Id == preselectedFile && preselectedUrl != null ? preselectedUrl : file.DownloadUrl;
                var archive = await DownloadFile(url, file.FileName, cancellation);
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
                        // Settings the game saved in the mod are put back after extracting
                        if (replace && kept == null)
                            kept = ArchiveHelper.SetAside(destination);
                        if (ArchiveHelper.Extract(archive, destination))
                            extractedAny = true;
                        else
                            Global.logger.WriteLine(Loc.T("Download.EmptyArchive", file.FileName), LoggerType.Warning);
                    });
                }
                catch (Exception e)
                {
                    Global.logger.WriteLine(Loc.T("Download.ExtractError", file.FileName, e.Message), LoggerType.Error);
                    MessageBox.Show(Loc.T("Download.ExtractError", file.FileName, e.Message), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    TryDelete(archive);
                }
            }

            // Nothing extracted: everything goes back as it was
            if (kept != null)
                await Task.Run(() => ArchiveHelper.RestoreUserFiles(destination, kept, extractedAny ? installedBefore : DateTime.MinValue));
            if (!extractedAny)
            {
                if (!replace && Directory.Exists(destination) && !Directory.EnumerateFileSystemEntries(destination).Any())
                    Directory.Delete(destination);
                return null;
            }
            WriteMetadata(destination, submission, string.Join("\n", fileBox.SelectedFiles.Select(x => x.Description).Where(x => !string.IsNullOrWhiteSpace(x))),
                fileBox.SelectedFiles.Select(x => x.FileName).ToList());
            Global.logger.WriteLine(Loc.T("Download.Installed", submission.Title, folderName), LoggerType.Info);
            return folderName;
        }

        // Name of the mod folder that was downloaded from the same GameBanana page, if any
        public static string FindInstalled(GameBananaSubmission submission)
        {
            if (!Directory.Exists(Global.ModsPath))
                return null;
            foreach (var dir in Directory.GetDirectories(Global.ModsPath))
            {
                var metadata = ReadMetadata(dir);
                if (metadata?.homepage != null && GameBananaApi.TryParseProfileUrl(metadata.homepage.ToString(), out var model, out var id)
                    && id == submission.Id && model.Equals(submission.Model, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileName(dir);
            }
            return null;
        }

        private static string UniqueFolderName(string name)
        {
            var candidate = name;
            var counter = 2;
            while (Directory.Exists(Path.Combine(Global.ModsPath, candidate)))
                candidate = $"{name} ({counter++})";
            return candidate;
        }

        public static Metadata ReadMetadata(string modFolder)
        {
            var file = Path.Combine(modFolder, "mod.json");
            if (!File.Exists(file))
                return null;
            try
            {
                return JsonSerializer.Deserialize<Metadata>(File.ReadAllText(file));
            }
            catch
            {
                return null;
            }
        }

        public static void SaveMetadata(string modFolder, Metadata metadata)
        {
            try
            {
                File.WriteAllText(Path.Combine(modFolder, "mod.json"), JsonSerializer.Serialize(metadata, Global.JsonOptions));
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(e.Message, LoggerType.Error);
            }
        }

        // files: names of the downloaded GameBanana files, used to pick the same ones when updating
        public static void WriteMetadata(string modFolder, GameBananaSubmission submission, string fileDescription, List<string> files = null)
        {
            var metadata = ReadMetadata(modFolder) ?? new Metadata();
            if (files != null)
            {
                metadata.files = files;
                metadata.installed = DateTime.UtcNow;
            }
            metadata.submitter = submission.Owner?.Name;
            metadata.description = submission.Description;
            if (fileDescription != null)
                metadata.filedescription = fileDescription;
            metadata.preview = submission.Image;
            metadata.homepage = submission.Link;
            metadata.avi = submission.Owner?.Avatar;
            metadata.upic = submission.Owner?.Upic;
            metadata.cat = submission.CategoryName;
            metadata.caticon = submission.Category?.Icon;
            metadata.lastupdate = submission.LatestUpdateDate ?? submission.DateUpdated ?? DateTime.UtcNow;
            Directory.CreateDirectory(modFolder);
            File.WriteAllText(Path.Combine(modFolder, "mod.json"), JsonSerializer.Serialize(metadata, Global.JsonOptions));
        }

        private void ReportProgress(DownloadProgress progress)
        {
            if (progressBox == null)
                return;
            progressBox.progressBar.Value = progress.Percentage * 100;
            progressBox.taskBarItem.ProgressValue = progress.Percentage;
            progressBox.progressTitle.Text = Loc.T("Download.Downloading", progress.FileName);
            progressBox.progressText.Text = Loc.T("Download.Progress", Math.Round(progress.Percentage * 100, 2),
                StringConverters.FormatSize(progress.DownloadedBytes), StringConverters.FormatSize(progress.TotalBytes));
        }

        // Downloads a file into the Downloads folder, returns its path or null if it failed or was cancelled
        public async Task<string> DownloadFile(string url, string fileName, CancellationTokenSource cancellation)
        {
            Directory.CreateDirectory(Global.DownloadsPath);
            var path = Path.Combine(Global.DownloadsPath, Path.GetFileName(fileName));
            TryDelete(path);
            progressBox = new ProgressBox(cancellation);
            progressBox.Title = Loc.T("Download.Title");
            progressBox.progressTitle.Text = Loc.T("Download.Downloading", fileName);
            progressBox.Show();
            progressBox.Activate();
            try
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    await GameBananaApi.Client.DownloadAsync(url, fs, fileName, new Progress<DownloadProgress>(ReportProgress), cancellation.Token);
                return path;
            }
            catch (OperationCanceledException)
            {
                Global.logger.WriteLine(Loc.T("Download.Cancelled", fileName), LoggerType.Info);
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Download.Error", fileName, e.Message), LoggerType.Error);
                MessageBox.Show(Loc.T("Download.Error", fileName, e.Message), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                progressBox.finished = true;
                progressBox.Close();
                progressBox = null;
            }
            TryDelete(path);
            return null;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }
}
