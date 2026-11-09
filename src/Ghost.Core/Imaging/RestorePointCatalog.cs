using System.Text;
using Ghost.Core.Safety;

namespace Ghost.Core.Imaging;

public sealed record RestorePoint(
    string Path,
    string Label,
    DateTime CreatedUtc,
    string SourceModel,
    long SourceDiskSize,
    long FileSize,
    bool FullDisk,
    int PartitionCount,
    string PartitionSummary,
    bool HasBitLocker);

public sealed record RestorePointListing(IReadOnlyList<RestorePoint> Points, int InvalidFiles);

/// <summary>
/// Un point de restauration est une image .gci du DISQUE ENTIER (toutes partitions et espace hors partition)
/// rangée dans un dossier choisi par l'utilisateur. Le catalogue est le dossier lui-même : aucune base de données.
/// Seuls les fichiers valides (pied d'image intègre) sont listés.
/// </summary>
public static class RestorePointCatalog
{
    public static RestorePointListing List(string folder)
    {
        var points = new List<RestorePoint>();
        int invalid = 0;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new RestorePointListing(points, 0);

        foreach (var file in Directory.EnumerateFiles(folder, "*" + ImageFormat.Extension, SearchOption.TopDirectoryOnly)
                     .Where(f => f.EndsWith(ImageFormat.Extension, StringComparison.OrdinalIgnoreCase)))   // Windows : "*.gci" accepterait aussi ".gci.partial"
        {
            try
            {
                using var r = ImageReader.Open(file);
                var m = r.Metadata;
                if (!m.IsRestorePoint) continue;   // les images ordinaires du même dossier ne sont pas des points
                string summary = string.Join(" + ", m.Layout.Partitions.Select(p => p.Name is { Length: > 0 } ? p.Name : p.Kind));
                points.Add(new RestorePoint(file,
                    string.IsNullOrWhiteSpace(m.Label) ? System.IO.Path.GetFileNameWithoutExtension(file) : m.Label!,
                    m.CreatedUtc, m.SourceModel, m.SourceDiskSize, r.FileSize, m.FullDisk,
                    m.Layout.Partitions.Count, summary, m.Layout.HasBitLocker));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                invalid++;   // fichier tronqué, corrompu ou illisible : ignoré, jamais proposé comme point valide
            }
        }
        return new RestorePointListing(points.OrderByDescending(p => p.CreatedUtc).ToList(), invalid);
    }

    public static string Sanitize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "Point";
        var sb = new StringBuilder();
        foreach (char c in label.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_');
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "Point" : s.Length > 60 ? s[..60].TrimEnd() : s;
    }

    public static string BuildPath(string folder, string? label, DateTime now) =>
        System.IO.Path.Combine(folder, $"{Sanitize(label)}_{now:yyyyMMdd-HHmmss}{ImageFormat.Extension}");

    public static string Describe(RestorePoint p) =>
        $"{p.Label} — {p.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} — {SafetyGuard.Fmt(p.FileSize)}";
}
