using System.Buffers.Binary;
using Ghost.Core.Devices;

namespace Ghost.Core.Partitions;

/// <summary>
/// Adapte une GPT copiée à la taille du disque destination :
///  1) <see cref="AdjustPrimaryInPlace"/> corrige l'en-tête principal dans le tampon AVANT son écriture
///     (adresse de la GPT de secours, dernier secteur utilisable, CRC) ;
///  2) <see cref="WriteBackup"/> écrit la GPT de secours en fin de disque destination.
/// Ainsi, ce qui est écrit puis vérifié par SHA-256 est exactement ce qui reste sur le disque.
/// </summary>
public static class GptFixer
{
    /// <param name="head">Début de l'étendue copiée (doit contenir secteurs 0 à fin de la table d'entrées).</param>
    public static void AdjustPrimaryInPlace(Span<byte> head, int ss, long destTotalSectors)
    {
        if (head.Length < ss * 2) throw new InvalidDataException("Zone GPT trop courte.");
        var hdr = head.Slice(ss, ss);
        if (!hdr[..8].SequenceEqual("EFI PART"u8))
            throw new InvalidDataException("Signature GPT absente.");

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(12, 4));
        long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(hdr.Slice(72, 8));
        int num = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(80, 4));
        int esz = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(84, 4));
        long entriesBytes = (long)num * esz;
        long entriesSectors = (entriesBytes + ss - 1) / ss;

        long backupHeaderLba = destTotalSectors - 1;
        long backupEntriesLba = backupHeaderLba - entriesSectors;
        long newLastUsable = backupEntriesLba - 1;

        long entriesStart = entriesLba * ss;
        if (entriesStart + entriesBytes > head.Length)
            throw new InvalidDataException("La table d'entrées GPT dépasse la zone initiale copiée.");
        for (int i = 0; i < num; i++)
        {
            var e = head.Slice((int)entriesStart + i * esz, esz);
            if (new Guid(e[..16]) == Guid.Empty) continue;
            long last = BinaryPrimitives.ReadInt64LittleEndian(e.Slice(40, 8));
            if (last > newLastUsable)
                throw new InvalidOperationException(
                    "La destination est trop petite pour contenir les partitions et la GPT de secours.");
        }

        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(32, 8), backupHeaderLba);
        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(48, 8), newLastUsable);
        SetHeaderCrc(hdr, headerSize);
    }

    /// <summary>Écrit la GPT de secours (entrées + en-tête) à la fin du disque, d'après l'en-tête principal déjà écrit.</summary>
    public static void WriteBackup(IBlockDevice dev)
    {
        int ss = dev.SectorSize;
        var h = PartitionTableReader.ReadGptHeader(dev, 1)
                ?? throw new InvalidDataException("En-tête GPT principal invalide sur la destination.");
        var entries = PartitionTableReader.ReadGptEntries(dev, h)
                      ?? throw new InvalidDataException("Table d'entrées GPT invalide sur la destination.");

        long total = dev.Length / ss;
        long backupHeaderLba = total - 1;
        long backupEntriesLba = backupHeaderLba - entries.Length / ss;
        if (h.AlternateLba != backupHeaderLba)
            throw new InvalidDataException("L'en-tête principal ne désigne pas l'emplacement attendu de la GPT de secours.");

        var backup = new byte[ss];
        dev.Read(ss, backup);
        BinaryPrimitives.WriteInt64LittleEndian(backup.AsSpan(24, 8), backupHeaderLba);
        BinaryPrimitives.WriteInt64LittleEndian(backup.AsSpan(32, 8), 1);
        BinaryPrimitives.WriteInt64LittleEndian(backup.AsSpan(72, 8), backupEntriesLba);
        SetHeaderCrc(backup, h.HeaderSize);

        dev.Write(backupEntriesLba * ss, entries);
        dev.Write(backupHeaderLba * ss, backup);
        dev.Flush();
    }

    private static void SetHeaderCrc(Span<byte> sector, int headerSize)
    {
        sector.Slice(16, 4).Clear();
        uint crc = Crc32.Compute(sector[..headerSize]);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.Slice(16, 4), crc);
    }

    /// <summary>Vérifie que l'en-tête de secours en fin de disque est valide et cohérent avec le principal.</summary>
    public static bool IsBackupValid(IBlockDevice dev)
    {
        long last = dev.Length / dev.SectorSize - 1;
        var p = PartitionTableReader.ReadGptHeader(dev, 1);
        var b = PartitionTableReader.ReadGptHeader(dev, last);
        if (p == null || b == null) return false;
        return p.AlternateLba == last && b.AlternateLba == 1
               && PartitionTableReader.ReadGptEntries(dev, b) != null
               && p.DiskGuid == b.DiskGuid && p.EntriesCrc == b.EntriesCrc
               && p.LastUsableLba == b.LastUsableLba;
    }
}
