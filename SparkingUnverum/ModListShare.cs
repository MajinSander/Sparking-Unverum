using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SparkingUnverum
{
    // A list of mods to share with friends: order, what is enabled, where to download each one and, if wanted, the folders.
    // Short names keep the code small enough to paste in a chat
    public class SharedList
    {
        [JsonPropertyName("v")] public int Version { get; set; } = 1;
        [JsonPropertyName("t")] public string Title { get; set; }
        [JsonPropertyName("a")] public string Author { get; set; }
        [JsonPropertyName("d")] public long Date { get; set; }
        [JsonPropertyName("o")] public bool OnlyEnabled { get; set; }
        [JsonPropertyName("m")] public List<SharedMod> Mods { get; set; } = new();
        [JsonPropertyName("c")] public List<SharedFolder> Folders { get; set; }
    }

    public class SharedMod
    {
        // Only for mods GameBanana can't name (not there, or withdrawn): the others get their title from it
        [JsonPropertyName("n")] public string Name { get; set; }
        [JsonPropertyName("x")] public bool Disabled { get; set; }
        [JsonIgnore] public bool Enabled { get => !Disabled; set => Disabled = !value; }
        // Name to show: the shared one or the GameBanana title once known
        [JsonIgnore] public string Title { get; set; }
        [JsonIgnore] public string DisplayName => Name ?? Title ?? $"GameBanana #{Id}";
        // GameBanana page ("Mod" is left out), null for mods that aren't there
        [JsonPropertyName("g")] public string Model { get; set; }
        [JsonPropertyName("i")] public long Id { get; set; }
        [JsonPropertyName("f")] public List<long> FileIds { get; set; }
        [JsonPropertyName("fn")] public List<string> FileNames { get; set; }
        [JsonIgnore] public bool OnGameBanana => Id > 0;
        [JsonIgnore] public string GameBananaModel => string.IsNullOrEmpty(Model) ? "Mod" : Model;
    }

    public class SharedFolder
    {
        [JsonPropertyName("n")] public string Name { get; set; }
        [JsonPropertyName("p")] public bool Character { get; set; }
        // Positions in the list of mods
        [JsonPropertyName("m")] public List<int> Mods { get; set; } = new();
    }

    public enum ImportState
    {
        Installed,
        Download,
        NotOnGameBanana,
        Unavailable
    }

    // What importing a shared mod means here: already installed, to download, or impossible to get
    public class ImportItem
    {
        public SharedMod Shared { get; set; }
        public ImportState State { get; set; }
        public string LocalFolder { get; set; }
        public GameBananaSubmission Submission { get; set; }
        public List<GameBananaItemFile> Files { get; set; } = new();
        public long Size => Files.Sum(f => f.Filesize);
    }

    public static class ModListShare
    {
        public const string Prefix = "MAJIN1:";
        public const string Extension = ".majin";

        // ---------- Sharing ----------

        // The mods of a loadout in their order, all or only the enabled ones
        public static async Task<SharedList> Create(IList<Mod> loadout, bool onlyEnabled, bool withFolders, string title, string author)
        {
            var list = new SharedList
            {
                Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
                Date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                OnlyEnabled = onlyEnabled
            };
            foreach (var mod in loadout.Where(m => !onlyEnabled || m.enabled))
            {
                var shared = new SharedMod { Name = mod.name, Title = mod.name, Enabled = mod.enabled };
                var metadata = ModDownloader.ReadMetadata(Path.Combine(Global.ModsPath, mod.name));
                if (metadata?.homepage != null && GameBananaApi.TryParseProfileUrl(metadata.homepage.ToString(), out var model, out var id))
                {
                    shared.Model = model.Equals("Mod", StringComparison.OrdinalIgnoreCase) ? null : model;
                    shared.Id = id;
                    shared.FileNames = metadata.files?.Count > 0 ? metadata.files : null;
                }
                list.Mods.Add(shared);
            }
            var positions = list.Mods.Select((m, i) => (m.Name, i)).ToDictionary(x => x.Name, x => x.i);
            await AskGameBanana(list);
            if (withFolders)
            {
                list.Folders = Categories.Data.Folders
                    .Select(f => new SharedFolder { Name = f.Name, Character = f.Character, Mods = f.Mods.Where(m => positions.ContainsKey(m)).Select(m => positions[m]).OrderBy(i => i).ToList() })
                    .Where(f => f.Mods.Count > 0).ToList();
            }
            return list;
        }

        // GameBanana names the mods still there (their name is left out to keep the code short) and gives the
        // downloaded files by number. Offline everything keeps its name, the code is just longer
        private static async Task AskGameBanana(SharedList list)
        {
            foreach (var byModel in list.Mods.Where(m => m.OnGameBanana).GroupBy(m => m.GameBananaModel))
            {
                var errors = new List<Exception>();
                List<GameBananaSubmission> submissions;
                try
                {
                    submissions = await GameBananaApi.GetSubmissions(byModel.Key, byModel.Select(m => m.Id).Distinct(), errors);
                }
                catch
                {
                    continue;
                }
                if (errors.Count > 0)
                    continue;
                foreach (var mod in byModel)
                {
                    var submission = submissions.FirstOrDefault(s => s.Id == mod.Id);
                    if (submission == null || submission.IsUnavailable)
                        continue;
                    mod.Name = null;
                    var files = submission.AllFiles;
                    var ids = mod.FileNames?.Select(n => files?.FirstOrDefault(f => f.FileName == n)?.Id ?? 0).Where(id => id > 0).ToList();
                    if (ids?.Count > 0 && ids.Count == mod.FileNames.Count)
                    {
                        mod.FileIds = ids;
                        mod.FileNames = null;
                    }
                }
            }
        }

        // JSON, compressed, as text that survives chats: MAJIN1:...
        public static string Encode(SharedList list)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(list, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault });
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize))
                brotli.Write(json);
            return Prefix + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // Finds the code inside whatever was pasted (a whole chat message, line breaks added by the chat...)
        public static SharedList Decode(string text)
        {
            var start = text?.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (start < 0)
                throw new FormatException(Loc.T("Share.NotACode"));
            var code = Regex.Match(Regex.Replace(text[(start + Prefix.Length)..], @"\s+", string.Empty), "^[A-Za-z0-9_-]+").Value
                .Replace('-', '+').Replace('_', '/');
            code = code.PadRight(code.Length + (4 - code.Length % 4) % 4, '=');
            try
            {
                using var input = new BrotliStream(new MemoryStream(Convert.FromBase64String(code)), CompressionMode.Decompress);
                using var json = new MemoryStream();
                input.CopyTo(json);
                var list = JsonSerializer.Deserialize<SharedList>(json.ToArray());
                if (list?.Mods == null)
                    throw new FormatException();
                return list;
            }
            catch (Exception e) when (e is not FormatException)
            {
                throw new FormatException(Loc.T("Share.Damaged"), e);
            }
        }

        // ---------- Importing ----------

        // Installed folder of every GameBanana page: (model, id) -> folders
        private static Dictionary<(string, long), List<(string Folder, Metadata Metadata)>> InstalledByPage()
        {
            var result = new Dictionary<(string, long), List<(string, Metadata)>>();
            if (!Directory.Exists(Global.ModsPath))
                return result;
            foreach (var dir in Directory.GetDirectories(Global.ModsPath))
            {
                var metadata = ModDownloader.ReadMetadata(dir);
                if (metadata?.homepage == null || !GameBananaApi.TryParseProfileUrl(metadata.homepage.ToString(), out var model, out var id))
                    continue;
                var key = (model.ToLowerInvariant(), id);
                if (!result.TryGetValue(key, out var folders))
                    result[key] = folders = new();
                folders.Add((Path.GetFileName(dir), metadata));
            }
            return result;
        }

        // What is already here, what has to be downloaded and what can't be got
        public static async Task<List<ImportItem>> Plan(SharedList list)
        {
            var installed = InstalledByPage();
            var folders = Directory.Exists(Global.ModsPath)
                ? new HashSet<string>(Directory.GetDirectories(Global.ModsPath).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<ImportItem>();
            foreach (var shared in list.Mods)
            {
                var item = new ImportItem { Shared = shared };
                if (shared.OnGameBanana && installed.TryGetValue((shared.GameBananaModel.ToLowerInvariant(), shared.Id), out var candidates))
                {
                    // A page installed in several folders: the one with the same downloads, or the same name
                    var match = candidates.Where(c => !used.Contains(c.Folder)).OrderByDescending(c => shared.FileNames != null && c.Metadata.files != null && shared.FileNames.Intersect(c.Metadata.files).Any())
                        .ThenByDescending(c => c.Folder.Equals(shared.Name, StringComparison.OrdinalIgnoreCase)).Select(c => c.Folder).FirstOrDefault();
                    if (match != null)
                    {
                        item.State = ImportState.Installed;
                        item.LocalFolder = match;
                    }
                }
                if (item.LocalFolder == null && shared.Name != null && folders.Contains(shared.Name) && !used.Contains(shared.Name))
                {
                    item.State = ImportState.Installed;
                    item.LocalFolder = folders.First(f => f.Equals(shared.Name, StringComparison.OrdinalIgnoreCase));
                }
                if (item.LocalFolder == null)
                    item.State = shared.OnGameBanana ? ImportState.Download : ImportState.NotOnGameBanana;
                else
                {
                    used.Add(item.LocalFolder);
                    shared.Title = item.LocalFolder;
                }
                items.Add(item);
            }

            // Pages to download: their files now, so the size is known before starting
            foreach (var byModel in items.Where(i => i.State == ImportState.Download).GroupBy(i => i.Shared.GameBananaModel))
            {
                List<GameBananaSubmission> submissions;
                try
                {
                    submissions = await GameBananaApi.GetSubmissions(byModel.Key, byModel.Select(i => i.Shared.Id).Distinct());
                }
                catch
                {
                    continue;
                }
                foreach (var item in byModel)
                {
                    var submission = submissions.FirstOrDefault(s => s.Id == item.Shared.Id);
                    if (submission?.Title != null)
                        item.Shared.Title = submission.Title;
                    if (submission == null || submission.IsUnavailable || !submission.HasFiles)
                    {
                        item.State = ImportState.Unavailable;
                        continue;
                    }
                    item.Submission = submission;
                    item.Files = ChooseFiles(item.Shared, submission.AllFiles);
                }
            }
            return items;
        }

        // The same downloads the friend has: by number, by name, the name without its version, or the only/newest one
        public static List<GameBananaItemFile> ChooseFiles(SharedMod shared, List<GameBananaItemFile> files)
        {
            if (files == null || files.Count == 0)
                return new();
            var chosen = files.Where(f => shared.FileIds?.Contains(f.Id) == true).ToList();
            if (chosen.Count == 0 && shared.FileNames != null)
                chosen = files.Where(f => shared.FileNames.Contains(f.FileName, StringComparer.OrdinalIgnoreCase)).ToList();
            if (chosen.Count == 0 && shared.FileNames != null)
                chosen = files.Where(f => shared.FileNames.Any(n => ModUpdater.BaseName(n) == ModUpdater.BaseName(f.FileName))).ToList();
            if (chosen.Count == 0)
                chosen = new List<GameBananaItemFile> { files.OrderByDescending(f => f.DateAddedLong).First() };
            return chosen;
        }

        // Downloads and extracts one mod, returns its folder or null
        public static async Task<string> Download(ImportItem item, IProgress<DownloadProgress> progress, CancellationToken cancellation)
        {
            var folderName = UniqueFolderName(ArchiveHelper.SafeFolderName(item.Shared.Name ?? item.Submission.Title ?? item.Shared.DisplayName));
            var destination = Path.Combine(Global.ModsPath, folderName);
            Directory.CreateDirectory(Global.DownloadsPath);
            var extracted = false;
            foreach (var file in item.Files)
            {
                var archive = Path.Combine(Global.DownloadsPath, Path.GetFileName(file.FileName));
                try
                {
                    using (var stream = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None))
                        await GameBananaApi.Client.DownloadAsync(file.DownloadUrl, stream, file.FileName, progress, cancellation);
                    if (await Task.Run(() => ArchiveHelper.Extract(archive, destination)))
                        extracted = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Global.logger.WriteLine(Loc.T("Download.Error", file.FileName, e.Message), LoggerType.Error);
                }
                finally
                {
                    try { File.Delete(archive); } catch { }
                }
            }
            if (!extracted)
            {
                if (Directory.Exists(destination) && !Directory.EnumerateFileSystemEntries(destination).Any())
                    Directory.Delete(destination);
                return null;
            }
            ModDownloader.WriteMetadata(destination, item.Submission, string.Join("\n", item.Files.Select(f => f.Description).Where(d => !string.IsNullOrWhiteSpace(d))),
                item.Files.Select(f => f.FileName).ToList());
            return folderName;
        }

        private static string UniqueFolderName(string name)
        {
            var candidate = name;
            var counter = 2;
            while (Directory.Exists(Path.Combine(Global.ModsPath, candidate)))
                candidate = $"{name} ({counter++})";
            return candidate;
        }

        public static string UniqueLoadoutName(string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? Loc.T("Share.DefaultTitle") : name.Trim();
            var candidate = name;
            var counter = 2;
            while (Global.Game.Loadouts.ContainsKey(candidate))
                candidate = $"{name} ({counter++})";
            return candidate;
        }

        // A new loadout with the shared order and what was enabled, the other installed mods after it, disabled
        public static ObservableCollection<Mod> BuildLoadout(List<ImportItem> items)
        {
            var loadout = new ObservableCollection<Mod>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.Where(i => i.LocalFolder != null))
                if (added.Add(item.LocalFolder))
                    loadout.Add(new Mod { name = item.LocalFolder, enabled = item.Shared.Enabled });
            if (Directory.Exists(Global.ModsPath))
                foreach (var folder in Directory.GetDirectories(Global.ModsPath).Select(Path.GetFileName).OrderBy(n => n, new NaturalSort()))
                    if (added.Add(folder))
                        loadout.Add(new Mod { name = folder, enabled = false });
            return loadout;
        }

        // The shared folders join the user's ones with the same name, or are created
        public static int ApplyFolders(SharedList list, List<ImportItem> items)
        {
            var added = 0;
            foreach (var shared in list.Folders ?? new())
            {
                var mods = shared.Mods.Where(i => i >= 0 && i < items.Count && items[i].LocalFolder != null).Select(i => items[i].LocalFolder).ToList();
                if (mods.Count == 0)
                    continue;
                var folder = Categories.Data.Folders.FirstOrDefault(f => f.Name.Equals(shared.Name, StringComparison.OrdinalIgnoreCase));
                if (folder == null)
                {
                    folder = new ModCategory { Name = shared.Name, Character = shared.Character };
                    Categories.Data.Folders.Add(folder);
                }
                added += Categories.Add(folder, mods);
            }
            Categories.Save();
            return added;
        }
    }
}
