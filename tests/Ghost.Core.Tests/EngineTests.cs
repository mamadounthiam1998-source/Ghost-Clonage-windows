using Ghost.Core.Devices;
using Ghost.Core.Disks;
using Ghost.Core.Imaging;
using Ghost.Core.Operations;
using Ghost.Core.Partitions;
using Ghost.Core.Safety;
using Xunit;

namespace Ghost.Core.Tests;

public class PartitionTests
{
    [Fact]
    public void Reads_gpt_layout_with_all_windows_partitions()
    {
        using var env = new TestEnv();
        var p = env.NewDisk("src", 64L << 20);
        using (var d = FileBlockDevice.Open(p, true)) DiskBuilder.BuildGpt(d, DiskBuilder.WindowsLikeParts());
        using var r = FileBlockDevice.Open(p, false);
        var l = PartitionTableReader.Read(r);
        Assert.Equal(PartitionStyle.Gpt, l.Style);
        Assert.True(l.IsConsistent);
        Assert.Equal(3, l.Partitions.Count);
        Assert.Contains("EFI", l.Partitions[0].Kind);
        Assert.Contains("Récupération", l.Partitions[2].Kind);
        Assert.Equal((24576L + 4096) * 512, l.ExtentBytes);
    }

    [Fact]
    public void Corrupted_gpt_header_is_flagged_inconsistent()
    {
        using var env = new TestEnv();
        var p = env.NewDisk("src", 64L << 20);
        using (var d = FileBlockDevice.Open(p, true))
        {
            DiskBuilder.BuildGpt(d, DiskBuilder.WindowsLikeParts());
            var s = new byte[512]; d.Read(512, s); s[40] ^= 0xFF; d.Write(512, s); // casse le CRC
        }
        using var r = FileBlockDevice.Open(p, false);
        Assert.False(PartitionTableReader.Read(r).IsConsistent);
    }

    [Fact]
    public void Reads_mbr_and_detects_bitlocker()
    {
        using var env = new TestEnv();
        var p = env.NewDisk("src", 32L << 20);
        using (var d = FileBlockDevice.Open(p, true))
        {
            DiskBuilder.BuildMbr(d, new[] { (2048L, 8192L, (byte)0x07), (12288L, 4096L, (byte)0x07) });
            DiskBuilder.WriteNtfsLikeBoot(d, 12288L * 512, bitLocker: true);
        }
        using var r = FileBlockDevice.Open(p, false);
        var l = PartitionTableReader.Read(r);
        Assert.Equal(PartitionStyle.Mbr, l.Style);
        Assert.Equal(2, l.Partitions.Count);
        Assert.False(l.Partitions[0].IsBitLocker);
        Assert.True(l.Partitions[1].IsBitLocker);
        Assert.True(l.HasBitLocker);
    }

    [Fact]
    public void Unpartitioned_disk_is_copied_entirely()
    {
        using var env = new TestEnv();
        var p = env.NewDisk("src", 4L << 20);
        using var r = FileBlockDevice.Open(p, false);
        var l = PartitionTableReader.Read(r);
        Assert.Equal(PartitionStyle.Unknown, l.Style);
        Assert.Equal(4L << 20, l.ExtentBytes);
    }
}

public class CloneTests
{
    private static CloneRequest Req(TestEnv env, string src, string dst, bool confirm = true, string? confirmedId = null) => new()
    {
        Provider = env.Provider, SourceId = src, DestinationId = dst,
        ErasureConfirmed = confirm, ConfirmedDestinationId = confirmedId ?? dst, ChunkSize = 1 << 20
    };

