using System.IO;

namespace SparkingUnverum
{
    // Everything that identifies DRAGON BALL Sparking! ZERO
    public static class SparkingGame
    {
        // Key used by the classic Unverum in Config.json and as the Mods subfolder name, kept for compatibility
        public const string ConfigKey = "Dragon Ball Sparking! ZERO";
        public const string DisplayName = "DRAGON BALL Sparking! ZERO";
        public const string Exe = "SparkingZERO.exe";
        public const string ProjectName = "SparkingZERO";
        public const string SteamAppId = "1790600";
        public const string SteamFolderName = "DRAGON BALL Sparking! ZERO";
        public const int GameBananaId = 21179;
        public const string GameBananaUrl = "https://gamebanana.com/games/21179";
        // SparkingZERO Mod Loader (SZModLib + ZeroSpark)
        public const int ModLoaderGameBananaId = 559777;
        // GameBanana categories used by the browser filters
        public const int ZeroSparkJsonCategory = 33583;
        public const int SkinsCategory = 32453;
        public const string RepositoryOwner = "MajinSander";
        public const string RepositoryName = "Majin-Unverum";
        public const string RepositoryUrl = "https://github.com/MajinSander/Majin-Unverum";
    }

    // Folders of a game install, computed from the path of SparkingZERO.exe
    public class GamePaths
    {
        public string Root { get; }
        public string Project => Path.Combine(Root, SparkingGame.ProjectName);
        public string Paks => Path.Combine(Project, "Content", "Paks");
        public string TildeMods => Path.Combine(Paks, "~mods");
        public string LogicMods => Path.Combine(Paks, "LogicMods");
        public string Win64 => Path.Combine(Project, "Binaries", "Win64");
        public string Ue4ssMods => Path.Combine(Win64, "Mods");
        // Loaded by the ASI loader (dsound.dll) installed with the UTOC bypass
        public string AsiPlugins => Path.Combine(Win64, "plugins");
        // SZModLib loads plugins from SparkingZERO/Mods
        public string SZModLib => Path.Combine(Project, "Mods");
        public string ZeroSparkJson => Path.Combine(SZModLib, "ZeroSpark", "Json");
        public string Content => Path.Combine(Project, "Content");

        public GamePaths(string exePath)
        {
            Root = Path.GetDirectoryName(exePath);
        }
        public static GamePaths Current => string.IsNullOrEmpty(Global.Game.Launcher) ? null : new GamePaths(Global.Game.Launcher);
    }
}
