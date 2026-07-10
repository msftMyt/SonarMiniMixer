using System.Text.Json;

namespace SonarMiniMixer.Core;

public static class SonarStateParser
{
    private sealed record ChannelMetadata(string Name, string Accent, int SortOrder);

    private static readonly IReadOnlyDictionary<string, ChannelMetadata> Metadata =
        new Dictionary<string, ChannelMetadata>(StringComparer.Ordinal)
        {
            ["master"] = new("Master", "#7C8CFF", 0),
            ["game"] = new("Game", "#50FA7B", 10),
            ["chatRender"] = new("Chat", "#4DD8FF", 20),
            ["media"] = new("Media", "#FF79C6", 30),
            ["aux"] = new("Aux", "#BD93F9", 40),
            ["chatCapture"] = new("Mic", "#FFB86C", 50)
        };

    public static MixerState Parse(string volumesJson, string chatMixJson, string modeJson)
    {
        try
        {
            using var volumes = JsonDocument.Parse(volumesJson);
            using var chatMix = JsonDocument.Parse(chatMixJson);
            var mode = JsonSerializer.Deserialize<string>(modeJson) ?? "classic";
            var channels = new List<MixerChannel>();

            if (volumes.RootElement.TryGetProperty("masters", out var master))
                AddChannel(channels, "master", master);

            if (volumes.RootElement.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Object)
                foreach (var property in devices.EnumerateObject())
                    AddChannel(channels, property.Name, property.Value);

            channels.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            var balance = chatMix.RootElement.TryGetProperty("balance", out var value) ? value.GetDouble() : 0;
            return new MixerState(mode, channels, Math.Clamp(balance, -1, 1));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new SonarConnectionException("Sonar returned an unexpected mixer response.", exception);
        }
    }

    public static bool IsSafeChannel(string channel) => Metadata.ContainsKey(channel);

    private static void AddChannel(List<MixerChannel> channels, string id, JsonElement source)
    {
        if (!Metadata.TryGetValue(id, out var metadata) ||
            !source.TryGetProperty("classic", out var classic) ||
            !classic.TryGetProperty("volume", out var volume) ||
            !classic.TryGetProperty("muted", out var muted)) return;

        channels.Add(new MixerChannel(
            id,
            metadata.Name,
            Math.Clamp(volume.GetDouble(), 0, 1),
            muted.GetBoolean(),
            metadata.Accent,
            metadata.SortOrder));
    }
}