    [Fact]
    public void Gpt_clone_to_larger_disk_is_bit_exact_and_backup_is_relocated()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 64L << 20);
        var dp = env.NewDisk("dst", 96L << 20);
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildGpt(d, DiskBuilder.WindowsLikeParts());
        using (var d = FileBlockDevice.Open(dp, true)) DiskBuilder.FillPattern(d, 0, d.Length, 99, false); // ancien contenu

        var res = CloneEngine.Run(Req(env, "src", "dst"), null, NullLog.Instance, CancellationToken.None);
        Assert.True(res.IsSuccess, res.Summary);

        using var chk = FileBlockDevice.Open(dp, false);
        var l = PartitionTableReader.Read(chk);
        Assert.Equal(PartitionStyle.Gpt, l.Style);
        Assert.Equal(3, l.Partitions.Count);
        Assert.True(GptFixer.IsBackupValid(chk));

        // Données partitions identiques octet pour octet (hors secteur 1 : en-tête GPT ajusté)
        var extent = (24576L + 4096) * 512;
        using var s = FileBlockDevice.Open(sp, false);
        var a = new byte[1 << 20]; var b = new byte[1 << 20];
        for (long o = 2L * 512 * 33 / 33; o < extent; o += a.Length)
        {
            long start = Math.Max(o, 34 * 512); // après la table GPT
            start -= start % 512;
            int n = (int)Math.Min(a.Length, extent - start);
            s.Read(start, a.AsSpan(0, n)); chk.Read(start, b.AsSpan(0, n));
            Assert.True(a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n)), $"différence vers {start}");
            if (n < a.Length) break;
            o = start;
        }
        Assert.Contains(env.Provider.LayoutNotifications, x => x == "dst");
    }

    [Fact]
    public void Mbr_clone_preserves_boot_code_and_signature()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 32L << 20);
        var dp = env.NewDisk("dst", 32L << 20);
        using (var d = FileBlockDevice.Open(sp, true))
            DiskBuilder.BuildMbr(d, new[] { (2048L, 20000L, (byte)0x07), (24000L, 8000L, (byte)0x27) });

        var res = CloneEngine.Run(Req(env, "src", "dst"), null, NullLog.Instance, CancellationToken.None);
        Assert.True(res.IsSuccess, res.Summary);
        Assert.True(DiskBuilder.RangesEqual(sp, dp, 32000L * 512));
    }

    [Fact]
    public void Clone_reports_progress_with_all_phases()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 32L << 20);
        env.NewDisk("dst", 32L << 20);
        using (var d = FileBlockDevice.Open(sp, true))
            DiskBuilder.BuildMbr(d, new[] { (2048L, 40000L, (byte)0x07) });
        var phases = new List<OperationPhase>();
        var prog = new SyncProgress<ProgressInfo>(p => { if (phases.LastOrDefault() != p.Phase) phases.Add(p.Phase); });
        var res = CloneEngine.Run(Req(env, "src", "dst"), prog, NullLog.Instance, CancellationToken.None);
        Assert.True(res.IsSuccess);
        Assert.Equal(new[] { OperationPhase.Analysing, OperationPhase.Preparing, OperationPhase.Working,
            OperationPhase.Finalizing, OperationPhase.Verifying, OperationPhase.Finalizing, OperationPhase.Completed }, phases);
    }

    [Fact]
    public void No_confirmation_means_nothing_is_written()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 32L << 20);
        var dp = env.NewDisk("dst", 32L << 20);
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildMbr(d, new[] { (2048L, 20000L, (byte)0x07) });
        using (var d = FileBlockDevice.Open(dp, true)) DiskBuilder.FillPattern(d, 0, d.Length, 5, false);
        var before = File.ReadAllBytes(dp);

        var res = CloneEngine.Run(Req(env, "src", "dst", confirm: false), null, NullLog.Instance, CancellationToken.None);
        Assert.Equal(OperationOutcome.Failed, res.Outcome);
        Assert.Equal(before, File.ReadAllBytes(dp));

        res = CloneEngine.Run(Req(env, "src", "dst", confirm: true, confirmedId: "autre"), null, NullLog.Instance, CancellationToken.None);
        Assert.Equal(OperationOutcome.Failed, res.Outcome);
        Assert.Equal(before, File.ReadAllBytes(dp));
    }

    [Fact]
    public void Safety_blocks_system_destination_same_disk_and_small_destination()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 32L << 20);
        env.NewDisk("small", 8L << 20);
        env.NewDisk("sys", 64L << 20);
        env.Provider.SystemDiskIds.Add("sys");
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildMbr(d, new[] { (2048L, 60000L, (byte)0x07) });

        Assert.Contains(CloneEngine.Preflight(env.Provider, "src", "src").Issues, i => i.Code == "SAME_DISK");
        Assert.Contains(CloneEngine.Preflight(env.Provider, "src", "small").Issues, i => i.Code == "CAPACITY");
        Assert.Contains(CloneEngine.Preflight(env.Provider, "src", "sys").Issues, i => i.Code == "DEST_SYSTEM_DISK");

        var res = CloneEngine.Run(Req(env, "src", "sys"), null, NullLog.Instance, CancellationToken.None);
        Assert.Equal(OperationOutcome.Failed, res.Outcome);
    }

    [Fact]
    public void Different_sector_sizes_are_refused()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 32L << 20, 512);
        env.NewDisk("dst", 32L << 20, 4096);
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildMbr(d, new[] { (2048L, 20000L, (byte)0x07) });
        Assert.Contains(CloneEngine.Preflight(env.Provider, "src", "dst").Issues, i => i.Code == "SECTOR_SIZE");
    }

    [Fact]
    public void Bitlocker_partition_is_copied_encrypted_with_warning()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 32L << 20);
        var dp = env.NewDisk("dst", 32L << 20);
        using (var d = FileBlockDevice.Open(sp, true))
        {
            DiskBuilder.BuildMbr(d, new[] { (2048L, 20000L, (byte)0x07) });
            DiskBuilder.WriteNtfsLikeBoot(d, 2048L * 512, bitLocker: true);
        }
        var res = CloneEngine.Run(Req(env, "src", "dst"), null, NullLog.Instance, CancellationToken.None);
        Assert.True(res.IsSuccess);
        Assert.Contains(res.Warnings, w => w.Contains("BitLocker"));
        Assert.True(DiskBuilder.RangesEqual(sp, dp, 22048L * 512));
    }

    [Fact]
    public void Unrecoverable_read_error_fails_and_never_reports_success()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 32L << 20);
        env.NewDisk("dst", 32L << 20);
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildMbr(d, new[] { (2048L, 40000L, (byte)0x07) });
        env.Provider.ReadWrapper = (id, dev) => id == "src" ? new FaultyDevice(dev) { BadReadOffset = 5L << 20 } : dev;

        var res = CloneEngine.Run(Req(env, "src", "dst"), null, NullLog.Instance, CancellationToken.None);
        Assert.Equal(OperationOutcome.Failed, res.Outcome);
        Assert.Contains("illisible", res.Summary);
        Assert.Contains("incomplet", res.Summary);
    }

    [Fact]
    public void Transient_read_error_is_retried()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 32L << 20);
        env.NewDisk("dst", 32L << 20);
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildMbr(d, new[] { (2048L, 40000L, (byte)0x07) });
        int opens = 0;
        env.Provider.ReadWrapper = (id, dev) =>
            id == "src" && ++opens == 2 ? new FaultyDevice(dev) { TransientReadFailures = 2 } : dev; // 1re ouverture = analyse, 2e = copie
        var res = CloneEngine.Run(Req(env, "src", "dst"), null, NullLog.Instance, CancellationToken.None);
        Assert.True(res.IsSuccess, res.Summary);
    }

    [Fact]
    public void Write_error_fails_with_explicit_message()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 32L << 20);
        env.NewDisk("dst", 32L << 20);
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildMbr(d, new[] { (2048L, 40000L, (byte)0x07) });
        env.Provider.WriteWrapper = (id, dev) => new FaultyDevice(dev) { BadWriteOffset = 3L << 20 };
        var res = CloneEngine.Run(Req(env, "src", "dst"), null, NullLog.Instance, CancellationToken.None);
        Assert.Equal(OperationOutcome.Failed, res.Outcome);
        Assert.Contains("incomplet", res.Summary);
    }

    [Fact]
    public void Silent_write_corruption_is_caught_by_sha256_verification()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 32L << 20);
        var dp = env.NewDisk("dst", 32L << 20);
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildMbr(d, new[] { (2048L, 40000L, (byte)0x07) });
        // Un disque défaillant qui écrit un octet faux sans signaler d'erreur
        env.Provider.WriteWrapper = (id, dev) => new CorruptingDevice(dev, 6L << 20);
        var res = CloneEngine.Run(Req(env, "src", "dst"), null, NullLog.Instance, CancellationToken.None);
        Assert.Equal(OperationOutcome.Failed, res.Outcome);
        Assert.Contains("Vérification échouée", res.Summary);
        _ = dp;
    }

    [Fact]
    public void Cancellation_stops_the_operation_cleanly()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 64L << 20);
        env.NewDisk("dst", 64L << 20);
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildMbr(d, new[] { (2048L, 120000L, (byte)0x07) });
        using var cts = new CancellationTokenSource();
        env.Provider.ReadWrapper = (id, dev) => new HookDevice(dev, off => { if (off >= (8L << 20)) cts.Cancel(); });
        var res = CloneEngine.Run(Req(env, "src", "dst"), null, NullLog.Instance, cts.Token);
        Assert.Equal(OperationOutcome.Cancelled, res.Outcome);
        Assert.Contains("incomplet", res.Summary);
    }
}

