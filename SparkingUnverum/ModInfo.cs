using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SparkingUnverum
{
    // Size of each mod folder and whether it is linked to GameBanana, refreshed in the background
    public static class ModInfo
    {
        private static Dictionary<string, long> sizes = new(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> linked = new(StringComparer.OrdinalIgnoreCase);

        public static long? Size(string name) => name != null && sizes.TryGetValue(name, out var size) ? size : null;
        public static bool IsLinked(string name) => name != null && linked.Contains(name);
        public static long Total => sizes.Values.Sum();

        public static async Task Scan()
        {
            var (newSizes, newLinked) = await Task.Run(() =>
            {
                var s = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var l = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(Global.ModsPath))
                    foreach (var folder in Directory.GetDirectories(Global.ModsPath))
                    {
                        var name = Path.GetFileName(folder);
                        try
                        {
                            s[name] = new DirectoryInfo(folder).GetDirectorySize();
                        }
                        catch { }
                        var metadata = ModDownloader.ReadMetadata(folder);
                        if (metadata?.homepage != null && GameBananaApi.TryParseProfileUrl(metadata.homepage.ToString(), out _, out _))
                            l.Add(name);
                    }
                return (s, l);
            });
            sizes = newSizes;
            linked = newLinked;
        }
    }

    public static class GameProcess
    {
        // The launcher (SparkingZERO.exe) starts SparkingZERO-Win64-Shipping.exe
        private static readonly string[] Names = { "SparkingZERO-Win64-Shipping", "SparkingZERO" };

        public static bool IsRunning()
        {
            foreach (var name in Names)
            {
                var processes = Process.GetProcessesByName(name);
                var running = processes.Length > 0;
                foreach (var process in processes)
                    process.Dispose();
                if (running)
                    return true;
            }
            return false;
        }
    }
}
