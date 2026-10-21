using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Markup;

namespace SparkingUnverum
{
    // Translations live in Lang/es.json and Lang/en.json
    public static class Loc
    {
        public static readonly string[] Languages = { "es", "en" };
        private static Dictionary<string, string> strings;
        private static Dictionary<string, string> fallback;
        public static string Language { get; private set; }

        public static void Init(string language)
        {
            if (language == null || Array.IndexOf(Languages, language) < 0)
                language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";
            Language = language;
            fallback = Load("en");
            strings = language == "en" ? fallback : Load(language);
            var culture = new CultureInfo(language == "es" ? "es-ES" : "en-US");
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        private static Dictionary<string, string> Load(string language)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Lang/{language}.json");
            if (stream == null)
                return new();
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd()) ?? new();
        }

        public static string T(string key)
        {
            if (strings == null)
                Init(null);
            if (strings.TryGetValue(key, out var value) || fallback.TryGetValue(key, out value))
                return value;
            return key;
        }

        public static string T(string key, params object[] args)
        {
            try
            {
                return string.Format(T(key), args);
            }
            catch (FormatException)
            {
                return T(key);
            }
        }
    }

    // Usage in XAML: Content="{l:Tr Main.Launch}"
    [MarkupExtensionReturnType(typeof(string))]
    public class TrExtension : MarkupExtension
    {
        public string Key { get; set; }
        public TrExtension() { }
        public TrExtension(string key)
        {
            Key = key;
        }
        public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
    }
}