public class ImageTests
{
    [Fact]
    public void Create_verify_restore_roundtrip_gpt_to_larger_disk()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 64L << 20);
        var dp = env.NewDisk("dst", 80L << 20);
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildGpt(d, DiskBuilder.WindowsLikeParts());
        var img = env.PathOf("backup.gci");

        var created = ImageEngine.Create(new ImageRequest { Provider = env.Provider, SourceId = "src", OutputPath = img, ChunkSize = 1 << 20 },
            null, NullLog.Instance, CancellationToken.None);
        Assert.True(created.IsSuccess, created.Summary);
        Assert.True(File.Exists(img));
        Assert.False(File.Exists(img + ".partial"));
        Assert.True(new FileInfo(img).Length < (24576L + 4096) * 512, "la compression doit réduire la taille (zones nulles)");

        using (var r = ImageReader.Open(img))
        {
            Assert.True(r.Verify(null, CancellationToken.None).IsValid);
            Assert.Equal(PartitionStyle.Gpt, r.Metadata.Layout.Style);
            Assert.Equal(3, r.Metadata.Layout.Partitions.Count);
        }

        var restored = RestoreEngine.Run(new RestoreRequest
        {
            Provider = env.Provider, ImagePath = img, DestinationId = "dst",
            ErasureConfirmed = true, ConfirmedDestinationId = "dst"
        }, null, NullLog.Instance, CancellationToken.None);
        Assert.True(restored.IsSuccess, restored.Summary);
        Assert.Equal(created.Sha256Hex?.Length, 64);

        using var chk = FileBlockDevice.Open(dp, false);
        Assert.True(GptFixer.IsBackupValid(chk));
        var l = PartitionTableReader.Read(chk);
        Assert.Equal(3, l.Partitions.Count);

        // Données identiques à la source (après la table GPT)
        using var s = FileBlockDevice.Open(sp, false);
        long from = 34 * 512, to = (24576L + 4096) * 512;
        var a = new byte[to - from]; var b = new byte[to - from];
        s.Read(from, a); chk.Read(from, b);
        Assert.True(a.AsSpan().SequenceEqual(b));
    }

    private static string MakeImage(TestEnv env, out string diskPath)
    {
        diskPath = env.NewDisk("src", 32L << 20);
        using (var d = FileBlockDevice.Open(diskPath, true))
            DiskBuilder.BuildMbr(d, new[] { (2048L, 40000L, (byte)0x07) });
        var img = env.PathOf("b.gci");
        var r = ImageEngine.Create(new ImageRequest { Provider = env.Provider, SourceId = "src", OutputPath = img, ChunkSize = 1 << 20 },
            null, NullLog.Instance, CancellationToken.None);
        Assert.True(r.IsSuccess, r.Summary);
        return img;
    }

    [Fact]
    public void Corrupted_image_is_detected_and_restore_refuses_before_erasing()
    {
        using var env = new TestEnv();
        var img = MakeImage(env, out _);
        var dp = env.NewDisk("dst", 32L << 20);
        using (var d = FileBlockDevice.Open(dp, true)) DiskBuilder.FillPattern(d, 0, d.Length, 3, false);
        var before = File.ReadAllBytes(dp);

        var bytes = File.ReadAllBytes(img);
        bytes[ImageFormat.HeaderSize + ImageFormat.ChunkHeaderSize + 1000] ^= 0x55; // corruption silencieuse dans les données
        File.WriteAllBytes(img, bytes);

        using (var r = ImageReader.Open(img)) Assert.False(r.Verify(null, CancellationToken.None).IsValid);

        var res = RestoreEngine.Run(new RestoreRequest
        {
            Provider = env.Provider, ImagePath = img, DestinationId = "dst",
            ErasureConfirmed = true, ConfirmedDestinationId = "dst"
        }, null, NullLog.Instance, CancellationToken.None);
        Assert.Equal(OperationOutcome.Failed, res.Outcome);
        Assert.Contains("corrompue", res.Summary);
        Assert.Equal(before, File.ReadAllBytes(dp)); // destination intacte
    }

    [Fact]
    public void Truncated_image_is_not_a_valid_backup()
    {
        using var env = new TestEnv();
        var img = MakeImage(env, out _);
        var bytes = File.ReadAllBytes(img);
        File.WriteAllBytes(img, bytes.AsSpan(0, bytes.Length - 500).ToArray());
        Assert.Throws<InvalidDataException>(() => ImageReader.Open(img));

        env.NewDisk("dst", 32L << 20);
        var pre = RestoreEngine.Preflight(env.Provider, img, "dst");
        Assert.True(pre.IsBlocked);
        Assert.Contains("incomplète", pre.BlockingMessage);
    }

    [Fact]
    public void Cancelled_image_creation_leaves_no_file()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 64L << 20);
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildMbr(d, new[] { (2048L, 120000L, (byte)0x07) });
        var img = env.PathOf("c.gci");
        using var cts = new CancellationTokenSource();
        env.Provider.ReadWrapper = (id, dev) => new HookDevice(dev, off => { if (off >= (6L << 20)) cts.Cancel(); });
        var res = ImageEngine.Create(new ImageRequest { Provider = env.Provider, SourceId = "src", OutputPath = img, ChunkSize = 1 << 20 },
            null, NullLog.Instance, cts.Token);
        Assert.Equal(OperationOutcome.Cancelled, res.Outcome);
        Assert.False(File.Exists(img));
        Assert.False(File.Exists(img + ".partial"));
    }

    [Fact]
    public void Read_error_during_image_creation_produces_no_image()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 32L << 20);
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildMbr(d, new[] { (2048L, 40000L, (byte)0x07) });
        env.Provider.ReadWrapper = (id, dev) => new FaultyDevice(dev) { BadReadOffset = 7L << 20 };
        var img = env.PathOf("d.gci");
        var res = ImageEngine.Create(new ImageRequest { Provider = env.Provider, SourceId = "src", OutputPath = img },
            null, NullLog.Instance, CancellationToken.None);
        Assert.Equal(OperationOutcome.Failed, res.Outcome);
        Assert.False(File.Exists(img));
        Assert.False(File.Exists(img + ".partial"));
    }

    [Fact]
    public void Existing_image_is_not_overwritten_without_confirmation()
    {
        using var env = new TestEnv();
        var img = MakeImage(env, out _);
        var res = ImageEngine.Create(new ImageRequest { Provider = env.Provider, SourceId = "src", OutputPath = img },
            null, NullLog.Instance, CancellationToken.None);
        Assert.Equal(OperationOutcome.Failed, res.Outcome);
        Assert.True(ImageReader.Open(img).Verify(null, CancellationToken.None).IsValid);
    }

    [Fact]
    public void Restore_to_too_small_disk_is_blocked()
    {
        using var env = new TestEnv();
        var img = MakeImage(env, out _);
        env.NewDisk("tiny", 4L << 20);
        var pre = RestoreEngine.Preflight(env.Provider, img, "tiny");
        Assert.Contains(pre.Issues, i => i.Code == "CAPACITY");
        Assert.True(pre.IsBlocked);
    }

    [Fact]
    public void Compat_report_flags_firmware_and_hardware_differences_and_never_guarantees_boot()
    {
        using var env = new TestEnv();
        var sp = env.NewDisk("src", 64L << 20);
        env.NewDisk("dst", 80L << 20);
        using (var d = FileBlockDevice.Open(sp, true)) DiskBuilder.BuildGpt(d, DiskBuilder.WindowsLikeParts());
        var img = env.PathOf("g.gci");
        Assert.True(ImageEngine.Create(new ImageRequest { Provider = env.Provider, SourceId = "src", OutputPath = img },
            null, NullLog.Instance, CancellationToken.None).IsSuccess);

        env.Provider.Machine = new("Dell", "XPS 15", "0ABC", "BIOS", "Windows", "X64");
        var pre = RestoreEngine.Preflight(env.Provider, img, "dst");
        Assert.Contains(pre.Compatibility, c => c.Title.Contains("UEFI"));
        Assert.Contains(pre.Compatibility, c => c.Title.Contains("Matériel différent"));
        Assert.Contains(pre.Compatibility, c => c.Title.Contains("Licence"));
        Assert.Contains(pre.Compatibility, c => c.Title.Contains("Aucune garantie"));
    }
}

