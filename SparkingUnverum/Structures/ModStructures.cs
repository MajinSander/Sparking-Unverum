using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace SparkingUnverum
{
    public class Mod
    {
        public string name { get; set; }
        public bool enabled { get; set; }
        public Dictionary<string, bool> paks { get; set; }
    }
    // mod.json, same fields as the classic Unverum so mod folders can be shared between both
    public class Metadata
    {
        public Uri preview { get; set; }
        public string submitter { get; set; }
        public Uri avi { get; set; }
        public Uri upic { get; set; }
        public Uri caticon { get; set; }
        public string cat { get; set; }
        public string description { get; set; }
        public string filedescription { get; set; }
        public Uri homepage { get; set; }
        public DateTime? lastupdate { get; set; }
    }
    public class Config
    {
        public string CurrentGame { get; set; }
        public Dictionary<string, GameConfig> Configs { get; set; }
        public double? LeftGridWidth { get; set; }
        public double? RightGridWidth { get; set; }
        public double? TopGridHeight { get; set; }
        public double? BottomGridHeight { get; set; }
        public double? Height { get; set; }
        public double? Width { get; set; }
        public bool Maximized { get; set; }
        // "es" or "en", null means follow the Windows language
        public string Language { get; set; }
        public bool CheckAppUpdates { get; set; } = true;
        public bool CheckModUpdatesOnStart { get; set; } = true;
        public bool HideZeroSparkJsons { get; set; }
        public bool HideColorZ { get; set; } = true;
        public bool IncludeNsfw { get; set; }
        public bool ClassicImportOffered { get; set; }
    }
    public class GameConfig
    {
        // Path to SparkingZERO.exe
        public string Launcher { get; set; }
        // 0 = executable, 1 = Steam
        public int LauncherOptionIndex { get; set; } = 1;
        public bool FirstOpen { get; set; }
        // SparkingZERO/Content/Paks/~mods
        public string ModsFolder { get; set; }
        // Only read from very old configs
        public ObservableCollection<Mod> ModList { get; set; }
        public string CurrentLoadout { get; set; }
        public Dictionary<string, ObservableCollection<Mod>> Loadouts { get; set; }
        public void DisableAllMods(string loadout)
        {
            foreach (var mod in Loadouts[loadout])
                mod.enabled = false;
        }
    }
    public class Choice
    {
        public string OptionText { get; set; }
        public string OptionSubText { get; set; }
        public int Index { get; set; }
    }
    // Files written to the game folder by the last build so the next one only removes what Sparking Unverum put there
    public class DeployManifest
    {
        public string GameRoot { get; set; }
        public List<string> Files { get; set; } = new();
        [JsonIgnore]
        public HashSet<string> FileSet => new(Files, StringComparer.OrdinalIgnoreCase);
    }
}
