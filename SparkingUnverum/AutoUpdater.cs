using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SparkingUnverum.UI;

namespace SparkingUnverum
{
    // Self updates come only from the GitHub releases of Majin Unverum, never from the classic Unverum on GameBanana
    public static class AutoUpdater
    {
        // Files attached to every GitHub release: two zips with the files at their root and the single .exe
        private static readonly Regex AssetName = new(@"^(Majin|Sparking)Unverum.*\.(zip|exe)$", RegexOptions.IgnoreCase);
        private const string ExeName = "MajinUnverum.exe";

        // The single .exe has no dll next to it, the standalone folder carries .NET inside
        private enum InstallKind { Framework, Standalone, SingleFile }
        private static InstallKind Kind =>
            !File.Exists(Path.Combine(Global.assemblyLocation, Assembly.GetExecutingAssembly().GetName().Name + ".dll")) ? InstallKind.SingleFile
            : File.Exists(Path.Combine(Global.assemblyLocation, "coreclr.dll")) ? InstallKind.Standalone
            : InstallKind.Framework;

        private static bool Fits(string asset, InstallKind kind) => kind switch
        {
            InstallKind.SingleFile => asset.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
            InstallKind.Standalone => asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && asset.Contains("-standalone", StringComparison.OrdinalIgnoreCase),
            _ => asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !asset.Contains("-standalone", StringComparison.OrdinalIgnoreCase)
        };
        private static readonly HttpClient client = CreateClient();

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"MajinUnverum/{App.Version}");
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
                // Same kind of package as the installed one
                var kind = Kind;
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    if (!AssetName.IsMatch(name ?? string.Empty) || !Fits(name, kind))
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
                "Majin Unverum", Loc.T("AppUpdate.Available", release.Version), null);
            changelog.ShowDialog();
            if (!changelog.YesNo)
                return false;

            var kind = Kind;
            var work = Path.Combine(Path.GetTempPath(), "MajinUnverumUpdate");
            var zip = Path.Combine(work, release.AssetName);
            var extracted = Path.Combine(work, release.Version.ToString());
            string newExe = null;
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
                if (kind == InstallKind.SingleFile)
                {
                    // The download is the new program itself
                    if (!IsProgram(zip))
                        throw new InvalidDataException(Loc.T("AppUpdate.InvalidPackage"));
                }
                else
                {
                    await Task.Run(() =>
                    {
                        ArchiveHelper.Extract(zip, extracted);
                        // Zips with a single folder inside
                        var entries = Directory.GetFileSystemEntries(extracted);
                        if (entries.Length == 1 && Directory.Exists(entries[0]))
                            extracted = entries[0];
                    });
                    newExe = new[] { Path.GetFileName(Environment.ProcessPath), ExeName }.FirstOrDefault(name => File.Exists(Path.Combine(extracted, name)));
                    if (newExe == null)
                        throw new InvalidDataException(Loc.T("AppUpdate.InvalidPackage"));
                }
            }
            catch (OperationCanceledException)
            {
                Global.logger.WriteLine(Loc.T("AppUpdate.Cancelled"), LoggerType.Info);
                return false;
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("AppUpdate.Error", e.Message), LoggerType.Error);
                MessageBox.Show(Loc.T("AppUpdate.Error", e.Message), "Majin Unverum", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally
            {
                progressBox.finished = true;
                progressBox.Close();
            }
            MessageBox.Show(Loc.T("AppUpdate.Restart"), "Majin Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
            if (kind == InstallKind.SingleFile)
                LaunchExeUpdater(zip, work, Environment.ProcessPath, Environment.ProcessId);
            else
                LaunchUpdater(extracted, work, Global.assemblyLocation, Path.Combine(Global.assemblyLocation, newExe), Environment.ProcessId);
            return true;
        }

        // A Windows program starts with "MZ"
        private static bool IsProgram(string file)
        {
            using var stream = File.OpenRead(file);
            return stream.Length > 1_000_000 && stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
        }

        private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

        // Copies the new files once this process has closed and opens Majin Unverum again
        public static void LaunchUpdater(string source, string work, string appDir, string exe, int pid)
        {
            var lines = new StringBuilder();
            lines.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            lines.AppendLine($"Wait-Process -Id {pid} -Timeout 120");
            lines.AppendLine("Start-Sleep -Milliseconds 500");
            lines.AppendLine($"robocopy {Quote(source)} {Quote(appDir)} /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null");
            lines.AppendLine($"Start-Process -FilePath {Quote(exe)} -WorkingDirectory {Quote(appDir)}");
            lines.AppendLine($"Remove-Item -LiteralPath {Quote(work)} -Recurse -Force");
            lines.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force");
            RunScript(lines);
        }

        // The single .exe: puts the new one in place of this one once it has closed and opens it
        public static void LaunchExeUpdater(string newExe, string work, string exe, int pid)
        {
            var lines = new StringBuilder();
            lines.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            lines.AppendLine($"Wait-Process -Id {pid} -Timeout 120");
            lines.AppendLine("Start-Sleep -Milliseconds 500");
            lines.AppendLine("for ($i = 0; $i -lt 30; $i++) {");
            lines.AppendLine($"    try {{ Copy-Item -LiteralPath {Quote(newExe)} -Destination {Quote(exe)} -Force -ErrorAction Stop; break }} catch {{ Start-Sleep -Seconds 1 }}");
            lines.AppendLine("}");
            lines.AppendLine($"Start-Process -FilePath {Quote(exe)} -WorkingDirectory {Quote(Path.GetDirectoryName(exe))}");
            lines.AppendLine($"Remove-Item -LiteralPath {Quote(work)} -Recurse -Force");
            lines.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force");
            RunScript(lines);
        }

        private static void RunScript(StringBuilder lines)
        {
            var script = Path.Combine(Path.GetTempPath(), "MajinUnverumUpdate.ps1");
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