public class FormatTests
{
    [Fact]
    public void Crc32_matches_known_vector() =>
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

    [Fact]
    public void Image_roundtrip_chunks_zero_raw_and_compressed()
    {
        using var env = new TestEnv();
        var path = env.PathOf("x.gci");
        var rnd = new byte[8192]; new Random(1).NextBytes(rnd);
        var comp = new byte[8192]; for (int i = 0; i < comp.Length; i++) comp[i] = (byte)(i % 7);
        var zero = new byte[8192];
        using (var w = new ImageWriter(path, 512, 8192, 3 * 8192, 3 * 8192))
        {
            w.WriteChunk(rnd); w.WriteChunk(comp); w.WriteChunk(zero);
            w.Complete(new ImageMetadata
            {
                Layout = new DiskLayout(PartitionStyle.Unknown, null, 512, 3 * 8192, Array.Empty<PartitionEntry>(), 3 * 8192, true, Array.Empty<string>()),
                Machine = new("a", "b", "c", "UEFI", "d", "X64")
            });
        }
        using var r = ImageReader.Open(path);
        var buf = new byte[8192];
        Assert.True(r.TryReadNext(buf, out var c0)); Assert.Equal(rnd, buf);
        Assert.True(r.TryReadNext(buf, out var c1)); Assert.Equal(comp, buf);
        Assert.True(r.TryReadNext(buf, out var c2)); Assert.True(c2.IsZero); Assert.Equal(zero, buf);
        Assert.False(r.TryReadNext(buf, out _));
        Assert.Equal(8192, c1.Offset);
        Assert.True(r.Verify(null, CancellationToken.None).IsValid);
    }
}

