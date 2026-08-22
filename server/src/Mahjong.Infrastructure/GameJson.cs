using System.Text.Json;
using System.Text.Json.Serialization;
using Mahjong.Domain;

namespace Mahjong.Infrastructure;

/// <summary>
/// One serializer configuration shared by everything that writes JSON into the database or onto
/// the wire, so a value written by the API reads back the same way in a test or a migration.
/// </summary>
public static class GameJson
{
    public static readonly JsonSerializerOptions Options = Create();

    /// <summary>Same settings, but indented. Used for the game log, which gets read by humans.</summary>
    public static readonly JsonSerializerOptions Readable = Create(indented: true);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Serialises a game event by what it actually is, not by the type the caller is holding it as.
    ///
    /// Events are passed around as <see cref="GameEvent"/>, and System.Text.Json writes the
    /// declared type's properties, so <c>Serialize(evt)</c> on a <c>List&lt;GameEvent&gt;</c> item
    /// writes only <c>Seat</c> and silently drops the tile, the meld, the outcome - everything the
    /// log exists to record. Passing the runtime type is what makes the payload the whole event.
    ///
    /// No type discriminator is written: <c>GameActions.ActionType</c> already stores the name, and
    /// adding one would mean the stored JSON no longer reads back into the concrete event type.
    /// </summary>
    public static string SerializeEvent(GameEvent value) =>
        JsonSerializer.Serialize(value, value.GetType(), Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidOperationException($"Could not read {typeof(T).Name} from stored JSON.");

    private static JsonSerializerOptions Create(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        // Applies to enum values and, just as importantly, to enum dictionary keys: the scoring
        // profile is keyed by Ambition and WinBonus, and storing those as bare numbers would make
        // the stored rules break the moment an enum member is inserted anywhere but the end.
        options.Converters.Add(new JsonStringEnumConverter());

        // Tiles are written as their short code and physical tiles as their id. Serialising them
        // as objects would be both bulky (the wall alone is 144 of them) and wrong: Tile exposes
        // a PlayableIndex that throws for bonus tiles, so reflection over its properties would
        // blow up the first time a flower reached a snapshot.
        options.Converters.Add(new TileConverter());
        options.Converters.Add(new TileRefConverter());

        // The scoring tables are keyed by Ambition and WinBonus, and a room's rules JSON outlives
        // the enum. Reading has to survive a rule being retired, which ThirteenFlowers was.
        options.Converters.Add(new ScoreTableConverter());

        return options;
    }
}

/// <summary>Writes a <see cref="Tile"/> as its two-character code, e.g. "D5".</summary>
internal sealed class TileConverter : JsonConverter<Tile>
{
    public override Tile Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        Tile.Parse(reader.GetString() ?? throw new JsonException("Expected a tile code."));

    public override void Write(Utf8JsonWriter writer, Tile value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Code);

    public override Tile ReadAsPropertyName(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        Tile.Parse(reader.GetString() ?? throw new JsonException("Expected a tile code."));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Tile value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Code);
}

/// <summary>Writes a <see cref="TileRef"/> as its id, which is all it is.</summary>
internal sealed class TileRefConverter : JsonConverter<TileRef>
{
    public override TileRef Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, TileRef value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Id);
}

/// <summary>
/// Reads a scoring table keyed by an enum, dropping any key the enum no longer has a member for.
/// A room stores its rules as JSON once and reads them back on every connection, so retiring a
/// rule would otherwise make every room saved before the change fail to load. Both tables are read
/// with TryGetValue, so a missing entry is already handled as "this rule pays nothing".
/// </summary>
internal sealed class ScoreTableConverter : JsonConverterFactory
{
    public override bool CanConvert(Type type) =>
        type.IsGenericType
        && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)
        && type.GetGenericArguments() is [{ IsEnum: true }, var value]
        && value == typeof(int);

    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(Table<>).MakeGenericType(type.GetGenericArguments()[0]))!;

    private sealed class Table<TKey> : JsonConverter<IReadOnlyDictionary<TKey, int>>
        where TKey : struct, Enum
    {
        public override IReadOnlyDictionary<TKey, int> Read(
            ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException($"Expected an object for the {typeof(TKey).Name} table.");

            var table = new Dictionary<TKey, int>();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString()!;
                reader.Read();
                var units = reader.GetInt32();

                // Enum.TryParse also accepts a bare number and hands back an undefined member,
                // so the IsDefined check is what actually rejects a retired rule.
                if (Enum.TryParse<TKey>(name, out var key) && Enum.IsDefined(key))
                    table[key] = units;
            }

            return table;
        }

        public override void Write(
            Utf8JsonWriter writer, IReadOnlyDictionary<TKey, int> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var (key, units) in value) writer.WriteNumber(key.ToString(), units);
            writer.WriteEndObject();
        }
    }
}
