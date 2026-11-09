using System.Buffers.Binary;
using System.Text;
using Ghost.Core.Devices;
using Ghost.Core.Disks;
using Ghost.Core.Partitions;

namespace Ghost.Core.Tests;

/// <summary>Fabrique de disques virtuels réalistes (MBR / GPT) pour les tests.</summary>
public sealed class TestEnv : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "ghost-tests-" + Guid.NewGuid().ToString("N"));
    public VirtualDiskProvider Provider { get; } = new();

    public TestEnv() => Directory.CreateDirectory(Dir);

    public string NewDisk(string id, long size, int sectorSize = 512)
    {
        var path = Path.Combine(Dir, id + ".img");
        using (FileBlockDevice.Create(path, size, sectorSize)) { }
        Provider.SectorSizes[id] = sectorSize;
        Provider.Add(id, path);
        return path;
    }

    public string PathOf(string name) => Path.Combine(Dir, name);

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { }
    }
}

public static class DiskBuilder
{
    private static readonly Guid Esp = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    private static readonly Guid Basic = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    private static readonly Guid Recovery = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");

    /// <summary>Remplit [start, start+len) avec des données pseudo-aléatoires déterministes (sauf une zone nulle au milieu).</summary>
    public static void FillPattern(IBlockDevice d, long start, long len, int seed, bool sparseTail = true)
    {
        var rnd = new Random(seed);
        var buf = new byte[1024 * 1024];
        long half = sparseTail ? len / 2 : len;
        for (long o = 0; o < len; o += buf.Length)
        {
            int n = (int)Math.Min(buf.Length, len - o);
            n -= n % d.SectorSize;
            if (n <= 0) break;
            if (o >= half) Array.Clear(buf, 0, n); else rnd.NextBytes(buf.AsSpan(0, n));
            d.Write(start + o, buf.AsSpan(0, n));
        }
    }

    public static void WriteNtfsLikeBoot(IBlockDevice d, long start, bool bitLocker = false)
    {
        var s = new byte[d.SectorSize];
        s[0] = 0xEB; s[1] = 0x52; s[2] = 0x90;
        Encoding.ASCII.GetBytes(bitLocker ? "-FVE-FS-" : "NTFS    ").CopyTo(s, 3);
        s[510] = 0x55; s[511] = 0xAA;
        d.Write(start, s);
    }

    public record Part(long StartLba, long Sectors, Guid Type, string Name, bool BitLocker = false);

