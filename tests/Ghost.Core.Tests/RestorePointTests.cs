using Ghost.Core.Devices;
using Ghost.Core.Imaging;
using Ghost.Core.Operations;
using Ghost.Core.Partitions;
using Xunit;

namespace Ghost.Core.Tests;

/// <summary>Scénario « salle Skype/Teams » : un disque, deux partitions (Windows + Skype Room), tout doit revenir.</summary>
public class RestorePointTests
{
    private static readonly Guid Basic = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    private const long Disk = 64L << 20;

    private static string MakeSkypeRoomDisk(TestEnv env, string id)
    {
        var path = env.NewDisk(id, Disk);
        using var d = FileBlockDevice.Open(path, true);
        DiskBuilder.BuildGpt(d, new[]
        {
            new DiskBuilder.Part(2048, 40000, Basic, "Windows"),
            new DiskBuilder.Part(44000, 40000, Basic, "SkypeRoom"),
        });
        // Données dans l'espace HORS partition (entre la dernière partition et la GPT de secours)
        DiskBuilder.FillPattern(d, 100000L * 512, 4L << 20, 4242, false);
        return path;
    }

    private static OperationResult Point(TestEnv env, string src, string path, bool full = true) =>
        ImageEngine.Create(new ImageRequest
        {
            Provider = env.Provider, SourceId = src, OutputPath = path, FullDisk = full,
            Label = "Avant mise à jour", IsRestorePoint = true, ChunkSize = 1 << 20
        }, null, NullLog.Instance, CancellationToken.None);

    private static OperationResult Revert(TestEnv env, string path, string dst) =>
        RestoreEngine.Run(new RestoreRequest
        {
            Provider = env.Provider, ImagePath = path, DestinationId = dst,
            ErasureConfirmed = true, ConfirmedDestinationId = dst
        }, null, NullLog.Instance, CancellationToken.None);

    [Fact]
    public void Full_disk_point_restores_both_partitions_and_the_space_between_them_bit_for_bit()
    {
        using var env = new TestEnv();
        var sp = MakeSkypeRoomDisk(env, "src");
        var dp = env.NewDisk("dst", Disk);
        using (var d = FileBlockDevice.Open(dp, true)) DiskBuilder.FillPattern(d, 0, d.Length, 5, false);

        var img = env.PathOf("p.gci");
        var created = Point(env, "src", img);
        Assert.True(created.IsSuccess, created.Summary);
        using (var r = ImageReader.Open(img))
        {
            Assert.True(r.Metadata.FullDisk);
            Assert.True(r.Metadata.IsRestorePoint);
            Assert.Equal(Disk, r.ExtentLength);              // le disque ENTIER, pas seulement jusqu'à la dernière partition
            Assert.Equal(2, r.Metadata.Layout.Partitions.Count);
        }

        var res = Revert(env, img, "dst");
        Assert.True(res.IsSuccess, res.Summary);
        Assert.True(DiskBuilder.RangesEqual(sp, dp, Disk), "le disque restauré doit être identique à la source sur toute sa taille");
    }

    [Fact]
    public void Ordinary_image_does_not_carry_space_outside_partitions_but_full_disk_does()
    {
        using var env = new TestEnv();
        var sp = MakeSkypeRoomDisk(env, "src");
        var dp = env.NewDisk("dst", Disk);

        var img = env.PathOf("o.gci");
        Assert.True(Point(env, "src", img, full: false).IsSuccess);
        using (var r = ImageReader.Open(img)) Assert.True(r.ExtentLength < Disk);
        Assert.True(Revert(env, img, "dst").IsSuccess);
        Assert.False(DiskBuilder.RangesEqual(sp, dp, Disk));   // l'espace hors partition n'est pas copié : d'où l'option « disque entier »
    }

    [Fact]
    public void Full_disk_point_restores_onto_a_larger_disk_with_valid_gpt_backup()
    {
        using var env = new TestEnv();
        var sp = MakeSkypeRoomDisk(env, "src");
        var dp = env.NewDisk("big", 96L << 20);
        var img = env.PathOf("p.gci");
        Assert.True(Point(env, "src", img).IsSuccess);

        var res = Revert(env, img, "big");
        Assert.True(res.IsSuccess, res.Summary);

        using var chk = FileBlockDevice.Open(dp, false);
        Assert.True(GptFixer.IsBackupValid(chk));
        Assert.Equal(2, PartitionTableReader.Read(chk).Partitions.Count);

        // contenu identique de la fin de la table GPT jusqu'à la GPT de secours d'origine (exclue)
        using var s = FileBlockDevice.Open(sp, false);
        long from = 34 * 512, to = Disk - 33 * 512;
        var a = new byte[to - from]; var b = new byte[to - from];
        s.Read(from, a); chk.Read(from, b);
        Assert.True(a.AsSpan().SequenceEqual(b));
    }

