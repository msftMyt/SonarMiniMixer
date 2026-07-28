using System.Globalization;
﻿using System.Net;
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
    ("endpoint discovery waits for Sonar startup and accepts encrypted metadata", async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "SonarEndpointTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var props = Path.Combine(root, "coreProps.json");
        await File.WriteAllTextAsync(props, "{\"ggEncryptedAddress\":\"127.0.0.1:6327\"}");
        var calls = 0;
        var handler = new RecordingHandler(_ =>
        {
            calls++;
            return Json(calls < 3
                ? "{\"subApps\":{\"sonar\":{\"metadata\":{\"webServerAddress\":\"\",\"encryptedWebServerAddress\":\"\"}}}}"
                : "{\"subApps\":{\"sonar\":{\"metadata\":{\"webServerAddress\":\"\",\"encryptedWebServerAddress\":\"127.0.0.1:65000\"}}}}");
        });
        using var provider = new SteelSeriesEndpointProvider(props, handler);

        Equal("https://127.0.0.1:65000/", (await provider.GetAsync()).ToString());
        Equal(3, calls);
        Directory.Delete(root, true);
    }),

    ("state parser maps every classic channel", () => Sync(() =>
    {
        var state = SonarStateParser.Parse(VolumesJson, "{\"balance\":-0.25,\"state\":\"enabled\"}", "\"classic\"");
        Equal("classic", state.Mode);
        Equal(6, state.Channels.Count);
        Equal("Mic", state.Channels.Single(x => x.Id == "chatCapture").Name);
        Equal(0.42, state.Channels.Single(x => x.Id == "game").Volume);
        Equal(true, state.Channels.Single(x => x.Id == "media").Muted);
        Equal(-0.25, state.ChatMix);
        Equal(true, state.CanControl);
        Equal(true, state.CanControlChatMix);
    })),
    ("state parser fails closed on null mode and disabled ChatMix", () => Sync(() =>
    {
        var state = SonarStateParser.Parse(VolumesJson, "{\"balance\":0.4,\"state\":\"differentDeviceSelected\"}", "null");
        Equal("unknown", state.Mode);
        Equal(false, state.CanControl);
        Equal(false, state.CanControlChatMix);
    })),
    ("client writes channel changes where Sonar broadcasts them", async () =>
    {
        // Core Audio writes make Sonar raise SONAR_EVENT_VOLUME_DATA, which is what
        // keeps GG's own mixer in sync. The volumeSettings HTTP route changes state
        // silently (and aliases playback channels onto the master), so it is not used.
        var handler = HealthyHandler();
        var audio = new RecordingAudioController();
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, audio);
        var state = await client.GetStateAsync();
        Equal(6, state.Channels.Count);
        await client.SetVolumeAsync("game", 0.75);
        await client.SetMuteAsync("chatRender", true);
        await client.SetVolumeAsync("master", 0.8);
        Contains(audio.Writes, "VOLUME game 0.75");
        Contains(audio.Writes, "MUTE chatRender true");
        Contains(audio.Writes, "VOLUME master 0.8");
        Equal(true, handler.Requests.Contains("PUT /volumeSettings/classic/master/Volume/0.8"));
        Equal(false, handler.Requests.Any(x => x.StartsWith("PUT /volumeSettings/classic/game", StringComparison.Ordinal)));
    }),
    ("client refuses absent channels and non-classic mode", async () =>
    {
        var mode = "classic";
        var sparseVolumes = "{\"masters\":{\"classic\":{\"volume\":1,\"muted\":false}},\"devices\":{}}";
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/volumeSettings/classic" => Json(sparseVolumes),
            "/chatMix" => Json("{\"balance\":0,\"state\":\"enabled\"}"),
            "/mode/" => Json(System.Text.Json.JsonSerializer.Serialize(mode)),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent)
        });
        var audio = new RecordingAudioController();
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, audio);
        await ThrowsAsync<SonarConnectionException>(() => client.SetVolumeAsync("game", 0.5));
        mode = "stream";
        await ThrowsAsync<SonarConnectionException>(() => client.SetVolumeAsync("master", 0.5));
        Equal(0, audio.Writes.Count);
    }),
    ("routing reports channels Sonar has stopped running", async () =>
    {
        // Sonar marks a redirection isRunning=false when its device is gone or the
        // route failed. GG surfaces that; the mixer must expose it too instead of
        // showing a healthy-looking selection that is not actually passing audio.
        var outputId = "{0.0.0.00000000}.{aaaa}";
        var micId = "{0.0.1.00000000}.{bbbb}";
        var handler = new RecordingHandler(request => request.RequestUri!.PathAndQuery switch
        {
            var p when p.StartsWith("/audioDevices?deviceDataFlow=render") =>
                Json($$"""[{"id":"{{outputId}}","name":"Speakers","dataFlow":"render"}]"""),
            var p when p.StartsWith("/audioDevices?deviceDataFlow=capture") =>
                Json($$"""[{"id":"{{micId}}","name":"Mic","dataFlow":"capture"}]"""),
            "/classicRedirections" => Json($$"""
                [{"id":"game","deviceId":"{{outputId}}","isRunning":true},
                 {"id":"chat","deviceId":"{{outputId}}","isRunning":false},
                 {"id":"media","deviceId":"{{outputId}}","isRunning":true},
                 {"id":"aux","deviceId":"{{outputId}}","isRunning":true},
                 {"id":"mic","deviceId":"{{micId}}","isRunning":false}]
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent)
        });
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, new RecordingAudioController());
        var routing = await client.GetDeviceRoutingAsync();

        Equal(true, routing.StalledChannels.Contains("chatRender"));
        Equal(true, routing.StalledChannels.Contains("chatCapture"));
        Equal(false, routing.StalledChannels.Contains("game"));
        Equal(2, routing.StalledChannels.Count);
    }),

    ("mixer mirrors Sonar values exactly", async () =>
    {
        // The mixer is a remote control for GG: what Sonar stores is what we show,
        // and what the user drags is written back unmodified. No scaling either way.
        var master = 0.5;
        var gameVolume = 0.4;
        var writes = new List<string>();
        string Volumes() =>
            $"{{\"masters\":{{\"classic\":{{\"volume\":{master.ToString(CultureInfo.InvariantCulture)},\"muted\":false}}}}," +
            $"\"devices\":{{\"game\":{{\"classic\":{{\"volume\":{gameVolume.ToString(CultureInfo.InvariantCulture)},\"muted\":false}}}}}}}}";

        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put) { writes.Add(path); return Json(Volumes()); }
            return path switch
            {
                "/volumeSettings/classic" => Json(Volumes()),
                "/mode/" => Json("\"classic\""),
                "/v1/chatMix" => Json("{\"balance\":0,\"state\":\"enabled\"}"),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent)
            };
        });
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, new RecordingAudioController());

        // Displayed values are Sonar's values, untouched.
        var state = await client.GetStateAsync();
        Equal(0.4, Math.Round(state.Channels.First(c => c.Id == "game").Volume, 4));
        Equal(0.5, Math.Round(state.Channels.First(c => c.Id == "master").Volume, 4));

        // Values go out verbatim: no scaling applied on the way to Sonar.
        var audio = new RecordingAudioController();
        using var writer = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, audio);
        await writer.SetVolumeAsync("game", 0.6);
        Contains(audio.Writes, "VOLUME game 0.6");
        // Master commits over HTTP, then nudges Core Audio so Sonar broadcasts it.
        await writer.SetVolumeAsync("master", 0.75);
        Contains(audio.Writes, "VOLUME master 0.75");

        // Reading a value and writing it straight back must not shift it.
        var echoed = (await writer.GetStateAsync()).Channels.First(c => c.Id == "game").Volume;
        await writer.SetVolumeAsync("game", echoed);
        Contains(audio.Writes, "VOLUME game 0.4");
    }),

    ("event stream maps Sonar socket payloads", () =>
    {
        // Captured from a live SteelSeries GG session.
        Equal(true, SonarEventStream.TryParse(
            "{\"event\":\"EVENT_SONAR_CHATMIX_DATA\",\"data\":{\"balance\":-0.33,\"state\":\"enabled\"}}", out var chatMix));
        Equal(SonarEventKind.ChatMixChanged, chatMix.Kind);
        Equal(-0.33, Math.Round(chatMix.Balance, 4));
        Equal("enabled", chatMix.ChatMixState);

        // Mid-fan-out Sonar reports ChatMix as unavailable until every playback
        // channel shares one output device.
        Equal(true, SonarEventStream.TryParse(
            "{\"event\":\"EVENT_SONAR_CHATMIX_DATA\",\"data\":{\"balance\":0.0,\"state\":\"differentDeviceSelected\"}}", out var mixed));
        Equal("differentDeviceSelected", mixed.ChatMixState);

        Equal(true, SonarEventStream.TryParse(
            "{\"event\":\"SONAR_EVENT_REDIRECTION_STATUS_UPDATE\",\"data\":null}", out var routing));
        Equal(SonarEventKind.RoutingChanged, routing.Kind);

        // Physical-device volume events do not describe the six Sonar channel faders.
        Equal(false, SonarEventStream.TryParse(
            "{\"event\":\"SONAR_EVENT_DEVICE_VOLUMES_UPDATE\",\"data\":[]}", out _));

        // Captured from current GG when a channel volume changes through Core Audio.
        Equal(true, SonarEventStream.TryParse(
            "{\"event\":\"SONAR_EVENT_VOLUME_DATA\",\"data\":{\"masters\":{}}}", out var channelVolumes));
        Equal(SonarEventKind.VolumesChanged, channelVolumes.Kind);
        Equal("{\"masters\":{}}", channelVolumes.VolumePayload);

        Equal(true, SonarEventStream.TryParse(
            "{\"event\":\"SONAR_EVENT_FALLBACK_UPDATED\",\"data\":{}}", out var devices));
        Equal(SonarEventKind.DevicesChanged, devices.Kind);

        // Irrelevant and malformed payloads are ignored rather than throwing.
        Equal(false, SonarEventStream.TryParse("{\"event\":\"SONAR_EVENT_FEATURE_UPDATED\",\"data\":{}}", out _));
        Equal(false, SonarEventStream.TryParse("not json", out _));
        Equal(false, SonarEventStream.TryParse("", out _));
        return Task.CompletedTask;
    }),

    ("client rediscovers ChatMix path after GG endpoint changes", async () =>
    {
        var versioned = true;
        var volumes = "{\"masters\":{\"classic\":{\"volume\":1.0,\"muted\":false}},\"devices\":{}}";
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/volumeSettings/classic" => Json(volumes),
            "/mode/" => Json("\"classic\""),
            "/v1/chatMix" => versioned
                ? Json("{\"balance\":0.2,\"state\":\"enabled\"}")
                : new HttpResponseMessage(HttpStatusCode.NotFound),
            "/chatMix" => versioned
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json("{\"balance\":-0.4,\"state\":\"enabled\"}"),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent)
        });
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, new RecordingAudioController());

        Equal(0.2, Math.Round((await client.GetStateAsync()).ChatMix, 4));
        versioned = false;
        Equal(-0.4, Math.Round((await client.GetStateAsync()).ChatMix, 4));
        Equal(true, handler.Requests.Contains("GET /chatMix"));
    }),

    ("client reads and writes ChatMix through the versioned endpoint", async () =>
    {
        // Newer SteelSeries GG moved ChatMix to /v1/chatMix; older builds serve /chatMix.
        var volumes = "{\"masters\":{\"classic\":{\"volume\":1.0,\"muted\":false}}," +
                      "\"devices\":{\"game\":{\"classic\":{\"volume\":0.5,\"muted\":false}}}}";
        var writes = new List<string>();
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put)
            {
                writes.Add(path + request.RequestUri.Query);
                return path == "/v1/chatMix"
                    ? Json("{\"balance\":0.2,\"state\":\"enabled\"}")
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return path switch
            {
                "/volumeSettings/classic" => Json(volumes),
                "/mode/" => Json("\"classic\""),
                "/chatMix" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/v1/chatMix" => Json("{\"balance\":-0.1,\"state\":\"enabled\"}"),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent)
            };
        });
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, new RecordingAudioController());

        var state = await client.GetStateAsync();
        Equal(true, state.CanControlChatMix);
        Equal(-0.1, Math.Round(state.ChatMix, 4));

        await client.SetChatMixAsync(0.2);
        Equal(true, writes.Any(w => w.StartsWith("/v1/chatMix?", StringComparison.Ordinal)));
    }),

    ("client stays connected when Sonar no longer exposes ChatMix", async () =>
    {
        // Newer SteelSeries GG builds removed /chatMix from the Sonar sub-app.
        // The mixer must keep working with ChatMix simply unavailable.
        var volumes = "{\"masters\":{\"classic\":{\"volume\":1.0,\"muted\":false}}," +
                      "\"devices\":{\"game\":{\"classic\":{\"volume\":0.5,\"muted\":false}}}}";
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/volumeSettings/classic" => Json(volumes),
            "/mode/" => Json("\"classic\""),
            "/chatMix" => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent)
        });
        var audio = new RecordingAudioController();
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, audio);

        var state = await client.GetStateAsync();
        Equal("classic", state.Mode);
        Equal(false, state.CanControlChatMix);
        Equal(true, state.Channels.Count >= 2);

        // Volume still writes even though ChatMix is gone.
        await client.SetVolumeAsync("game", 0.4);
        Equal(1, audio.Writes.Count);

        // Attempting ChatMix fails cleanly rather than breaking the connection.
        await ThrowsAsync<SonarConnectionException>(() => client.SetChatMixAsync(0.2));
    }),

    ("client refuses ChatMix outside Classic or when disabled", async () =>
    {
        var mode = "stream";
        var chatMix = "{\"balance\":0,\"state\":\"enabled\"}";
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/volumeSettings/classic" => Json(VolumesJson),
            "/chatMix" => Json(chatMix),
            "/mode/" => Json(System.Text.Json.JsonSerializer.Serialize(mode)),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent)
        });
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, new RecordingAudioController());
        await ThrowsAsync<SonarConnectionException>(() => client.SetChatMixAsync(0.5));
        mode = "classic";
        chatMix = "{\"balance\":0,\"state\":\"differentDeviceSelected\"}";
        await ThrowsAsync<SonarConnectionException>(() => client.SetChatMixAsync(0.5));
        Equal(false, handler.Requests.Any(x => x.StartsWith("PUT", StringComparison.Ordinal)));
    }),
    ("client rejects unsafe and non-finite values", async () =>
    {
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), new RecordingHandler(_ => Json("{}")), new RecordingAudioController());
        await ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetVolumeAsync("game", 1.1));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetVolumeAsync("game", double.NaN));
        await ThrowsAsync<ArgumentException>(() => client.SetVolumeAsync("../game", 0.5));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => client.SetChatMixAsync(double.PositiveInfinity));
    }),
    ("client does not retry HTTP status failures", async () =>
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, new RecordingAudioController());
        await ThrowsAsync<HttpRequestException>(() => client.GetStateAsync());
        Equal(3, handler.Requests.Count);
    }),
    ("client invalidates and retries endpoint after transport failure", async () =>
    {
        var endpoints = new RotatingEndpointProvider();
        var first = true;
        var handler = new RecordingHandler(request =>
        {
            if (first) { first = false; throw new HttpRequestException("stale port"); }
            return request.RequestUri!.AbsolutePath switch
            {
                "/volumeSettings/classic" => Json(VolumesJson),
                "/chatMix" => Json("{\"balance\":0,\"state\":\"enabled\"}"),
                "/mode/" => Json("\"classic\""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        using var client = new SonarClient(endpoints, handler, new RecordingAudioController());
        Equal(6, (await client.GetStateAsync()).Channels.Count);
        Equal(1, endpoints.Invalidations);
        Equal(true, handler.Authorities.Contains("127.0.0.1:64708"));
    }),
    ("client loads and orders presets with the selected preset", async () =>
    {
        var selectedId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/presets/game" => Json($$"""
                [
                  {"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","name":"Balanced","isFavorite":false,"favoritePosition":0},
                  {"id":"{{selectedId}}","name":"FPS Footsteps","isFavorite":true,"favoritePosition":0}
                ]
                """),
            "/configs/selected" => Json($$"""
                [{"id":"{{selectedId}}","name":"FPS Footsteps","virtualAudioDevice":"game"}]
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, new RecordingAudioController());
        var catalog = await client.GetPresetsAsync("game");
        Equal("game", catalog.Channel);
        Equal(2, catalog.Items.Count);
        Equal("FPS Footsteps", catalog.Items[0].Name);
        Equal(true, catalog.Items[0].IsFavorite);
        Equal(selectedId, catalog.SelectedId);

        var presetReads = handler.Requests.Count(request => request.StartsWith("GET /presets/", StringComparison.Ordinal));
        var selected = await client.GetSelectedPresetIdsAsync();
        Equal(selectedId, selected["game"]);
        // Refreshing selected IDs must not download the large preset catalogs again.
        Equal(presetReads, handler.Requests.Count(request => request.StartsWith("GET /presets/", StringComparison.Ordinal)));
    }),
    ("client validates preset ownership before selecting", async () =>
    {
        var gamePreset = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var otherPreset = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/presets/game" => Json($$"""
                [{"id":"{{gamePreset}}","name":"FPS Footsteps","isFavorite":true,"favoritePosition":0}]
                """),
            "/configs/selected" => Json("[]"),
            _ when request.Method == HttpMethod.Put => new HttpResponseMessage(HttpStatusCode.OK),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, new RecordingAudioController());
        await ThrowsAsync<ArgumentException>(() => client.SelectPresetAsync("master", gamePreset));
        await ThrowsAsync<ArgumentException>(() => client.SelectPresetAsync("game", otherPreset));
        Equal(false, handler.Requests.Any(x => x.StartsWith("PUT", StringComparison.Ordinal)));
        await client.SelectPresetAsync("game", gamePreset);
        Equal(true, handler.Requests.Contains($"PUT /configs/{gamePreset}/select"));
    }),
    ("client loads physical devices and each classic channel selection", async () =>
    {
        const string outputId = "{0.0.0.00000000}.{output-device}";
        const string microphoneId = "{0.0.1.00000000}.{microphone-device}";
        var handler = DeviceHandler(outputId, microphoneId);
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, new RecordingAudioController());
        var routing = await client.GetDeviceRoutingAsync();
        Equal(1, routing.OutputDevices.Count);
        Equal("Arctis Nova Pro", routing.OutputDevices[0].Name);
        Equal(1, routing.MicrophoneDevices.Count);
        Equal(outputId, routing.ChannelDeviceIds["game"]);
        Equal(outputId, routing.ChannelDeviceIds["chatRender"]);
        Equal(outputId, routing.ChannelDeviceIds["media"]);
        Equal(outputId, routing.ChannelDeviceIds["aux"]);
        Equal(microphoneId, routing.ChannelDeviceIds["chatCapture"]);
    }),
    ("client validates and changes physical devices per channel", async () =>
    {
        const string outputId = "{0.0.0.00000000}.{output-device}";
        const string microphoneId = "{0.0.1.00000000}.{microphone-device}";
        var handler = DeviceHandler(outputId, microphoneId);
        using var client = new SonarClient(new FixedEndpointProvider("http://127.0.0.1:64707/"), handler, new RecordingAudioController());
        await ThrowsAsync<ArgumentException>(() => client.SetChannelDeviceAsync("master", outputId));
        await ThrowsAsync<ArgumentException>(() => client.SetChannelDeviceAsync("game", "unknown"));
        await ThrowsAsync<ArgumentException>(() => client.SetChannelDeviceAsync("chatCapture", outputId));
        await client.SetChannelDeviceAsync("game", outputId);
        await client.SetChannelDeviceAsync("chatRender", outputId);
        await client.SetChannelDeviceAsync("chatCapture", microphoneId);
        Equal(true, handler.Requests.Any(x => x.StartsWith("PUT /classicRedirections/game/deviceId/", StringComparison.Ordinal)));
        Equal(true, handler.Requests.Any(x => x.StartsWith("PUT /classicRedirections/chat/deviceId/", StringComparison.Ordinal)));
        Equal(true, handler.Requests.Any(x => x.StartsWith("PUT /classicRedirections/mic/deviceId/", StringComparison.Ordinal)));
        Equal(false, handler.Requests.Any(x => x.StartsWith("PUT /classicRedirections/render/deviceId/", StringComparison.Ordinal)));
    }),
    ("settings store recovers from corrupt JSON and writes atomically", async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "SonarMiniMixerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(path, "not-json");
        var store = new SettingsStore(path);
        Equal(AppSettings.Default, await store.LoadAsync());
        var expected = new AppSettings(true, true, 900, 424, 44, 55);
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

static RecordingHandler HealthyHandler() => new(request => request.RequestUri!.AbsolutePath switch
{
    "/volumeSettings/classic" => Json(VolumesJson),
    "/chatMix" => Json("{\"balance\":0.1,\"state\":\"enabled\"}"),
    "/mode/" => Json("\"classic\""),
    _ => new HttpResponseMessage(HttpStatusCode.NoContent)
});
static RecordingHandler DeviceHandler(string outputId, string microphoneId) => new(request => request.RequestUri!.AbsolutePath switch
{
    "/audioDevices" when request.RequestUri.Query.Contains("deviceDataFlow=render", StringComparison.OrdinalIgnoreCase) => Json($$"""
        [{"friendlyName":"Arctis Nova Pro","id":"{{outputId}}","dataFlow":"render","role":"none","channels":2,"defaultRole":"none","fwUpdateRequired":false,"state":"active","isVad":false}]
        """),
    "/audioDevices" when request.RequestUri.Query.Contains("deviceDataFlow=capture", StringComparison.OrdinalIgnoreCase) => Json($$"""
        [{"friendlyName":"Broadcast Microphone","id":"{{microphoneId}}","dataFlow":"capture","role":"none","channels":1,"defaultRole":"none","fwUpdateRequired":false,"state":"active","isVad":false}]
        """),
    "/classicRedirections" => Json($$"""
        [
          {"id":"game","deviceId":"{{outputId}}","isRunning":true},
          {"id":"chat","deviceId":"{{outputId}}","isRunning":true},
          {"id":"media","deviceId":"{{outputId}}","isRunning":true},
          {"id":"aux","deviceId":"{{outputId}}","isRunning":true},
          {"id":"mic","deviceId":"{{microphoneId}}","isRunning":true}
        ]
        """),
    _ when request.Method == HttpMethod.Put => new HttpResponseMessage(HttpStatusCode.OK),
    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
});
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
sealed class RecordingAudioController : ISonarAudioController
{
    public List<string> Writes { get; } = [];
    public Task SetVolumeAsync(string channel, double volume, CancellationToken cancellationToken = default)
    {
        Writes.Add($"VOLUME {channel} {volume.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return Task.CompletedTask;
    }
    public Task SetMuteAsync(string channel, bool muted, CancellationToken cancellationToken = default)
    {
        Writes.Add($"MUTE {channel} {muted.ToString().ToLowerInvariant()}");
        return Task.CompletedTask;
    }
}
