namespace SonarMiniMixer.Core;

public sealed record MixerChannel(
    string Id,
    string Name,
    double Volume,
    bool Muted,
    string Accent,
    int SortOrder);

public sealed record MixerState(
    string Mode,
    IReadOnlyList<MixerChannel> Channels,
    double ChatMix,
    string ChatMixState)
{
    public bool CanControl => string.Equals(Mode, "classic", StringComparison.OrdinalIgnoreCase);
    public bool CanControlChatMix => CanControl && string.Equals(ChatMixState, "enabled", StringComparison.OrdinalIgnoreCase);
}

public sealed record SonarPreset(
    Guid Id,
    string Name,
    bool IsFavorite,
    int FavoritePosition);

public sealed record SonarPresetCatalog(
    string Channel,
    IReadOnlyList<SonarPreset> Items,
    Guid? SelectedId);

public sealed record SonarAudioDevice(
    string Id,
    string Name,
    string DataFlow);

public sealed record SonarDeviceRouting(
    IReadOnlyList<SonarAudioDevice> OutputDevices,
    IReadOnlyList<SonarAudioDevice> MicrophoneDevices,
    IReadOnlyDictionary<string, string?> ChannelDeviceIds,
    /// <summary>Channels whose Sonar redirection is not currently running (route not passing audio).</summary>
    IReadOnlySet<string>? StalledChannels = null)
{
    /// <summary>Never null, so callers can query without guarding.</summary>
    public IReadOnlySet<string> StalledChannels { get; init; } =
        StalledChannels ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public interface ISonarEndpointProvider
{
    Task<Uri> GetAsync(CancellationToken cancellationToken = default);
    void Invalidate();
}

public interface ISonarClient
{
    Task<MixerState> GetStateAsync(CancellationToken cancellationToken = default);
    Task<SonarPresetCatalog> GetPresetsAsync(string channel, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, Guid?>> GetSelectedPresetIdsAsync(CancellationToken cancellationToken = default);
    Task SelectPresetAsync(string channel, Guid presetId, CancellationToken cancellationToken = default);
    Task<SonarDeviceRouting> GetDeviceRoutingAsync(CancellationToken cancellationToken = default);
    Task SetChannelDeviceAsync(string channel, string deviceId, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(string channel, double volume, CancellationToken cancellationToken = default);
    Task SetMuteAsync(string channel, bool muted, CancellationToken cancellationToken = default);
    Task SetChatMixAsync(double balance, CancellationToken cancellationToken = default);
}

public interface ISonarAudioController
{
    Task SetVolumeAsync(string channel, double volume, CancellationToken cancellationToken = default);
    Task SetMuteAsync(string channel, bool muted, CancellationToken cancellationToken = default);
}

public sealed class SonarConnectionException(string message, Exception? inner = null) : Exception(message, inner);