    [Fact]
    public void Full_disk_point_is_blocked_on_a_smaller_disk_even_if_partitions_would_fit()
    {
        using var env = new TestEnv();
        MakeSkypeRoomDisk(env, "src");
        env.NewDisk("small", 48L << 20);
        var img = env.PathOf("p.gci");
        Assert.True(Point(env, "src", img).IsSuccess);
        var pre = RestoreEngine.Preflight(env.Provider, img, "small");
        Assert.Contains(pre.Issues, i => i.Code == "CAPACITY");
        Assert.True(pre.IsBlocked);
    }

    [Fact]
    public void Reverting_on_the_same_disk_brings_back_deleted_data()
    {
        using var env = new TestEnv();
        var sp = MakeSkypeRoomDisk(env, "src");
        var copy = env.PathOf("reference.img");
        File.Copy(sp, copy);

        var img = env.PathOf("p.gci");
        Assert.True(Point(env, "src", img).IsSuccess);

        // « Mise à jour qui tourne mal » : le contenu des deux partitions est abîmé
        using (var d = FileBlockDevice.Open(sp, true))
        {
            DiskBuilder.FillPattern(d, 2048L * 512, 8L << 20, 999, false);
            DiskBuilder.FillPattern(d, 44000L * 512, 8L << 20, 998, false);
        }
        Assert.False(DiskBuilder.RangesEqual(sp, copy, Disk));

        var res = Revert(env, img, "src");
        Assert.True(res.IsSuccess, res.Summary);
        Assert.True(DiskBuilder.RangesEqual(sp, copy, Disk), "retour exact à l'état du point de restauration");
    }

    [Fact]
    public void Catalog_lists_only_valid_restore_points_newest_first()
    {
        using var env = new TestEnv();
        MakeSkypeRoomDisk(env, "src");
        var folder = env.PathOf("points");
        Directory.CreateDirectory(folder);

        var p1 = RestorePointCatalog.BuildPath(folder, "Avant mise à jour", new DateTime(2026, 1, 1, 10, 0, 0));
        Assert.True(Point(env, "src", p1).IsSuccess);
        Thread.Sleep(1100);
        var p2 = RestorePointCatalog.BuildPath(folder, "Après mise à jour", new DateTime(2026, 1, 2, 10, 0, 0));
        Assert.True(ImageEngine.Create(new ImageRequest
        {
            Provider = env.Provider, SourceId = "src", OutputPath = p2, FullDisk = true, Label = "Après mise à jour", IsRestorePoint = true
        }, null, NullLog.Instance, CancellationToken.None).IsSuccess);

        // image ordinaire (pas un point), fichier corrompu, fichier temporaire
        Assert.True(ImageEngine.Create(new ImageRequest { Provider = env.Provider, SourceId = "src", OutputPath = Path.Combine(folder, "ordinaire.gci") },
            null, NullLog.Instance, CancellationToken.None).IsSuccess);
        File.WriteAllBytes(Path.Combine(folder, "corrompu.gci"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(folder, "en-cours.gci.partial"), new byte[2048]);

        var list = RestorePointCatalog.List(folder);
        Assert.Equal(2, list.Points.Count);
        Assert.Equal(1, list.InvalidFiles);
        Assert.Equal("Après mise à jour", list.Points[0].Label);     // le plus récent d'abord
        Assert.Equal("Avant mise à jour", list.Points[1].Label);
        Assert.True(list.Points.All(p => p.FullDisk && p.PartitionCount == 2));
        Assert.Contains("Windows", list.Points[0].PartitionSummary);
        Assert.Contains("SkypeRoom", list.Points[0].PartitionSummary);
    }

    [Fact]
    public void Catalog_handles_missing_folder_and_sanitizes_names()
    {
        Assert.Empty(RestorePointCatalog.List(Path.Combine(Path.GetTempPath(), "n-existe-pas-" + Guid.NewGuid())).Points);
        Assert.Empty(RestorePointCatalog.List("").Points);
        Assert.Equal("a_b_c", RestorePointCatalog.Sanitize("a/b\\c"));
        Assert.Equal("Point", RestorePointCatalog.Sanitize("   "));
        var name = Path.GetFileName(RestorePointCatalog.BuildPath("D:\\x", "Salle 1: avant", new DateTime(2026, 3, 4, 5, 6, 7)));
        Assert.Equal("Salle 1_ avant_20260304-050607.gci", name);
    }
}
