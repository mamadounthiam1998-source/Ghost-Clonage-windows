using Ghost.Core.Devices;
using Ghost.Core.Partitions;

namespace Ghost.Core.Disks;

public enum MediaKind { Unknown, Hdd, Ssd }

public sealed record DiskDescriptor(
    string Id,
    string DisplayName,
    string Model,
    long Size,
    int SectorSize,
    MediaKind Media,
    PartitionStyle Style,
    int PartitionCount,
    long UsedBytes,          // -1 = inconnu
    bool IsSystemDisk,
    bool IsRemovable,
    string BusType);

public sealed record MachineInfo(
    string Manufacturer,
    string Product,
    string BoardProduct,
    string Firmware,        // "UEFI", "BIOS" ou "Inconnu"
    string OsDescription,
    string Architecture);

/// <summary>Accès aux disques d'une plate-forme (Windows réel, ou disques virtuels pour les tests).</summary>
public interface IDiskProvider
{
    IReadOnlyList<DiskDescriptor> ListDisks();

    /// <summary>Ouvre un disque en lecture seule.</summary>
    IBlockDevice OpenRead(string id);

    /// <summary>
    /// Ouvre un disque en écriture exclusive. Ne doit être appelé qu'APRÈS confirmation explicite de l'effacement.
    /// Le fournisseur prépare le disque (verrouillage/démontage des volumes, suppression de l'ancienne table).
    /// </summary>
    IBlockDevice OpenWrite(string id);

    /// <summary>Demande au système de relire la table de partitions après écriture.</summary>
    void NotifyLayoutChanged(string id);

    /// <summary>Vrai si le fichier se trouve sur le disque indiqué (évite d'écrire une image sur son propre disque source).</summary>
    bool IsPathOnDisk(string filePath, string diskId);

    MachineInfo GetMachineInfo();
}
