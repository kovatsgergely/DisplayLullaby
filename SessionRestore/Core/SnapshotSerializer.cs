using System.Text.Json;
using System.Text.Json.Serialization;

namespace DisplayLullaby.SessionRestore;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionSnapshot))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;

/// <summary>AOT-safe (source generated) JSON serialization for snapshots.</summary>
internal static class SnapshotSerializer
{
    public static string Serialize(SessionSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, SessionJsonContext.Default.SessionSnapshot);

    public static bool TryDeserialize(string json, out SessionSnapshot? snapshot)
    {
        try
        {
            snapshot = JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionSnapshot);
            if (snapshot is null || snapshot.Version < 1 || snapshot.Version > SessionSnapshot.CurrentVersion)
            {
                snapshot = null;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            snapshot = null;
            return false;
        }
    }
}
