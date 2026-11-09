using System.Buffers.Binary;
using System.Text;
using Ghost.Core.Devices;

namespace Ghost.Core.Partitions;

/// <summary>Lecture réelle des tables de partitions MBR et GPT (aucune hypothèse, tout est lu sur le disque).</summary>
public static class PartitionTableReader
{
    public sealed record GptHeader(
        long MyLba, long AlternateLba, long FirstUsableLba, long LastUsableLba,
        Guid DiskGuid, long EntriesLba, int NumEntries, int EntrySize, uint EntriesCrc, int HeaderSize);

    private static readonly Dictionary<Guid, string> GptTypes = new()
    {
        [new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B")] = "Système EFI (ESP)",
        [new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE")] = "Réservée Microsoft (MSR)",
        [new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")] = "Données de base (Windows)",
        [new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC")] = "Récupération Windows (WinRE)",
        [new("5808C8AA-7E8F-42E0-85D2-E1E90434CFB3")] = "Métadonnées LDM",
        [new("AF9B60A0-1431-4F62-BC68-3311714A69AD")] = "Données LDM",
        [new("0FC63DAF-8483-4772-8E79-3D69D8477DE4")] = "Linux (données)",
        [new("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F")] = "Linux (swap)",
        [new("48465300-0000-11AA-AA11-00306543ECAC")] = "Apple HFS+",
        [new("7C3457EF-0000-11AA-AA11-00306543ECAC")] = "Apple APFS",
    };

    private static string MbrKind(byte t) => t switch
    {
        0x01 => "FAT12",
        0x04 or 0x06 or 0x0E => "FAT16",
        0x05 or 0x0F or 0x85 => "Étendue (conteneur)",
        0x07 => "NTFS / exFAT",
        0x0B or 0x0C => "FAT32",
        0x12 => "Utilitaire constructeur",
        0x27 => "Récupération Windows (WinRE)",
        0x82 => "Linux (swap)",
        0x83 => "Linux",
        0xEE => "GPT protectrice",
        0xEF => "Système EFI",
        _ => $"Type 0x{t:X2}"
    };

    public static DiskLayout Read(IBlockDevice dev)
    {
        int ss = dev.SectorSize;
        var s0 = new byte[ss];
        dev.Read(0, s0);

        if (s0[510] != 0x55 || s0[511] != 0xAA)
            return Raw(dev, "Aucune table de partitions reconnue : copie intégrale secteur par secteur.");

        bool protective = false;
        for (int i = 0; i < 4; i++)
            if (s0[446 + i * 16 + 4] == 0xEE) protective = true;

        if (protective)
        {
            var gpt = TryReadGpt(dev);
            if (gpt != null) return gpt;
            // MBR protectrice mais GPT illisible : on ne devine pas
            return new DiskLayout(PartitionStyle.Gpt, null, ss, dev.Length, Array.Empty<PartitionEntry>(),
                dev.Length, false,
                new[] { "L'en-tête GPT est absent ou corrompu (CRC invalide). Réparez la table avant toute copie." });
        }

        return ReadMbr(dev, s0);
    }

    private static DiskLayout Raw(IBlockDevice dev, string note) =>
        new(PartitionStyle.Unknown, null, dev.SectorSize, dev.Length, Array.Empty<PartitionEntry>(),
            dev.Length, true, new[] { note });

    private static DiskLayout ReadMbr(IBlockDevice dev, byte[] s0)
    {
        int ss = dev.SectorSize;
        var parts = new List<PartitionEntry>();
        var notes = new List<string>();
        bool ok = true;
        long extent = 0;
        uint sig = BinaryPrimitives.ReadUInt32LittleEndian(s0.AsSpan(440, 4));

        for (int i = 0; i < 4; i++)
        {
            int o = 446 + i * 16;
            byte type = s0[o + 4];
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(s0.AsSpan(o + 8, 4));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(s0.AsSpan(o + 12, 4));
            if (type == 0 || count == 0) continue;

            long so = (long)start * ss;
            long len = (long)count * ss;
            if (so + len > dev.Length)
            {
                ok = false;
                notes.Add($"La partition {i + 1} dépasse la capacité du disque.");
            }
            extent = Math.Max(extent, so + len);
            parts.Add(new PartitionEntry(i + 1, so, len, MbrKind(type), $"0x{type:X2}", null, null,
                IsBitLockerVolume(dev, so, len)));
        }

        if (parts.Count == 0) extent = ss; // MBR vide : on copie au moins le secteur 0
        return new DiskLayout(PartitionStyle.Mbr, $"{sig:X8}", ss, dev.Length, parts, extent, ok, notes);
    }

    public static GptHeader? ReadGptHeader(IBlockDevice dev, long lba)
    {
        int ss = dev.SectorSize;
        if (lba < 1 || (lba + 1) * ss > dev.Length) return null;
        var buf = new byte[ss];
        dev.Read(lba * ss, buf);
        if (!buf.AsSpan(0, 8).SequenceEqual("EFI PART"u8)) return null;

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(12, 4));
        if (headerSize < 92 || headerSize > ss) return null;

        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16, 4));
        var tmp = buf.AsSpan(0, headerSize).ToArray();
        tmp.AsSpan(16, 4).Clear();
        if (Crc32.Compute(tmp) != crc) return null;

        var h = new GptHeader(
            MyLba: BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(24, 8)),
            AlternateLba: BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(32, 8)),
            FirstUsableLba: BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(40, 8)),
            LastUsableLba: BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(48, 8)),
            DiskGuid: new Guid(buf.AsSpan(56, 16)),
            EntriesLba: BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(72, 8)),
            NumEntries: BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(80, 4)),
            EntrySize: BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(84, 4)),
            EntriesCrc: BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(88, 4)),
            HeaderSize: headerSize);

        if (h.MyLba != lba) return null;
        if (h.NumEntries <= 0 || h.NumEntries > 16384 || h.EntrySize < 128 || h.EntrySize > 1024 || (h.EntrySize & 127) != 0)
            return null;
        if (h.FirstUsableLba < 2 || h.LastUsableLba < h.FirstUsableLba) return null;
        return h;
    }

    public static byte[]? ReadGptEntries(IBlockDevice dev, GptHeader h)
    {
        int ss = dev.SectorSize;
        long bytes = (long)h.NumEntries * h.EntrySize;
        long padded = (bytes + ss - 1) / ss * ss;
        if (h.EntriesLba < 1 || (h.EntriesLba * ss) + padded > dev.Length) return null;
        var buf = new byte[padded];
        dev.Read(h.EntriesLba * ss, buf);
        if (Crc32.Compute(buf.AsSpan(0, (int)bytes)) != h.EntriesCrc) return null;
        return buf;
    }

    private static DiskLayout? TryReadGpt(IBlockDevice dev)
    {
        int ss = dev.SectorSize;
        var h = ReadGptHeader(dev, 1);
        if (h == null) return null;
        var entries = ReadGptEntries(dev, h);
        if (entries == null) return null;

        var parts = new List<PartitionEntry>();
        var notes = new List<string>();
        bool ok = true;
        long extent = h.FirstUsableLba * ss;

        for (int i = 0; i < h.NumEntries; i++)
        {
            var e = entries.AsSpan(i * h.EntrySize, h.EntrySize);
            var type = new Guid(e[..16]);
            if (type == Guid.Empty) continue;
            var uid = new Guid(e.Slice(16, 16));
            long first = BinaryPrimitives.ReadInt64LittleEndian(e.Slice(32, 8));
            long last = BinaryPrimitives.ReadInt64LittleEndian(e.Slice(40, 8));
            string name = Encoding.Unicode.GetString(e.Slice(56, Math.Min(72, h.EntrySize - 56))).TrimEnd('\0');

            long so = first * ss, len = (last - first + 1) * ss;
            if (last < first || so < h.FirstUsableLba * ss || last > h.LastUsableLba || (last + 1) * ss > dev.Length)
            {
                ok = false;
                notes.Add($"La partition GPT {parts.Count + 1} a des bornes invalides.");
                continue;
            }
            extent = Math.Max(extent, (last + 1) * ss);
            parts.Add(new PartitionEntry(parts.Count + 1, so, len,
                GptTypes.TryGetValue(type, out var kind) ? kind : $"Type {type}",
                type.ToString().ToUpperInvariant(), name, uid.ToString().ToUpperInvariant(),
                IsBitLockerVolume(dev, so, len)));
        }

        return new DiskLayout(PartitionStyle.Gpt, h.DiskGuid.ToString().ToUpperInvariant(), ss, dev.Length,
            parts, extent, ok, notes);
    }

    /// <summary>Détecte un volume BitLocker par sa signature "-FVE-FS-" (ne déchiffre rien).</summary>
    public static bool IsBitLockerVolume(IBlockDevice dev, long start, long length)
    {
        int ss = dev.SectorSize;
        if (length < ss || start + ss > dev.Length) return false;
        try
        {
            var b = new byte[ss];
            dev.Read(start, b);
            return b.AsSpan(3, 8).SequenceEqual("-FVE-FS-"u8);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
