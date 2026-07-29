using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using SonarMiniMixer.Core;

namespace SonarMiniMixer.App;

public sealed class MixerViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly string[] PlaybackOutputChannels = ["game", "chatRender", "media", "aux"];
    private readonly ISonarClient _client;
    private readonly DispatcherTimer _pollTimer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _isApplyingState;
    private string _status = "Connecting";
    private string _statusDetail = "Finding SteelSeries Sonar...";
    private bool _connected;
    private bool _canControl;
    private bool _canControlChatMix;
    private double _chatMix;
    private CancellationTokenSource? _chatMixDebounce;
    private const int OptionsRefreshSeconds = 30;
    private DateTimeOffset _nextOptionsRefresh;
    private bool _presetsLoaded;
    private SonarEventStream? _events;
    private CancellationTokenSource? _routingDebounce;
    private bool _suppressRoutingRefresh;
    private bool _surfaceVisible = true;
    private bool _disposed;
    private readonly object _masterWriteLock = new();
    private Dictionary<string, double>? _masterRatios;
    private CancellationTokenSource? _masterGestureSettle;
    private double? _pendingMasterPercent;
    private bool _masterWriterRunning;
    private static readonly TimeSpan MasterWriteInterval = TimeSpan.FromMilliseconds(80);

    public ObservableCollection<ChannelViewModel> Channels { get; } = [];
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public bool Connected { get => _connected; private set => Set(ref _connected, value); }
    public bool CanControl { get => _canControl; private set => Set(ref _canControl, value); }
    public bool CanControlChatMix { get => _canControlChatMix; private set => Set(ref _canControlChatMix, value); }
    public double ChatMix
    {
        get => _chatMix;
        set
        {
            if (_isApplyingState)
            {
                Set(ref _chatMix, value);
                return;
            }
            if (!CanControlChatMix || !Set(ref _chatMix, value)) return;
            _chatMixDebounce?.Cancel();
            var debounce = new CancellationTokenSource();
            _chatMixDebounce = debounce;
            _ = DebounceChatMixAsync(value, debounce);
        }
    }

    public MixerViewModel(ISonarClient client)
    {
        _client = client;
        // Sonar pushes changes over its event socket, so polling is only a slow
        // safety net for missed events / reconnects rather than the primary path.
        // Visible fallback cadence matches the original release's responsiveness.
        // Hidden windows do not poll at all; Sonar's socket remains authoritative.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
    }

    /// <summary>Attaches a live Sonar push feed so external changes appear immediately.</summary>
    public void AttachEventStream(SonarEventStream stream)
    {
        _events = stream;
        stream.EventReceived += OnSonarEvent;
        stream.ConnectionChanged += OnEventStreamConnectionChanged;
    }

    internal void OnEventStreamConnectionChanged(bool connected)
    {
        if (_disposed) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnEventStreamConnectionChanged(connected));
            return;
        }
        if (!connected)
        {
            Connected = false;
            CanControl = false;
            CanControlChatMix = false;
            Status = "Sonar reconnecting";
            StatusDetail = "Waiting for SteelSeries GG to restart Sonar...";
            return;
        }
        // A fresh socket means Sonar restarted or moved ports. Force the next state read
        // to rebuild routing/options immediately rather than waiting up to 30 seconds.
        _nextOptionsRefresh = DateTimeOffset.MinValue;
        _ = RefreshAsync();
    }

    internal void OnSonarEvent(SonarEvent sonarEvent)
    {
        if (_disposed) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnSonarEvent(sonarEvent));
            return;
        }

        switch (sonarEvent.Kind)
        {
            case SonarEventKind.ChatMixChanged:
                // Sonar reports ChatMix as unavailable while playback channels are
                // split across devices; mirror that instead of guessing.
                CanControlChatMix = string.Equals(sonarEvent.ChatMixState, "enabled", StringComparison.OrdinalIgnoreCase);
                if (_chatMixDebounce is null)
                {
                    _isApplyingState = true;
                    try { ChatMix = sonarEvent.Balance * 100; }
                    finally { _isApplyingState = false; }
                }
                break;
            case SonarEventKind.RoutingChanged:
            case SonarEventKind.DevicesChanged:
                if (_suppressRoutingRefresh) break;
                _ = RefreshRoutingSoonAsync();
                break;
            case SonarEventKind.VolumesChanged:
                if (_suppressRoutingRefresh) break;
                if (sonarEvent.VolumePayload is { } payload)
                {
                    try
                    {
                        var pushed = SonarStateParser.Parse(payload, "{}", "\"classic\"");
                        _isApplyingState = true;
                        try
                        {
                            foreach (var channel in pushed.Channels)
                                Channels.FirstOrDefault(existing => existing.Id == channel.Id)?.Apply(channel);
                        }
                        finally { _isApplyingState = false; }
                    }
                    catch (SonarConnectionException) { /* malformed push; safety poll will recover */ }
                }
                else _ = RefreshAsync();
                break;
        }
    }

    /// <summary>
    /// Coalesces the burst of routing events Sonar emits during a multi-channel
    /// change into a single refresh once the dust settles.
    /// </summary>
    private async Task<IReadOnlyList<string>> WriteEachAsync(IEnumerable<ChannelViewModel> channels, string deviceId)
    {
        var failed = new List<string>();
        foreach (var channel in channels)
        {
            try { await _client.SetChannelDeviceAsync(channel.Id, deviceId); }
            catch { failed.Add(channel.Id); }
        }
        return failed;
    }

    private async Task RefreshRoutingSoonAsync()
    {
        _routingDebounce?.Cancel();
        var debounce = new CancellationTokenSource();
        _routingDebounce = debounce;
        try
        {
            await Task.Delay(120, debounce.Token);
            if (_suppressRoutingRefresh) return;
            await RefreshChannelOptionsAsync();
        }
        catch (OperationCanceledException) { }
        catch { /* transient; the poll safety net will catch up */ }
        finally
        {
            if (ReferenceEquals(_routingDebounce, debounce)) _routingDebounce = null;
            debounce.Dispose();
        }
    }

    public async Task StartAsync()
    {
        await RefreshAsync();
        // Subscribe only after the initial channel/catalog state exists. Starting the
        // socket earlier lets a routing event race initialization and cache an empty UI.
        _events?.Start();
        if (_surfaceVisible) _pollTimer.Start();
    }

    /// <summary>
    /// Tracks whether the mixer surface is on screen. A hidden tray popup does not need
    /// to poll: Sonar's push socket already reports every change, so polling only burns
    /// HTTP and allocations no one can see.
    /// </summary>
    public void SetSurfaceVisible(bool visible)
    {
        if (_surfaceVisible == visible) return;
        _surfaceVisible = visible;
        if (visible) _pollTimer.Start();
        else _pollTimer.Stop();
    }

    /// <summary>Becomes visible and immediately resyncs so nothing on screen is stale.</summary>
    public async Task SetSurfaceVisibleAsync(bool visible)
    {
        SetSurfaceVisible(visible);
        if (visible) await RefreshAsync();
    }

    public void PollForTest()
    {
        if (!_surfaceVisible) return;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed || !await _refreshGate.WaitAsync(0)) return;
        try
        {
            if (_disposed) return;
            var state = await _client.GetStateAsync();
            if (_disposed) return;
            _isApplyingState = true;
            foreach (var channel in state.Channels)
            {
                var existing = Channels.FirstOrDefault(item => item.Id == channel.Id);
                if (existing is null)
                {
                    existing = new ChannelViewModel(channel, this);
                    Channels.Add(existing);
                }
                else existing.Apply(channel);
            }
            for (var i = Channels.Count - 1; i >= 0; i--)
            {
                if (!state.Channels.Any(x => x.Id == Channels[i].Id))
                {
                    Channels[i].Dispose();
                    Channels.RemoveAt(i);
                }
            }
            if (_chatMixDebounce is null) ChatMix = state.ChatMix * 100;
            CanControl = state.CanControl;
            CanControlChatMix = state.CanControlChatMix;
            Connected = true;
            Status = state.CanControl ? "Sonar connected" : $"{state.Mode} mode";
            StatusDetail = state.CanControl ? "Live · Classic mixer" : "Switch Sonar to Classic mode to make changes";
            // Sonar does not broadcast preset selection changes on /sock. Refresh only
            // the five selected IDs (not the 380-item catalogs) while the UI is visible.
            if (state.CanControl && _presetsLoaded)
            {
                try { await RefreshSelectedPresetsAsync(); }
                catch { /* optional surface; keep core controls live */ }
            }
            if (state.CanControl && DateTimeOffset.UtcNow >= _nextOptionsRefresh)
            {
                _nextOptionsRefresh = DateTimeOffset.UtcNow.AddSeconds(OptionsRefreshSeconds);
                try { await RefreshChannelOptionsAsync(); }
                catch { StatusDetail = "Live · presets and routing options unavailable"; }
            }
        }
        catch (Exception exception)
        {
            Connected = false;
            CanControl = false;
            CanControlChatMix = false;
            Status = "Sonar unavailable";
            StatusDetail = exception is SonarConnectionException ? exception.Message : "SteelSeries GG did not respond.";
        }
        finally { _isApplyingState = false; _refreshGate.Release(); }
    }

    internal Task SetVolumeAsync(ChannelViewModel channel, double percent)
    {
        if (!CanControl) throw new SonarConnectionException("Sonar is not accepting Classic mixer changes.");
        return _client.SetVolumeAsync(channel.Id, Math.Clamp(percent / 100, 0, 1));
    }

    internal void SetMasterVolume(ChannelViewModel master, double previousPercent, double percent)
    {
        if (!CanControl || !master.IsMaster) return;

        if (_masterRatios is null)
        {
            _masterRatios = PlaybackOutputChannels.ToDictionary(
                id => id,
                id => previousPercent == 0
                    ? 1
                    : (Channels.FirstOrDefault(channel => channel.Id == id)?.Volume ?? 0) / previousPercent,
                StringComparer.OrdinalIgnoreCase);
        }
        if (percent == 0)
            foreach (var id in PlaybackOutputChannels) _masterRatios[id] = 1;

        foreach (var id in PlaybackOutputChannels)
        {
            var channel = Channels.FirstOrDefault(candidate => candidate.Id == id);
            if (channel is not null)
                channel.ApplyMasterVolume(Math.Clamp(_masterRatios[id] * percent, 0, 100));
        }

        QueueMasterWrite(percent);
        _masterGestureSettle?.Cancel();
        _masterGestureSettle?.Dispose();
        var settle = new CancellationTokenSource();
        _masterGestureSettle = settle;
        _ = SettleMasterGestureAsync(settle);
    }

    private void QueueMasterWrite(double percent)
    {
        var start = false;
        lock (_masterWriteLock)
        {
            _pendingMasterPercent = percent;
            if (!_masterWriterRunning)
            {
                _masterWriterRunning = true;
                start = true;
            }
        }
        if (start) _ = RunMasterWriterAsync();
    }

    private async Task RunMasterWriterAsync()
    {
        while (!_disposed)
        {
            double percent;
            lock (_masterWriteLock)
            {
                if (_pendingMasterPercent is not double next)
                {
                    _masterWriterRunning = false;
                    return;
                }
                percent = next;
                _pendingMasterPercent = null;
            }

            var interval = System.Diagnostics.Stopwatch.StartNew();
            try { await _client.SetVolumeAsync("master", Math.Clamp(percent / 100, 0, 1)); }
            catch
            {
                lock (_masterWriteLock)
                {
                    _pendingMasterPercent = null;
                    _masterWriterRunning = false;
                }
                await RecoverFromWriteFailureAsync("Master volume");
                return;
            }
            var remaining = MasterWriteInterval - interval.Elapsed;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        }
    }

    private async Task SettleMasterGestureAsync(CancellationTokenSource settle)
    {
        try { await Task.Delay(140, settle.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_masterGestureSettle, settle))
            {
                _masterGestureSettle = null;
                _masterRatios = null;
            }
            settle.Dispose();
        }
    }

    internal async Task ToggleMuteAsync(ChannelViewModel channel)
    {
        if (!CanControl) return;
        var target = !channel.Muted;
        channel.Muted = target;
        try { await _client.SetMuteAsync(channel.Id, target); }
        catch { await RecoverFromWriteFailureAsync($"{channel.Name} mute"); }
    }

    public async Task SelectPresetAsync(ChannelViewModel channel, Guid presetId)
    {
        if (!CanControl || !channel.HasChannelOptions || channel.SelectedPresetId == presetId) return;
        try
        {
            await _client.SelectPresetAsync(channel.Id, presetId);
            channel.SetSelectedPreset(presetId);
            StatusDetail = $"{channel.Name} preset updated";
        }
        catch { await RecoverFromWriteFailureAsync($"{channel.Name} preset"); }
    }

    public async Task SelectDeviceAsync(ChannelViewModel channel, string deviceId)
    {
        if (!CanControl || !channel.HasChannelOptions || channel.SelectedDeviceId == deviceId) return;
        try
        {
            await _client.SetChannelDeviceAsync(channel.Id, deviceId);
            channel.SetSelectedDevice(deviceId);
            RefreshMasterOutputSelection();
            StatusDetail = $"{channel.Name} device updated";
        }
        catch { await RecoverFromWriteFailureAsync($"{channel.Name} device"); }
    }

    public async Task SelectMasterOutputAsync(ChannelViewModel master, string deviceId)
    {
        if (!CanControl || !master.IsMaster || !master.Devices.Any(device =>
                string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase))) return;

        // Sonar emits a routing-event burst per channel during a fan-out. Ignore our
        // own echo while writing, then reconcile once from the authoritative state.
        _suppressRoutingRefresh = true;
        _pollTimer.Stop();
        _nextOptionsRefresh = DateTimeOffset.UtcNow.AddSeconds(OptionsRefreshSeconds);

        var pending = PlaybackOutputChannels
            .Select(channelId => Channels.FirstOrDefault(candidate => candidate.Id == channelId))
            .Where(channel => channel is not null &&
                !string.Equals(channel.SelectedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            .Select(channel => channel!)
            .ToArray();

        if (pending.Length == 0)
        {
            master.SetSelectedDevice(deviceId);
            _suppressRoutingRefresh = false;
            _pollTimer.Start();
            return;
        }

        // Apply the selection to the UI immediately, the way the SteelSeries mixer does,
        // then write in the background and roll back only what actually failed.
        var previous = pending.ToDictionary(channel => channel.Id, channel => channel.SelectedDeviceId);
        foreach (var channel in pending) channel.SetSelectedDevice(deviceId);
        master.SetSelectedDevice(deviceId);

        IReadOnlyList<string> failedIds;
        try
        {
            failedIds = _client is SonarClient bulk
                ? await bulk.SetChannelDevicesAsync(pending.Select(channel => channel.Id), deviceId)
                : await WriteEachAsync(pending, deviceId);
        }
        catch { failedIds = pending.Select(channel => channel.Id).ToArray(); }

        var failures = new List<string>();
        foreach (var channel in pending)
        {
            if (!failedIds.Contains(channel.Id, StringComparer.OrdinalIgnoreCase)) continue;
            channel.SetSelectedDevice(previous[channel.Id]);
            failures.Add(channel.Name);
        }

        _suppressRoutingRefresh = false;
        _pollTimer.Start();
        RefreshMasterOutputSelection();
        // Sonar's own routing/ChatMix events reconcile the final state; the debounced
        // handler collapses that burst into a single refresh.
        (_client as SonarClient)?.InvalidateRoutingCache();
        _ = RefreshRoutingSoonAsync();
        if (failures.Count == 0)
        {
            var deviceName = master.Devices.First(device =>
                string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase)).Name;
            StatusDetail = $"Playback outputs changed to {deviceName}";
            return;
        }

        Status = "Output partially updated";
        StatusDetail = $"Could not update {string.Join(", ", failures)}. Other playback channels were changed.";
        _nextOptionsRefresh = DateTimeOffset.MinValue;
    }

    public Task RefreshChannelOptionsForTestAsync() => RefreshChannelOptionsAsync();

    private async Task RefreshSelectedPresetsAsync()
    {
        var selected = await _client.GetSelectedPresetIdsAsync();
        foreach (var channel in Channels.Where(channel => channel.HasChannelOptions))
        {
            if (!selected.TryGetValue(channel.Id, out var presetId)) continue;
            if (presetId is Guid id && !channel.Presets.Any(preset => preset.Id == id))
            {
                // A custom preset was created/selected in GG after our initial catalog load.
                // Refresh only that channel's catalog instead of all 380 preset entries.
                channel.ApplyPresetCatalog(await _client.GetPresetsAsync(channel.Id));
            }
            else channel.SetSelectedPreset(presetId);
        }
    }

    private async Task RefreshChannelOptionsAsync()
    {
        var channels = Channels.Where(channel => channel.HasChannelOptions).ToArray();
        // A socket event can arrive during startup/shutdown; never treat an empty
        // channel set as a successfully-loaded preset catalog.
        if (channels.Length == 0) return;
        var routingTask = _client.GetDeviceRoutingAsync();

        // Preset catalogs are large (hundreds of entries) and effectively static, so only
        // pull the full list once. Routing is cheap and is what actually changes at runtime.
        var presetTasks = _presetsLoaded
            ? null
            : channels.ToDictionary(channel => channel.Id, channel => _client.GetPresetsAsync(channel.Id));

        if (presetTasks is null) await routingTask;
        else await Task.WhenAll(presetTasks.Values.Append((Task)routingTask));

        var routing = await routingTask;
        foreach (var channel in channels)
        {
            var devices = channel.Id == "chatCapture" ? routing.MicrophoneDevices : routing.OutputDevices;
            routing.ChannelDeviceIds.TryGetValue(channel.Id, out var deviceId);
            var stalled = routing.StalledChannels.Contains(channel.Id);
            if (presetTasks is null) channel.ApplyRouting(devices, deviceId, stalled);
            else channel.ApplyOptions(await presetTasks[channel.Id], devices, deviceId, stalled);
        }
        _presetsLoaded = true;
        var master = Channels.FirstOrDefault(channel => channel.IsMaster);
        master?.ApplyMasterOutputOptions(routing.OutputDevices);
        RefreshMasterOutputSelection();
    }

    private void RefreshMasterOutputSelection()
    {
        var master = Channels.FirstOrDefault(channel => channel.IsMaster);
        if (master is null) return;
        var selectedIds = PlaybackOutputChannels
            .Select(channelId => Channels.FirstOrDefault(channel => channel.Id == channelId)?.SelectedDeviceId)
            .Where(deviceId => deviceId is not null)
            .ToArray();
        var selectedId = selectedIds.Length == PlaybackOutputChannels.Length &&
                         selectedIds.All(deviceId => string.Equals(deviceId, selectedIds[0], StringComparison.OrdinalIgnoreCase))
            ? selectedIds[0]
            : null;
        master.SetSelectedDevice(selectedId);
    }

    private async Task DebounceChatMixAsync(double percent, CancellationTokenSource debounce)
    {
        var failed = false;
        try
        {
            await Task.Delay(90, debounce.Token);
            if (!CanControlChatMix) throw new SonarConnectionException("ChatMix is not currently available.");
            await _client.SetChatMixAsync(Math.Clamp(percent / 100, -1, 1), debounce.Token);
        }
        catch (OperationCanceledException) { }
        catch { failed = true; }
        finally
        {
            if (ReferenceEquals(_chatMixDebounce, debounce)) _chatMixDebounce = null;
            debounce.Dispose();
        }
        if (failed) await RecoverFromWriteFailureAsync("ChatMix");
    }

    internal async Task RecoverFromWriteFailureAsync(string control)
    {
        await RefreshAsync();
        if (!Connected) return;
        Status = "Change failed";
        StatusDetail = $"Could not update {control}. Check Sonar and try again.";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)); return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _routingDebounce?.Cancel();
        _routingDebounce?.Dispose();
        if (_events is not null)
        {
            _events.EventReceived -= OnSonarEvent;
            _events.ConnectionChanged -= OnEventStreamConnectionChanged;
            _ = _events.DisposeAsync();
            _events = null;
        }
        _chatMixDebounce?.Cancel();
        _chatMixDebounce?.Dispose();
        _masterGestureSettle?.Cancel();
        _masterGestureSettle?.Dispose();
        lock (_masterWriteLock) _pendingMasterPercent = null;
        foreach (var channel in Channels) channel.Dispose();
        // Do not dispose _refreshGate here: an already-running RefreshAsync owns it
        // and must still release it safely while shutdown drains.
        if (_client is IDisposable disposable) disposable.Dispose();
    }
}

