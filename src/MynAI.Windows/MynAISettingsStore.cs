using System.IO;
using System.Text.Json;

namespace MynAI.Windows;

public sealed class MynAISettingsStore
{
    private readonly string _path;

    public MynAISettingsStore()
    {
        var directory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "MynAI");

        Directory.CreateDirectory(
            directory);

        _path =
            Path.Combine(
                directory,
                "settings.json");
    }

    public MynAISettings Load()
    {
        if (!File.Exists(_path))
            return new MynAISettings();

        try
        {
            var json =
                File.ReadAllText(_path);

            return JsonSerializer.Deserialize<MynAISettings>(
                       json)
                   ?? new MynAISettings();
        }
        catch
        {
            return new MynAISettings();
        }
    }

    public void Save(
        MynAISettings settings)
    {
        var json =
            JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

        File.WriteAllText(
            _path,
            json);
    }
}
