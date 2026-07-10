using NAudio.CoreAudioApi;

namespace SonarMiniMixer.Core;

public sealed class WindowsSonarAudioController : ISonarAudioController
{
    private static readonly IReadOnlyDictionary<string, (string[] Hints, DataFlow Flow)> Endpoints =
        new Dictionary<string, (string[], DataFlow)>(StringComparer.Ordinal)
        {
            ["game"] = (["SteelSeries Sonar - Gaming"], DataFlow.Render),
            ["chatRender"] = (["SteelSeries Sonar - Chat"], DataFlow.Render),
            ["media"] = (["SteelSeries Sonar - Media"], DataFlow.Render),
            ["aux"] = (["SteelSeries Sonar - Aux"], DataFlow.Render),
            ["chatCapture"] = (["SteelSeries Sonar - Microphone"], DataFlow.Capture)
        };

    private static readonly string[] MasterChannels = ["game", "chatRender", "media", "aux"];

    public Task SetVolumeAsync(string channel, double volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channel == "master") SetMasterVolume((float)volume);
        else SetChannelVolume(channel, (float)volume);
        return Task.CompletedTask;
    }

    public Task SetMuteAsync(string channel, bool muted, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (channel == "master")
        {
            foreach (var id in MasterChannels) WithEndpoint(id, endpoint => endpoint.AudioEndpointVolume.Mute = muted);
        }
        else WithEndpoint(channel, endpoint => endpoint.AudioEndpointVolume.Mute = muted);
        return Task.CompletedTask;
    }

    private static void SetChannelVolume(string channel, float volume) =>
        WithEndpoint(channel, endpoint =>
        {
            endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0, 1);
        });

    private static void SetMasterVolume(float requested)
    {
        requested = Math.Clamp(requested, 0, 1);
        var current = MasterChannels.ToDictionary(id => id, ReadVolume);
        var oldMaster = current.Values.DefaultIfEmpty(0).Max();
        foreach (var (channel, value) in current)
        {
            var ratio = oldMaster > 0.0001f ? value / oldMaster : 1f;
            SetChannelVolume(channel, requested * ratio);
        }
    }

    private static float ReadVolume(string channel)
    {
        var value = 0f;
        WithEndpoint(channel, endpoint => value = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar);
        return value;
    }

    private static void WithEndpoint(string channel, Action<MMDevice> action)
    {
        if (!Endpoints.TryGetValue(channel, out var descriptor))
            throw new SonarConnectionException($"Unknown Sonar audio channel '{channel}'.");

        using var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(descriptor.Flow, DeviceState.Active);
        var endpoint = devices.FirstOrDefault(device => descriptor.Hints.Any(hint =>
            device.FriendlyName.Contains(hint, StringComparison.OrdinalIgnoreCase)));
        if (endpoint is null)
            throw new SonarConnectionException($"The Windows audio endpoint for Sonar {channel} is unavailable.");
        using (endpoint) action(endpoint);
    }
}
