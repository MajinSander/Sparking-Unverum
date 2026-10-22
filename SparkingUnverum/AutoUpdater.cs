using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SparkingUnverum.UI;

namespace SparkingUnverum
{
    // Self updates come only from the GitHub releases of Sparking Unverum, never from the classic Unverum on GameBanana
    public static class AutoUpdater
    {
        // Name of the zip attached to every GitHub release, its files are at the root of the zip
        private static readonly Regex AssetName = new(@"^SparkingUnverum.*\.zip$", RegexOptions.IgnoreCase);
        private static readonly HttpClient client = CreateClient();

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"SparkingUnverum/{App.Version}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        // 1.2.3 or 1.2.3-beta, a final version is newer than its betas
        public class AppVersion : IComparable<AppVersion>
        {
            public Version Number;
            public string Suffix;
            public static AppVersion Parse(string text)
            {
                var match = Regex.Match(text ?? string.Empty, @"(\d+(?:\.\d+){1,3})(?:-([0-9A-Za-z.\-]+))?");
                if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var number))
                    return null;
                return new AppVersion
                {
                    Number = new Version(number.Major, number.Minor, Math.Max(0, number.Build), Math.Max(0, number.Revision)),
                    Suffix = match.Groups[2].Success ? match.Groups[2].Value : null
                };
            }
            public bool IsPrerelease => Suffix != null;
            public int CompareTo(AppVersion other)
            {
                var result = Number.CompareTo(other.Number);
                if (result != 0)
                    return result;
                if (Suffix == other.Suffix)
                    return 0;
                if (Suffix == null)
                    return 1;
                if (other.Suffix == null)
                    return -1;
                return string.Compare(Suffix, other.Suffix, StringComparison.OrdinalIgnoreCase);
            }
            public override string ToString() => Suffix == null ? Number.ToString(3) : $"{Number.ToString(3)}-{Suffix}";
        }

        private class Release
        {
            public AppVersion Version;
            public string Notes;
            public string AssetUrl;
            public string AssetName;
        }

        private static async Task<Release> GetLatestRelease()
        {
            var current = AppVersion.Parse(App.Version);
            var json = await client.GetStringAsync($"https://api.github.com/repos/{SparkingGame.RepositoryOwner}/{SparkingGame.RepositoryName}/releases?per_page=30");
            using var document = JsonDocument.Parse(json);
            var releases = new List<Release>();
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                    continue;
                var version = AppVersion.Parse(release.GetProperty("tag_name").GetString());
                if (version == null)
                    continue;
                // Betas are only offered to people already using a beta
                var prerelease = release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
                if ((prerelease || version.IsPrerelease) && current != null && !current.IsPrerelease)
                    continue;
                // Same kind of package as the installed one (standalone carries .NET inside)
                var standalone = File.Exists(Path.Combine(Global.assemblyLocation, "coreclr.dll"));
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    if (!AssetName.IsMatch(name ?? string.Empty) || name.Contains("-standalone", StringComparison.OrdinalIgnoreCase) != standalone)
                        continue;
                    releases.Add(new Release
                    {
                        Version = version,
                        Notes = release.TryGetProperty("body", out var body) ? body.GetString() : null,
                        AssetUrl = asset.GetProperty("browser_download_url").GetString(),
                        AssetName = name
                    });
                    break;
                }
            }
            var latest = releases.OrderByDescending(x => x.Version).FirstOrDefault();
            return latest != null && current != null && latest.Version.CompareTo(current) > 0 ? latest : null;
        }

        // Returns true if the update was prepared and the app has to close
        public static async Task<bool> CheckForUpdate(bool silent)
        {
            Release release;
            try
            {
                release = await GetLatestRelease();
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("AppUpdate.Error", e.Message), silent ? LoggerType.Info : LoggerType.Warning);
                return false;
            }
            if (release == null)
            {
                if (!silent)
                    Global.logger.WriteLine(Loc.T("AppUpdate.UpToDate", App.Version), LoggerType.Info);
                return false;
            }
            var changelog = new ChangelogBox(new GameBananaItemUpdate { Title = $"v{release.Version}", Text = release.Notes ?? string.Empty },
                "Sparking Unverum", Loc.T("AppUpdate.Available", release.Version), null);
            changelog.ShowDialog();
            if (!changelog.YesNo)
                return false;

            var work = Path.Combine(Path.GetTempPath(), "SparkingUnverumUpdate");
            var zip = Path.Combine(work, release.AssetName);
            var extracted = Path.Combine(work, release.Version.ToString());
            var cancellation = new CancellationTokenSource();
            var progressBox = new ProgressBox(cancellation) { Title = Loc.T("AppUpdate.Title") };
            progressBox.progressTitle.Text = Loc.T("AppUpdate.Downloading", release.Version);
            progressBox.Show();
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, true);
                Directory.CreateDirectory(work);
                using (var fs = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None))
                    await client.DownloadAsync(release.AssetUrl, fs, release.AssetName, new Progress<DownloadProgress>(p =>
                    {
                        progressBox.progressBar.Value = p.Percentage * 100;
                        progressBox.taskBarItem.ProgressValue = p.Percentage;
                        progressBox.progressText.Text = Loc.T("Download.Progress", Math.Round(p.Percentage * 100, 2),
                            StringConverters.FormatSize(p.DownloadedBytes), StringConverters.FormatSize(p.TotalBytes));
                    }), cancellation.Token);
                await Task.Run(() =>
                {
                    ArchiveHelper.Extract(zip, extracted);
                    // Zips with a single folder inside
                    var entries = Directory.GetFileSystemEntries(extracted);
                    if (entries.Length == 1 && Directory.Exists(entries[0]))
                        extracted = entries[0];
                });
                if (!File.Exists(Path.Combine(extracted, Path.GetFileName(Environment.ProcessPath))))
                    throw new InvalidDataException(Loc.T("AppUpdate.InvalidPackage"));
            }
            catch (OperationCanceledException)
            {
                Global.logger.WriteLine(Loc.T("AppUpdate.Cancelled"), LoggerType.Info);
                return false;
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("AppUpdate.Error", e.Message), LoggerType.Error);
                MessageBox.Show(Loc.T("AppUpdate.Error", e.Message), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally
            {
                progressBox.finished = true;
                progressBox.Close();
            }
            MessageBox.Show(Loc.T("AppUpdate.Restart"), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
            LaunchUpdater(extracted, work, Global.assemblyLocation, Environment.ProcessPath, Environment.ProcessId);
            return true;
        }

        // Copies the new files once this process has closed and opens Sparking Unverum again
        public static void LaunchUpdater(string source, string work, string appDir, string exe, int pid)
        {
            static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
            var script = Path.Combine(Path.GetTempPath(), "SparkingUnverumUpdate.ps1");
            var lines = new StringBuilder();
            lines.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            lines.AppendLine($"Wait-Process -Id {pid} -Timeout 120");
            lines.AppendLine("Start-Sleep -Milliseconds 500");
            lines.AppendLine($"robocopy {Quote(source)} {Quote(appDir)} /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null");
            lines.AppendLine($"Start-Process -FilePath {Quote(exe)} -WorkingDirectory {Quote(appDir)}");
            lines.AppendLine($"Remove-Item -LiteralPath {Quote(work)} -Recurse -Force");
            lines.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force");
            // Windows PowerShell needs the BOM to read accents in paths
            File.WriteAllText(script, lines.ToString(), new UTF8Encoding(true));
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script },
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath()
            });
        }
    }
}
