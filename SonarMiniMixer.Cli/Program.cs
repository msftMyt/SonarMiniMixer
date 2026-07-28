using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SonarMiniMixer.Core;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
if (command is "show" or "exit")
{
    try
    {
        using var pipe = new NamedPipeClientStream(".", "SonarMiniMixer.Command.v1", PipeDirection.Out, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(command), timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        Console.WriteLine($"OK {command}");
        return 0;
    }
    catch { Console.Error.WriteLine("ERROR: Sonar Mini Mixer is not running."); return 3; }
}

if (command == "config")
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        coreProps = SteelSeriesEndpointProvider.DefaultCorePropsPath,
        settings = SettingsStore.DefaultPath
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (command is not ("status" or "selftest"))
{
    Console.Error.WriteLine("Usage: SonarMiniMixer.Cli [status|selftest|config|show|exit]");
    return 1;
}

try
{
    using var endpoints = new SteelSeriesEndpointProvider();
    using var client = new SonarClient(endpoints);
    var state = await client.GetStateAsync();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        connected = true,
        mode = state.Mode,
        canControl = state.CanControl,
        chatMix = state.ChatMix,
        chatMixState = state.ChatMixState,
        canControlChatMix = state.CanControlChatMix,
        channels = state.Channels.Select(x => new { x.Id, x.Name, x.Volume, x.Muted })
    }, new JsonSerializerOptions { WriteIndented = true }));
    if (command == "selftest") Console.WriteLine("SELFTEST PASS: read-only Sonar discovery and mixer read succeeded; writes skipped.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"ERROR: {exception.Message}");
    return 2;
}
