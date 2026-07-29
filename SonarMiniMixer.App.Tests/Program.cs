using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;
using SonarMiniMixer.App;
using SonarMiniMixer.Core;

namespace SonarMiniMixer.App.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var application = new SonarMiniMixer.App.App();
        application.InitializeComponent();

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("fader exposes a wide hit target without unnamed automation controls", FaderHitTargetAndAutomationAsync),
            ("mute controls expose toggle semantics and checked state", MuteToggleSemanticsAsync),
            ("mute controls render speaker and speaker-off icons", MuteIconographyAsync),
            ("mic channel mutes with a microphone icon, not a speaker", MicrophoneMuteIconographyAsync),
            ("mute button automation name flips to Unmute when muted", MuteAutomationNameFlipsAsync),
            ("fader renders a groove with an accent level fill", FaderGrooveAndLevelFillAsync),
            ("responsive metrics scale between min, reference, and max", ResponsiveMetricScalingAsync),
            ("option refresh preserves collections and selection", OptionRefreshIsStableAsync),
            ("early routing refresh cannot cache an empty preset surface", EarlyRefreshDoesNotPoisonPresetsAsync),
            ("socket reconnect forces routing refresh", SocketReconnectRefreshesRoutingAsync),
            ("disposed mixer ignores late socket and refresh callbacks", DisposedMixerIgnoresLateCallbacksAsync),
            ("visible refresh follows external preset selection without rebuilding catalogs", ExternalPresetSelectionSyncAsync),
            ("unknown external preset refreshes only its channel catalog", UnknownExternalPresetRefreshesCatalogAsync),
            ("pushed volume data applies without an HTTP refresh", PushedVolumeAppliesDirectlyAsync),

            ("hidden mixer stops polling and resyncs on show", IdlePollingIsSuspendedAsync),
            ("master fan-out issues one write per channel", MasterFanOutIsEfficientAsync),
            ("master fan-out is optimistic and rolls back failures", MasterFanOutRollsBackAsync),
            ("Master drag mirrors GG ratios and throttled native writes", MasterDragMirrorsGgAsync),
            ("layout grows and shrinks without clipping", LayoutAdaptsToWindowSizeAsync),
            ("OLED theme uses only a subtle plum backdrop", OledThemeUsesSubtlePlumAsync),
            ("selector chrome renders its current display value", SelectorChromeRendersCurrentValueAsync),
            ("channel options load per Sonar route", ChannelOptionsLoadPerRouteAsync),
            ("Master quick output reports mixed playback routes", MasterQuickOutputReportsMixedRoutesAsync),
            ("Master quick output fans out without touching Mic", MasterQuickOutputFansOutPlaybackRoutesAsync),
            ("Master quick output keeps successful routes on partial failure", MasterQuickOutputHandlesPartialFailureAsync),
            ("channel preset and device selections write independently", ChannelSelectionsWriteIndependentlyAsync),
            ("channel option failure leaves core mixer controls live", ChannelOptionFailureLeavesMixerLiveAsync),
            ("poll refresh preserves and writes a pending volume edit", PendingVolumeSurvivesRefreshAsync),
            ("read-only channel rejects local volume drift", ReadOnlyChannelRejectsLocalDriftAsync),
            ("failed volume write surfaces an actionable status", FailedVolumeWriteSurfacesStatusAsync),
            ("poll refresh preserves and writes a pending ChatMix edit", PendingChatMixSurvivesRefreshAsync),
            ("mouse wheel changes a focused fader by its small step", MouseWheelChangesFaderAsync),
            ("never-shown tray exit preserves existing settings", NeverShownExitPreservesSettingsAsync),
            ("compact settings dimensions survive sanitization", CompactSettingsDimensionsAsync),
        };

        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                run().GetAwaiter().GetResult();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"RESULT {tests.Length - failed}/{tests.Length} passed");
        application.Shutdown();
        return failed == 0 ? 0 : 1;
    }

    private static Task FaderHitTargetAndAutomationAsync()
    {
        using var fixture = new WindowFixture();
        var slider = fixture.VerticalSliders.Single();
        var repeatButtons = Descendants<RepeatButton>(slider).ToArray();
        Equal(2, repeatButtons.Length);
        Equal(true, repeatButtons.All(button => button.IsHitTestVisible));
        Equal(true, repeatButtons.All(button => button.GetType().Name == "MixerTrackButton"));
        Equal(true, repeatButtons.All(button => CreateAutomationPeer(button) is null));

        var narrowestHitRegion = repeatButtons.Min(button => button.ActualWidth);
        Equal(true, narrowestHitRegion >= slider.ActualWidth - 2,
            $"narrowest hit region={narrowestHitRegion}/{slider.ActualWidth}");
        return Task.CompletedTask;
    }

    private static Task MuteToggleSemanticsAsync()
    {
        using var fixture = new WindowFixture();
        Equal(typeof(ToggleButton), fixture.MuteStyle.TargetType);
        var game = new ToggleButton { Style = fixture.MuteStyle, IsChecked = false };
        AutomationProperties.SetName(game, "Mute Game");
        game.ApplyTemplate();
        var peer = new ToggleButtonAutomationPeer(game);
        Equal(AutomationControlType.Button, peer.GetAutomationControlType());
        Equal(false, game.IsChecked);
        game.IsChecked = true;
        Equal(true, game.IsChecked);
        Equal(ToggleState.On, ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!).ToggleState);
        return Task.CompletedTask;
    }

    private static Task MuteIconographyAsync()
    {
        using var fixture = new WindowFixture();
        var button = new ToggleButton { Style = fixture.MuteStyle, IsChecked = false };
        button.ApplyTemplate();
        var soundWaves = (ShapePath)button.Template.FindName("SoundWaves", button)!;
        var muteSlash = (ShapePath)button.Template.FindName("MuteSlash", button)!;
        Equal(Visibility.Visible, soundWaves.Visibility);
        Equal(Visibility.Collapsed, muteSlash.Visibility);
        button.IsChecked = true;
        Equal(Visibility.Collapsed, soundWaves.Visibility);
        Equal(Visibility.Visible, muteSlash.Visibility);
        return Task.CompletedTask;
    }

    private static async Task MicrophoneMuteIconographyAsync()
    {
        using var fixture = new WindowFixture();
        var client = new FakeSonarClient(State());
        using var viewModel = new MixerViewModel(client);
        await viewModel.RefreshAsync();

        var mic = viewModel.Channels.Single(channel => channel.Id == "chatCapture");
        var game = viewModel.Channels.Single(channel => channel.Id == "game");
        Equal(true, mic.IsMicrophone);
        Equal(false, game.IsMicrophone);

        var micButton = Render(fixture.MuteStyle, mic);
        var gameButton = Render(fixture.MuteStyle, game);

        Equal(Visibility.Visible, Part<Grid>(micButton, "MicIcon").Visibility);
        Equal(Visibility.Collapsed, Part<Grid>(micButton, "SpeakerIcon").Visibility);
        Equal(Visibility.Collapsed, Part<Grid>(gameButton, "MicIcon").Visibility);
        Equal(Visibility.Visible, Part<Grid>(gameButton, "SpeakerIcon").Visibility);

        micButton.IsChecked = true;
        micButton.UpdateLayout();
        Equal(Visibility.Visible, Part<Grid>(micButton, "MicIcon").Visibility);
        Equal(Visibility.Collapsed, Part<ShapePath>(micButton, "MicStand").Visibility);
        Equal(Visibility.Visible, Part<ShapePath>(micButton, "MuteSlash").Visibility);

        static ToggleButton Render(Style style, object dataContext)
        {
            var button = new ToggleButton { Style = style, IsChecked = false, DataContext = dataContext };
            button.Measure(new Size(30, 30));
            button.Arrange(new Rect(0, 0, 30, 30));
            button.ApplyTemplate();
            button.UpdateLayout();
            return button;
        }

        static T Part<T>(ToggleButton button, string name) where T : FrameworkElement =>
            (T)button.Template.FindName(name, button)!;
    }

    private static async Task MuteAutomationNameFlipsAsync()
    {
        using var fixture = new WindowFixture();
        var client = new FakeSonarClient(State());
        using var viewModel = new MixerViewModel(client);
        await viewModel.RefreshAsync();

        var mic = viewModel.Channels.Single(channel => channel.Id == "chatCapture");
        Equal("Mute Mic", mic.MuteAction);

        var button = new ToggleButton { Style = fixture.MuteStyle, DataContext = mic };
        button.SetBinding(AutomationProperties.NameProperty, new System.Windows.Data.Binding(nameof(ChannelViewModel.MuteAction)));
        button.SetBinding(ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding(nameof(ChannelViewModel.Muted)) { Mode = System.Windows.Data.BindingMode.OneWay });
        button.Measure(new Size(30, 30));
        button.Arrange(new Rect(0, 0, 30, 30));
        button.ApplyTemplate();
        button.UpdateLayout();
        Equal("Mute Mic", AutomationProperties.GetName(button));

        await mic.ToggleMuteAsync();
        button.UpdateLayout();

        Equal(true, mic.Muted);
        Equal("Unmute Mic", mic.MuteAction);
        Equal(true, button.IsChecked);
        Equal("Unmute Mic", AutomationProperties.GetName(button));

        var peer = UIElementAutomationPeer.CreatePeerForElement(button)!;
        Equal("Unmute Mic", peer.GetName());
    }



    private static async Task IdlePollingIsSuspendedAsync()
    {
        var client = new FakeSonarClient(State());
        using var vm = new MixerViewModel(client);
        await vm.RefreshAsync();

        // While the tray popup is hidden, Sonar's push socket is the only thing that
        // should drive state; polling it costs HTTP work no one can see.
        vm.SetSurfaceVisible(false);
        var baseline = client.StateReads;
        vm.PollForTest();
        vm.PollForTest();
        Equal(baseline, client.StateReads);

        // Becoming visible must resync immediately so nothing is stale on screen.
        await vm.SetSurfaceVisibleAsync(true);
        Equal(true, client.StateReads > baseline, $"reads={client.StateReads} baseline={baseline}");

        var visible = client.StateReads;
        vm.PollForTest();
        Equal(true, client.StateReads > visible, "visible mixer should still poll as a safety net");
    }

    private static async Task PushedVolumeAppliesDirectlyAsync()
    {
        var client = new FakeSonarClient(State());
        using var vm = new MixerViewModel(client);
        await vm.RefreshAsync();
        var reads = client.StateReads;
        var game = vm.Channels.First(channel => channel.Id == "game");

        vm.OnSonarEvent(new SonarEvent(
            SonarEventKind.VolumesChanged, 0, string.Empty,
            "{\"masters\":{\"classic\":{\"volume\":1.0,\"muted\":false}}," +
            "\"devices\":{\"game\":{\"classic\":{\"volume\":0.23,\"muted\":true}}}}"));

        Equal(23d, game.Volume);
        Equal(true, game.Muted);
        Equal(reads, client.StateReads);
    }


    private static async Task UnknownExternalPresetRefreshesCatalogAsync()
    {
        var client = new FakeSonarClient(State());
        var first = new SonarPreset(Guid.Parse("44444444-4444-4444-4444-444444444444"), "Existing", false, 0);
        var added = new SonarPreset(Guid.Parse("55555555-5555-5555-5555-555555555555"), "Created in GG", false, 0);
        client.PresetCatalogs["game"] = new SonarPresetCatalog("game", [first], first.Id);
        using var vm = new MixerViewModel(client);
        await vm.RefreshAsync();
        var reads = client.PresetReads;

        client.PresetCatalogs["game"] = new SonarPresetCatalog("game", [first, added], added.Id);
        await vm.RefreshAsync();

        var game = vm.Channels.First(channel => channel.Id == "game");
        Equal(added.Id, game.SelectedPresetId);
        Equal("Created in GG", game.SelectedPresetName);
        Equal(2, game.Presets.Count);
        Equal(reads + 1, client.PresetReads);
    }

    private static async Task ExternalPresetSelectionSyncAsync()
    {
        var client = new FakeSonarClient(State());
        var first = new SonarPreset(Guid.Parse("11111111-1111-1111-1111-111111111111"), "First", false, 0);
        var second = new SonarPreset(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Second", false, 0);
        client.PresetCatalogs["game"] = new SonarPresetCatalog("game", [first, second], first.Id);
        using var vm = new MixerViewModel(client);
        await vm.RefreshAsync();

        var game = vm.Channels.First(channel => channel.Id == "game");
        var items = game.Presets.ToArray();
        Equal(true, items.Length >= 2);
        var originalCollection = game.Presets;
        var changed = items.First(item => item.Id != game.SelectedPresetId);
        client.PresetCatalogs["game"] = new SonarPresetCatalog("game", items, changed.Id);

        await vm.RefreshAsync();

        Equal(changed.Id, game.SelectedPresetId);
        Equal(true, ReferenceEquals(originalCollection, game.Presets));
        Equal(items.Length, game.Presets.Count);
    }

    private static async Task DisposedMixerIgnoresLateCallbacksAsync()
    {
        var client = new FakeSonarClient(State());
        var vm = new MixerViewModel(client);
        await vm.RefreshAsync();
        var reads = client.StateReads;
        var connected = vm.Connected;
        vm.Dispose();

        vm.OnEventStreamConnectionChanged(false);
        vm.OnSonarEvent(new SonarEvent(SonarEventKind.ChatMixChanged, 0.5, "enabled"));
        await vm.RefreshAsync();

        Equal(connected, vm.Connected);
        Equal(reads, client.StateReads);
    }

    private static async Task SocketReconnectRefreshesRoutingAsync()
    {
        var client = new FakeSonarClient(State());
        using var vm = new MixerViewModel(client);
        await vm.RefreshAsync();
        var reads = client.RoutingReads;

        vm.OnEventStreamConnectionChanged(false);
        Equal(false, vm.Connected);
        Equal("Sonar reconnecting", vm.Status);

        vm.OnEventStreamConnectionChanged(true);
        await Task.Yield();

        Equal(true, vm.Connected);
        Equal(true, client.RoutingReads > reads, $"routing reads stayed at {reads}");
    }

    private static async Task EarlyRefreshDoesNotPoisonPresetsAsync()
    {
        var client = new FakeSonarClient(State());
        var preset = new SonarPreset(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Loaded", false, 0);
        client.PresetCatalogs["game"] = new SonarPresetCatalog("game", [preset], preset.Id);
        using var vm = new MixerViewModel(client);

        // Simulates a routing socket event beating the initial state load.
        await vm.RefreshChannelOptionsForTestAsync();
        await vm.RefreshAsync();

        var game = vm.Channels.First(channel => channel.Id == "game");
        Equal(1, game.Presets.Count);
        Equal(preset.Id, game.SelectedPresetId);
    }

    private static async Task OptionRefreshIsStableAsync()
    {
        var client = new FakeSonarClient(State());
        using var vm = new MixerViewModel(client);
        await vm.RefreshAsync();

        var game = vm.Channels.First(c => c.Id == "game");
        var presetsRef = game.Presets;
        var devicesRef = game.Devices;
        var presetCount = game.Presets.Count;
        var deviceCount = game.Devices.Count;

        var resets = 0;
        game.Presets.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
        };
        game.Devices.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
        };

        var selectedDevice = game.SelectedDeviceId;
        var selectedPreset = game.SelectedPresetId;

        // Re-applying identical option data must not churn the bound collections,
        // otherwise an open ComboBox loses its selection every poll.
        await vm.RefreshChannelOptionsForTestAsync();
        await vm.RefreshChannelOptionsForTestAsync();

        Equal(0, resets);
        Equal(true, ReferenceEquals(presetsRef, game.Presets));
        Equal(true, ReferenceEquals(devicesRef, game.Devices));
        Equal(presetCount, game.Presets.Count);
        Equal(deviceCount, game.Devices.Count);
        Equal(selectedDevice, game.SelectedDeviceId);
        Equal(selectedPreset, game.SelectedPresetId);
    }


    private static async Task MasterFanOutRollsBackAsync()
    {
        var client = new FakeSonarClient(State());
        var speakers = new SonarAudioDevice("dev-speakers", "Speakers", "render");
        var headphones = new SonarAudioDevice("dev-headphones", "Headphones", "render");
        var mic = new SonarAudioDevice("dev-mic", "Headset Mic", "capture");
        client.Routing = new SonarDeviceRouting([speakers, headphones], [mic],
            new Dictionary<string, string?>
            {
                ["game"] = speakers.Id, ["chatRender"] = speakers.Id,
                ["media"] = speakers.Id, ["aux"] = speakers.Id, ["chatCapture"] = mic.Id,
            });

        using var vm = new MixerViewModel(client);
        await vm.RefreshAsync();
        var master = vm.Channels.First(c => c.IsMaster);

        // Media cannot be routed; every other channel must still move.
        client.FailDeviceChannels.Add("media");
        await vm.SelectMasterOutputAsync(master, headphones.Id);

        Equal(headphones.Id, vm.Channels.First(c => c.Id == "game").SelectedDeviceId);
        Equal(headphones.Id, vm.Channels.First(c => c.Id == "chatRender").SelectedDeviceId);
        Equal(headphones.Id, vm.Channels.First(c => c.Id == "aux").SelectedDeviceId);
        // The failed channel is rolled back rather than left showing a lie.
        Equal(speakers.Id, vm.Channels.First(c => c.Id == "media").SelectedDeviceId);
        // Mixed routing means the Master selector shows no single device.
        Equal(null, master.SelectedDeviceId);
        Equal(true, vm.StatusDetail.Contains("Media", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task MasterFanOutIsEfficientAsync()
    {
        var client = new FakeSonarClient(State());
        var speakers = new SonarAudioDevice("dev-speakers", "Speakers", "render");
        var headphones = new SonarAudioDevice("dev-headphones", "Headphones", "render");
        var mic = new SonarAudioDevice("dev-mic", "Headset Mic", "capture");
        client.Routing = new SonarDeviceRouting(
            [speakers, headphones],
            [mic],
            new Dictionary<string, string?>
            {
                ["game"] = speakers.Id,
                ["chatRender"] = speakers.Id,
                ["media"] = speakers.Id,
                ["aux"] = speakers.Id,
                ["chatCapture"] = mic.Id,
            });

        using var vm = new MixerViewModel(client);
        await vm.RefreshAsync();

        var master = vm.Channels.First(c => c.IsMaster);
        client.DeviceWrites.Clear();
        client.RoutingReads = 0;

        await vm.SelectMasterOutputAsync(master, headphones.Id);

        // Exactly one write per playback channel, and Mic is never touched.
        Equal(4, client.DeviceWrites.Count);
        Equal(false, client.DeviceWrites.Any(w => w.Channel == "chatCapture"));
        Equal(true, client.DeviceWrites.All(w => w.Device == headphones.Id));
        Equal(headphones.Id, master.SelectedDeviceId);
        foreach (var id in new[] { "game", "chatRender", "media", "aux" })
            Equal(headphones.Id, vm.Channels.First(c => c.Id == id).SelectedDeviceId);

        // Re-selecting the same device is a no-op rather than four redundant writes.
        client.DeviceWrites.Clear();
        await vm.SelectMasterOutputAsync(master, headphones.Id);
        Equal(0, client.DeviceWrites.Count);
    }

    private static async Task MasterDragMirrorsGgAsync()
    {
        var state = new MixerState(
            "classic",
            [
                new MixerChannel("master", "Master", 1, false, "#786CFF", 0),
                new MixerChannel("game", "Game", .4, false, "#48F27A", 10),
                new MixerChannel("chatRender", "Chat", .6, false, "#44D7F4", 20),
                new MixerChannel("media", "Media", .7, false, "#FF5EBB", 30),
                new MixerChannel("aux", "Aux", .3, false, "#B870FF", 40),
                new MixerChannel("chatCapture", "Mic", .8, false, "#FFB35C", 50),
            ],
            0,
            "enabled");
        var client = new FakeSonarClient(state);
        client.VolumeWriteDelay = TimeSpan.FromMilliseconds(50);
        using var viewModel = new MixerViewModel(client);
        await viewModel.RefreshAsync();
        var master = viewModel.Channels.Single(channel => channel.IsMaster);

        master.Volume = 80;
        Equal(32d, viewModel.Channels.Single(channel => channel.Id == "game").Volume);
        Equal(48d, viewModel.Channels.Single(channel => channel.Id == "chatRender").Volume);
        Equal(56d, viewModel.Channels.Single(channel => channel.Id == "media").Volume);
        Equal(24d, viewModel.Channels.Single(channel => channel.Id == "aux").Volume);
        Equal(80d, viewModel.Channels.Single(channel => channel.Id == "chatCapture").Volume);
        await viewModel.RefreshAsync();
        Equal(32d, viewModel.Channels.Single(channel => channel.Id == "game").Volume);
        Equal(48d, viewModel.Channels.Single(channel => channel.Id == "chatRender").Volume);
        await Task.Delay(30);
        Equal(1, client.VolumeWrites.Count);
        Equal(("master", .8), client.VolumeWrites[0]);

        master.Volume = 70;
        await Task.Delay(10);
        master.Volume = 60;
        await Task.Delay(10);
        master.Volume = 50;
        await Task.Delay(280);

        Equal(.5, client.VolumeWrites.Last().Volume);
        Equal(true, client.VolumeWrites.Count <= 3, $"writes={client.VolumeWrites.Count}");
        Equal(true, client.VolumeWrites.All(write => write.Channel == "master"));
        var writeGaps = client.VolumeWriteTimes.Zip(client.VolumeWriteTimes.Skip(1), (first, second) => second - first).ToArray();
        Equal(true, writeGaps.All(gap => gap <= TimeSpan.FromMilliseconds(105)),
            $"gaps=[{string.Join(',', writeGaps.Select(gap => gap.TotalMilliseconds.ToString("0.0")))}]");
        Equal(0, client.MuteWrites.Count);
        Equal(20d, viewModel.Channels.Single(channel => channel.Id == "game").Volume);
        Equal(30d, viewModel.Channels.Single(channel => channel.Id == "chatRender").Volume);
        Equal(35d, viewModel.Channels.Single(channel => channel.Id == "media").Volume);
        Equal(15d, viewModel.Channels.Single(channel => channel.Id == "aux").Volume);
        Equal(80d, viewModel.Channels.Single(channel => channel.Id == "chatCapture").Volume);

        await Task.Delay(170);
        master.Volume = 0;
        master.Volume = 20;
        Equal(20d, viewModel.Channels.Single(channel => channel.Id == "game").Volume);
        Equal(20d, viewModel.Channels.Single(channel => channel.Id == "chatRender").Volume);
        Equal(20d, viewModel.Channels.Single(channel => channel.Id == "media").Volume);
        Equal(20d, viewModel.Channels.Single(channel => channel.Id == "aux").Volume);
        Equal(80d, viewModel.Channels.Single(channel => channel.Id == "chatCapture").Volume);

    }

    private static Task ResponsiveMetricScalingAsync()
    {
        var converter = new ResponsiveMetricConverter();
        object At(double h) => converter.Convert(h, typeof(double), "10,14,20", CultureInfo.InvariantCulture);

        Equal(10d, (double)At(ResponsiveMetricConverter.MinHeight));
        Equal(14d, (double)At(ResponsiveMetricConverter.ReferenceHeight));
        Equal(20d, (double)At(ResponsiveMetricConverter.MaxHeight));
        Equal(10d, (double)At(120));
        Equal(20d, (double)At(4000));
        var previous = double.MinValue;
        for (var h = 300d; h <= 900d; h += 25)
        {
            var current = (double)At(h);
            Equal(true, current >= previous, $"h={h} current={current} previous={previous}");
            previous = current;
        }
        Equal(DependencyProperty.UnsetValue, converter.Convert(double.NaN, typeof(double), "10,14,20", CultureInfo.InvariantCulture));
        Equal(DependencyProperty.UnsetValue, converter.Convert(400d, typeof(double), "bad", CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }

    private static Task LayoutAdaptsToWindowSizeAsync()
    {
        using var fixture = new WindowFixture();
        var root = (FrameworkElement)fixture.Window.Content;

        static IEnumerable<T> Descendants<T>(DependencyObject node) where T : DependencyObject
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                if (child is T match) yield return match;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }

        double GrooveHeightAt(double width, double height)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();

            var grooves = Descendants<Border>(root)
                .Where(b => b.Name == "FaderGroove")
                .Select(b => b.ActualHeight)
                .ToArray();

            Equal(true, grooves.Length >= 6, $"grooves={grooves.Length} at {width}x{height}");
            Equal(true, root.ActualHeight <= height + 0.5, $"root={root.ActualHeight} window={height}");
            Equal(true, grooves.All(h => h > 20), $"grooves=[{string.Join(",", grooves)}] at {width}x{height}");
            return grooves[0];
        }

        var atMin = GrooveHeightAt(640, 372);
        var atDefault = GrooveHeightAt(864, 424);
        var atLarge = GrooveHeightAt(1180, 650);

        Equal(true, atDefault > atMin, $"default={atDefault} min={atMin}");
        Equal(true, atLarge > atDefault, $"large={atLarge} default={atDefault}");
        return Task.CompletedTask;
    }

    private static Task OledThemeUsesSubtlePlumAsync()
    {
        var brush = (SolidColorBrush)Application.Current.Resources["WindowBrush"];
        Equal(true, brush.Color.R is >= 12 and <= 22, $"red={brush.Color.R}");
        Equal(true, brush.Color.B > brush.Color.R, $"rgb={brush.Color}");
        Equal(true, brush.Color.R > brush.Color.G, $"rgb={brush.Color}");
        return Task.CompletedTask;
    }

    private static Task FaderGrooveAndLevelFillAsync()
    {
        using var fixture = new WindowFixture();
        var slider = fixture.VerticalSliders.Single();
        var groove = (Border)slider.Template.FindName("FaderGroove", slider)!;
        var fill = (Border)slider.Template.FindName("FaderFill", slider)!;
        Equal(true, groove.ActualHeight > 60, $"groove={groove.ActualHeight}");
        Equal(groove.Width, fill.Width);
        Equal("#FF48F27A", ((SolidColorBrush)fill.Background).Color.ToString());
        Equal(true, ((SolidColorBrush)groove.Background).Color != ((SolidColorBrush)fill.Background).Color);
        return Task.CompletedTask;
    }

    private static Task SelectorChromeRendersCurrentValueAsync()
    {
        using var fixture = new WindowFixture();
        var selector = new ComboBox { Style = fixture.ChannelComboStyle, Tag = "Forza Horizon 6" };
        selector.ApplyTemplate();
        var toggle = (ToggleButton)selector.Template.FindName("DropDownToggle", selector)!;
        toggle.ApplyTemplate();
        var text = (TextBlock)toggle.Template.FindName("SelectedText", toggle)!;
        Equal("Forza Horizon 6", text.Text);
        return Task.CompletedTask;
    }

    private static async Task ChannelOptionsLoadPerRouteAsync()
    {
        var gamePreset = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var micPreset = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var client = new FakeSonarClient(State()) { Routing = Routing() };
        client.PresetCatalogs["game"] = new SonarPresetCatalog("game", [new SonarPreset(gamePreset, "FPS Footsteps", true, 0)], gamePreset);
        client.PresetCatalogs["chatCapture"] = new SonarPresetCatalog("chatCapture", [new SonarPreset(micPreset, "Deep Voice", true, 0)], micPreset);
        using var viewModel = new MixerViewModel(client);

        await viewModel.RefreshAsync();

        var master = viewModel.Channels.Single(channel => channel.Id == "master");
        var game = viewModel.Channels.Single(channel => channel.Id == "game");
        var mic = viewModel.Channels.Single(channel => channel.Id == "chatCapture");
        Equal(false, master.HasChannelOptions);
        Equal(0, master.Presets.Count);
        Equal(true, game.HasChannelOptions);
        Equal(gamePreset, game.SelectedPresetId);
        Equal("FPS Footsteps", game.SelectedPresetName);
        Equal("output-1", game.SelectedDeviceId);
        Equal("Arctis Game", game.SelectedDeviceName);
        Equal("capture", mic.Devices.Single().DataFlow);
        Equal("mic-1", mic.SelectedDeviceId);
    }

    private static async Task MasterQuickOutputReportsMixedRoutesAsync()
    {
        var client = new FakeSonarClient(State()) { Routing = Routing() };
        using var viewModel = new MixerViewModel(client);

        await viewModel.RefreshAsync();

        var master = viewModel.Channels.Single(channel => channel.Id == "master");
        Equal(true, master.IsMaster);
        Equal(2, master.Devices.Count);
        Equal<string?>(null, master.SelectedDeviceId);
        Equal("Mixed outputs", master.SelectedDeviceName);
    }

    private static async Task MasterQuickOutputFansOutPlaybackRoutesAsync()
    {
        var client = new FakeSonarClient(State()) { Routing = Routing() };
        using var viewModel = new MixerViewModel(client);
        await viewModel.RefreshAsync();
        var master = viewModel.Channels.Single(channel => channel.IsMaster);

        await viewModel.SelectMasterOutputAsync(master, "output-2");

        Equal("game,chatRender,media", string.Join(',', client.DeviceWrites.Select(write => write.Channel)));
        Equal(false, client.DeviceWrites.Any(write => write.Channel is "chatCapture" or "master"));
        Equal(true, viewModel.Channels.Where(channel => channel.Id is "game" or "chatRender" or "media" or "aux")
            .All(channel => channel.SelectedDeviceId == "output-2"));
        Equal("output-2", master.SelectedDeviceId);
        Equal("Speakers", master.SelectedDeviceName);
    }

    private static async Task MasterQuickOutputHandlesPartialFailureAsync()
    {
        var client = new FakeSonarClient(State()) { Routing = Routing() };
        client.FailDeviceChannels.Add("media");
        using var viewModel = new MixerViewModel(client);
        await viewModel.RefreshAsync();
        var master = viewModel.Channels.Single(channel => channel.IsMaster);

        await viewModel.SelectMasterOutputAsync(master, "output-2");

        Equal("game,chatRender", string.Join(',', client.DeviceWrites.Select(write => write.Channel)));
        Equal("output-1", viewModel.Channels.Single(channel => channel.Id == "media").SelectedDeviceId);
        Equal<string?>(null, master.SelectedDeviceId);
        Equal("Mixed outputs", master.SelectedDeviceName);
        Equal(true, viewModel.CanControl);
        Contains(viewModel.StatusDetail, "Media");
    }

    private static async Task ChannelSelectionsWriteIndependentlyAsync()
    {
        var currentPreset = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var nextPreset = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var client = new FakeSonarClient(State()) { Routing = Routing() };
        client.PresetCatalogs["game"] = new SonarPresetCatalog(
            "game",
            [new SonarPreset(currentPreset, "Current", true, 0), new SonarPreset(nextPreset, "Next", false, 0)],
            currentPreset);
        using var viewModel = new MixerViewModel(client);
        await viewModel.RefreshAsync();
        var game = viewModel.Channels.Single(channel => channel.Id == "game");

        await viewModel.SelectPresetAsync(game, nextPreset);
        await viewModel.SelectDeviceAsync(game, "output-2");

        Equal(("game", nextPreset), client.PresetWrites.Single());
        Equal(("game", "output-2"), client.DeviceWrites.Single());
        Equal(nextPreset, game.SelectedPresetId);
        Equal("Next", game.SelectedPresetName);
        Equal("output-2", game.SelectedDeviceId);
        Equal("Speakers", game.SelectedDeviceName);
    }

    private static async Task ChannelOptionFailureLeavesMixerLiveAsync()
    {
        var client = new FakeSonarClient(State()) { FailOptionReads = true };
        using var viewModel = new MixerViewModel(client);

        await viewModel.RefreshAsync();

        Equal(true, viewModel.Connected);
        Equal(true, viewModel.CanControl);
        Contains(viewModel.StatusDetail, "options unavailable");
    }

    private static async Task PendingVolumeSurvivesRefreshAsync()
    {
        var client = new FakeSonarClient(State(volume: 0.80));
        using var viewModel = new MixerViewModel(client);
        await viewModel.RefreshAsync();
        var game = viewModel.Channels.Single(channel => channel.Id == "game");

        game.Volume = 55;
        client.State = State(volume: 0.20);
        await viewModel.RefreshAsync();
        Equal(55d, game.Volume);
        await Task.Delay(160);
        Equal(0.55, client.VolumeWrites.Single().Volume);
    }

    private static async Task ReadOnlyChannelRejectsLocalDriftAsync()
    {
        var client = new FakeSonarClient(State(mode: "stream", volume: 0.80));
        using var viewModel = new MixerViewModel(client);
        await viewModel.RefreshAsync();
        var game = viewModel.Channels.Single(channel => channel.Id == "game");

        game.Volume = 55;
        Equal(80d, game.Volume);
        await Task.Delay(120);
        Equal(0, client.VolumeWrites.Count);
    }

    private static async Task FailedVolumeWriteSurfacesStatusAsync()
    {
        var client = new FakeSonarClient(State(volume: 0.80)) { FailVolumeWrites = true };
        using var viewModel = new MixerViewModel(client);
        await viewModel.RefreshAsync();
        var game = viewModel.Channels.Single(channel => channel.Id == "game");

        game.Volume = 55;
        await Task.Delay(180);
        Equal("Change failed", viewModel.Status);
        Contains(viewModel.StatusDetail, "Game volume");
        Equal(80d, game.Volume);
    }

    private static async Task PendingChatMixSurvivesRefreshAsync()
    {
        var client = new FakeSonarClient(State(chatMix: 0.10));
        using var viewModel = new MixerViewModel(client);
        await viewModel.RefreshAsync();

        viewModel.ChatMix = 45;
        client.State = State(chatMix: -0.20);
        await viewModel.RefreshAsync();
        Equal(45d, viewModel.ChatMix);
        await Task.Delay(160);
        Equal(0.45, client.ChatMixWrites.Single());
    }

    private static Task MouseWheelChangesFaderAsync()
    {
        using var fixture = new WindowFixture();
        var slider = fixture.VerticalSliders.Single(slider => AutomationProperties.GetName(slider) == "Game volume");
        var initial = slider.Value;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent,
            Source = slider,
        };
        slider.RaiseEvent(wheel);
        Equal(true, wheel.Handled);
        Equal(initial + slider.SmallChange, slider.Value);
        return Task.CompletedTask;
    }

    private static Task NeverShownExitPreservesSettingsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "SonarMiniMixerAppTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        var store = new SettingsStore(path);
        var expected = new AppSettings(true, true, 900, 500, 44, 55);
        store.SaveAsync(expected).GetAwaiter().GetResult();
        var vm = new MixerViewModel(new FakeSonarClient(State()));
        var window = new MainWindow(vm, store);

        // Exit without ever showing/loading the window.
        window.ExitApplication();

        Equal(expected, store.LoadAsync().GetAwaiter().GetResult());
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private static async Task CompactSettingsDimensionsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "SonarMiniMixerAppTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "settings.json");
        var store = new SettingsStore(path);
        await store.SaveAsync(new AppSettings(false, false, 660, 322, null, null));
        var settings = await store.LoadAsync();
        Equal(660d, settings.Width);
        Equal(372d, settings.Height);
        await store.SaveAsync(new AppSettings(false, false, 400, 200, null, null));
        var floored = await store.LoadAsync();
        Equal(640d, floored.Width);
        Equal(372d, floored.Height);
        Directory.Delete(root, true);
    }

    private static MixerState State(string mode = "classic", double volume = 0.80, double chatMix = 0.10) => new(
        mode,
        [
            new MixerChannel("master", "Master", 1, false, "#786CFF", 0),
            new MixerChannel("game", "Game", volume, false, "#48F27A", 10),
            new MixerChannel("chatRender", "Chat", 1, false, "#44D7F4", 20),
            new MixerChannel("media", "Media", 1, false, "#FF5EBB", 30),
            new MixerChannel("aux", "Aux", 1, false, "#B870FF", 40),
            new MixerChannel("chatCapture", "Mic", 1, false, "#FFB35C", 50),
        ],
        chatMix,
        "enabled");

    private static SonarDeviceRouting Routing() => new(
        [new SonarAudioDevice("output-1", "Arctis Game", "render"), new SonarAudioDevice("output-2", "Speakers", "render")],
        [new SonarAudioDevice("mic-1", "Arctis Mic", "capture")],
        new Dictionary<string, string?>
        {
            ["game"] = "output-1",
            ["chatRender"] = "output-1",
            ["media"] = "output-1",
            ["aux"] = "output-2",
            ["chatCapture"] = "mic-1",
        });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static AutomationPeer? CreateAutomationPeer(UIElement element) =>
        (AutomationPeer?)typeof(UIElement)
            .GetMethod("OnCreateAutomationPeer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(element, null);

    private static void Equal<T>(T expected, T actual, string? detail = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected {expected}, got {actual}{(detail is null ? string.Empty : $" ({detail})")}");
        }
    }

    private static void Contains(string actual, string expected)
    {
        if (!actual.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"expected '{actual}' to contain '{expected}'");
        }
    }

    private sealed class WindowFixture : IDisposable
    {
        private readonly MixerViewModel _viewModel;
        private readonly string _settingsRoot;
        public MainWindow Window { get; }
        public Style MuteStyle { get; }
        public Style ChannelComboStyle { get; }
        public Slider[] VerticalSliders { get; }

        public WindowFixture()
        {
            var client = new FakeSonarClient(State());
            _viewModel = new MixerViewModel(client);
            _viewModel.RefreshAsync().GetAwaiter().GetResult();
            _settingsRoot = Path.Combine(Path.GetTempPath(), "SonarMiniMixerAppTests", Guid.NewGuid().ToString("N"));
            Window = new MainWindow(_viewModel, new SettingsStore(Path.Combine(_settingsRoot, "settings.json")));
            MuteStyle = (Style)Window.Resources["MuteButton"];
            ChannelComboStyle = (Style)Window.Resources["ChannelComboBox"];
            var slider = new Slider
            {
                Style = (Style)Window.Resources["VerticalFader"],
                DataContext = new AccentSource("#48F27A"),
                Value = 50,
            };
            AutomationProperties.SetName(slider, "Game volume");
            slider.Measure(new Size(44, 132));
            slider.Arrange(new Rect(0, 0, 44, 132));
            slider.ApplyTemplate();
            slider.UpdateLayout();
            VerticalSliders = [slider];
        }

        public void Dispose()
        {
            _viewModel.Dispose();
            if (Directory.Exists(_settingsRoot)) Directory.Delete(_settingsRoot, true);
        }
    }

    private sealed record AccentSource(string Accent);

    private sealed class FakeSonarClient(MixerState state) : ISonarClient
    {
        public MixerState State { get; set; } = state;
        public bool FailVolumeWrites { get; set; }
        public TimeSpan VolumeWriteDelay { get; set; }
        public bool FailOptionReads { get; set; }
        public List<(string Channel, double Volume)> VolumeWrites { get; } = [];
        public List<TimeSpan> VolumeWriteTimes { get; } = [];
        private readonly System.Diagnostics.Stopwatch _volumeWriteWatch = System.Diagnostics.Stopwatch.StartNew();
        public List<(string Channel, bool Muted)> MuteWrites { get; } = [];
        public List<double> ChatMixWrites { get; } = [];
        public Dictionary<string, SonarPresetCatalog> PresetCatalogs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SonarDeviceRouting Routing { get; set; } = new([], [], new Dictionary<string, string?>());
        public List<(string Channel, Guid Preset)> PresetWrites { get; } = [];
        public List<(string Channel, string Device)> DeviceWrites { get; } = [];
        public HashSet<string> FailDeviceChannels { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int StateReads { get; private set; }

        public Task<MixerState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            StateReads++;
            return Task.FromResult(State);
        }

        public int PresetReads { get; private set; }

        public Task<SonarPresetCatalog> GetPresetsAsync(string channel, CancellationToken cancellationToken = default)
        {
            PresetReads++;
            return FailOptionReads
                ? Task.FromException<SonarPresetCatalog>(new InvalidOperationException("options unavailable"))
                : Task.FromResult(PresetCatalogs.TryGetValue(channel, out var catalog) ? catalog : new SonarPresetCatalog(channel, [], null));
        }

        public Task<IReadOnlyDictionary<string, Guid?>> GetSelectedPresetIdsAsync(CancellationToken cancellationToken = default)
        {
            if (FailOptionReads)
                return Task.FromException<IReadOnlyDictionary<string, Guid?>>(new InvalidOperationException("options unavailable"));
            IReadOnlyDictionary<string, Guid?> selected = PresetCatalogs.ToDictionary(
                pair => pair.Key, pair => pair.Value.SelectedId, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(selected);
        }

        public Task SelectPresetAsync(string channel, Guid presetId, CancellationToken cancellationToken = default)
        {
            PresetWrites.Add((channel, presetId));
            return Task.CompletedTask;
        }

        public int RoutingReads { get; set; }

        public Task<SonarDeviceRouting> GetDeviceRoutingAsync(CancellationToken cancellationToken = default)
        {
            RoutingReads++;
            return FailOptionReads
                ? Task.FromException<SonarDeviceRouting>(new InvalidOperationException("options unavailable"))
                : Task.FromResult(Routing);
        }

        public Task SetChannelDeviceAsync(string channel, string deviceId, CancellationToken cancellationToken = default)
        {
            if (FailDeviceChannels.Contains(channel)) throw new InvalidOperationException("device write failed");
            DeviceWrites.Add((channel, deviceId));
            return Task.CompletedTask;
        }

        public async Task SetVolumeAsync(string channel, double volume, CancellationToken cancellationToken = default)
        {
            if (FailVolumeWrites) throw new InvalidOperationException("rejected");
            VolumeWriteTimes.Add(_volumeWriteWatch.Elapsed);
            VolumeWrites.Add((channel, volume));
            if (VolumeWriteDelay > TimeSpan.Zero) await Task.Delay(VolumeWriteDelay, cancellationToken);
        }

        public Task SetMuteAsync(string channel, bool muted, CancellationToken cancellationToken = default)
        {
            MuteWrites.Add((channel, muted));
            return Task.CompletedTask;
        }

        public Task SetChatMixAsync(double balance, CancellationToken cancellationToken = default)
        {
            ChatMixWrites.Add(balance);
            return Task.CompletedTask;
        }
    }
}