/// <summary>Disque défaillant qui écrit un octet erroné sans lever d'erreur (corruption silencieuse).</summary>
public sealed class CorruptingDevice : IBlockDevice
{
    private readonly IBlockDevice _inner;
    private readonly long _at;
    public CorruptingDevice(IBlockDevice inner, long at) { _inner = inner; _at = at; }
    public string Name => _inner.Name;
    public long Length => _inner.Length;
    public int SectorSize => _inner.SectorSize;
    public void Read(long offset, Span<byte> buffer) => _inner.Read(offset, buffer);
    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (_at >= offset && _at < offset + buffer.Length)
        {
            var copy = buffer.ToArray();
            copy[_at - offset] ^= 0xFF;
            _inner.Write(offset, copy);
        }
        else _inner.Write(offset, buffer);
    }
    public void Flush() => _inner.Flush();
    public void Dispose() => _inner.Dispose();
}

/// <summary>IProgress synchrone (Progress&lt;T&gt; est asynchrone, ce qui rend les tests non déterministes).</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _a;
    public SyncProgress(Action<T> a) => _a = a;
    public void Report(T value) => _a(value);
}

/// <summary>Appelle un crochet à chaque lecture (permet de déclencher une annulation de façon déterministe).</summary>
public sealed class HookDevice : IBlockDevice
{
    private readonly IBlockDevice _inner;
    private readonly Action<long> _onRead;
    public HookDevice(IBlockDevice inner, Action<long> onRead) { _inner = inner; _onRead = onRead; }
    public string Name => _inner.Name;
    public long Length => _inner.Length;
    public int SectorSize => _inner.SectorSize;
    public void Read(long offset, Span<byte> buffer) { _onRead(offset); _inner.Read(offset, buffer); }
    public void Write(long offset, ReadOnlySpan<byte> buffer) => _inner.Write(offset, buffer);
    public void Flush() => _inner.Flush();
    public void Dispose() => _inner.Dispose();
}
