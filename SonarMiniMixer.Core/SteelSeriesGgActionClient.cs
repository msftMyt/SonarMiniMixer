using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SonarMiniMixer.Core;

public sealed class SteelSeriesGgActionClient
{
    private const string ChatAction = "CHATMIX_CHAT";
    private const string GameAction = "CHATMIX_GAME";
    private static readonly Guid ChatActionId = new("12b9e3f0-9403-4181-981e-1ea6c1889418");
    private static readonly Guid GameActionId = new("8773f4c4-cbc3-4bc0-991b-3cf8317eb669");

    public async Task AdjustChatMixAsync(bool towardChat, CancellationToken cancellationToken = default)
    {
        var (endpoint, token) = DiscoverSonarGgSession();
        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        await socket.ConnectAsync(endpoint, cancellationToken);
        await SendAsync(socket, new
        {
            @event = "EVENT_SOCKET_AUTHENTICATION_TOKEN",
            data = new { token }
        }, cancellationToken);
        await Task.Delay(25, cancellationToken);
        await SendAsync(socket, new
        {
            @event = "EVENT_SUB_APP_ACTIONS_DATA_CHANGED",
            data = new
            {
                subAppName = "sonar",
                id = towardChat ? ChatActionId : GameActionId,
                actionName = towardChat ? ChatAction : GameAction,
                value = string.Empty
            }
        }, cancellationToken);
    }

    private static async Task SendAsync(ClientWebSocket socket, object value, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static (Uri Endpoint, string Token) DiscoverSonarGgSession()
    {
        var process = Process.GetProcessesByName("SteelSeriesSonar").SingleOrDefault()
            ?? throw new SonarConnectionException("SteelSeries Sonar is not running.");
        try
        {
            var environment = ProcessEnvironmentReader.Read(process.Id);
            var endpointText = GetRequired(environment, "GG_WS_ENDPOINT");
            var token = GetRequired(environment, "GG_API_AUTH_TOKEN");
            if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != "wss" || !EndpointSecurity.IsLoopback(endpoint) ||
                endpoint.AbsolutePath != "/eventing")
                throw new SonarConnectionException("SteelSeries GG returned an unsafe event endpoint.");
            return (endpoint, token);
        }
        finally { process.Dispose(); }
    }

    private static string GetRequired(string environment, string name)
    {
        var marker = name + "=";
        var start = environment.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new SonarConnectionException($"SteelSeries GG did not expose {name}.");
        start += marker.Length;
        var end = environment.IndexOf('\0', start);
        var value = environment[start..(end < 0 ? environment.Length : end)];
        if (string.IsNullOrWhiteSpace(value)) throw new SonarConnectionException($"SteelSeries GG returned an empty {name}.");
        return value;
    }

    private static class ProcessEnvironmentReader
    {
        private const int QueryInformation = 0x400;
        private const int VmRead = 0x10;
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr Reserved1, PebBaseAddress, Reserved20, Reserved21, UniqueProcessId, Reserved3;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);
        [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr bytesRead);
        [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref ProcessBasicInformation information, int size, out int returnedLength);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        public static string Read(int processId)
        {
            var process = OpenProcess(QueryInformation | VmRead, false, processId);
            if (process == IntPtr.Zero) throw new SonarConnectionException("SteelSeries Sonar session could not be inspected.");
            try
            {
                var information = new ProcessBasicInformation();
                if (NtQueryInformationProcess(process, 0, ref information, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                    throw new SonarConnectionException("SteelSeries Sonar process information could not be read.");
                var peb = ReadBytes(process, information.PebBaseAddress, 0x30);
                var parametersAddress = new IntPtr(BitConverter.ToInt64(peb, 0x20));
                var parameters = ReadBytes(process, parametersAddress, 0x100);
                var environmentAddress = new IntPtr(BitConverter.ToInt64(parameters, 0x80));
                var bytes = new List<byte>();
                var chunk = new byte[4096];
                for (var offset = 0; offset < 1024 * 1024; offset += chunk.Length)
                {
                    if (!ReadProcessMemory(process, environmentAddress + offset, chunk, chunk.Length, out var count) || count == IntPtr.Zero) break;
                    bytes.AddRange(chunk.AsSpan(0, (int)count).ToArray());
                    if (bytes.Count >= 4 && bytes[^1] == 0 && bytes[^2] == 0 && bytes[^3] == 0 && bytes[^4] == 0) break;
                }
                if (bytes.Count == 0) throw new SonarConnectionException("SteelSeries Sonar environment could not be read.");
                return Encoding.Unicode.GetString(bytes.ToArray());
            }
            finally { CloseHandle(process); }
        }

        private static byte[] ReadBytes(IntPtr process, IntPtr address, int size)
        {
            var buffer = new byte[size];
            if (!ReadProcessMemory(process, address, buffer, size, out var count) || count.ToInt64() != size)
                throw new SonarConnectionException("SteelSeries Sonar process memory could not be read.");
            return buffer;
        }
    }
}
