using System.IO;
using System.Text.Json;

namespace MynAI.Windows.Google;

public sealed class GoogleAccountStore
{
    private readonly string _path;

    public GoogleAccountStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "MynAI",
            "Google");

        Directory.CreateDirectory(directory);

        _path = Path.Combine(
            directory,
            "account.json");
    }

    public void Save(GoogleAccount account)
    {
        var json = JsonSerializer.Serialize(
            account,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(
            _path,
            json);
    }

    public GoogleAccount? Load()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            var json =
                File.ReadAllText(_path);

            return JsonSerializer.Deserialize<GoogleAccount>(
                json);
        }
        catch
        {
            return null;
        }
    }
}
