using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using SonarMiniMixer.Core;
using Forms = System.Windows.Forms;

namespace SonarMiniMixer.App;

public partial class App : System.Windows.Application
{
    public const string PipeName = "SonarMiniMixer.Command.v1";
    private const string MutexName = "Local\\SonarMiniMixer.SingleInstance.v1";
    private Mutex? _mutex;
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _window;
    private CancellationTokenSource? _ipcCancellation;

    [STAThread]
    public static int Main(string[] args)
    {
        if (CommandLine.IsCommand(args))
        {
            EnsureConsole();
            return CommandLine.RunAsync(args).GetAwaiter().GetResult();
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _mutex = new Mutex(true, MutexName, out var created);
            if (!created)
            {
                _ = CommandLine.RunAsync(["show"]);
                Shutdown();
                return;
            }

            var endpoints = new SteelSeriesEndpointProvider();
            var client = new SonarClient(endpoints);
            _window = new MainWindow(new MixerViewModel(client), new SettingsStore());
            MainWindow = _window;
            StartupRegistration.RepairIfEnabled();
            CreateTrayIcon();
            StartIpcServer();
            if (!e.Args.Contains("--background")) _window.ShowFromTray();
        }
        catch (Exception exception)
        {
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SonarMiniMixer");
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(Path.Combine(logDirectory, "startup-error.log"), exception.ToString());
            System.Windows.MessageBox.Show("Sonar Mini Mixer could not start. See startup-error.log under Local AppData.", "Sonar Mini Mixer startup error");
            Shutdown(1);
        }
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Sonar Mini Mixer",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Items.Add("Open mixer", null, (_, _) => Dispatcher.Invoke(() => _window?.ShowFromTray()));
        _trayIcon.ContextMenuStrip.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(() => new SettingsWindow { Owner = _window }.ShowDialog()));
        _trayIcon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(() => _window?.ToggleFromTray());
        };
    }

    private void StartIpcServer()
    {
        _ipcCancellation = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_ipcCancellation.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_ipcCancellation.Token);
                    var buffer = new byte[32];
                    var count = await pipe.ReadAsync(buffer, _ipcCancellation.Token);
                    var command = Encoding.UTF8.GetString(buffer, 0, count);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (command == "show") _window?.ShowFromTray();
                        else if (command == "exit") ExitApp();
                    });
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(250); }
            }
        });
    }

    private void ExitApp()
    {
        _ipcCancellation?.Cancel();
        _trayIcon?.Dispose();
        _window?.ExitApplication();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ipcCancellation?.Cancel();
        _trayIcon?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static Icon LoadTrayIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute);
        using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream
            ?? throw new InvalidOperationException("The embedded application icon could not be loaded.");
        using var icon = new Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (Icon)icon.Clone();
    }

    private static void EnsureConsole()
    {
        if (!NativeMethods.AttachConsole(-1)) NativeMethods.AllocConsole();
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetOut(output);
        Console.SetError(error);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] internal static extern bool AttachConsole(int processId);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] internal static extern bool AllocConsole();
    }
}
