using System.Text.Json;

namespace SonarMiniMixer.Core;

public static class SonarStateParser
{
    private sealed record ChannelMetadata(string Name, string Accent, int SortOrder);

    private static readonly IReadOnlyDictionary<string, ChannelMetadata> Metadata =
        new Dictionary<string, ChannelMetadata>(StringComparer.Ordinal)
        {
            ["master"] = new("Master", "#19D3C5", 0),
            ["game"] = new("Game", "#47F06A", 10),
            ["chatRender"] = new("Chat", "#36B7FF", 20),
            ["media"] = new("Media", "#FFB000", 30),
            ["aux"] = new("Aux", "#B96CFF", 40),
            ["chatCapture"] = new("Mic", "#FF5F69", 50)
        };

    public static MixerState Parse(string volumesJson, string chatMixJson, string modeJson)
    {
        try
        {
            using var volumes = JsonDocument.Parse(volumesJson);
            using var chatMix = JsonDocument.Parse(chatMixJson);
            var mode = JsonSerializer.Deserialize<string>(modeJson);
            if (string.IsNullOrWhiteSpace(mode)) mode = "unknown";
            var channels = new List<MixerChannel>();

            // Mirror Sonar verbatim: show exactly the values SteelSeries GG displays.
            if (volumes.RootElement.TryGetProperty("masters", out var master))
                AddChannel(channels, "master", master);

            if (volumes.RootElement.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Object)
                foreach (var property in devices.EnumerateObject())
                    AddChannel(channels, property.Name, property.Value);

            channels.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            var balance = chatMix.RootElement.TryGetProperty("balance", out var value) ? value.GetDouble() : 0;
            var chatMixState = chatMix.RootElement.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                ? state.GetString() ?? "unknown"
                // No ChatMix payload at all (endpoint removed in newer GG builds) -> fail closed.
                : chatMix.RootElement.TryGetProperty("balance", out _) ? "enabled" : "unavailable";
            return new MixerState(mode, channels, Math.Clamp(balance, -1, 1), chatMixState);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new SonarConnectionException("Sonar returned an unexpected mixer response.", exception);
        }
    }

    public static bool IsSafeChannel(string channel) => Metadata.ContainsKey(channel);
    public static string GetWireChannel(string channel) => channel == "master" ? "Master" : channel;

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
