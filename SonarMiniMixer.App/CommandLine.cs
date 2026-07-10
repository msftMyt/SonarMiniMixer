using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SonarMiniMixer.Core;

namespace SonarMiniMixer.App;

internal static class CommandLine
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "status" or "selftest" or "config" or "show" or "exit";

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args[0];
        if (command is "show" or "exit") return await SendIpcAsync(command);

        if (command == "config")
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                coreProps = SteelSeriesEndpointProvider.DefaultCorePropsPath,
                settings = SettingsStore.DefaultPath,
                startupEnabled = StartupRegistration.IsEnabled()
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
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
    }

    private static async Task<int> SendIpcAsync(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", App.PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1500);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(command));
            return 0;
        }
        catch { Console.Error.WriteLine("ERROR: Sonar Mini Mixer is not running."); return 3; }
    }
}
