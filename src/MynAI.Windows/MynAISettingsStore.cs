using System.IO;
using System.Text.Json;
using System.Text;

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

        // Write beside the current file and replace atomically so an interrupted
        // save does not truncate the last valid settings JSON.
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // A leftover temporary file is recoverable; preserve the original save error.
            }
        }
    }
}
