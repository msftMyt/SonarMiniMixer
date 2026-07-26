using System.Text.Json;

namespace SonarMiniMixer.Core;

public sealed record AppSettings(
    bool StartWithWindows,
    bool Pinned,
    double Width,
    double Height,
    double? Left,
    double? Top)
{
    public static AppSettings Default { get; } = new(false, false, 864, 424, null, null);
}

public sealed class SettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SonarMiniMixer", "settings.json");

    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SettingsStore(string? path = null) => _path = path ?? DefaultPath;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path)) return AppSettings.Default;
            var result = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(_path, cancellationToken));
            return result is null ? AppSettings.Default : Sanitize(result);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return AppSettings.Default;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(Sanitize(settings), JsonOptions), cancellationToken);
        File.Move(tempPath, _path, true);
    }

    private static AppSettings Sanitize(AppSettings value) => value with
    {
        Width = Math.Clamp(value.Width, 640, 1180),
        Height = Math.Clamp(value.Height, 372, 650)
    };
}
