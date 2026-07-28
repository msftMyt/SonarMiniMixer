using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SonarMiniMixer.Core;

/// <summary>Kinds of change Sonar pushes over its event socket.</summary>
public enum SonarEventKind
{
    /// <summary>Channel routing changed; re-read routing.</summary>
    RoutingChanged,
    /// <summary>ChatMix balance or availability changed.</summary>
    ChatMixChanged,
    /// <summary>Channel volume or mute state changed.</summary>
    VolumesChanged,
    /// <summary>Device inventory or labels changed.</summary>
    DevicesChanged,
}

public sealed record SonarEvent(
    SonarEventKind Kind,
    double Balance,
    string ChatMixState,
    /// <summary>Raw volumeSettings-compatible JSON from SONAR_EVENT_VOLUME_DATA.</summary>
    string? VolumePayload = null);

/// <summary>
/// Subscribes to Sonar's push socket so the mixer reflects external changes
/// immediately instead of waiting for a poll interval.
/// </summary>
public sealed class SonarEventStream : IAsyncDisposable
{
    private readonly ISonarEndpointProvider _endpoints;
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> _connect;
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public event Action<SonarEvent>? EventReceived;
    public event Action<bool>? ConnectionChanged;

    public SonarEventStream(
        ISonarEndpointProvider endpoints,
        Func<Uri, CancellationToken, Task<WebSocket>>? connect = null)
    {
        _endpoints = endpoints;
        _connect = connect ?? DefaultConnectAsync;
    }

    public void Start()
    {
        if (_pump is not null) return;
        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    private static async Task<WebSocket> DefaultConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            WebSocket? socket = null;
            try
            {
                var baseUri = await _endpoints.GetAsync(cancellationToken);
                var socketUri = new UriBuilder(new Uri(baseUri, "sock"))
                {
                    Scheme = baseUri.Scheme == "https" ? "wss" : "ws"
                }.Uri;

                socket = await _connect(socketUri, cancellationToken);
                ConnectionChanged?.Invoke(true);
                backoff = TimeSpan.FromSeconds(1);
                await ReceiveLoopAsync(socket, cancellationToken);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Sonar restarted or the endpoint moved; rediscover and retry.
                _endpoints.Invalidate();
            }
            finally
            {
                socket?.Dispose();
                ConnectionChanged?.Invoke(false);
            }

            if (cancellationToken.IsCancellationRequested) break;
            try { await Task.Delay(backoff, cancellationToken); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(15, backoff.TotalSeconds * 2));
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream(64 * 1024);
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (message.Length + result.Count > 1024 * 1024)
                    throw new SonarConnectionException("Sonar event message exceeded the 1 MB safety limit.");
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var payload = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            if (TryParse(payload, out var sonarEvent))
                EventReceived?.Invoke(sonarEvent);
        }
    }

    /// <summary>Maps a raw Sonar socket payload onto a mixer-relevant event.</summary>
    public static bool TryParse(string payload, out SonarEvent sonarEvent)
    {
        sonarEvent = default!;
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("event", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String) return false;
            var name = nameElement.GetString() ?? string.Empty;

            if (name.Contains("CHATMIX", StringComparison.OrdinalIgnoreCase))
            {
                var balance = 0d;
                var state = "unknown";
                if (document.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("balance", out var b) && b.ValueKind == JsonValueKind.Number)
                        balance = b.GetDouble();
                    if (data.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String)
                        state = s.GetString() ?? "unknown";
                }
                sonarEvent = new SonarEvent(SonarEventKind.ChatMixChanged, Math.Clamp(balance, -1, 1), state);
                return true;
            }

            if (name.Contains("VOLUME_DATA", StringComparison.OrdinalIgnoreCase) &&
                document.RootElement.TryGetProperty("data", out var volumeData) &&
                volumeData.ValueKind == JsonValueKind.Object)
            {
                sonarEvent = new SonarEvent(
                    SonarEventKind.VolumesChanged, 0, string.Empty, volumeData.GetRawText());
                return true;
            }

            var kind = name switch
            {
                var n when n.Contains("REDIRECTION", StringComparison.OrdinalIgnoreCase) => SonarEventKind.RoutingChanged,
                var n when n.Contains("FALLBACK", StringComparison.OrdinalIgnoreCase) => SonarEventKind.DevicesChanged,
                _ => (SonarEventKind?)null
            } ?? default;

            if (name.Contains("REDIRECTION", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("FALLBACK", StringComparison.OrdinalIgnoreCase))
            {
                sonarEvent = new SonarEvent(kind, 0, string.Empty);
                return true;
            }
            return false;
        }
        catch (JsonException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        try { if (_pump is not null) await _pump; } catch { /* shutdown */ }
        _cts.Dispose();
        _cts = null;
        _pump = null;
    }
}
