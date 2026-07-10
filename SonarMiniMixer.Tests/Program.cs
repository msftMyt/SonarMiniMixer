using System.Net;
using System.Text;
using SonarMiniMixer.Core;

const string VolumesJson = """
{"masters":{"classic":{"volume":1.0,"muted":false}},"devices":{"game":{"classic":{"volume":0.42,"muted":false}},"chatRender":{"classic":{"volume":0.8,"muted":false}},"chatCapture":{"classic":{"volume":0.65,"muted":false}},"media":{"classic":{"volume":0.5,"muted":true}},"aux":{"classic":{"volume":0.25,"muted":false}}}}
""";

var tests = new (string Name, Func<Task> Run)[]
{
    ("loopback URI accepts local HTTP endpoints", () => Sync(() =>
    {
        Equal("http://127.0.0.1:64707/", EndpointSecurity.CreateLoopbackBaseUri("127.0.0.1:64707", "http").ToString());
        Equal("https://[::1]:6327/", EndpointSecurity.CreateLoopbackBaseUri("https://[::1]:6327", "https").ToString());
    })),
    ("loopback URI rejects remote and credentialed endpoints", () => Sync(() =>
    {
        Throws<InvalidDataException>(() => EndpointSecurity.CreateLoopbackBaseUri("example.com:443", "https"));
        Throws<InvalidDataException>(() => EndpointSecurity.CreateLoopbackBaseUri("http://user:pass@127.0.0.1:10", "http"));
        Throws<InvalidDataException>(() => EndpointSecurity.CreateLoopbackBaseUri("file:///c:/temp/x", "http"));
    })),
    ("state parser maps every classic channel", () => Sync(() =>
    {
        var state = SonarStateParser.Parse(VolumesJson, "{\"balance\":-0.25}", "\"classic\"");
        Equal("classic", state.Mode);
        Equal(6, state.Channels.Count);
        Equal("Mic", state.Channels.Single(x => x.Id == "chatCapture").Name);
        Equal(0.42, state.Channels.Single(x => x.Id == "game").Volume);
        Equal(true, state.Channels.Single(x => x.Id == "media").Muted);
        Equal(-0.25, state.ChatMix);
        Equal(true, state.CanControl);
    })),
    ("state parser marks non-classic mode read-only", () => Sync(() =>
    {
        var state = SonarStateParser.Parse(VolumesJson, "{\"balance\":0}", "\"stream\"");
        Equal(false, state.CanControl);
    })),
    ("client reads state and emits bounded API writes", async () =>
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (request.Method == HttpMethod.Get && path == "/volumeSettings/classic") return Json(VolumesJson);
            if (request.Method == HttpMethod.Get && path == "/chatMix") return Json("{\"balance\":0.1}");
            if (request.Method == HttpMethod.Get && path == "/mode/") return Json("\"classic\"");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler);
        var state = await client.GetStateAsync();
        Equal(6, state.Channels.Count);
        await client.SetVolumeAsync("game", 0.75);
        await client.SetMuteAsync("chatRender", true);
        await client.SetChatMixAsync(-0.4);
        Contains(handler.Requests, "PUT /volumeSettings/classic/game/Volume/0.75");
        Contains(handler.Requests, "PUT /volumeSettings/classic/chatRender/Mute/true");
        Contains(handler.Requests, "PUT /chatMix?balance=-0.4");
    }),
    ("client rejects unsafe channels and out-of-range values", async () =>
    {
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), new RecordingHandler(_ => Json("{}")));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetVolumeAsync("game", 1.1));
        await ThrowsAsync<ArgumentException>(() => client.SetVolumeAsync("../game", 0.5));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetChatMixAsync(-1.1));
    }),
    ("client invalidates and retries endpoint after transport failure", async () =>
    {
        var endpoints = new RotatingEndpointProvider();
        var first = true;
        var handler = new RecordingHandler(request =>
        {
            if (first) { first = false; throw new HttpRequestException("stale port"); }
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/volumeSettings/classic" => Json(VolumesJson),
                "/chatMix" => Json("{\"balance\":0}"),
                "/mode/" => Json("\"classic\""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var client = new SonarClient(endpoints, handler);
        var state = await client.GetStateAsync();
        Equal(6, state.Channels.Count);
        Equal(1, endpoints.Invalidations);
        Equal(true, handler.Authorities.Contains("127.0.0.1:64708"));
    }),
    ("settings store recovers from corrupt JSON and writes atomically", async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "SonarMiniMixerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(path, "not-json");
        var store = new SettingsStore(path);
        Equal(AppSettings.Default, await store.LoadAsync());
        var expected = new AppSettings(true, true, 815, 380, 44, 55);
        await store.SaveAsync(expected);
        Equal(expected, await store.LoadAsync());
        Equal(false, File.Exists(path + ".tmp"));
        Directory.Delete(root, true);
    })
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
}
Console.WriteLine($"RESULT {tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static Task Sync(Action action) { action(); return Task.CompletedTask; }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}"); }
static void Contains(IEnumerable<string> values, string expected) { if (!values.Contains(expected)) throw new Exception($"missing '{expected}' in [{string.Join(", ", values)}]"); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"expected {typeof(T).Name}"); }
static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception($"expected {typeof(T).Name}"); }
static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

sealed class FixedEndpointProvider(string endpoint) : ISonarEndpointProvider
{
    public Task<Uri> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Uri(endpoint));
    public void Invalidate() { }
}

sealed class RotatingEndpointProvider : ISonarEndpointProvider
{
    private int _generation;
    public int Invalidations { get; private set; }
    public Task<Uri> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Uri($"http://127.0.0.1:{64707 + _generation}/"));
    public void Invalidate() { Invalidations++; _generation++; }
}

sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    public List<string> Authorities { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
        Authorities.Add(request.RequestUri.Authority);
        return Task.FromResult(responder(request));
    }
}
