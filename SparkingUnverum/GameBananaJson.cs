using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SparkingUnverum
{
    // GameBanana answers aren't always consistent: deleted mods come with null dates and counters,
    // empty objects sometimes come as [] and numbers sometimes as strings.
    // These options read all of that without failing, and a broken item only skips that item.
    public static class GameBananaJson
    {
        public static readonly JsonSerializerOptions Options = Create(true);
        // Used inside TolerantObjectConverter to deserialize the object itself without calling the converter again
        internal static readonly JsonSerializerOptions InnerOptions = Create(false);

        private static JsonSerializerOptions Create(bool withObjectConverter)
        {
            var options = new JsonSerializerOptions
            {
                NumberHandling = JsonNumberHandling.AllowReadingFromString,
                Converters =
                {
                    new LenientInt64Converter(),
                    new LenientInt32Converter(),
                    new LenientBooleanConverter(),
                    new TolerantListConverterFactory()
                }
            };
            if (withObjectConverter)
                options.Converters.Add(new TolerantObjectConverterFactory());
            return options;
        }

        private static bool TryReadDouble(ref Utf8JsonReader reader, out double value)
        {
            value = 0;
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return reader.TryGetDouble(out value);
                case JsonTokenType.String:
                    return double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                case JsonTokenType.True:
                    value = 1;
                    return true;
                case JsonTokenType.False:
                    return true;
                default:
                    // null, objects and arrays
                    reader.Skip();
                    return false;
            }
        }

        public class LenientInt64Converter : JsonConverter<long>
        {
            public override bool HandleNull => true;
            public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var exact))
                    return exact;
                return TryReadDouble(ref reader, out var value) && !double.IsNaN(value)
                    ? (long)Math.Clamp(value, long.MinValue, long.MaxValue) : 0;
            }
            public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
        }

        public class LenientInt32Converter : JsonConverter<int>
        {
            public override bool HandleNull => true;
            public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var exact))
                    return exact;
                return TryReadDouble(ref reader, out var value) && !double.IsNaN(value)
                    ? (int)Math.Clamp(value, int.MinValue, int.MaxValue) : 0;
            }
            public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
        }

        public class LenientBooleanConverter : JsonConverter<bool>
        {
            public override bool HandleNull => true;
            public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.True:
                        return true;
                    case JsonTokenType.False:
                        return false;
                    case JsonTokenType.String:
                        var text = reader.GetString();
                        return text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
                    case JsonTokenType.Number:
                        return reader.TryGetDouble(out var number) && number != 0;
                    default:
                        reader.Skip();
                        return false;
                }
            }
            public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
        }

        // Lists of GameBanana items: anything that isn't an array becomes null and items that can't be read are skipped
        public class TolerantListConverterFactory : JsonConverterFactory
        {
            public override bool CanConvert(Type typeToConvert) =>
                typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(List<>)
                && typeToConvert.GetGenericArguments()[0].Namespace == typeof(GameBananaJson).Namespace;

            public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
                (JsonConverter)Activator.CreateInstance(typeof(TolerantListConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]));

            private class TolerantListConverter<T> : JsonConverter<List<T>>
            {
                public override List<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                {
                    if (reader.TokenType != JsonTokenType.StartArray)
                    {
                        reader.Skip();
                        return null;
                    }
                    using var document = JsonDocument.ParseValue(ref reader);
                    var list = new List<T>();
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        try
                        {
                            var item = element.Deserialize<T>(options);
                            if (item != null)
                                list.Add(item);
                        }
                        catch (Exception e) when (e is JsonException || e is InvalidOperationException || e is FormatException)
                        {
                            Global.logger?.WriteLine(Loc.T("Log.SkippedItem", typeof(T).Name, e.Message), LoggerType.Warning);
                        }
                    }
                    return list;
                }
                public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
                {
                    writer.WriteStartArray();
                    foreach (var item in value)
                        JsonSerializer.Serialize(writer, item, options);
                    writer.WriteEndArray();
                }
            }
        }

        // Objects that GameBanana sometimes sends as [] or null when they are empty
        public class TolerantObjectConverterFactory : JsonConverterFactory
        {
            private static readonly Type[] Types = { typeof(GameBananaPreviewMedia), typeof(GameBananaCategory), typeof(GameBananaMember), typeof(GameBananaGame) };
            public override bool CanConvert(Type typeToConvert) => Types.Contains(typeToConvert);

            public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
                (JsonConverter)Activator.CreateInstance(typeof(TolerantObjectConverter<>).MakeGenericType(typeToConvert));

            private class TolerantObjectConverter<T> : JsonConverter<T> where T : class
            {
                public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        return null;
                    }
                    using var document = JsonDocument.ParseValue(ref reader);
                    try
                    {
                        return document.RootElement.Deserialize<T>(InnerOptions);
                    }
                    catch (Exception e) when (e is JsonException || e is InvalidOperationException || e is FormatException)
                    {
                        return null;
                    }
                }
                public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
                    JsonSerializer.Serialize(writer, value, InnerOptions);
            }
        }
    }
}
