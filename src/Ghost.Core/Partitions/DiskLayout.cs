namespace Ghost.Core.Partitions;

public enum PartitionStyle { Unknown = 0, Mbr = 1, Gpt = 2 }

public sealed record PartitionEntry(
    int Index,
    long StartOffset,
    long Length,
    string Kind,
    string? TypeId,
    string? Name,
    string? UniqueId,
    bool IsBitLocker)
{
    public long EndOffset => StartOffset + Length;
}

/// <summary>
/// Description de la structure d'un disque telle que lue sur le support (jamais devinée).
/// <see cref="ExtentBytes"/> = nombre d'octets, depuis le début du disque, à copier pour
/// reproduire fidèlement toute la structure (au minimum jusqu'à la fin de la dernière partition).
/// </summary>
public sealed record DiskLayout(
    PartitionStyle Style,
    string? DiskId,
    int SectorSize,
    long DiskLength,
    IReadOnlyList<PartitionEntry> Partitions,
    long ExtentBytes,
    bool IsConsistent,
    IReadOnlyList<string> Notes)
{
    public bool HasBitLocker => Partitions.Any(p => p.IsBitLocker);

    /// <summary>Taille des structures GPT de secours à recréer en fin de disque (en-tête + 128 entrées).</summary>
    public const int GptBackupSectors = 33;

    /// <summary>Capacité minimale que doit avoir un disque destination pour recevoir ce layout.</summary>
    public long RequiredBytesOnDestination =>
        Style == PartitionStyle.Gpt ? ExtentBytes + (long)GptBackupSectors * SectorSize : ExtentBytes;
}