    /// <summary>Crée une GPT complète (MBR protectrice, en-têtes principal + secours, 128 entrées).</summary>
    public static void BuildGpt(IBlockDevice d, IReadOnlyList<Part> parts, int fillSeed = 1)
    {
        int ss = d.SectorSize;
        long total = d.Length / ss;
        const int num = 128, esz = 128;
        long entriesSectors = num * esz / ss;
        long firstUsable = 2 + entriesSectors;
        long lastUsable = total - 1 - entriesSectors - 1;

        var entries = new byte[num * esz];
        for (int i = 0; i < parts.Count; i++)
        {
            var e = entries.AsSpan(i * esz, esz);
            parts[i].Type.TryWriteBytes(e[..16]);
            Guid.NewGuid().TryWriteBytes(e.Slice(16, 16));
            BinaryPrimitives.WriteInt64LittleEndian(e.Slice(32, 8), parts[i].StartLba);
            BinaryPrimitives.WriteInt64LittleEndian(e.Slice(40, 8), parts[i].StartLba + parts[i].Sectors - 1);
            Encoding.Unicode.GetBytes(parts[i].Name).CopyTo(e.Slice(56));
        }

        // MBR protectrice
        var mbr = new byte[ss];
        mbr[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(446 + 12), (uint)Math.Min(total - 1, uint.MaxValue));
        mbr[510] = 0x55; mbr[511] = 0xAA;
        d.Write(0, mbr);

        var guid = Guid.NewGuid();
        byte[] Header(long my, long alt, long entriesLba)
        {
            var h = new byte[ss];
            "EFI PART"u8.CopyTo(h);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), 0x00010000);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), 92);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), my);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), alt);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(40), firstUsable);
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(48), lastUsable);
            guid.TryWriteBytes(h.AsSpan(56, 16));
            BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(72), entriesLba);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(80), num);
            BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(84), esz);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), Crc32.Compute(entries));
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc32.Compute(h.AsSpan(0, 92)));
            return h;
        }

        d.Write(1L * ss, Header(1, total - 1, 2));
        d.Write(2L * ss, entries);
        d.Write((total - 1 - entriesSectors) * ss, entries);
        d.Write((total - 1) * ss, Header(total - 1, 1, total - 1 - entriesSectors));

        for (int i = 0; i < parts.Count; i++)
        {
            long start = parts[i].StartLba * ss;
            long len = parts[i].Sectors * ss;
            FillPattern(d, start, len, fillSeed * 100 + i);
            if (parts[i].Type == Basic) WriteNtfsLikeBoot(d, start, parts[i].BitLocker);
        }
    }

    public static IReadOnlyList<Part> WindowsLikeParts(int ss = 512) => new[]
    {
        new Part(2048L * 512 / ss, 4096L * 512 / ss, Esp, "EFI System Partition"),
        new Part(8192L * 512 / ss, 16384L * 512 / ss, Basic, "Windows"),
        new Part(24576L * 512 / ss, 4096L * 512 / ss, Recovery, "Recovery"),
    };

    public static void BuildMbr(IBlockDevice d, (long startLba, long sectors, byte type)[] parts, int seed = 7)
    {
        var mbr = new byte[d.SectorSize];
        new Random(seed).NextBytes(mbr.AsSpan(0, 440)); // code d'amorçage factice
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(440), 0xCAFEBABE);
        for (int i = 0; i < parts.Length; i++)
        {
            int o = 446 + i * 16;
            mbr[o] = (byte)(i == 0 ? 0x80 : 0);
            mbr[o + 4] = parts[i].type;
            BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(o + 8), (uint)parts[i].startLba);
            BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(o + 12), (uint)parts[i].sectors);
        }
        mbr[510] = 0x55; mbr[511] = 0xAA;
        d.Write(0, mbr);
        for (int i = 0; i < parts.Length; i++)
        {
            long s = parts[i].startLba * d.SectorSize;
            FillPattern(d, s, parts[i].sectors * d.SectorSize, seed + i);
            if (parts[i].type == 0x07) WriteNtfsLikeBoot(d, s);
        }
    }

    public static bool RangesEqual(string a, string b, long length)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        var ba = new byte[1 << 20];
        var bb = new byte[1 << 20];
        for (long o = 0; o < length; o += ba.Length)
        {
            int n = (int)Math.Min(ba.Length, length - o);
            fa.ReadExactly(ba, 0, n);
            fb.ReadExactly(bb, 0, n);
            if (!ba.AsSpan(0, n).SequenceEqual(bb.AsSpan(0, n))) return false;
        }
        return true;
    }
}

/// <summary>Périphérique qui simule des pannes (lecture/écriture).</summary>
public sealed class FaultyDevice : IBlockDevice
{
    private readonly IBlockDevice _inner;
    public long? BadReadOffset { get; set; }
    public long? BadWriteOffset { get; set; }
    public int TransientReadFailures { get; set; }

    public FaultyDevice(IBlockDevice inner) => _inner = inner;
    public string Name => _inner.Name;
    public long Length => _inner.Length;
    public int SectorSize => _inner.SectorSize;

    public void Read(long offset, Span<byte> buffer)
    {
        if (TransientReadFailures > 0) { TransientReadFailures--; throw new DeviceIoException("transitoire", offset, true); }
        if (BadReadOffset is long b && b >= offset && b < offset + buffer.Length)
            throw new DeviceIoException("secteur défectueux", b, true);
        _inner.Read(offset, buffer);
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (BadWriteOffset is long b && b >= offset && b < offset + buffer.Length)
            throw new DeviceIoException("écriture impossible", b, false);
        _inner.Write(offset, buffer);
    }

    public void Flush() => _inner.Flush();
    public void Dispose() => _inner.Dispose();
}
