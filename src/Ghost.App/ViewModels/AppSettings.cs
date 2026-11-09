using System.IO;
using System.Text.Json;

namespace Ghost.App.ViewModels;

/// <summary>Réglages locaux (un seul : le dossier des points de restauration). Stockés dans %LocalAppData%, jamais envoyés.</summary>
public sealed class AppSettings
{
    public string PointsFolder { get; set; } = "";

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhostClonage", "settings.json");

    public static AppSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch { /* réglage non enregistré : sans conséquence */ }
    }
}
