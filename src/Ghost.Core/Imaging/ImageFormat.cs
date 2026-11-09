using System.Text.Json.Serialization;
using Ghost.Core.Disks;
using Ghost.Core.Partitions;

namespace Ghost.Core.Imaging;

/// <summary>
/// Format d'image GHOST (.gci), version 1 — documenté dans docs/FORMAT-IMAGE.md.
///
///  [En-tête 64 o]   "GHOSTIMG", version u32, secteur u32, taille bloc u32, réservé u32,
///                   taille disque i64, taille de l'étendue i64, réservé jusqu'à 64
///  [Blocs…]         type u8 (0 = zéros, 1 = brut, 2 = Brotli) | tailleBrute u32 | tailleStockée u32 |
///                   SHA-256 des données brutes (32 o) | données stockées
///  [0xFF]           marqueur de fin
///  [Métadonnées]    longueur u32 + JSON UTF-8 (structure du disque, machine source…)
///  [Pied 96 o]      "GHOSTEND", nb blocs i64, taille étendue i64, offset métadonnées i64,
///                   SHA-256 global des données brutes (32 o), SHA-256 des 64 premiers octets du pied (32 o)
///
/// Une image n'est valide que si le pied est présent et cohérent : un fichier tronqué ou interrompu
/// n'est jamais reconnu comme une sauvegarde.
/// </summary>
public static class ImageFormat
{
    public static readonly byte[] Magic = "GHOSTIMG"u8.ToArray();
    public static readonly byte[] FooterMagic = "GHOSTEND"u8.ToArray();
    public const int Version = 1;
    public const int HeaderSize = 64;
    public const int FooterSize = 96;
    public const byte ChunkZero = 0;
    public const byte ChunkRaw = 1;
    public const byte ChunkBrotli = 2;
    public const byte EndMarker = 0xFF;
    public const int ChunkHeaderSize = 1 + 4 + 4 + 32;
    public const string Extension = ".gci";
}

public sealed class ImageMetadata
{
    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string SourceModel { get; set; } = "";
    public long SourceDiskSize { get; set; }
    public string SourceBusType { get; set; } = "";
    public MediaKind SourceMedia { get; set; }
    public bool SourceWasLiveSystem { get; set; }
    public DiskLayout Layout { get; set; } = null!;
    public MachineInfo Machine { get; set; } = null!;
    public string Compression { get; set; } = "Brotli";

    /// <summary>Nom donné par l'utilisateur à un point de restauration (null pour une image ordinaire).</summary>
    public string? Label { get; set; }
    public bool IsRestorePoint { get; set; }

    /// <summary>Vrai si TOUS les secteurs du disque ont été imagés (y compris l'espace hors partition).</summary>
    public bool FullDisk { get; set; }
}
