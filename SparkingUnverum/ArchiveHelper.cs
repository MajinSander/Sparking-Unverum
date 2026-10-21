using System;
using System.IO;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace SparkingUnverum
{
    public static class ArchiveHelper
    {
        // Files GameBanana sometimes serves without an archive around them
        private static readonly string[] LooseExtensions = { ".pak", ".utoc", ".ucas", ".sig", ".json", ".lua", ".dll", ".txt" };

        // Extracts zip, rar and 7z archives into destination, returns false if nothing was extracted
        public static bool Extract(string archivePath, string destination)
        {
            Directory.CreateDirectory(destination);
            if (LooseExtensions.Contains(Path.GetExtension(archivePath).ToLowerInvariant()))
            {
                File.Copy(archivePath, Path.Combine(destination, Path.GetFileName(archivePath)), true);
                return true;
            }
            var count = 0;
            using (var archive = ArchiveFactory.OpenArchive(archivePath))
            {
                foreach (var entry in archive.Entries.Where(x => !x.IsDirectory))
                {
                    entry.WriteToDirectory(destination, new ExtractionOptions()
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                    count++;
                }
            }
            return count > 0;
        }

        // Deletes everything in a mod folder except its mod.json, used before extracting an update
        public static void ClearModFolder(string path)
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
                return;
            foreach (var file in dir.GetFiles())
                if (!file.Name.Equals("mod.json", StringComparison.OrdinalIgnoreCase))
                    file.Delete();
            foreach (var sub in dir.GetDirectories())
                sub.Delete(true);
        }

        public static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        public static string SafeFolderName(string name)
        {
            var clean = string.Concat((name ?? string.Empty).Split(Path.GetInvalidFileNameChars())).Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(clean) ? "Mod" : clean;
        }
    }
}
