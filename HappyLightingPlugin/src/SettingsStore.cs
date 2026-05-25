using System.Text.Json;

namespace HappyLightingPlugin;

public sealed class SettingsStore
{
    private readonly string _path;

    public SettingsStore(string rootPath)
    {
        Directory.CreateDirectory(rootPath);
        _path = Path.Combine(rootPath, "HappyLightingPlugin.settings.json");
    }

    public async Task<PluginSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new PluginSettings();
        await using var fs = File.OpenRead(_path);
        return (await JsonSerializer.DeserializeAsync<PluginSettings>(fs, cancellationToken: cancellationToken)) ?? new PluginSettings();
    }

    public async Task SaveAsync(PluginSettings settings, CancellationToken cancellationToken)
    {
        await using var fs = File.Create(_path);
        await JsonSerializer.SerializeAsync(fs, settings, cancellationToken: cancellationToken, options: new JsonSerializerOptions { WriteIndented = true });
    }
}
