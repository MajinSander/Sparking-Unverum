using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SparkingUnverum
{
    public enum XrayKind
    {
        Other,
        Character,
        Map,
        Interface,
        Sound
    }

    // What a mod changes in the game, read from its packages
    public class ModReport
    {
        public string Mod { get; set; }
        public string Submitter { get; set; }
        // Game path -> content hash, only the packages enabled for the mod
        public Dictionary<string, string> Assets { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Character family (first three digits of the number: 015 = every form of Frieza) -> files that mention it
        public Dictionary<string, int> Families { get; } = new();
        // Character number (0150, 0151...) -> files that mention it
        public Dictionary<string, int> Characters { get; } = new();
        // Whole character id of the game (0000_23 is one form of one Goku) -> files that mention it
        public Dictionary<string, int> Ids { get; } = new();
        // Ids in files that take the place of files of the game; inside a mod's own plugin they change nothing else
        public HashSet<string> GameIds { get; } = new();
        // Whole id of the main character
        public string MainId => MainFamily == null ? null
            : Ids.Where(x => x.Key.StartsWith(MainFamily, StringComparison.Ordinal)).OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => x.Key).FirstOrDefault();
        // Character slots the mod defines ("0000_23"), two mods defining the same one fight for it
        public HashSet<string> DefinedSlots { get; } = new();
        public List<string> Maps { get; } = new();
        public int Unreadable { get; set; }
        public XrayKind Kind { get; set; }
        // Family and number most of the files are about, null when the mod isn't about one character
        public string MainFamily { get; set; }
        public string MainCharacter { get; set; }
    }

    // Two enabled mods changing the same thing: the one higher in the list wins
    public record XrayClash(string Winner, string Loser, int Files, string Example, string Slot);

    // Mods that seem to belong to a folder, or to a new one
    public class XraySuggestion
    {
        public ModCategory Folder { get; set; }
        public string NewFolderName { get; set; }
        public bool Character { get; set; }
        public string Reason { get; set; }
        public List<string> Mods { get; set; } = new();
        public string Title => Folder?.Name ?? NewFolderName;
        public bool IsNew => Folder == null;
        // Key saved when the user ignores it
        public string Target => Folder?.Name ?? "+" + NewFolderName;
    }

    public static class ModXray
    {
        private static readonly Regex CharacterId = new(@"(?<![0-9])([0-9]{4})_([0-9]{2})(?![0-9])");
        private static readonly Regex NotRealMap = new(@"(?i)/(tools?|examples?|level ?sequences?|levelsequneces|sequences?|cameras?|specialmoves|cinematics?)/|(^|/)(level|newworld|untitled)\.umap$");
        private static readonly string[] PackageExtensions = { ".utoc", ".pak" };
        private static readonly Dictionary<string, (long Size, DateTime Time, List<AssetFile> Assets)> cache = new(StringComparer.OrdinalIgnoreCase);

        public static Dictionary<string, ModReport> Analyze(IEnumerable<Mod> mods)
        {
            var reports = new Dictionary<string, ModReport>();
            foreach (var mod in mods)
                reports[mod.name] = Analyze(mod);
            // Mods whose files mix several characters but whose title names only one of them
            var names = reports.Values.Where(r => r.MainFamily != null).GroupBy(r => r.MainFamily)
                .ToDictionary(g => g.Key, g => GuessName(g.Key, g.Select(r => r.Mod)));
            foreach (var report in reports.Values.Where(r => r.Kind == XrayKind.Other && r.Families.Count > 0))
            {
                var title = Words(report.Mod);
                var named = names.Where(n => Regex.IsMatch(title, $@"(?i)(?<![\p{{L}}]){Regex.Escape(n.Value)}(?![\p{{L}}])")).ToList();
                if (named.Count != 1)
                    continue;
                report.MainFamily = named[0].Key;
                report.MainCharacter = report.Characters.Where(x => x.Key.StartsWith(report.MainFamily, StringComparison.Ordinal))
                    .OrderByDescending(x => x.Value).Select(x => x.Key).FirstOrDefault() ?? report.MainFamily + "0";
                report.Kind = XrayKind.Character;
            }
            return reports;
        }

        public static ModReport Analyze(Mod mod)
        {
            var report = new ModReport { Mod = mod.name };
            var folder = Path.Combine(Global.ModsPath, mod.name);
            if (!Directory.Exists(folder))
                return report;
            report.Submitter = ModDownloader.ReadMetadata(folder)?.submitter;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var files = Directory.EnumerateFiles(folder, "*", options)
                .Where(f => PackageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
            foreach (var file in files)
            {
                // An IoStore package goes with its .pak, which says whether it is enabled
                var pak = Path.ChangeExtension(file, ".pak");
                if (mod.paks != null && mod.paks.TryGetValue(pak, out var enabled) && !enabled)
                    continue;
                var assets = Read(file);
                if (assets == null)
                {
                    report.Unreadable++;
                    continue;
                }
                foreach (var asset in assets)
                    report.Assets[asset.Path] = asset.Hash;
            }

            var mentions = 0;
            var realMaps = 0;
            foreach (var path in report.Assets.Keys)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                {
                    report.Maps.Add(name);
                    // Camera sequences and tools of special moves are maps too, but not stages
                    if (!NotRealMap.IsMatch(path))
                        realMaps++;
                }
                foreach (Match match in CharacterId.Matches(name))
                {
                    var id = match.Groups[1].Value;
                    // 9999 is not a character
                    if (id.StartsWith("999", StringComparison.Ordinal))
                        continue;
                    report.Characters[id] = report.Characters.GetValueOrDefault(id) + 1;
                    report.Ids[match.Value] = report.Ids.GetValueOrDefault(match.Value) + 1;
                    if (!path.StartsWith("SparkingZERO/Mods/", StringComparison.OrdinalIgnoreCase))
                        report.GameIds.Add(match.Value);
                    report.Families[id[..3]] = report.Families.GetValueOrDefault(id[..3]) + 1;
                    mentions++;
                    if (name.StartsWith("CharacterData_", StringComparison.OrdinalIgnoreCase) && match.Index == "CharacterData_".Length)
                        report.DefinedSlots.Add(match.Value);
                }
            }
            // The character of the mod is the family most of its files are about
            if (mentions >= 3)
            {
                var top = report.Families.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First();
                var second = report.Families.Where(x => x.Key != top.Key).Select(x => x.Value).DefaultIfEmpty(0).Max();
                if (top.Value >= mentions * 0.35 && top.Value >= second * 1.5)
                {
                    report.MainFamily = top.Key;
                    report.MainCharacter = report.Characters.Where(x => x.Key.StartsWith(top.Key, StringComparison.Ordinal))
                        .OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key;
                }
            }
            var count = Math.Max(1, report.Assets.Count);
            if (realMaps >= 2 || (realMaps == 1 && report.MainFamily == null))
                report.Kind = XrayKind.Map;
            else if (report.MainFamily != null)
                report.Kind = XrayKind.Character;
            else if (report.Assets.Keys.Count(p => p.Contains("/UI/", StringComparison.OrdinalIgnoreCase)) >= count * 0.6)
                report.Kind = XrayKind.Interface;
            else if (report.Assets.Keys.Count(IsSound) >= count * 0.6
                || (report.Families.Count == 0 && Regex.IsMatch(mod.name, @"(?i)\b(bgm|music|songs?|sound|voices?|ost)\b")))
                report.Kind = XrayKind.Sound;
            return report;
        }

        private static bool IsSound(string path) =>
            Regex.IsMatch(path, @"(?i)(/|_)(sound|sounds|voice|voices|bgm|music|audio|se)(/|_|\.)|\.(awb|acb|wem|bnk)$");

        // Packages only change when the mod does: keep what was read
        private static List<AssetFile> Read(string file)
        {
            var info = new FileInfo(file);
            lock (cache)
                if (cache.TryGetValue(file, out var cached) && cached.Size == info.Length && cached.Time == info.LastWriteTimeUtc)
                    return cached.Assets;
            var assets = AssetIndex.Read(file);
            lock (cache)
                cache[file] = (info.Length, info.LastWriteTimeUtc, assets);
            return assets;
        }

        // Enabled mods, highest priority first, that change the same file with a different content
        // or define the same character slot
        public static List<XrayClash> Clashes(IList<Mod> order, IReadOnlyDictionary<string, ModReport> reports)
        {
            var owners = new Dictionary<string, (string Mod, string Hash)>(StringComparer.OrdinalIgnoreCase);
            var pairs = new Dictionary<(string, string), (int Files, string Example)>();
            var slots = new Dictionary<string, string>();
            var result = new List<XrayClash>();
            foreach (var mod in order.Where(m => m.enabled))
            {
                if (!reports.TryGetValue(mod.name, out var report))
                    continue;
                foreach (var (path, hash) in report.Assets)
                {
                    if (!owners.TryGetValue(path, out var owner))
                    {
                        owners[path] = (mod.name, hash);
                        continue;
                    }
                    // The same file copied in both (a shared library, for example) changes nothing
                    if (hash != null && hash == owner.Hash)
                        continue;
                    var key = (owner.Mod, mod.name);
                    var current = pairs.GetValueOrDefault(key);
                    pairs[key] = (current.Files + 1, current.Example ?? path);
                }
                foreach (var slot in report.DefinedSlots)
                {
                    if (slots.TryGetValue(slot, out var other))
                        result.Add(new XrayClash(other, mod.name, 0, null, slot));
                    else
                        slots[slot] = mod.name;
                }
            }
            result.AddRange(pairs.Select(p => new XrayClash(p.Key.Item1, p.Key.Item2, p.Value.Files, p.Value.Example, null)));
            return result.OrderByDescending(c => c.Slot != null).ThenByDescending(c => c.Files).ToList();
        }

        // ---------- Names of the characters ----------

        // Names as they appear in mod titles, longest first so "Goku Black" beats "Goku"
        private static readonly string[] KnownNames =
        {
            "Goku Black", "Future Trunks", "Future Gohan", "Frieza Force", "Eis Shenron", "Omega Shenron", "Nuova Shenron", "Syn Shenron", "Mercenary Tao", "Super Buu", "Kid Buu", "Majin Buu", "Fat Buu", "Super Baby", "Super 17",
            "Android 13", "Android 16", "Android 17", "Android 18", "Android 19", "Android 20", "Android 21", "Android13", "Android16",
            "Android17", "Android18", "Android19", "Android20", "Android21", "Metal Cooler", "Great Ape", "Mr. Satan", "Master Roshi",
            "Goku", "Vegeta", "Gohan", "Goten", "Trunks", "Piccolo", "Krillin", "Yamcha", "Tien", "Chiaotzu", "Yajirobe", "Videl", "Pan",
            "Bulma", "Chi-Chi", "Chichi", "Hercule", "Bardock", "Gine", "Raditz", "Nappa", "Frieza", "Freeza", "Freezer", "Cooler", "Cell",
            "Buu", "Dabura", "Babidi", "Broly", "Paragus", "Bojack", "Zangya", "Janemba", "Gogeta", "Vegito", "Vegetto", "Gotenks", "Zarbon",
            "Dodoria", "Ginyu", "Recoome", "Burter", "Jeice", "Guldo", "Nail", "Dende", "Kami", "Turles", "Slug", "Garlic", "Tapion",
            "Hirudegarn", "Baby", "Shenron", "Eis", "Nuova", "Syn", "Omega", "Beerus", "Whis", "Champa", "Vados", "Hit", "Cabba", "Caulifla",
            "Kale", "Kefla", "Jiren", "Toppo", "Dyspo", "Ribrianne", "Zamasu", "Cheelai", "Tarble", "Shallot", "Hearts", "Moro", "Granolah",
            "Gas", "Pui Pui", "Spopovich", "Uub", "Shin", "Kibito", "Kaioshin", "Supreme Kai", "Gamma", "Orange Piccolo", "Glorio", "Gomah",
            "Degesu", "Masked Majin", "Naz", "Rilldo", "Pilaf", "Ichigo", "Rukia", "Sasuke", "Jotaro", "Tao", "Saibaman", "Bergamo", "Lavender", "Basil", "Toppo"
        };

        // How the Spanish version calls some of them
        private static readonly Dictionary<string, string> Spanish = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Frieza"] = "Freezer", ["Freeza"] = "Freezer", ["Krillin"] = "Krilin", ["Tien"] = "Ten Shin Han", ["Chiaotzu"] = "Chaoz",
            ["Mr. Satan"] = "Mr. Satán", ["Hercule"] = "Mr. Satán", ["Vegito"] = "Vegetto", ["Chichi"] = "Chi-Chi", ["Master Roshi"] = "Muten Roshi",
            ["Kid Buu"] = "Buu", ["Fat Buu"] = "Buu", ["Majin Buu"] = "Buu", ["Super Buu"] = "Buu", ["Great Ape"] = "Ozaru"
        };

        private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "new", "slot", "slots", "costume", "costumes", "additional", "pack", "mod", "mods", "made", "legacy", "team", "moveset", "port",
            "the", "and", "for", "with", "from", "form", "forms", "all", "end", "early", "mid", "version", "remake", "revamp", "revamped",
            "transformation", "transformations", "custom", "moves", "move", "skill", "skills", "super", "saiyan", "ssj", "ultra", "instinct",
            "blue", "god", "golden", "base", "evolved", "kaioken", "false", "true", "full", "power", "dragon", "ball", "sparking", "zero",
            "dbz", "dbs", "daima", "densetsu", "legends", "kakumei", "mastaklo", "characters", "character", "animations", "fixes", "fix",
            "extended", "definitive", "ultimate", "edition", "tag", "switch", "cpu", "default", "style", "outfit", "outfits", "saga",
            "kid", "teen", "adult", "final", "max", "mode", "plus", "update", "lenon", "nyoh972", "accurate", "anime", "neo", "dlc", "upgrade"
        };

        // "Characters_HyperMetaRilldo_Mastaklo" -> "Characters Hyper Meta Rilldo Mastaklo"
        private static string Words(string title) =>
            Regex.Replace(Regex.Replace(title, @"(?<=\p{Ll})(?=\p{Lu})", " "), @"[_\-\[\]\(\)]", " ");

        // The character name most titles of a family share: "Goku", "Freezer"...
        public static string GuessName(string family, IEnumerable<string> titles)
        {
            var list = titles.Select(Words).ToList();
            var known = KnownNames
                .Select(name => (Name: name, Count: list.Count(t => Regex.IsMatch(t, $@"(?i)(?<![\p{{L}}]){Regex.Escape(name)}(?![\p{{L}}])"))))
                .Where(x => x.Count > 0)
                .OrderByDescending(x => x.Count).ThenByDescending(x => x.Name.Length)
                .Select(x => x.Name).FirstOrDefault();
            string name;
            if (known != null)
                name = Regex.Replace(known, @"(?i)^android ?(\d+)$", "Android $1");
            else
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var title in list)
                    foreach (var word in Regex.Matches(title, @"\p{L}{3,}").Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase))
                        if (!NotNames.Contains(word))
                            counts[word] = counts.GetValueOrDefault(word) + 1;
                if (counts.Count == 0)
                    return Loc.T("Xray.Character", family + "x");
                name = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(counts.OrderByDescending(x => x.Value).ThenByDescending(x => x.Key.Length).First().Key.ToLowerInvariant());
            }
            if (Loc.Language == "es")
            {
                if (Spanish.TryGetValue(name, out var spanish))
                    name = spanish;
                name = Regex.Replace(name, @"^Android (\d+)$", "Androide $1");
            }
            return name;
        }

        // Name of each family: from the user's folders when they have one, guessed from the titles otherwise
        public static Dictionary<string, string> FamilyNames(CategoryData data, IReadOnlyDictionary<string, ModReport> reports)
        {
            var names = reports.Values.Where(r => r.MainFamily != null).GroupBy(r => r.MainFamily)
                .ToDictionary(g => g.Key, g => GuessName(g.Key, g.Select(r => r.Mod)));
            foreach (var (family, folder) in LearnFamilies(data, reports))
                names[family] = folder.Name;
            return names;
        }

        // ---------- Learning from the user's folders ----------

        // Folder of each character number and family, from how the user sorted mods into character folders
        public static (Dictionary<string, ModCategory> Numbers, Dictionary<string, ModCategory> Families) Learn(CategoryData data, IReadOnlyDictionary<string, ModReport> reports)
        {
            static Dictionary<string, ModCategory> Vote(IEnumerable<(string Key, ModCategory Folder)> votes) =>
                votes.GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.GroupBy(v => v.Folder)
                    .OrderByDescending(f => f.Count()).ThenBy(f => f.Key.Name, new NaturalSort()).First().Key);
            var characterMods = data.Folders.Where(f => f.Character)
                .SelectMany(f => f.Mods.Select(m => (Folder: f, Report: reports.GetValueOrDefault(m))))
                .Where(x => x.Report?.MainFamily != null).ToList();
            return (Vote(characterMods.Select(x => (x.Report.MainCharacter, x.Folder))), Vote(characterMods.Select(x => (x.Report.MainFamily, x.Folder))));
        }

        public static Dictionary<string, ModCategory> LearnFamilies(CategoryData data, IReadOnlyDictionary<string, ModReport> reports) => Learn(data, reports).Families;

        // General folder for maps, interface and sound: one the user filled mostly with them, or the one of that kind
        private static ModCategory FolderForKind(CategoryData data, IReadOnlyDictionary<string, ModReport> reports, XrayKind kind)
        {
            var learned = data.Folders.Where(f => !f.Character)
                .Select(f =>
                {
                    var known = f.Mods.Where(reports.ContainsKey).ToList();
                    return (Folder: f, Count: known.Count(m => reports[m].Kind == kind), Total: known.Count);
                })
                .Where(x => x.Count >= 1 && x.Count >= x.Total * 0.6)
                .OrderByDescending(x => x.Count).Select(x => x.Folder).FirstOrDefault();
            if (learned != null)
                return learned;
            var symbol = kind switch { XrayKind.Map => "Solid_Map", XrayKind.Interface => "Solid_Desktop", XrayKind.Sound => "Solid_Music", _ => null };
            return symbol == null ? null : data.Folders.FirstOrDefault(f => !f.Character && f.Symbol == symbol);
        }

        // Folders the user made for an author or a theme ("MASTAKLO"): mods with that word in their name or by that author
        private static IEnumerable<(ModCategory Folder, string Word)> NamedFolders(CategoryData data, IReadOnlyDictionary<string, ModReport> reports)
        {
            foreach (var folder in data.Folders.Where(f => !f.Character && f.Symbol == null))
            {
                var word = Regex.Matches(folder.Name, @"[\p{L}\p{N}]{4,}").Select(m => m.Value).FirstOrDefault();
                if (word != null)
                    yield return (folder, word);
                // Or the author most of its mods share
                var submitters = folder.Mods.Select(m => reports.GetValueOrDefault(m)?.Submitter).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                var author = submitters.GroupBy(s => s, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
                if (author != null && author.Count() >= 2 && author.Count() >= folder.Mods.Count * 0.8 && !author.Key.Equals(word, StringComparison.OrdinalIgnoreCase))
                    yield return (folder, author.Key);
            }
        }

        // ---------- Suggestions ----------

        public static List<XraySuggestion> Suggest(CategoryData data, IReadOnlyDictionary<string, ModReport> reports)
        {
            var dismissed = new HashSet<string>(data.Dismissed ?? new(), StringComparer.OrdinalIgnoreCase);
            var inCharacterFolder = new HashSet<string>(data.Folders.Where(f => f.Character).SelectMany(f => f.Mods));
            var inGeneralFolder = new HashSet<string>(data.Folders.Where(f => !f.Character).SelectMany(f => f.Mods));
            var (numbers, families) = Learn(data, reports);
            var names = reports.Values.Where(r => r.MainFamily != null).GroupBy(r => r.MainFamily)
                .ToDictionary(g => g.Key, g => GuessName(g.Key, g.Select(r => r.Mod)));
            var named = NamedFolders(data, reports).ToList();
            var groups = new Dictionary<string, XraySuggestion>(StringComparer.OrdinalIgnoreCase);

            void Add(string key, Func<XraySuggestion> create, string mod)
            {
                if (!groups.TryGetValue(key, out var group))
                    groups[key] = group = create();
                if (group.Folder?.Mods.Contains(mod) == true || dismissed.Contains(group.Target + "|" + mod) || group.Mods.Contains(mod))
                    return;
                group.Mods.Add(mod);
            }

            foreach (var report in reports.Values)
            {
                var mod = report.Mod;
                if (report.Kind == XrayKind.Character && !inCharacterFolder.Contains(mod))
                {
                    var folder = numbers.GetValueOrDefault(report.MainCharacter) ?? families.GetValueOrDefault(report.MainFamily);
                    if (folder != null)
                        Add("folder:" + folder.Name, () => new XraySuggestion { Folder = folder, Character = true, Reason = Loc.T("Xray.ReasonLearned") }, mod);
                    else
                        Add("family:" + report.MainFamily, () => new XraySuggestion
                        {
                            NewFolderName = names[report.MainFamily],
                            Character = true,
                            Reason = Loc.T("Xray.ReasonCharacter", report.MainFamily + "x")
                        }, mod);
                }
                else if ((report.Kind is XrayKind.Map or XrayKind.Interface or XrayKind.Sound) && !inGeneralFolder.Contains(mod))
                {
                    var folder = FolderForKind(data, reports, report.Kind);
                    var reason = Loc.T(report.Kind switch { XrayKind.Map => "Xray.ReasonMap", XrayKind.Interface => "Xray.ReasonInterface", _ => "Xray.ReasonSound" });
                    if (folder != null)
                        Add("folder:" + folder.Name, () => new XraySuggestion { Folder = folder, Reason = reason }, mod);
                    else
                    {
                        var name = Loc.T(report.Kind switch { XrayKind.Map => "Categories.DefaultMaps", XrayKind.Interface => "Categories.DefaultInterface", _ => "Categories.DefaultSound" });
                        Add("new:" + name, () => new XraySuggestion { NewFolderName = name, Reason = reason }, mod);
                    }
                }
                // Author or theme folders, a mod can be there and in a character folder
                foreach (var (folder, word) in named)
                {
                    var matches = Regex.IsMatch(mod, $@"(?i)(?<![\p{{L}}\p{{N}}]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}])")
                        || string.Equals(report.Submitter, word, StringComparison.OrdinalIgnoreCase);
                    if (matches)
                        Add("folder:" + folder.Name, () => new XraySuggestion { Folder = folder, Reason = Loc.T("Xray.ReasonNamed", word) }, mod);
                }
            }
            // Two new characters guessed with the same name keep their number apart
            foreach (var same in groups.Values.Where(g => g.IsNew && g.Character).GroupBy(g => g.NewFolderName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var group in same)
                    group.NewFolderName += " " + groups.First(g => g.Value == group).Key["family:".Length..] + "x";
            foreach (var group in groups.Values)
                group.Mods.Sort(new NaturalSort());
            return groups.Values.Where(g => g.Mods.Count > 0)
                .OrderBy(g => g.IsNew).ThenByDescending(g => g.Mods.Count).ThenBy(g => g.Title, new NaturalSort()).ToList();
        }
    }
}
