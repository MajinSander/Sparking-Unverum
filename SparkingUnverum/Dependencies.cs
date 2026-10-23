using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SparkingUnverum
{
    // Files the program carries inside (so the single .exe works on its own) and writes next to itself when needed
    public static class Dependencies
    {
        private const string Ue4ssPrefix = "ue4ss/";
        private static readonly object extracting = new();

        // Folder with UE4SS, extracted the first time and again when a file is missing or different
        public static string Ue4ss()
        {
            var folder = Path.Combine(Global.assemblyLocation, "Dependencies", "ue4ss");
            var assembly = Assembly.GetExecutingAssembly();
            lock (extracting)
            {
                foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Ue4ssPrefix, StringComparison.Ordinal)))
                {
                    var relative = name[Ue4ssPrefix.Length..].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    var target = Path.Combine(folder, relative);
                    using var stream = assembly.GetManifestResourceStream(name);
                    if (stream == null || (File.Exists(target) && new FileInfo(target).Length == stream.Length))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using var file = File.Create(target);
                    stream.CopyTo(file);
                }
            }
            return folder;
        }
    }
}
