using Ghost.Core.Disks;
using Ghost.Core.Imaging;
using Ghost.Core.Partitions;
using Ghost.Core.Safety;

namespace Ghost.Core.Operations;

public enum CompressionLevel { Fast = 1, Normal = 4, Maximum = 9 }

public sealed class ImageRequest
{
    public required IDiskProvider Provider { get; init; }
    public required string SourceId { get; init; }
    public required string OutputPath { get; init; }
    public bool OverwriteConfirmed { get; init; }
    public CompressionLevel Compression { get; init; } = CompressionLevel.Normal;
    public int ChunkSize { get; init; } = TransferCore.DefaultChunk;
    public string AppVersion { get; init; } = "1.0.0";

    /// <summary>Image de TOUS les secteurs du disque (toutes partitions et espace hors partition), pas seulement jusqu'à la dernière partition.</summary>
    public bool FullDisk { get; init; }
    public string? Label { get; init; }
    public bool IsRestorePoint { get; init; }
}

public static class ImageEngine
{
    public static PreflightResult Preflight(IDiskProvider provider, string sourceId)
    {
        var src = EngineSupport.Find(provider, sourceId);
        if (src == null)
            return new PreflightResult(null, null, null,
                new[] { new SafetyIssue(IssueSeverity.Blocking, "DISK_MISSING", "Le disque sélectionné n'est plus disponible.") });
        using var dev = provider.OpenRead(sourceId);
        var layout = PartitionTableReader.Read(dev);
        return new PreflightResult(src, null, layout, SafetyGuard.CheckImageSource(src, layout));
    }

    public static Task<OperationResult> CreateAsync(ImageRequest req, IProgress<ProgressInfo>? progress, ILog log, CancellationToken ct)
        => Task.Run(() => Create(req, progress, log, ct), CancellationToken.None);

    public static OperationResult Create(ImageRequest req, IProgress<ProgressInfo>? progress, ILog log, CancellationToken ct)
    {
        var t = new ProgressTracker(progress);
        string partial = req.OutputPath + ".partial";
        bool partialCreated = false;
        try
        {
            t.Phase(OperationPhase.Analysing, "Analyse du disque…");
            log.Info($"Création d'image : {req.SourceId} -> {req.OutputPath}");
            var pre = Preflight(req.Provider, req.SourceId);
            foreach (var i in pre.Issues) log.Info($"[{i.Severity}] {i.Code}: {i.Message}");
            if (pre.IsBlocked) return EngineSupport.Fail(t, pre.BlockingMessage);

            var layout = pre.Layout!;
            var src = pre.Source!;
            var warnings = pre.Issues.Where(i => i.Severity == IssueSeverity.Warning).Select(i => i.Message).ToList();

            string? dir = Path.GetDirectoryName(Path.GetFullPath(req.OutputPath));
            if (dir == null || !Directory.Exists(dir))
                return EngineSupport.Fail(t, "Le dossier de destination de l'image n'existe pas.");
            if (req.Provider.IsPathOnDisk(req.OutputPath, req.SourceId))
                return EngineSupport.Fail(t, "L'image ne peut pas être enregistrée sur le disque qu'elle sauvegarde. Choisissez un autre disque.");
            if (File.Exists(req.OutputPath) && !req.OverwriteConfirmed)
                return EngineSupport.Fail(t, "Le fichier image existe déjà et son remplacement n'a pas été confirmé.");

            t.Phase(OperationPhase.Preparing, "Préparation…");
            long extent = req.FullDisk ? src.Size : layout.ExtentBytes;
            t.SetTotal(extent * 2);

            byte[] overall;
            using (var dev = req.Provider.OpenRead(req.SourceId))
            {
                var meta = new ImageMetadata
                {
                    AppVersion = req.AppVersion,
                    CreatedUtc = DateTime.UtcNow,
                    SourceModel = src.Model,
                    SourceDiskSize = dev.Length,
                    SourceBusType = src.BusType,
                    SourceMedia = src.Media,
                    SourceWasLiveSystem = src.IsSystemDisk,
                    Layout = layout,
                    Machine = req.Provider.GetMachineInfo(),
                    Compression = "Brotli",
                    Label = req.Label,
                    IsRestorePoint = req.IsRestorePoint,
                    FullDisk = req.FullDisk
                };

                using var w = new ImageWriter(partial, dev.SectorSize, req.ChunkSize, dev.Length, extent, (int)req.Compression);
                partialCreated = true;

                t.Phase(OperationPhase.Working, "Création de l'image en cours…");
                var buf = new byte[req.ChunkSize];
                for (long off = 0; off < extent; off += req.ChunkSize)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(req.ChunkSize, extent - off);
                    var span = buf.AsSpan(0, n);
                    TransferCore.ReadResilient(dev, off, span, log);
                    w.WriteChunk(span);
                    t.Report(off + n);
                }

                t.Phase(OperationPhase.Finalizing, "Écriture des métadonnées…");
                overall = w.Complete(meta);
            }

            // Une image n'est valide qu'après relecture complète
            t.Phase(OperationPhase.Verifying, "Vérification de l'intégrité de l'image…");
            using (var r = ImageReader.Open(partial))
            {
                var v = r.Verify(t, ct, extent);
                if (!v.IsValid) throw new VerificationFailedException("Vérification de l'image échouée : " + v.Message, 0);
                if (!r.OverallHash.AsSpan().SequenceEqual(overall))
                    throw new VerificationFailedException("Le SHA-256 relu ne correspond pas à celui calculé pendant la lecture du disque.", 0);
            }

            t.Phase(OperationPhase.Finalizing, "Finalisation…");
            if (File.Exists(req.OutputPath)) File.Delete(req.OutputPath);
            File.Move(partial, req.OutputPath);
            partialCreated = false;

            long size = new FileInfo(req.OutputPath).Length;
            string sha = TransferCore.Hex(overall);
            string summary = $"Image créée et vérifiée ({SafetyGuard.Fmt(extent)} → {SafetyGuard.Fmt(size)}).";
            log.Info(summary + " SHA-256 global : " + sha);
            t.Final(OperationPhase.Completed, "Opération terminée");
            return new OperationResult(OperationOutcome.Success, summary, warnings, extent, t.Elapsed, sha);
        }
        catch (OperationCanceledException)
        {
            log.Warn("Opération annulée par l'utilisateur.");
            return EngineSupport.Cancelled(t, "Création de l'image annulée. Le fichier incomplet a été supprimé.", 0);
        }
        catch (Exception ex)
        {
            log.Error("Échec : " + ex);
            return EngineSupport.Fail(t, EngineSupport.DescribeError(ex) + " Aucune image valide n'a été produite.");
        }
        finally
        {
            if (partialCreated)
            {
                try { File.Delete(partial); } catch { /* meilleur effort */ }
            }
        }
    }
}
