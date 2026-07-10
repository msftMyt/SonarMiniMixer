using System.Globalization;

namespace SonarMiniMixer.Core;

public sealed class SonarClient : ISonarClient, IDisposable
{
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

    public void Dispose() { _http.Dispose(); _writeGate.Dispose(); }
}
