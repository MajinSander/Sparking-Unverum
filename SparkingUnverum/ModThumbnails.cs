using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SparkingUnverum
{
    // Pictures of the installed mods: the one chosen by the user, the GameBanana preview (kept on disk so it
    // also works offline) or an image found inside the mod folder
    public static class ModThumbnails
    {
        public static string CachePath => Path.Combine(Global.assemblyLocation, "Cache", "Previews");
        private const int DecodeWidth = 340;
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };
        private static readonly string[] PreferredWords = { "preview", "thumb", "cover", "banner", "icon", "portada" };
        private static readonly HttpClient http = CreateClient();
        private static readonly SemaphoreSlim downloads = new(4);
        private static readonly ConcurrentDictionary<string, ImageSource> memory = new(StringComparer.OrdinalIgnoreCase);

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"MajinUnverum/{App.Version}");
            return client;
        }

        // Never throws, null when the mod has no picture at all.
        // Files are read in the background but pictures are made on the calling thread: a picture made on a
        // thread pool thread leaves a WPF dispatcher there that later blocks the window
        public static async Task<ImageSource> ForMod(string mod, Metadata metadata)
        {
            try
            {
                if (Categories.Data.ModImages.TryGetValue(mod, out var chosen))
                {
                    var image = await FromFileAsync(Path.Combine(Categories.ImagesPath, chosen));
                    if (image != null)
                        return image;
                }
                if (metadata?.preview != null && metadata.preview.IsAbsoluteUri)
                {
                    var file = await Download(metadata.preview);
                    var image = file == null ? null : await FromFileAsync(file);
                    if (image != null)
                        return image;
                }
                var local = await Task.Run(() => FindInFolder(Path.Combine(Global.ModsPath, mod)));
                return local == null ? null : await FromFileAsync(local);
            }
            catch
            {
                return null;
            }
        }

        public static async Task<ImageSource> FromFileAsync(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            if (memory.TryGetValue(path, out var cached))
                return cached;
            try
            {
                return Decode(path, await File.ReadAllBytesAsync(path));
            }
            catch
            {
                return null;
            }
        }

        public static ImageSource FromFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;
            if (memory.TryGetValue(path, out var cached))
                return cached;
            try
            {
                return Decode(path, File.ReadAllBytes(path));
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource Decode(string path, byte[] bytes)
        {
            try
            {
                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = DecodeWidth;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                memory[path] = bitmap;
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        // A picture shipped inside the mod, preferring names like preview or cover
        private static string FindInFolder(string folder)
        {
            if (!Directory.Exists(folder))
                return null;
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var images = Directory.EnumerateFiles(folder, "*", options)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new FileInfo(f))
                .Where(f => f.Length is > 2_000 and < 10_000_000)
                .ToList();
            return images.OrderByDescending(f => PreferredWords.Any(w => f.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(f => Path.GetRelativePath(folder, f.FullName).Count(c => c == Path.DirectorySeparatorChar))
                .ThenByDescending(f => f.Length)
                .FirstOrDefault()?.FullName;
        }

        // GameBanana has a 530px wide copy of every screenshot: "530-90_<file>" next to the original
        private static Uri Small(Uri uri)
        {
            var text = uri.ToString();
            if (!uri.Host.EndsWith("gamebanana.com", StringComparison.OrdinalIgnoreCase))
                return null;
            var slash = text.LastIndexOf('/');
            var file = text[(slash + 1)..];
            if (file.StartsWith("530-90_") || file.StartsWith("220-90_") || file.StartsWith("100-90_"))
                return null;
            return new Uri(text[..(slash + 1)] + "530-90_" + file);
        }

        // Keeps the picture on disk and returns its file, null when it can't be downloaded
        public static async Task<string> Download(Uri uri)
        {
            var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(uri.ToString())))[..16].ToLowerInvariant();
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrEmpty(extension) || extension.Length > 5)
                extension = ".jpg";
            var file = Path.Combine(CachePath, hash + extension);
            if (File.Exists(file))
                return file;
            await downloads.WaitAsync();
            try
            {
                if (File.Exists(file))
                    return file;
                Directory.CreateDirectory(CachePath);
                foreach (var source in new[] { Small(uri), uri }.Where(u => u != null))
                {
                    try
                    {
                        var bytes = await http.GetByteArrayAsync(source);
                        if (bytes.Length == 0)
                            continue;
                        var temp = file + ".part";
                        await File.WriteAllBytesAsync(temp, bytes);
                        File.Move(temp, file, true);
                        return file;
                    }
                    catch
                    {
                        // Try the original picture, or nothing when offline
                    }
                }
                return null;
            }
            finally
            {
                downloads.Release();
            }
        }
    }
}
