using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SparkingUnverum
{
    // A costume of a character: one of the game or one added by a mod
    public class RosterCostume
    {
        public string Id { get; set; }
        public int Number { get; set; }
        public int Sort { get; set; }
        public string Name { get; set; }
        // Mod that brings it, null for a costume of the game
        public string Mod { get; set; }
        public bool ModEnabled { get; set; } = true;
        // The game has a costume with the same id and the mod takes its place
        public bool ReplacesOriginal { get; set; }
        public bool Hidden { get; set; }
        public string Picture { get; set; }
        // Other enabled mods that bring a costume with the same id: only one of them can stay
        public List<string> Rivals { get; } = new();
    }

    public enum RosterClashKind
    {
        Costume,
        // Two mods change the data of the same character
        Slot,
        // Two mods of the character change the same files
        Files
    }

    public class RosterClash
    {
        public RosterClashKind Kind { get; set; }
        // Id of the costume or the character, or one of the files
        public string What { get; set; }
        public int Costume { get; set; }
        // Enabled mods fighting, the one that wins now first when it is known
        public List<string> Mods { get; set; } = new();
    }

    // A character of the select screen with everything the mods do to it
    public class RosterCharacter
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int Sort { get; set; }
        public string Portrait { get; set; }
        // Mod that adds the character to the select screen, null for one of the game
        public string Mod { get; set; }
        public bool ModEnabled { get; set; } = true;
        public bool Hidden { get; set; }
        public List<RosterCostume> Costumes { get; } = new();
        // Mods that change something else of the character: moves, animations, voices...
        public List<string> Related { get; } = new();
        // Enabled mods that CSS Organizer hasn't scanned yet and seem to be about this character
        public List<string> Pending { get; } = new();
        public List<RosterClash> Clashes { get; } = new();

        public IEnumerable<string> AllMods => new[] { Mod }
            .Concat(Costumes.Select(c => c.Mod)).Concat(Costumes.SelectMany(c => c.Rivals))
            .Concat(Related).Concat(Pending).Where(m => m != null).Distinct();
        public bool HasMods => AllMods.Any();
        public int ModCostumes => Costumes.Count(c => c.Mod != null && c.ModEnabled);
    }

    // What a mod brings to the select screen, to warn before turning it off
    public class RosterContribution
    {
        public int Costumes { get; set; }
        public int Characters { get; set; }
        public HashSet<string> Touched { get; } = new();
    }

    public class RosterData
    {
        public List<RosterCharacter> Characters { get; } = new();
        // Folder of CSS Organizer, null when it isn't installed: then only the X-ray is used
        public string Organizer { get; set; }
        public string OrganizerMod { get; set; }
        public DateTime? Scanned { get; set; }
        // Enabled mods with characters or costumes that the last scan of CSS Organizer doesn't know
        public List<string> NotScanned { get; } = new();
        public Dictionary<string, RosterContribution> Contributions { get; } = new();
    }

    // The select screen of the game with the mods of each character. CSS Organizer (Mastaklo) keeps a list of
    // every character and costume, which mod adds each one and their pictures; without it the X-ray guesses
    public static class CharacterRoster
    {
        public static string FindOrganizer()
        {
            if (!Directory.Exists(Global.ModsPath))
                return null;
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.EnumerateFiles(Global.ModsPath, "config_costumes.json", options)
                .Select(f => Path.GetDirectoryName(Path.GetDirectoryName(f)))
                .Where(d => File.Exists(Path.Combine(d, "Json", "config.json")))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "Json", "config_costumes.json")))
                .FirstOrDefault();
        }

        // The program of CSS Organizer as the game sees it: it scans the folder it is in
        public static string ToolInGame(string organizer)
        {
            var paths = GamePaths.Current;
            if (organizer == null || paths == null)
                return null;
            var folder = Path.Combine(paths.SZModLib, Path.GetFileName(organizer));
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault() : null;
        }

        public static RosterData Build(IList<Mod> mods, IReadOnlyDictionary<string, ModReport> reports)
        {
            var organizer = FindOrganizer();
            var data = organizer == null ? null : FromOrganizer(organizer, mods, reports);
            data ??= FromXray(mods, reports);
            AddClashes(data, mods, reports);
            return data;
        }

        // Plugin folder (with a .uplugin) -> mod; an enabled mod wins when several bring the same plugin
        private static (Dictionary<string, Mod> Mods, Dictionary<string, List<string>> Plugins) PluginMap(IList<Mod> mods)
        {
            var byPlugin = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
            var ofMod = new Dictionary<string, List<string>>();
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var mod in mods.OrderByDescending(m => m.enabled))
            {
                var folder = Path.Combine(Global.ModsPath, mod.name);
                if (!Directory.Exists(folder))
                    continue;
                var plugins = Directory.EnumerateFiles(folder, "*.uplugin", options).Select(Path.GetFileNameWithoutExtension).ToList();
                ofMod[mod.name] = plugins;
                foreach (var plugin in plugins)
                    byPlugin.TryAdd(plugin, mod);
            }
            return (byPlugin, ofMod);
        }

        private static IEnumerable<JsonElement> Records(string file)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList()
                : root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Select(p => p.Value).ToList() : new List<JsonElement>();
            return items.Where(i => i.ValueKind == JsonValueKind.Object).Select(i => i.Clone()).ToList();
        }

        private static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
        private static int Number(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
        private static bool Flag(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static string Picture(params string[] parts)
        {
            var file = Path.Combine(parts);
            foreach (var extension in new[] { ".png", ".jpg" })
                if (File.Exists(file + extension))
                    return file + extension;
            return null;
        }

        private static RosterData FromOrganizer(string organizer, IList<Mod> mods, IReadOnlyDictionary<string, ModReport> reports)
        {
            List<JsonElement> slots, costumes;
            try
            {
                slots = Records(Path.Combine(organizer, "Json", "config.json")).ToList();
                costumes = Records(Path.Combine(organizer, "Json", "config_costumes.json")).ToList();
            }
            catch (Exception e)
            {
                Global.logger?.WriteLine(Loc.T("Roster.ReadError", e.Message), LoggerType.Warning);
                return null;
            }
            var data = new RosterData
            {
                Organizer = organizer,
                OrganizerMod = Path.GetRelativePath(Global.ModsPath, organizer).Split(Path.DirectorySeparatorChar)[0],
                Scanned = File.GetLastWriteTime(Path.Combine(organizer, "Json", "config_costumes.json"))
            };
            var (byPlugin, pluginsOf) = PluginMap(mods);
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var characters = new Dictionary<string, RosterCharacter>(StringComparer.OrdinalIgnoreCase);
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            RosterContribution Contribution(string mod) => data.Contributions.TryGetValue(mod, out var c) ? c : data.Contributions[mod] = new RosterContribution();

            foreach (var slot in slots.OrderBy(s => Number(s, "SortID")))
            {
                var id = Text(slot, "CharacterID");
                if (string.IsNullOrEmpty(id) || characters.ContainsKey(id))
                    continue;
                var plugin = Text(slot, "PluginName");
                var character = new RosterCharacter { Id = id, Name = Text(slot, "Name") ?? id, Sort = characters.Count };
                if (!string.IsNullOrEmpty(plugin))
                {
                    known.Add(plugin);
                    // A character of a mod that is no longer installed isn't in the game
                    if (!byPlugin.TryGetValue(plugin, out var mod))
                    {
                        skipped.Add(id);
                        continue;
                    }
                    character.Mod = mod.name;
                    character.ModEnabled = mod.enabled;
                    Contribution(mod.name).Characters++;
                    Contribution(mod.name).Touched.Add(id);
                }
                characters[id] = character;
            }

            foreach (var group in costumes.GroupBy(c => Text(c, "CharacterID") ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Key.Length == 0 || skipped.Contains(group.Key))
                    continue;
                if (!characters.TryGetValue(group.Key, out var character))
                {
                    character = new RosterCharacter { Id = group.Key, Name = Text(group.First(), "CharacterName") ?? group.Key, Sort = 100_000 + characters.Count };
                    characters[group.Key] = character;
                }
                foreach (var same in group.GroupBy(c => Text(c, "CostumeID") ?? string.Empty, StringComparer.OrdinalIgnoreCase).Where(g => g.Key.Length > 0))
                {
                    var original = same.FirstOrDefault(c => !Flag(c, "IsMod"));
                    var providers = new List<Mod>();
                    foreach (var entry in same.Where(c => Flag(c, "IsMod")))
                    {
                        var plugin = Text(entry, "PluginName");
                        if (string.IsNullOrEmpty(plugin))
                            continue;
                        known.Add(plugin);
                        if (byPlugin.TryGetValue(plugin, out var mod) && !providers.Contains(mod))
                            providers.Add(mod);
                    }
                    if (original.ValueKind == JsonValueKind.Undefined && providers.Count == 0)
                        continue;
                    var enabled = providers.Where(m => m.enabled).ToList();
                    var first = original.ValueKind != JsonValueKind.Undefined ? original : same.First();
                    var shown = enabled.FirstOrDefault() ?? (original.ValueKind == JsonValueKind.Undefined ? providers.First() : null);
                    var costume = new RosterCostume
                    {
                        Id = same.Key,
                        Number = Math.Max(1, Number(first, "OutfitNumber")),
                        Sort = Number(first, "SortID"),
                        Name = Text(shown == null ? first : same.First(c => Text(c, "PluginName") is string p && byPlugin.TryGetValue(p, out var m) && m == shown), "Name"),
                        Mod = shown?.name,
                        ModEnabled = shown == null || shown.enabled,
                        ReplacesOriginal = original.ValueKind != JsonValueKind.Undefined && enabled.Count > 0,
                        Hidden = same.Any(c => Flag(c, "Hidden")),
                        Picture = Picture(organizer, "images", "costumes", same.Key)
                    };
                    costume.Rivals.AddRange(enabled.Skip(1).Select(m => m.name));
                    character.Costumes.Add(costume);
                    foreach (var mod in providers)
                    {
                        Contribution(mod.name).Costumes++;
                        Contribution(mod.name).Touched.Add(character.Id);
                    }
                }
                // Numbered as they come in the costume screen: mods give their costumes numbers the game already uses
                character.Costumes.Sort((a, b) => a.Sort != b.Sort ? a.Sort.CompareTo(b.Sort) : a.Number.CompareTo(b.Number));
                for (int i = 0; i < character.Costumes.Count; i++)
                    character.Costumes[i].Number = i + 1;
                foreach (var costume in character.Costumes.Where(c => c.Rivals.Count > 0))
                    character.Clashes.Add(new RosterClash { Kind = RosterClashKind.Costume, What = costume.Id, Costume = costume.Number, Mods = costume.Rivals.Prepend(costume.Mod).ToList() });
            }

            foreach (var character in characters.Values)
                character.Portrait = Picture(organizer, "images", character.Id);

            // Mods that change a character in other ways: they replace its files in the game, change its data or are about it
            foreach (var mod in mods)
            {
                if (mod.name == data.OrganizerMod || !reports.TryGetValue(mod.name, out var report) || report.Kind != XrayKind.Character)
                    continue;
                foreach (var id in report.GameIds.Concat(report.DefinedSlots).Append(report.MainId).Where(i => i != null).Distinct())
                    if (characters.TryGetValue(id, out var character) && character.Mod != mod.name
                        && !character.Costumes.Any(c => c.Mod == mod.name || c.Rivals.Contains(mod.name)))
                        character.Related.Add(mod.name);
            }

            // Mods enabled after the last scan: their plugins aren't in the lists yet
            foreach (var mod in mods.Where(m => m.enabled && m.name != data.OrganizerMod))
            {
                if (!pluginsOf.TryGetValue(mod.name, out var plugins) || plugins.Count == 0 || plugins.Any(known.Contains))
                    continue;
                if (!reports.TryGetValue(mod.name, out var report) || report.Kind != XrayKind.Character)
                    continue;
                data.NotScanned.Add(mod.name);
                if (report.MainId != null && characters.TryGetValue(report.MainId, out var character))
                {
                    character.Related.Remove(mod.name);
                    character.Pending.Add(mod.name);
                }
            }

            data.Characters.AddRange(characters.Values.OrderBy(c => c.Sort));
            return data;
        }

        // Without CSS Organizer: one character for each main character the X-ray finds in the mods
        private static RosterData FromXray(IList<Mod> mods, IReadOnlyDictionary<string, ModReport> reports)
        {
            var data = new RosterData();
            var names = ModXray.FamilyNames(Categories.Data, reports);
            var characters = new Dictionary<string, RosterCharacter>();
            foreach (var mod in mods)
            {
                if (!reports.TryGetValue(mod.name, out var report) || report.Kind != XrayKind.Character || report.MainFamily == null)
                    continue;
                var id = report.MainId ?? report.MainCharacter + "_00";
                if (!characters.TryGetValue(id, out var character))
                    characters[id] = character = new RosterCharacter { Id = id, Name = names.GetValueOrDefault(report.MainFamily) ?? id };
                character.Related.Add(mod.name);
            }
            // Several forms of the same character keep their number apart
            foreach (var same in characters.Values.GroupBy(c => c.Name).Where(g => g.Count() > 1))
                foreach (var character in same)
                    character.Name += " · " + character.Id;
            data.Characters.AddRange(characters.Values.OrderBy(c => c.Id, StringComparer.Ordinal));
            for (int i = 0; i < data.Characters.Count; i++)
                data.Characters[i].Sort = i;
            return data;
        }

        // Two enabled mods defining the same character, or two mods of a character changing the same files
        private static void AddClashes(RosterData data, IList<Mod> mods, IReadOnlyDictionary<string, ModReport> reports)
        {
            var byId = data.Characters.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var clash in ModXray.Clashes(mods, reports))
            {
                if (clash.Slot != null)
                {
                    if (!byId.TryGetValue(clash.Slot, out var character))
                        continue;
                    var existing = character.Clashes.FirstOrDefault(c => c.Kind == RosterClashKind.Slot && c.What == clash.Slot);
                    if (existing == null)
                        character.Clashes.Add(existing = new RosterClash { Kind = RosterClashKind.Slot, What = clash.Slot });
                    foreach (var mod in new[] { clash.Winner, clash.Loser }.Where(m => !existing.Mods.Contains(m)))
                        existing.Mods.Add(mod);
                    foreach (var mod in existing.Mods.Where(m => !character.AllMods.Contains(m)).ToList())
                        character.Related.Add(mod);
                    continue;
                }
                // Only files of that character: shared libraries in two mods aren't about it
                foreach (var character in data.Characters.Where(c => clash.Example?.Contains(c.Id, StringComparison.OrdinalIgnoreCase) == true))
                {
                    var own = character.AllMods.ToHashSet();
                    if (own.Contains(clash.Winner) && own.Contains(clash.Loser))
                        character.Clashes.Add(new RosterClash { Kind = RosterClashKind.Files, What = clash.Example, Mods = new() { clash.Winner, clash.Loser } });
                }
            }
        }
    }
}
