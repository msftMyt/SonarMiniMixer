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
    private DateTimeOffset _nextOptionsRefresh;

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
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
    }

    public async Task StartAsync()
    {
        await RefreshAsync();
        _pollTimer.Start();
    }

    public async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            var state = await _client.GetStateAsync();
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
            if (state.CanControl && DateTimeOffset.UtcNow >= _nextOptionsRefresh)
            {
                _nextOptionsRefresh = DateTimeOffset.UtcNow.AddSeconds(30);
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

        var failures = new List<string>();
        foreach (var channelId in PlaybackOutputChannels)
        {
            var channel = Channels.FirstOrDefault(candidate => candidate.Id == channelId);
            if (channel is null || string.Equals(channel.SelectedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                await _client.SetChannelDeviceAsync(channel.Id, deviceId);
                channel.SetSelectedDevice(deviceId);
            }
            catch { failures.Add(channel.Name); }
        }

        RefreshMasterOutputSelection();
        if (failures.Count == 0)
        {
            var deviceName = master.Devices.First(device =>
                string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase)).Name;
            StatusDetail = $"Playback outputs changed to {deviceName}";
            return;
        }

        Status = "Output partially updated";
        StatusDetail = $"Could not update {string.Join(", ", failures)}. Other playback channels were changed.";
    }

    private async Task RefreshChannelOptionsAsync()
    {
        var channels = Channels.Where(channel => channel.HasChannelOptions).ToArray();
        var routingTask = _client.GetDeviceRoutingAsync();
        var presetTasks = channels.ToDictionary(channel => channel.Id, channel => _client.GetPresetsAsync(channel.Id));
        await Task.WhenAll(presetTasks.Values.Append((Task)routingTask));
        var routing = await routingTask;
        foreach (var channel in channels)
        {
            var devices = channel.Id == "chatCapture" ? routing.MicrophoneDevices : routing.OutputDevices;
            routing.ChannelDeviceIds.TryGetValue(channel.Id, out var deviceId);
            channel.ApplyOptions(await presetTasks[channel.Id], devices, deviceId);
        }
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
        _pollTimer.Stop();
        _chatMixDebounce?.Cancel();
        _chatMixDebounce?.Dispose();
        foreach (var channel in Channels) channel.Dispose();
        _refreshGate.Dispose();
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
    public bool HasChannelOptions => !IsMaster;
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
            if (!Set(ref _volume, value)) return;
            OnPropertyChanged(nameof(VolumeText));
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

    public Task ToggleMuteAsync() => _owner.ToggleMuteAsync(this);

    internal void ApplyOptions(
        SonarPresetCatalog presetCatalog,
        IReadOnlyList<SonarAudioDevice> devices,
        string? selectedDeviceId)
    {
        Replace(Presets, presetCatalog.Items);
        Replace(Devices, devices);
        SetSelectedPreset(presetCatalog.SelectedId);
        SetSelectedDevice(selectedDeviceId);
    }

    internal void ApplyMasterOutputOptions(IReadOnlyList<SonarAudioDevice> devices)
    {
        Replace(Devices, devices);
        _masterOutputOptionsLoaded = true;
        OnPropertyChanged(nameof(SelectedDeviceName));
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
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
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

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(propertyName); return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Dispose() { _volumeDebounce?.Cancel(); _volumeDebounce?.Dispose(); }
}
