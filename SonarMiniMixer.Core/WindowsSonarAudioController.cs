using NAudio.CoreAudioApi;

namespace SonarMiniMixer.Core;

/// <summary>
/// Controls Sonar's virtual Core Audio endpoints. Only endpoint IDs are cached:
/// each write opens and disposes a fresh MMDevice handle so GG/Sonar restarts
/// cannot leave long-lived COM registrations behind.
/// </summary>
public sealed class WindowsSonarAudioController : ISonarAudioController
{
    private static readonly IReadOnlyDictionary<string, (string[] Hints, DataFlow Flow)> Endpoints =
        new Dictionary<string, (string[], DataFlow)>(StringComparer.Ordinal)
        {
            ["game"] = (["SteelSeries Sonar - Gaming"], DataFlow.Render),
            ["chatRender"] = (["SteelSeries Sonar - Chat"], DataFlow.Render),
            ["media"] = (["SteelSeries Sonar - Media"], DataFlow.Render),
            ["aux"] = (["SteelSeries Sonar - Aux"], DataFlow.Render),
            ["chatCapture"] = (["SteelSeries Sonar - Microphone"], DataFlow.Render)
        };

    private static readonly string[] MasterChannels = ["game", "chatRender", "media", "aux"];

    private readonly Dictionary<string, string> _endpointIds = new(StringComparer.Ordinal);
    private readonly Lock _endpointIdsLock = new();

    /// <summary>Resolves all endpoint IDs with one enumeration per data flow.</summary>
    public void Prewarm()
    {
        lock (_endpointIdsLock)
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var flow in Endpoints.Values.Select(descriptor => descriptor.Flow).Distinct())
            {
                var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
                try
                {
                    foreach (var (channel, descriptor) in Endpoints)
                    {
                        if (descriptor.Flow != flow || _endpointIds.ContainsKey(channel)) continue;
                        var endpoint = devices.FirstOrDefault(device => descriptor.Hints.Any(hint =>
                            device.FriendlyName.Contains(hint, StringComparison.OrdinalIgnoreCase)));
                        if (endpoint is not null) _endpointIds[channel] = endpoint.ID;
                    }
                }
                finally
                {
                    foreach (var device in devices) device.Dispose();
                }
            }
        }
    }

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

    private void SetChannelVolume(string channel, float volume) =>
        WithEndpoint(channel, endpoint =>
        {
            endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0, 1);
        });

    private void SetMasterVolume(float requested)
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

    private float ReadVolume(string channel)
    {
        var value = 0f;
        WithEndpoint(channel, endpoint => value = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar);
        return value;
    }

    private void WithEndpoint(string channel, Action<MMDevice> action)
    {
        if (!Endpoints.TryGetValue(channel, out var descriptor))
            throw new SonarConnectionException($"Unknown Sonar audio channel '{channel}'.");

        try { OpenAndApply(ResolveId(channel, descriptor, allowCached: true), action); }
        catch (Exception exception) when (exception is not SonarConnectionException)
        {
            // Cached ID became stale after GG/Sonar or the device restarted. Rediscover
            // once, then use another short-lived handle.
            ForgetId(channel);
            OpenAndApply(ResolveId(channel, descriptor, allowCached: false), action);
        }
    }

    private static void OpenAndApply(string endpointId, Action<MMDevice> action)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDevice(endpointId);
        action(endpoint);
    }

    private string ResolveId(string channel, (string[] Hints, DataFlow Flow) descriptor, bool allowCached)
    {
        lock (_endpointIdsLock)
        {
            if (allowCached && _endpointIds.TryGetValue(channel, out var cached)) return cached;

            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(descriptor.Flow, DeviceState.Active);
            try
            {
                var endpoint = devices.FirstOrDefault(device => descriptor.Hints.Any(hint =>
                    device.FriendlyName.Contains(hint, StringComparison.OrdinalIgnoreCase)));
                if (endpoint is null)
                    throw new SonarConnectionException($"The Windows audio endpoint for Sonar {channel} is unavailable.");
                var endpointId = endpoint.ID;
                _endpointIds[channel] = endpointId;
                return endpointId;
            }
            finally
            {
                foreach (var device in devices) device.Dispose();
            }
        }
    }

    private void ForgetId(string channel)
    {
        lock (_endpointIdsLock) _endpointIds.Remove(channel);
    }
}
