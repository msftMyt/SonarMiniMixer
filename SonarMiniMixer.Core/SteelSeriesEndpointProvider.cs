using System.Text.Json;

namespace SonarMiniMixer.Core;

public sealed class SteelSeriesEndpointProvider : ISonarEndpointProvider, IDisposable
{
    private const int SonarAddressAttempts = 21;
    private static readonly TimeSpan SonarAddressRetryDelay = TimeSpan.FromMilliseconds(250);
    public static string DefaultCorePropsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteelSeries", "SteelSeries Engine 3", "coreProps.json");

    private readonly string _corePropsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http;
    private Uri? _cached;

    public SteelSeriesEndpointProvider(string? corePropsPath = null, HttpMessageHandler? handler = null)
    {
        _corePropsPath = corePropsPath ?? DefaultCorePropsPath;
        if (handler is null)
        {
            var secureHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                    request.RequestUri is not null && EndpointSecurity.IsLoopback(request.RequestUri)
            };
            _http = new HttpClient(secureHandler, true);
        }
        else _http = new HttpClient(handler, false);
        _http.Timeout = TimeSpan.FromSeconds(4);
    }

    public async Task<Uri> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null) return _cached;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null) return _cached;
            if (!File.Exists(_corePropsPath)) throw new SonarConnectionException("SteelSeries GG configuration was not found.");

            using var coreDocument = JsonDocument.Parse(await File.ReadAllTextAsync(_corePropsPath, cancellationToken));
            var ggAddress = coreDocument.RootElement.GetProperty("ggEncryptedAddress").GetString();
            var ggBaseUri = EndpointSecurity.CreateLoopbackBaseUri(ggAddress, "https");

            // GG creates the Sonar process before its web server address is ready. During
            // that short window subApps reports isRunning=true with empty addresses; retry
            // instead of turning a normal restart into a hard connection failure.
            for (var attempt = 0; attempt < SonarAddressAttempts; attempt++)
            {
                using var response = await _http.GetAsync(new Uri(ggBaseUri, "subApps"), cancellationToken);
                response.EnsureSuccessStatusCode();
                using var subApps = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var metadata = subApps.RootElement.GetProperty("subApps").GetProperty("sonar").GetProperty("metadata");
                var plain = metadata.TryGetProperty("webServerAddress", out var plainElement) ? plainElement.GetString() : null;
                var encrypted = metadata.TryGetProperty("encryptedWebServerAddress", out var encryptedElement) ? encryptedElement.GetString() : null;
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    _cached = EndpointSecurity.CreateLoopbackBaseUri(plain, "http");
                    return _cached;
                }
                if (!string.IsNullOrWhiteSpace(encrypted))
                {
                    _cached = EndpointSecurity.CreateLoopbackBaseUri(encrypted, "https");
                    return _cached;
                }
                if (attempt < SonarAddressAttempts - 1) await Task.Delay(SonarAddressRetryDelay, cancellationToken);
            }
            throw new SonarConnectionException("SteelSeries Sonar is still starting.");
        }
        catch (SonarConnectionException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or HttpRequestException)
        {
            throw new SonarConnectionException("SteelSeries GG returned invalid Sonar connection information.", exception);
        }
        finally { _gate.Release(); }
    }

    public void Invalidate() => Volatile.Write(ref _cached, null);
    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
