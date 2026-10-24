using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SparkingUnverum
{
    public enum HistoryKind
    {
        // How the mods were when Majin Unverum opened, or the first time a loadout was seen
        Start,
        Change,
        // Going back to an earlier moment
        Restore
    }

    // How the mods of a loadout were at a moment: order, which were enabled and the packages turned off inside them
    public class HistoryEntry
    {
        public DateTime Time { get; set; }
        public string Loadout { get; set; }
        public HistoryKind Kind { get; set; }
        // For a restore: the moment it went back to
        public DateTime? RestoredFrom { get; set; }
        public List<HistoryMod> Mods { get; set; } = new();
        public int EnabledCount => Mods.Count(m => m.Enabled);
    }

    public class HistoryMod
    {
        public string Name { get; set; }
        public bool Enabled { get; set; }
        // Null when every package of the mod is on
        public List<string> PaksOff { get; set; }
    }

    // What changed from one moment to the next
    public class HistoryDiff
    {
        public List<string> Enabled { get; } = new();
        public List<string> Disabled { get; } = new();
        public List<string> Added { get; } = new();
        public List<string> Removed { get; } = new();
        public List<string> Packages { get; } = new();
        public int Moved { get; set; }
        public bool IsEmpty => Enabled.Count + Disabled.Count + Added.Count + Removed.Count + Packages.Count + Moved == 0;
    }

    // What going back to a moment does to the current mods
    public class HistoryPlan
    {
        public HistoryEntry Target { get; set; }
        public HistoryDiff Diff { get; set; }
        // Installed after that moment: they stay, disabled
        public List<string> Newer { get; } = new();
        // No longer installed: they can't come back
        public List<string> Missing { get; } = new();
    }

    // Every change to the mods of a loadout is kept, so any earlier moment can come back
    public static class ModHistory
    {
        public static string FilePath => Path.Combine(Global.assemblyLocation, "History.json");
        // Changes this close together are one: enabling five mods in a row is a single step
        public static TimeSpan MergeWindow { get; set; } = TimeSpan.FromSeconds(30);
        private const int MaxEntries = 300;
        private static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);

        private static List<HistoryEntry> entries;
        private static string loadedFrom;
        // Entries of earlier sessions are never merged with new changes
        private static int sessionStart;
        private static HistoryKind? pendingKind;
        private static DateTime? pendingFrom;

        // Raised after something new was kept
        public static event Action Changed;

        public static IReadOnlyList<HistoryEntry> Entries
        {
            get
            {
                Load();
                return entries;
            }
        }

        public static List<HistoryEntry> Of(string loadout) => Entries.Where(e => e.Loadout == loadout).ToList();

        // Majin Unverum opened: the mods as they are now are the first step of this session
        public static void Start()
        {
            Load();
            sessionStart = entries.Count;
            pendingKind = HistoryKind.Start;
            try
            {
                Record();
            }
            finally
            {
                pendingKind = null;
            }
        }

        // Called every time the config is saved: keeps the mods of the current loadout when they changed
        public static void Record()
        {
            if (Global.config == null || Global.ModList == null)
                return;
            try
            {
                Load();
                var loadout = Global.Game.CurrentLoadout;
                var state = Capture(Global.ModList);
                var lastIndex = entries.FindLastIndex(e => e.Loadout == loadout);
                var last = lastIndex < 0 ? null : entries[lastIndex];
                if (last != null && Same(last.Mods, state))
                    return;
                var now = DateTime.Now;
                var kind = last == null ? HistoryKind.Start : pendingKind ?? HistoryKind.Change;
                if (kind == HistoryKind.Change && last.Kind == HistoryKind.Change && lastIndex >= sessionStart
                    && now - last.Time < MergeWindow && lastIndex == entries.Count - 1)
                {
                    // Part of the same change; when it undoes itself (on and off again) the step goes away
                    var before = entries.Take(lastIndex).LastOrDefault(e => e.Loadout == loadout);
                    if (before != null && Same(before.Mods, state))
                        entries.RemoveAt(lastIndex);
                    else
                    {
                        last.Mods = state;
                        last.Time = now;
                    }
                }
                else
                    entries.Add(new HistoryEntry
                    {
                        Time = now,
                        Loadout = loadout,
                        Kind = kind,
                        RestoredFrom = kind == HistoryKind.Restore ? pendingFrom : null,
                        Mods = state
                    });
                Trim();
                Save();
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                Global.logger?.WriteLine(Loc.T("History.SaveError", e.Message), LoggerType.Warning);
            }
        }

        private static List<HistoryMod> Capture(IEnumerable<Mod> mods) => mods.Select(m => Capture(m, m.enabled)).ToList();

        private static HistoryMod Capture(Mod mod, bool enabled) => new()
        {
            Name = mod.name,
            Enabled = enabled,
            PaksOff = mod.paks?.Where(p => !p.Value).Select(p => p.Key).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } off ? off : null
        };

        private static bool Same(List<HistoryMod> a, List<HistoryMod> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Name != b[i].Name || a[i].Enabled != b[i].Enabled || !SamePaks(a[i].PaksOff, b[i].PaksOff))
                    return false;
            return true;
        }

        private static bool SamePaks(List<string> a, List<string> b) =>
            (a?.Count ?? 0) == (b?.Count ?? 0) && (a == null || a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase));

        // ---------- Differences ----------

        // What changed from one moment to the next, null "before" compares with nothing
        public static HistoryDiff Compare(HistoryEntry before, HistoryEntry after)
        {
            var diff = new HistoryDiff();
            if (before == null)
                return diff;
            var old = before.Mods.ToDictionary(m => m.Name);
            var now = after.Mods.ToDictionary(m => m.Name);
            foreach (var mod in after.Mods)
            {
                if (!old.TryGetValue(mod.Name, out var was))
                {
                    diff.Added.Add(mod.Name);
                    if (mod.Enabled)
                        diff.Enabled.Add(mod.Name);
                    continue;
                }
                if (mod.Enabled && !was.Enabled)
                    diff.Enabled.Add(mod.Name);
                else if (!mod.Enabled && was.Enabled)
                    diff.Disabled.Add(mod.Name);
                if (!SamePaks(mod.PaksOff, was.PaksOff))
                    diff.Packages.Add(mod.Name);
            }
            diff.Removed.AddRange(before.Mods.Where(m => !now.ContainsKey(m.Name)).Select(m => m.Name));
            diff.Moved = Moved(before.Mods.Select(m => m.Name).Where(now.ContainsKey).ToList(), after.Mods.Select(m => m.Name).Where(old.ContainsKey).ToList());
            return diff;
        }

        // Fewest mods that changed place: the ones outside the longest run that kept its order
        private static int Moved(List<string> before, List<string> after)
        {
            var position = before.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
            var tails = new List<int>();
            foreach (var index in after.Select(n => position[n]))
            {
                var at = tails.BinarySearch(index);
                if (at < 0)
                    at = ~at;
                if (at == tails.Count)
                    tails.Add(index);
                else
                    tails[at] = index;
            }
            return after.Count - tails.Count;
        }

        // The moment before this one in the same loadout
        public static HistoryEntry Previous(HistoryEntry entry)
        {
            var index = Entries.ToList().IndexOf(entry);
            return index <= 0 ? null : entries.Take(index).LastOrDefault(e => e.Loadout == entry.Loadout);
        }

        // ---------- Going back ----------

        public static HistoryPlan Plan(HistoryEntry target, IList<Mod> mods)
        {
            var now = new HistoryEntry { Loadout = target.Loadout, Mods = Capture(mods) };
            var installed = new HashSet<string>(mods.Select(m => m.name));
            var wanted = new HistoryEntry { Loadout = target.Loadout, Mods = Result(target, mods).Select(x => x.State).ToList() };
            var plan = new HistoryPlan { Target = target, Diff = Compare(now, wanted) };
            var inTarget = new HashSet<string>(target.Mods.Select(m => m.Name));
            plan.Newer.AddRange(mods.Where(m => !inTarget.Contains(m.name)).Select(m => m.name));
            plan.Missing.AddRange(target.Mods.Where(m => !installed.Contains(m.Name)).Select(m => m.Name));
            return plan;
        }

        // The mods in the order and state of that moment; mods installed later keep their place, disabled
        private static List<(Mod Mod, HistoryMod State)> Result(HistoryEntry target, IList<Mod> mods)
        {
            var byName = mods.ToDictionary(m => m.name);
            var result = target.Mods.Where(m => byName.ContainsKey(m.Name)).Select(m => (byName[m.Name], m)).ToList();
            var inTarget = new HashSet<string>(target.Mods.Select(m => m.Name));
            for (int i = 0; i < mods.Count; i++)
                if (!inTarget.Contains(mods[i].name))
                    result.Insert(Math.Min(i, result.Count), (mods[i], Capture(mods[i], false)));
            return result;
        }

        // Puts the mods of the current loadout back as they were, the step is kept too so it can be undone
        public static void Restore(HistoryEntry target)
        {
            var list = Global.ModList;
            var result = Result(target, list);
            for (int i = 0; i < result.Count; i++)
            {
                var (mod, state) = result[i];
                mod.enabled = state.Enabled;
                if (mod.paks != null)
                    foreach (var pak in mod.paks.Keys.ToList())
                        mod.paks[pak] = state.PaksOff == null || !state.PaksOff.Contains(pak, StringComparer.OrdinalIgnoreCase);
                var at = list.IndexOf(mod);
                if (at != i)
                    list.Move(at, i);
            }
            pendingKind = HistoryKind.Restore;
            pendingFrom = target.Time;
            try
            {
                Global.UpdateConfig();
            }
            finally
            {
                pendingKind = null;
                pendingFrom = null;
            }
        }

        // ---------- Loadouts ----------

        public static void RenameLoadout(string from, string to)
        {
            foreach (var entry in Entries.Where(e => e.Loadout == from))
                entry.Loadout = to;
            Save();
        }

        public static void RemoveLoadout(string loadout)
        {
            Load();
            entries.RemoveAll(e => e.Loadout == loadout);
            sessionStart = Math.Min(sessionStart, entries.Count);
            Save();
        }

        // ---------- File ----------

        private static void Trim()
        {
            var limit = DateTime.Now - KeepFor;
            // The latest moment of each loadout always stays
            var latest = entries.GroupBy(e => e.Loadout).Select(g => g.Last()).ToHashSet();
            var removed = entries.RemoveAll(e => e.Time < limit && !latest.Contains(e));
            while (entries.Count > MaxEntries)
            {
                var oldest = entries.FindIndex(e => !latest.Contains(e));
                if (oldest < 0)
                    break;
                entries.RemoveAt(oldest);
                removed++;
            }
            sessionStart = Math.Max(0, sessionStart - removed);
        }

        private static void Load()
        {
            if (entries != null && loadedFrom == FilePath)
                return;
            loadedFrom = FilePath;
            entries = new();
            sessionStart = 0;
            try
            {
                if (File.Exists(FilePath))
                    entries = Unpack(JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(FilePath)));
            }
            catch (Exception e)
            {
                // A broken file is kept aside and the history starts again
                Global.logger?.WriteLine(Loc.T("History.ReadError", e.Message), LoggerType.Warning);
                try
                {
                    File.Copy(FilePath, FilePath + ".broken", true);
                }
                catch
                {
                }
            }
        }

        private static void Save()
        {
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Pack(entries)));
            File.Move(temp, FilePath, true);
        }

        // On disk every mod name is written once: each moment is a list of numbers, negative for disabled mods
        private class HistoryFile
        {
            public int Version { get; set; } = 1;
            public List<string> Names { get; set; } = new();
            public List<StoredEntry> Entries { get; set; } = new();
        }

        private class StoredEntry
        {
            public DateTime Time { get; set; }
            public string Loadout { get; set; }
            [JsonConverter(typeof(JsonStringEnumConverter))]
            public HistoryKind Kind { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public DateTime? From { get; set; }
            public List<int> Mods { get; set; } = new();
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public Dictionary<int, List<string>> PaksOff { get; set; }
        }

        private static HistoryFile Pack(List<HistoryEntry> list)
        {
            var file = new HistoryFile();
            var index = new Dictionary<string, int>();
            int Id(string name)
            {
                if (!index.TryGetValue(name, out var id))
                {
                    index[name] = id = file.Names.Count;
                    file.Names.Add(name);
                }
                return id;
            }
            foreach (var entry in list)
            {
                var stored = new StoredEntry { Time = entry.Time, Loadout = entry.Loadout, Kind = entry.Kind, From = entry.RestoredFrom };
                foreach (var mod in entry.Mods)
                {
                    var id = Id(mod.Name);
                    stored.Mods.Add(mod.Enabled ? id + 1 : -(id + 1));
                    if (mod.PaksOff != null)
                        (stored.PaksOff ??= new())[id] = mod.PaksOff;
                }
                file.Entries.Add(stored);
            }
            return file;
        }

        private static List<HistoryEntry> Unpack(HistoryFile file)
        {
            var result = new List<HistoryEntry>();
            if (file?.Entries == null || file.Names == null)
                return result;
            foreach (var stored in file.Entries)
            {
                var entry = new HistoryEntry { Time = stored.Time, Loadout = stored.Loadout, Kind = stored.Kind, RestoredFrom = stored.From };
                foreach (var number in stored.Mods)
                {
                    var id = Math.Abs(number) - 1;
                    if (id < 0 || id >= file.Names.Count)
                        continue;
                    entry.Mods.Add(new HistoryMod
                    {
                        Name = file.Names[id],
                        Enabled = number > 0,
                        PaksOff = stored.PaksOff != null && stored.PaksOff.TryGetValue(id, out var off) ? off : null
                    });
                }
                if (entry.Loadout != null)
                    result.Add(entry);
            }
            return result;
        }
    }
}
