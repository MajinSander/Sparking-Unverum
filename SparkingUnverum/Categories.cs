using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace SparkingUnverum
{
    // A folder the user sorts mods into: a character or a general one (mechanics, maps...).
    // A mod can be in several folders
    public class ModCategory : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public bool Character { get; set; }
        // "mod:<mod name>" uses that mod's picture, "file:<name>" an image in the Images folder, null picks one by itself
        public string Icon { get; set; }
        // FontAwesome icon of general folders without a picture
        public string Symbol { get; set; }
        public List<string> Mods { get; set; } = new();

        [JsonIgnore]
        public int Count { get; private set; }
        [JsonIgnore]
        public int EnabledCount { get; private set; }
        [JsonIgnore]
        public string Summary => $"{Loc.T(Count == 1 ? "Categories.ModsOne" : "Categories.Mods", Count)} · {Loc.T(EnabledCount == 1 ? "Categories.EnabledOne" : "Categories.Enabled", EnabledCount)}";
        [JsonIgnore]
        public string Initials => Categories.Initials(Name);
        [JsonIgnore]
        public bool HasImage => IconImage != null;
        private ImageSource iconImage;
        [JsonIgnore]
        public ImageSource IconImage
        {
            get => iconImage;
            set { iconImage = value; Changed(nameof(IconImage)); Changed(nameof(HasImage)); }
        }
        private bool isCurrent;
        // The folder whose mods are being shown
        [JsonIgnore]
        public bool IsCurrent
        {
            get => isCurrent;
            set { isCurrent = value; Changed(nameof(IsCurrent)); }
        }
        [JsonIgnore]
        public FontAwesome5.EFontAwesomeIcon SymbolIcon =>
            Enum.TryParse<FontAwesome5.EFontAwesomeIcon>(Symbol, out var icon) ? icon : FontAwesome5.EFontAwesomeIcon.Solid_Folder;

        // Counts only mods that are still installed, enabled ones in the current loadout
        public void Recount(IEnumerable<Mod> mods)
        {
            var names = new HashSet<string>(Mods);
            var present = mods.Where(m => names.Contains(m.name)).ToList();
            Count = present.Count;
            EnabledCount = present.Count(m => m.enabled);
            Changed(nameof(Count));
            Changed(nameof(EnabledCount));
            Changed(nameof(Summary));
        }

        public void NameChanged()
        {
            Changed(nameof(Name));
            Changed(nameof(Initials));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    // Categories.json, apart from SparkingUnverum.json so older versions never drop it
    public class CategoryData
    {
        public List<ModCategory> Folders { get; set; } = new();
        // Mod name -> picture chosen by the user for mods without one, in the Images folder
        public Dictionary<string, string> ModImages { get; set; } = new();
        public double CardWidth { get; set; } = 210;
    }

    public static class Categories
    {
        public static string FilePath => Path.Combine(Global.assemblyLocation, "Categories.json");
        public static string ImagesPath => Path.Combine(Global.assemblyLocation, "Images");
        private static CategoryData data;

        public static CategoryData Data
        {
            get
            {
                if (data == null)
                    Load();
                return data;
            }
        }

        public static void Load()
        {
            data = null;
            if (File.Exists(FilePath))
            {
                try
                {
                    data = JsonSerializer.Deserialize<CategoryData>(File.ReadAllText(FilePath));
                }
                catch (Exception e)
                {
                    Global.logger?.WriteLine(Loc.T("Categories.ReadError", e.Message), LoggerType.Error);
                    // Keep the broken file, never overwrite what the user sorted
                    try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
                }
            }
            if (data == null)
            {
                data = new CategoryData();
                // A starting point for the general folders, characters are created by the user
                data.Folders.Add(new ModCategory { Name = Loc.T("Categories.DefaultMechanics"), Symbol = "Solid_Cogs" });
                data.Folders.Add(new ModCategory { Name = Loc.T("Categories.DefaultMaps"), Symbol = "Solid_Map" });
                data.Folders.Add(new ModCategory { Name = Loc.T("Categories.DefaultInterface"), Symbol = "Solid_Desktop" });
                data.Folders.Add(new ModCategory { Name = Loc.T("Categories.DefaultSound"), Symbol = "Solid_Music" });
            }
            data.Folders ??= new();
            data.ModImages ??= new();
            foreach (var folder in data.Folders)
                folder.Mods ??= new();
            data.Folders.RemoveAll(f => string.IsNullOrWhiteSpace(f.Name));
        }

        public static void Save()
        {
            try
            {
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(Data, Global.JsonOptions));
                File.Move(temp, FilePath, true);
            }
            catch (Exception e)
            {
                Global.logger?.WriteLine(Loc.T("Categories.WriteError", e.Message), LoggerType.Error);
            }
        }

        public static List<ModCategory> Of(string mod) => Data.Folders.Where(f => f.Mods.Contains(mod)).ToList();

        public static List<ModCategory> Sorted(bool character) =>
            Data.Folders.Where(f => f.Character == character).OrderBy(f => f.Name, new NaturalSort()).ToList();

        public static bool NameTaken(string name, ModCategory except = null) =>
            Data.Folders.Any(f => f != except && f.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));

        // Returns how many were added
        public static int Add(ModCategory folder, IEnumerable<string> mods)
        {
            var added = 0;
            foreach (var mod in mods)
                if (!folder.Mods.Contains(mod))
                {
                    folder.Mods.Add(mod);
                    added++;
                }
            Save();
            return added;
        }

        public static void Remove(ModCategory folder, IEnumerable<string> mods)
        {
            var set = new HashSet<string>(mods);
            folder.Mods.RemoveAll(set.Contains);
            if (folder.Icon != null && folder.Icon.StartsWith("mod:") && set.Contains(folder.Icon[4..]))
                folder.Icon = null;
            Save();
        }

        // Called when a mod folder is renamed
        public static void RenameMod(string oldName, string newName)
        {
            var changed = false;
            foreach (var folder in Data.Folders)
            {
                var index = folder.Mods.IndexOf(oldName);
                if (index >= 0)
                {
                    if (folder.Mods.Contains(newName))
                        folder.Mods.RemoveAt(index);
                    else
                        folder.Mods[index] = newName;
                    changed = true;
                }
                if (folder.Icon == "mod:" + oldName)
                {
                    folder.Icon = "mod:" + newName;
                    changed = true;
                }
            }
            if (Data.ModImages.Remove(oldName, out var image))
            {
                Data.ModImages[newName] = image;
                changed = true;
            }
            if (changed)
                Save();
        }

        // Called when several folders of a mod are merged into one: the merged mod keeps every category
        public static void MergeMods(IEnumerable<string> parts, string merged)
        {
            var names = new HashSet<string>(parts);
            var changed = false;
            foreach (var folder in Data.Folders)
            {
                if (folder.Mods.RemoveAll(names.Contains) > 0)
                {
                    folder.Mods.Add(merged);
                    changed = true;
                }
                if (folder.Icon != null && folder.Icon.StartsWith("mod:") && names.Contains(folder.Icon[4..]))
                {
                    folder.Icon = "mod:" + merged;
                    changed = true;
                }
            }
            if (!Data.ModImages.ContainsKey(merged))
                foreach (var part in names)
                    if (Data.ModImages.TryGetValue(part, out var image))
                    {
                        Data.ModImages[merged] = image;
                        changed = true;
                        break;
                    }
            if (changed)
                Save();
        }

        // Copies a picture into the Images folder and returns its file name there
        public static string ImportImage(string file)
        {
            Directory.CreateDirectory(ImagesPath);
            var name = Guid.NewGuid().ToString("N")[..12] + Path.GetExtension(file).ToLowerInvariant();
            File.Copy(file, Path.Combine(ImagesPath, name));
            return name;
        }

        public static string Initials(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "?";
            var words = Regex.Matches(name, @"[\p{L}\p{N}]+").Select(m => m.Value).ToList();
            if (words.Count == 0)
                return name.Trim()[..1].ToUpperInvariant();
            var letters = words.Count == 1 ? words[0][..Math.Min(2, words[0].Length)] : $"{words[0][0]}{words[1][0]}";
            return letters.ToUpperInvariant();
        }
    }

    // Live text filter: words must all appear, commas give alternatives ("goku ssj, kakarot")
    public static class ModSearch
    {
        public static List<string[]> Parse(string query)
        {
            var result = new List<string[]>();
            if (string.IsNullOrWhiteSpace(query))
                return result;
            foreach (var part in query.Split(',', ';', '|'))
            {
                var words = Normalize(part).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 0)
                    result.Add(words);
            }
            return result;
        }

        public static bool Matches(string normalizedText, List<string[]> query)
        {
            if (query.Count == 0)
                return true;
            if (string.IsNullOrEmpty(normalizedText))
                return false;
            return query.Any(words => words.All(normalizedText.Contains));
        }

        // Lowercase, no accents, separators as spaces: "Gokú_SSJ-Dios" -> "goku ssj dios"
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark)
                    continue;
                builder.Append(c is '_' or '-' or '.' or '(' or ')' or '[' or ']' ? ' ' : c);
            }
            return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), @"\s+", " ").Trim();
        }

        // Text of mod.json that can also be searched: GameBanana description, download, author, category
        public static string MetadataText(Metadata metadata)
        {
            if (metadata == null)
                return string.Empty;
            var text = string.Join(" ", new[] { metadata.description, metadata.filedescription, metadata.submitter, metadata.cat }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return Normalize(Regex.Replace(text, "<[^>]+>", " "));
        }
    }
}
