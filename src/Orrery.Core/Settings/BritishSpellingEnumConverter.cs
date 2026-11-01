using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orrery.Core.Settings;

/// <summary>
/// Enums by camel-case name, as <see cref="JsonStringEnumConverter"/> writes and reads them, that also reads the
/// British spellings builds before the switch to American spelling wrote: "colourBlind" is read as
/// <see cref="InterfaceColorScheme.ColorBlind"/>. Only the American names are written.
/// </summary>
sealed class BritishSpellingEnumConverter : JsonConverterFactory
{
    readonly JsonStringEnumConverter _inner = new(JsonNamingPolicy.CamelCase);

    public override bool CanConvert(Type typeToConvert) => _inner.CanConvert(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert), _inner.CreateConverter(typeToConvert, options))!;

    /// <summary>The American name for <paramref name="name"/>, which is unchanged when it has no British spelling.</summary>
    internal static string American(string name) =>
        name.Replace("colour", "color", StringComparison.Ordinal).Replace("Colour", "Color", StringComparison.Ordinal);

    sealed class Converter<T>(JsonConverter inner) : JsonConverter<T>
        where T : struct, Enum
    {
        readonly JsonConverter<T> _inner = (JsonConverter<T>)inner;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } name && American(name) is var american && american != name)
            {
                var rewritten = new Utf8JsonReader(JsonSerializer.SerializeToUtf8Bytes(american));
                rewritten.Read();
                return _inner.Read(ref rewritten, typeToConvert, options);
            }

            return _inner.Read(ref reader, typeToConvert, options);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => _inner.Write(writer, value, options);
    }
}
