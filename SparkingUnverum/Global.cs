using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SparkingUnverum
{
    public static class Global
    {
        public static Config config;
        public static Logger logger;
        public static char s = Path.DirectorySeparatorChar;
        public static string assemblyLocation = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        public static ObservableCollection<string> LoadoutItems;
        public static ObservableCollection<Mod> ModList;

        public static string ConfigPath => Path.Combine(assemblyLocation, "MajinUnverum.json");
        // Name of the config before the program was called Majin Unverum
        public static string PreviousConfigPath => Path.Combine(assemblyLocation, "SparkingUnverum.json");
        // Config file written by the classic Unverum, only read to import Sparking! ZERO settings
        public static string ClassicConfigPath => Path.Combine(assemblyLocation, "Config.json");
        public static string ModsPath => Path.Combine(assemblyLocation, "Mods", SparkingGame.ConfigKey);
        public static string DownloadsPath => Path.Combine(assemblyLocation, "Downloads");
        public static string BackupsPath => Path.Combine(assemblyLocation, "Backups");

        // Settings of the game, Majin Unverum only manages DRAGON BALL Sparking! ZERO
        public static GameConfig Game => config.Configs[SparkingGame.ConfigKey];

        public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static void LoadConfig()
        {
            config = null;
            var path = File.Exists(ConfigPath) ? ConfigPath : File.Exists(PreviousConfigPath) ? PreviousConfigPath : null;
            var imported = false;
            if (path == null && File.Exists(ClassicConfigPath))
            {
                path = ClassicConfigPath;
                imported = true;
            }
            if (path != null)
            {
                try
                {
                    config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path));
                }
                catch (Exception e)
                {
                    StartupMessages.Add(("Log.ConfigReadError", new object[] { path, e.Message }, LoggerType.Error));
                }
            }
            config ??= new Config();
            config.Configs ??= new();
            // Drop every other game the classic Unverum supported
            foreach (var key in config.Configs.Keys.Where(x => x != SparkingGame.ConfigKey).ToList())
                config.Configs.Remove(key);
            if (!config.Configs.ContainsKey(SparkingGame.ConfigKey) || config.Configs[SparkingGame.ConfigKey] == null)
                config.Configs[SparkingGame.ConfigKey] = new GameConfig();
            config.CurrentGame = SparkingGame.ConfigKey;
            if (imported && Game.Launcher != null)
                StartupMessages.Add(("Log.ClassicConfigImported", Array.Empty<object>(), LoggerType.Info));

            var game = Game;
            if (string.IsNullOrEmpty(game.CurrentLoadout))
                game.CurrentLoadout = "Default";
            game.Loadouts ??= new();
            if (!game.Loadouts.ContainsKey(game.CurrentLoadout) || game.Loadouts[game.CurrentLoadout] == null)
            {
                // Very old configs only had a single ModList
                game.Loadouts[game.CurrentLoadout] = game.ModList != null && game.CurrentLoadout == "Default" ? game.ModList : new();
                game.ModList = null;
            }
            foreach (var key in game.Loadouts.Keys.ToList())
                game.Loadouts[key] ??= new();
            ModList = game.Loadouts[game.CurrentLoadout];
            LoadoutItems = new ObservableCollection<string>(game.Loadouts.Keys);
        }

        // Messages generated before the logger exists, shown once the main window is up
        public static readonly List<(string Key, object[] Args, LoggerType Type)> StartupMessages = new();

        public static void UpdateConfig()
        {
            if (config == null)
                return;
            Game.Loadouts[Game.CurrentLoadout] = ModList;
            Game.ModList = null;
            try
            {
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
            }
            catch (Exception e)
            {
                logger?.WriteLine(Loc.T("Log.ConfigWriteError", e.Message), LoggerType.Error);
            }
            ModHistory.Record();
        }
    }
}
