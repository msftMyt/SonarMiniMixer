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
    double ChatMix)
{
    public bool CanControl => string.Equals(Mode, "classic", StringComparison.OrdinalIgnoreCase);
}

public interface ISonarEndpointProvider
{
    Task<Uri> GetAsync(CancellationToken cancellationToken = default);
    void Invalidate();
}

public interface ISonarClient
{
    Task<MixerState> GetStateAsync(CancellationToken cancellationToken = default);
    Task SetVolumeAsync(string channel, double volume, CancellationToken cancellationToken = default);
    Task SetMuteAsync(string channel, bool muted, CancellationToken cancellationToken = default);
    Task SetChatMixAsync(double balance, CancellationToken cancellationToken = default);
}

public sealed class SonarConnectionException(string message, Exception? inner = null) : Exception(message, inner);
