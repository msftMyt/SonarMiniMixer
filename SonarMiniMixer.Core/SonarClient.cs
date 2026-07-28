using System.Globalization;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace SonarMiniMixer.Core;

public sealed class SonarClient : ISonarClient, IDisposable
{
    // SteelSeries GG moved ChatMix under /v1/ in mid-2026 builds; older installs
    // still serve the unversioned path. Probe newest first, then fall back.
    private static readonly string[] ChatMixPaths = ["v1/chatMix", "chatMix"];
    private string? _chatMixPath;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly IReadOnlyDictionary<string, (string RedirectionId, string DataFlow)> DeviceRoutes =
        new Dictionary<string, (string RedirectionId, string DataFlow)>(StringComparer.OrdinalIgnoreCase)
        {
            ["game"] = ("game", "render"),
            ["chatRender"] = ("chat", "render"),
            ["media"] = ("media", "render"),
            ["aux"] = ("aux", "render"),
            ["chatCapture"] = ("mic", "capture"),
        };
    private static readonly HashSet<string> PresetChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        "game", "chatRender", "media", "aux", "chatCapture"
    };
    private readonly ISonarEndpointProvider _endpoints;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    // Initial catalogs load concurrently across five channels.
    private readonly ConcurrentDictionary<string, HashSet<Guid>> _presetIdsByChannel =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ISonarAudioController _audio;

    public SonarClient(ISonarEndpointProvider endpoints, HttpMessageHandler? handler = null, ISonarAudioController? audio = null)
    {
        _endpoints = endpoints;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _audio = audio ?? new WindowsSonarAudioController();
        _http.Timeout = TimeSpan.FromSeconds(4);
        if (_audio is WindowsSonarAudioController windowsAudio)
            _ = Task.Run(() => { try { windowsAudio.Prewarm(); } catch { /* resolve on demand */ } });
    }

    public Task<MixerState> GetStateAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async baseUri =>
        {
            var volumes = GetStringAsync(baseUri, "volumeSettings/classic", cancellationToken);
            // ChatMix path varies by GG version; a fully missing endpoint must not
            // fail the entire mixer read.
            var chatMix = GetChatMixAsync(baseUri, cancellationToken);
            var mode = GetStringAsync(baseUri, "mode/", cancellationToken);
            await Task.WhenAll((Task)volumes, chatMix, mode);
            return SonarStateParser.Parse(await volumes, await chatMix ?? "{}", await mode);
        }, cancellationToken);

    public Task<SonarPresetCatalog> GetPresetsAsync(string channel, CancellationToken cancellationToken = default)
    {
        ValidatePresetChannel(channel);
        var canonicalChannel = PresetChannels.Single(candidate => candidate.Equals(channel, StringComparison.OrdinalIgnoreCase));
        return ExecuteAsync(async baseUri =>
        {
            var presetsTask = GetJsonAsync<List<SonarPreset>>(baseUri, $"presets/{canonicalChannel}", cancellationToken);
            var selectedTask = GetJsonAsync<List<SelectedConfig>>(baseUri, "configs/selected", cancellationToken);
            await Task.WhenAll(presetsTask, selectedTask);
            var items = (await presetsTask)
                .OrderByDescending(preset => preset.IsFavorite)
                .ThenBy(preset => preset.IsFavorite ? preset.FavoritePosition : int.MaxValue)
                .ThenBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selectedId = (await selectedTask)
                .FirstOrDefault(config => config.VirtualAudioDevice.Equals(canonicalChannel, StringComparison.OrdinalIgnoreCase))?.Id;
            _presetIdsByChannel[canonicalChannel] = items.Select(item => item.Id).ToHashSet();
            return new SonarPresetCatalog(canonicalChannel, items, selectedId);
        }, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, Guid?>> GetSelectedPresetIdsAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async baseUri =>
        {
            var selected = await GetJsonAsync<List<SelectedConfig>>(baseUri, "configs/selected", cancellationToken);
            return (IReadOnlyDictionary<string, Guid?>)PresetChannels.ToDictionary(
                channel => channel,
                channel => (Guid?)selected.FirstOrDefault(config =>
                    config.VirtualAudioDevice.Equals(channel, StringComparison.OrdinalIgnoreCase))?.Id,
                StringComparer.OrdinalIgnoreCase);
        }, cancellationToken);

    public async Task SelectPresetAsync(string channel, Guid presetId, CancellationToken cancellationToken = default)
    {
        ValidatePresetChannel(channel);
        var canonicalChannel = PresetChannels.Single(candidate => candidate.Equals(channel, StringComparison.OrdinalIgnoreCase));
        if (!_presetIdsByChannel.TryGetValue(canonicalChannel, out var knownIds))
        {
            await GetPresetsAsync(canonicalChannel, cancellationToken);
            knownIds = _presetIdsByChannel[canonicalChannel];
        }
        if (!knownIds.Contains(presetId))
            throw new ArgumentException("The preset does not belong to the selected Sonar channel.", nameof(presetId));
        await PutAsync($"configs/{presetId:D}/select", cancellationToken);
    }

    public Task<SonarDeviceRouting> GetDeviceRoutingAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async baseUri =>
        {
            var outputsTask = GetJsonAsync<List<AudioDevice>>(baseUri, "audioDevices?deviceDataFlow=render&removeSteelSeriesVAD=true", cancellationToken);
            var microphonesTask = GetJsonAsync<List<AudioDevice>>(baseUri, "audioDevices?deviceDataFlow=capture&removeSteelSeriesVAD=true", cancellationToken);
            var redirectionsTask = GetJsonAsync<List<ClassicRedirection>>(baseUri, "classicRedirections", cancellationToken);
            await Task.WhenAll(outputsTask, microphonesTask, redirectionsTask);

            var outputs = NormalizeDevices(await outputsTask, "render");
            var microphones = NormalizeDevices(await microphonesTask, "capture");
            var redirections = await redirectionsTask;
            var channelDeviceIds = DeviceRoutes.ToDictionary(
                route => route.Key,
                route => redirections.FirstOrDefault(redirection =>
                    redirection.Id.Equals(route.Value.RedirectionId, StringComparison.OrdinalIgnoreCase))?.DeviceId,
                StringComparer.OrdinalIgnoreCase);
            // Sonar reports isRunning=false when a redirection exists but is not passing
            // audio (device removed, driver failure). Surface it rather than showing a
            // selection that looks healthy.
            var stalled = DeviceRoutes
                .Where(route => redirections.FirstOrDefault(redirection =>
                    redirection.Id.Equals(route.Value.RedirectionId, StringComparison.OrdinalIgnoreCase))
                    is { IsRunning: false })
                .Select(route => route.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new SonarDeviceRouting(
                outputs,
                microphones,
                channelDeviceIds,
                stalled);
        }, cancellationToken);

    private SonarDeviceRouting? _routingCache;
    private DateTimeOffset _routingCacheExpiry;

    public async Task SetChannelDeviceAsync(string channel, string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel) || !DeviceRoutes.TryGetValue(channel, out var route))
            throw new ArgumentException("Physical routing is available only for Game, Chat, Media, Aux, and Mic.", nameof(channel));
        // Validate against a short-lived snapshot so a multi-channel fan-out does not
        // re-download the whole device inventory once per channel.
        var routing = await GetCachedRoutingAsync(cancellationToken);
        var candidates = route.DataFlow == "capture" ? routing.MicrophoneDevices : routing.OutputDevices;
        var device = candidates.FirstOrDefault(candidate => candidate.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            // Stale snapshot: re-read once before rejecting a device the user can see.
            routing = await GetDeviceRoutingAsync(cancellationToken);
            CacheRouting(routing);
            candidates = route.DataFlow == "capture" ? routing.MicrophoneDevices : routing.OutputDevices;
            device = candidates.FirstOrDefault(candidate => candidate.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"The device is not an active physical {route.DataFlow} device.", nameof(deviceId));
        }
        await PutAsync($"classicRedirections/{route.RedirectionId}/deviceId/{Uri.EscapeDataString(device.Id)}", cancellationToken);
    }

    /// <summary>
    /// Routes several channels to one device in a single pass: the inventory is validated
    /// once and the writes are issued back-to-back instead of re-validating per channel.
    /// </summary>
    public async Task<IReadOnlyList<string>> SetChannelDevicesAsync(
        IEnumerable<string> channels, string deviceId, CancellationToken cancellationToken = default)
    {
        var requested = channels.ToArray();
        foreach (var channel in requested)
            if (string.IsNullOrWhiteSpace(channel) || !DeviceRoutes.ContainsKey(channel))
                throw new ArgumentException("Physical routing is available only for Game, Chat, Media, Aux, and Mic.", nameof(channels));

        var routing = await GetCachedRoutingAsync(cancellationToken);
        var failures = new List<string>();
        foreach (var channel in requested)
        {
            var route = DeviceRoutes[channel];
            var candidates = route.DataFlow == "capture" ? routing.MicrophoneDevices : routing.OutputDevices;
            var device = candidates.FirstOrDefault(candidate => candidate.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null) { failures.Add(channel); continue; }
            try
            {
                await PutAsync($"classicRedirections/{route.RedirectionId}/deviceId/{Uri.EscapeDataString(device.Id)}", cancellationToken);
            }
            catch { failures.Add(channel); }
        }
        InvalidateRoutingCache();
        return failures;
    }

    /// <summary>
    /// Drops the cached device inventory. Callers that change routing in bulk invalidate
    /// once at the end instead of after every write, which would otherwise force each
    /// write in a fan-out to re-download the full inventory.
    /// </summary>
    public void InvalidateRoutingCache() => _routingCacheExpiry = DateTimeOffset.MinValue;

    private async Task<SonarDeviceRouting> GetCachedRoutingAsync(CancellationToken cancellationToken)
    {
        if (_routingCache is { } cached && DateTimeOffset.UtcNow < _routingCacheExpiry) return cached;
        var routing = await GetDeviceRoutingAsync(cancellationToken);
        CacheRouting(routing);
        return routing;
    }

    private void CacheRouting(SonarDeviceRouting routing)
    {
        _routingCache = routing;
        _routingCacheExpiry = DateTimeOffset.UtcNow.AddSeconds(2);
    }

    public Task SetVolumeAsync(string channel, double volume, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(volume));
        ValidateChannel(channel);
        return SetChannelVolumeAsync(channel, volume, cancellationToken);
    }

    private async Task SetChannelVolumeAsync(string channel, double volume, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);
        if (!state.CanControl || !state.Channels.Any(candidate => candidate.Id == channel))
            throw new SonarConnectionException($"Sonar channel '{channel}' is not controllable in the current mode.");

        // The two levels need different transports:
        //  - channels: Core Audio, the only path that raises SONAR_EVENT_VOLUME_DATA
        //    so GG's mixer redraws (the HTTP route changes state silently).
        //  - master: the HTTP master route, because a Core Audio write on the master
        //    endpoint rescales the channels without moving Sonar's master value.
        if (channel.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            await PutAsync(
                $"volumeSettings/classic/master/Volume/{volume.ToString("0.######", CultureInfo.InvariantCulture)}",
                cancellationToken);
            // The HTTP route stores the value but broadcasts nothing, so nudge the
            // master endpoint through Core Audio to make Sonar publish the update
            // and GG redraw. The nudge cannot change the stored master value.
            try { await _audio.SetVolumeAsync("master", volume, cancellationToken); }
            catch { /* notification only; the value is already committed */ }
        }
        else
            await _audio.SetVolumeAsync(channel, volume, cancellationToken);
    }

    public Task SetMuteAsync(string channel, bool muted, CancellationToken cancellationToken = default)
    {
        ValidateChannel(channel);
        return PutChannelAsync(channel, $"Mute/{muted.ToString().ToLowerInvariant()}", cancellationToken);
    }

    public Task SetChatMixAsync(double balance, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(balance) || balance is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(balance));
        return PutChatMixAsync(balance, cancellationToken);
    }

    private Task PutAsync(string relativeUri, CancellationToken cancellationToken) =>
        SerializeWriteAsync(() => ExecuteAsync(async baseUri =>
        {
            using var response = await _http.PutAsync(new Uri(baseUri, relativeUri), new ByteArrayContent([]), cancellationToken);
            response.EnsureSuccessStatusCode();
            return true;
        }, cancellationToken), cancellationToken);

    private async Task SerializeWriteAsync(Func<Task<bool>> write, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try { await write(); }
        finally { _writeGate.Release(); }
    }

    private async Task PutChannelAsync(string channel, string operation, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);
        if (!state.CanControl || !state.Channels.Any(candidate => candidate.Id == channel))
            throw new SonarConnectionException($"Sonar channel '{channel}' is not controllable in the current mode.");
        // Mute goes through Core Audio for the same reason as volume: it is the only
        // path that makes Sonar broadcast the change so GG's mixer stays in sync.
        await _audio.SetMuteAsync(channel, bool.Parse(operation[5..]), cancellationToken);
    }

    private async Task PutChatMixAsync(double balance, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);
        if (!state.CanControlChatMix)
            throw new SonarConnectionException("Sonar ChatMix is not controllable in the current device configuration.");
        // Use whichever ChatMix path this GG build actually serves (resolved during the read above).
        var chatMixPath = _chatMixPath ?? ChatMixPaths[^1];
        await PutAsync($"{chatMixPath}?balance={balance.ToString("0.####", CultureInfo.InvariantCulture)}", cancellationToken);
        // Current GG builds broadcast EVENT_SONAR_CHATMIX_DATA from the write itself, so
        // GG's own mixer follows without the old process-memory action nudges.
    }

    private async Task<string> GetStringAsync(Uri baseUri, string relativeUri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(baseUri, relativeUri), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<string?> GetOptionalStringAsync(Uri baseUri, string relativeUri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(baseUri, relativeUri), cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(payload) ? null : payload;
    }

    private async Task<string?> GetChatMixAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        if (_chatMixPath is { } known)
        {
            var knownPayload = await GetOptionalStringAsync(baseUri, known, cancellationToken);
            if (knownPayload is not null) return knownPayload;
            // GG changed versions or endpoint shape after a restart; rediscover rather
            // than pinning ChatMix unavailable until this process restarts.
            _chatMixPath = null;
        }

        foreach (var candidate in ChatMixPaths)
        {
            var payload = await GetOptionalStringAsync(baseUri, candidate, cancellationToken);
            if (payload is null) continue;
            _chatMixPath = candidate;
            return payload;
        }
        return null;
    }

    private async Task<T> GetJsonAsync<T>(Uri baseUri, string relativeUri, CancellationToken cancellationToken)
    {
        var json = await GetStringAsync(baseUri, relativeUri, cancellationToken);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new JsonException($"Sonar returned an empty response for '{relativeUri}'.");
    }

    private async Task<T> ExecuteAsync<T>(Func<Uri, Task<T>> operation, CancellationToken cancellationToken)
    {
        Exception? firstFailure = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try { return await operation(await _endpoints.GetAsync(cancellationToken)); }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested &&
                (exception is HttpRequestException { StatusCode: null } || exception is TaskCanceledException))
            {
                firstFailure ??= exception;
                _endpoints.Invalidate();
            }
        }
        throw new SonarConnectionException("Could not connect to SteelSeries Sonar. Make sure GG and Sonar are running.", firstFailure);
    }

    private static void ValidateChannel(string channel)
    {
        if (!SonarStateParser.IsSafeChannel(channel)) throw new ArgumentException("Unsafe Sonar channel id.", nameof(channel));
    }

    private static void ValidatePresetChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || !PresetChannels.Contains(channel))
            throw new ArgumentException("Sonar presets are available only for Game, Chat, Media, Aux, and Mic.", nameof(channel));
    }

    private static IReadOnlyList<SonarAudioDevice> NormalizeDevices(IEnumerable<AudioDevice> devices, string dataFlow) =>
        devices
            .Where(device =>
                !device.IsVad &&
                !string.IsNullOrWhiteSpace(device.Id) &&
                !string.IsNullOrWhiteSpace(device.FriendlyName) &&
                device.DataFlow.Equals(dataFlow, StringComparison.OrdinalIgnoreCase))
            .Select(device => new SonarAudioDevice(device.Id, device.FriendlyName, device.DataFlow))
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private sealed record SelectedConfig(Guid Id, string Name, string VirtualAudioDevice);
    private sealed record AudioDevice(string FriendlyName, string Id, string DataFlow, bool IsVad);
    private sealed record ClassicRedirection(string Id, string? DeviceId, bool IsRunning);

    public void Dispose()
    {
        _http.Dispose();
        _writeGate.Dispose();
    }
}
