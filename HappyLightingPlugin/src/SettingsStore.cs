using Newtonsoft.Json;

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
        var json = await Task.Run(() => File.ReadAllText(_path), cancellationToken);
        return JsonConvert.DeserializeObject<PluginSettings>(json) ?? new PluginSettings();
    }

    public async Task SaveAsync(PluginSettings settings, CancellationToken cancellationToken)
    {
        var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
        await Task.Run(() => File.WriteAllText(_path, json), cancellationToken);
    }
}
