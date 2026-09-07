using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saehakgi.Core.Util;

/// <summary>Shared JSON settings so manifests serialize consistently and readably.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException($"Failed to deserialize {typeof(T).Name}.");
}