public sealed class ChannelViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MixerViewModel _owner;
    private double _volume;
    private bool _muted;
    private Guid? _selectedPresetId;
    private string? _selectedDeviceId;
    private bool _masterOutputOptionsLoaded;
    private CancellationTokenSource? _volumeDebounce;

    public string Id { get; }
    public string Name { get; }
    public string Accent { get; }
    public bool IsMaster => Id.Equals("master", StringComparison.OrdinalIgnoreCase);
    public bool IsMicrophone => Id.Equals("chatCapture", StringComparison.OrdinalIgnoreCase);
    public bool HasChannelOptions => !IsMaster;

    private bool _routeStalled;
    /// <summary>True when Sonar reports this channel's route is not passing audio.</summary>
    public bool RouteStalled
    {
        get => _routeStalled;
        internal set
        {
            if (_routeStalled == value) return;
            _routeStalled = value;
            OnPropertyChanged(nameof(RouteStalled));
            OnPropertyChanged(nameof(RouteStatusTip));
        }
    }

    public string RouteStatusTip => _routeStalled
        ? $"{Name} {(IsMicrophone ? "input" : "output")} is not running. Sonar is not passing audio on this route."
        : SelectedDeviceName;
    public string DeviceRoleLabel => Id.Equals("chatCapture", StringComparison.OrdinalIgnoreCase) ? "IN" : "OUT";
    public ObservableCollection<SonarPreset> Presets { get; } = [];
    public ObservableCollection<SonarAudioDevice> Devices { get; } = [];
    public Guid? SelectedPresetId => _selectedPresetId;
    public string? SelectedDeviceId => _selectedDeviceId;
    public string SelectedPresetName => Presets.FirstOrDefault(preset => preset.Id == _selectedPresetId)?.Name ?? "Select EQ";
    public string SelectedDeviceName => Devices.FirstOrDefault(device =>
        string.Equals(device.Id, _selectedDeviceId, StringComparison.OrdinalIgnoreCase))?.Name ??
        (IsMaster && _masterOutputOptionsLoaded ? "Mixed outputs" : IsMaster ? "Quick output" : "Select device");
    public double Volume
    {
        get => _volume;
        set
        {
            if (!_owner.CanControl) return;
            var previous = _volume;
            if (!Set(ref _volume, value)) return;
            OnPropertyChanged(nameof(VolumeText));
            if (IsMaster)
            {
                HoldVolumeEditOpen();
                _owner.SetMasterVolume(this, previous, value);
                return;
            }
            _volumeDebounce?.Cancel();
            var debounce = new CancellationTokenSource();
            _volumeDebounce = debounce;
            _ = DebounceVolumeAsync(value, debounce);
        }
    }
    public string VolumeText => $"{Math.Round(Volume):0}";
    public bool Muted
    {
        get => _muted;
        set
        {
            if (!Set(ref _muted, value)) return;
            OnPropertyChanged(nameof(MuteGlyph));
            OnPropertyChanged(nameof(MuteAction));
        }
    }
    public string MuteGlyph => Muted ? "×" : "◖";
    public string MuteAction => $"{(Muted ? "Unmute" : "Mute")} {Name}";

    internal ChannelViewModel(MixerChannel source, MixerViewModel owner)
    {
        Id = source.Id; Name = source.Name; Accent = source.Accent; _owner = owner;
        _volume = source.Volume * 100; _muted = source.Muted;
    }

    internal void Apply(MixerChannel source)
    {
        if (_volumeDebounce is null && !EqualityComparer<double>.Default.Equals(_volume, source.Volume * 100))
        {
            _volume = source.Volume * 100;
            OnPropertyChanged(nameof(Volume));
            OnPropertyChanged(nameof(VolumeText));
        }
        if (_muted == source.Muted) return;
        _muted = source.Muted;
        OnPropertyChanged(nameof(Muted));
        OnPropertyChanged(nameof(MuteGlyph));
        OnPropertyChanged(nameof(MuteAction));
    }

    internal void ApplyMasterVolume(double value)
    {
        HoldVolumeEditOpen();
        if (EqualityComparer<double>.Default.Equals(_volume, value)) return;
        _volume = value;
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(VolumeText));
    }

    public Task ToggleMuteAsync() => _owner.ToggleMuteAsync(this);

    internal void ApplyPresetCatalog(SonarPresetCatalog presetCatalog)
    {
        Replace(Presets, presetCatalog.Items);
        SetSelectedPreset(presetCatalog.SelectedId);
    }

    internal void ApplyOptions(
        SonarPresetCatalog presetCatalog,
        IReadOnlyList<SonarAudioDevice> devices,
        string? selectedDeviceId,
        bool stalled = false)
    {
        ApplyPresetCatalog(presetCatalog);
        Replace(Devices, devices);
        SetSelectedDevice(selectedDeviceId);
        RouteStalled = stalled;
    }

    internal void ApplyMasterOutputOptions(IReadOnlyList<SonarAudioDevice> devices)
    {
        Replace(Devices, devices);
        _masterOutputOptionsLoaded = true;
        OnPropertyChanged(nameof(SelectedDeviceName));
    }

    /// <summary>Refreshes only device routing, leaving the cached preset catalog intact.</summary>
    internal void ApplyRouting(IReadOnlyList<SonarAudioDevice> devices, string? selectedDeviceId, bool stalled = false)
    {
        Replace(Devices, devices);
        SetSelectedDevice(selectedDeviceId);
        RouteStalled = stalled;
    }

    internal void SetSelectedPreset(Guid? presetId)
    {
        if (_selectedPresetId != presetId)
        {
            _selectedPresetId = presetId;
            OnPropertyChanged(nameof(SelectedPresetId));
        }
        OnPropertyChanged(nameof(SelectedPresetName));
    }

    internal void SetSelectedDevice(string? deviceId)
    {
        if (!string.Equals(_selectedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedDeviceId = deviceId;
            OnPropertyChanged(nameof(SelectedDeviceId));
        }
        else if (IsMaster) OnPropertyChanged(nameof(SelectedDeviceId));
        OnPropertyChanged(nameof(SelectedDeviceName));
        OnPropertyChanged(nameof(RouteStatusTip));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        // Clearing an ObservableCollection raises a Reset, which makes every bound
        // ComboBox drop its selection and closes an open dropdown. Only mutate when
        // the contents genuinely changed, and then patch in place.
        var items = source as IList<T> ?? source.ToList();
        if (target.Count == items.Count)
        {
            var identical = true;
            for (var i = 0; i < items.Count; i++)
            {
                if (EqualityComparer<T>.Default.Equals(target[i], items[i])) continue;
                identical = false;
                break;
            }
            if (identical) return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count) { if (!EqualityComparer<T>.Default.Equals(target[i], items[i])) target[i] = items[i]; }
            else target.Add(items[i]);
        }
        while (target.Count > items.Count) target.RemoveAt(target.Count - 1);
    }

    private async Task DebounceVolumeAsync(double value, CancellationTokenSource debounce)
    {
        var failed = false;
        try
        {
            await Task.Delay(90, debounce.Token);
            await _owner.SetVolumeAsync(this, value);
        }
        catch (OperationCanceledException) { }
        catch { failed = true; }
        finally
        {
            if (ReferenceEquals(_volumeDebounce, debounce)) _volumeDebounce = null;
            debounce.Dispose();
        }
        if (failed) await _owner.RecoverFromWriteFailureAsync($"{Name} volume");
    }

    private void HoldVolumeEditOpen()
    {
        _volumeDebounce?.Cancel();
        _volumeDebounce?.Dispose();
        var edit = new CancellationTokenSource();
        _volumeDebounce = edit;
        _ = ReleaseMasterEditAsync(edit);
    }

    private async Task ReleaseMasterEditAsync(CancellationTokenSource edit)
    {
        try { await Task.Delay(160, edit.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_volumeDebounce, edit)) _volumeDebounce = null;
            edit.Dispose();
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(propertyName); return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Dispose() { _volumeDebounce?.Cancel(); _volumeDebounce?.Dispose(); }
}
