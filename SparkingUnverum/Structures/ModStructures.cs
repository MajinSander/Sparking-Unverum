using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace SparkingUnverum
{
    public class Mod : System.ComponentModel.INotifyPropertyChanged
    {
        public string name { get; set; }
        public bool enabled { get; set; }
        public Dictionary<string, bool> paks { get; set; }
        // Update available or gone from GameBanana, shown with an icon in the mod list
        [JsonIgnore]
        public ModState State => ModUpdater.GetState(name);
        [JsonIgnore]
        public string StateTooltip => State switch
        {
            ModState.UpdateAvailable => Loc.T("Main.StateUpdate"),
            ModState.Unavailable => Loc.T("Main.StateUnavailable"),
            _ => null
        };
        [JsonIgnore]
        public string SizeText => ModInfo.Size(name) is long size ? StringConverters.FormatSize(size) : string.Empty;
        [JsonIgnore]
        public long SizeBytes => ModInfo.Size(name) ?? 0;
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        // Size, state and link are shown from caches, this tells the list to read them again
        public void InfoChanged()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SizeText)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(State)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StateTooltip)));
        }
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
        // Majin Unverum only: GameBanana files that were downloaded
        public List<string> files { get; set; }
        // Majin Unverum only: when the files were extracted, newer files in the mod were written by the game or the user
        public DateTime? installed { get; set; }
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
        // GameBanana pages ("Mod:123") whose several folders the user wants to keep separate
        public List<string> KeepSeparate { get; set; } = new();
        // A test copy can leave the GameBanana 1-click buttons to the main install
        public bool RegisterOneClick { get; set; } = true;
        // Name shown in the lists of mods the user shares
        public string ShareAuthor { get; set; }
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
    // Files written to the game folder by the last build so the next one only removes what Majin Unverum put there
    public class DeployManifest
    {
        public string GameRoot { get; set; }
        public List<string> Files { get; set; } = new();
        // Plugin folders in the game that are links (junctions) to the mod folder
        public List<string> Junctions { get; set; } = new();
        // Plugin folder in the game -> folder of the mod it comes from, to save back what the game writes there
        public Dictionary<string, string> Folders { get; set; } = new();
        // Files Majin Unverum edits after installing them (JsonFiles.json, mods.txt), never saved back into mods
        public List<string> Generated { get; set; } = new();
        [JsonIgnore]
        public HashSet<string> FileSet => new(Files, StringComparer.OrdinalIgnoreCase);
    }
}
