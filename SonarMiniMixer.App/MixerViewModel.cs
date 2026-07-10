using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using SonarMiniMixer.Core;

namespace SonarMiniMixer.App;

public sealed class MixerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISonarClient _client;
    private readonly DispatcherTimer _pollTimer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _isApplyingState;
    private string _status = "Connecting";
    private string _statusDetail = "Finding SteelSeries Sonar...";
    private bool _connected;
    private bool _canControl;
    private double _chatMix;

    public ObservableCollection<ChannelViewModel> Channels { get; } = [];
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public bool Connected { get => _connected; private set => Set(ref _connected, value); }
    public bool CanControl { get => _canControl; private set => Set(ref _canControl, value); }
    public double ChatMix
    {
        get => _chatMix;
        set
        {
            if (!Set(ref _chatMix, value) || _isApplyingState || !CanControl) return;
            _ = WriteChatMixAsync(value);
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
                if (!state.Channels.Any(x => x.Id == Channels[i].Id)) Channels.RemoveAt(i);
            ChatMix = state.ChatMix * 100;
            CanControl = state.CanControl;
            Connected = true;
            Status = state.CanControl ? "Sonar connected" : $"{state.Mode} mode";
            StatusDetail = state.CanControl ? "Live · Classic mixer" : "Switch Sonar to Classic mode to make changes";
        }
        catch (Exception exception)
        {
            Connected = false;
            CanControl = false;
            Status = "Sonar unavailable";
            StatusDetail = exception is SonarConnectionException ? exception.Message : "SteelSeries GG did not respond.";
        }
        finally { _isApplyingState = false; _refreshGate.Release(); }
    }

    internal async Task SetVolumeAsync(ChannelViewModel channel, double percent)
    {
        if (!CanControl) return;
        try { await _client.SetVolumeAsync(channel.Id, Math.Clamp(percent / 100, 0, 1)); }
        catch { await RefreshAsync(); }
    }

    internal async Task ToggleMuteAsync(ChannelViewModel channel)
    {
        if (!CanControl) return;
        var target = !channel.Muted;
        channel.Muted = target;
        try { await _client.SetMuteAsync(channel.Id, target); }
        catch { await RefreshAsync(); }
    }

    private async Task WriteChatMixAsync(double percent)
    {
        try { await _client.SetChatMixAsync(Math.Clamp(percent / 100, -1, 1)); }
        catch { await RefreshAsync(); }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)); return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Dispose() { _pollTimer.Stop(); _refreshGate.Dispose(); if (_client is IDisposable disposable) disposable.Dispose(); }
}

public sealed class ChannelViewModel : INotifyPropertyChanged
{
    private readonly MixerViewModel _owner;
    private double _volume;
    private bool _muted;
    private CancellationTokenSource? _volumeDebounce;

    public string Id { get; }
    public string Name { get; }
    public string Accent { get; }
    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, value)) return;
            OnPropertyChanged(nameof(VolumeText));
            _volumeDebounce?.Cancel();
            _volumeDebounce = new CancellationTokenSource();
            _ = DebounceVolumeAsync(value, _volumeDebounce.Token);
        }
    }
    public string VolumeText => $"{Math.Round(Volume):0}";
    public bool Muted { get => _muted; set { if (Set(ref _muted, value)) OnPropertyChanged(nameof(MuteGlyph)); } }
    public string MuteGlyph => Muted ? "×" : "◖";

    internal ChannelViewModel(MixerChannel source, MixerViewModel owner)
    {
        Id = source.Id; Name = source.Name; Accent = source.Accent; _owner = owner;
        _volume = source.Volume * 100; _muted = source.Muted;
    }

    internal void Apply(MixerChannel source)
    {
        _volumeDebounce?.Cancel();
        _volume = source.Volume * 100; _muted = source.Muted;
        OnPropertyChanged(nameof(Volume)); OnPropertyChanged(nameof(VolumeText)); OnPropertyChanged(nameof(Muted)); OnPropertyChanged(nameof(MuteGlyph));
    }

    public Task ToggleMuteAsync() => _owner.ToggleMuteAsync(this);

    private async Task DebounceVolumeAsync(double value, CancellationToken token)
    {
        try { await Task.Delay(90, token); await _owner.SetVolumeAsync(this, value); }
        catch (OperationCanceledException) { }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(propertyName); return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
