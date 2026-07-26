using System.Globalization;
using System.Text.Json;

namespace SonarMiniMixer.Core;

public sealed class SonarClient : ISonarClient, IDisposable
{
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
    private readonly ISonarAudioController _audio;
    private readonly SteelSeriesGgActionClient _ggActions = new();

    public SonarClient(ISonarEndpointProvider endpoints, HttpMessageHandler? handler = null, ISonarAudioController? audio = null)
    {
        _endpoints = endpoints;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _audio = audio ?? new WindowsSonarAudioController();
        _http.Timeout = TimeSpan.FromSeconds(4);
    }

    public Task<MixerState> GetStateAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async baseUri =>
        {
            var volumes = GetStringAsync(baseUri, "volumeSettings/classic", cancellationToken);
            var chatMix = GetStringAsync(baseUri, "chatMix", cancellationToken);
            var mode = GetStringAsync(baseUri, "mode/", cancellationToken);
            await Task.WhenAll(volumes, chatMix, mode);
            return SonarStateParser.Parse(await volumes, await chatMix, await mode);
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
            return new SonarPresetCatalog(canonicalChannel, items, selectedId);
        }, cancellationToken);
    }

    public async Task SelectPresetAsync(string channel, Guid presetId, CancellationToken cancellationToken = default)
    {
        ValidatePresetChannel(channel);
        var catalog = await GetPresetsAsync(channel, cancellationToken);
        if (!catalog.Items.Any(preset => preset.Id == presetId))
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
            return new SonarDeviceRouting(
                outputs,
                microphones,
                channelDeviceIds);
        }, cancellationToken);

    public async Task SetChannelDeviceAsync(string channel, string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel) || !DeviceRoutes.TryGetValue(channel, out var route))
            throw new ArgumentException("Physical routing is available only for Game, Chat, Media, Aux, and Mic.", nameof(channel));
        var routing = await GetDeviceRoutingAsync(cancellationToken);
        var candidates = route.DataFlow == "capture" ? routing.MicrophoneDevices : routing.OutputDevices;
        var device = candidates.FirstOrDefault(candidate => candidate.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
        if (device is null)
            throw new ArgumentException($"The device is not an active physical {route.DataFlow} device.", nameof(deviceId));
        await PutAsync($"classicRedirections/{route.RedirectionId}/deviceId/{Uri.EscapeDataString(device.Id)}", cancellationToken);
    }

    public Task SetVolumeAsync(string channel, double volume, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(volume));
        ValidateChannel(channel);
        return PutChannelAsync(channel, $"Volume/{volume.ToString("0.####", CultureInfo.InvariantCulture)}", cancellationToken);
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
        if (operation.StartsWith("Volume/", StringComparison.Ordinal))
            await _audio.SetVolumeAsync(channel, double.Parse(operation[7..], CultureInfo.InvariantCulture), cancellationToken);
        else
            await _audio.SetMuteAsync(channel, bool.Parse(operation[5..]), cancellationToken);
    }

    private async Task PutChatMixAsync(double balance, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken);
        if (!state.CanControlChatMix)
            throw new SonarConnectionException("Sonar ChatMix is not controllable in the current device configuration.");
        var towardChatFirst = balance < 0.95;
        await PutAsync($"chatMix?balance={balance.ToString("0.####", CultureInfo.InvariantCulture)}", cancellationToken);
        await _ggActions.AdjustChatMixAsync(towardChatFirst, cancellationToken);
        await _ggActions.AdjustChatMixAsync(!towardChatFirst, cancellationToken);
    }

    private async Task<string> GetStringAsync(Uri baseUri, string relativeUri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(baseUri, relativeUri), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
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

    public void Dispose() { _http.Dispose(); _writeGate.Dispose(); }
}
