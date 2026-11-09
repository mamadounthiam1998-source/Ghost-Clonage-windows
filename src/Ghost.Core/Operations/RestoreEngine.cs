using System.Security.Cryptography;
using Ghost.Core.Disks;
using Ghost.Core.Imaging;
using Ghost.Core.Partitions;
using Ghost.Core.Safety;

namespace Ghost.Core.Operations;

public sealed class RestoreRequest
{
    public required IDiskProvider Provider { get; init; }
    public required string ImagePath { get; init; }
    public required string DestinationId { get; init; }
    public bool ErasureConfirmed { get; init; }
    public string? ConfirmedDestinationId { get; init; }
    public bool Verify { get; init; } = true;
}

public sealed record RestorePreflight(
    ImageMetadata? Image,
    DiskDescriptor? Destination,
    IReadOnlyList<SafetyIssue> Issues,
    IReadOnlyList<Compat.CompatFinding> Compatibility,
    string? ImageError)
{
    public bool IsBlocked => ImageError != null || SafetyGuard.HasBlocking(Issues);
    public string BlockingMessage => ImageError ??
        string.Join(Environment.NewLine, Issues.Where(i => i.Severity == IssueSeverity.Blocking).Select(i => i.Message));
}

public static class RestoreEngine
{
    public static RestorePreflight Preflight(IDiskProvider provider, string imagePath, string destinationId)
    {
        ImageReader reader;
        try { reader = ImageReader.Open(imagePath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Text.Json.JsonException)
        {
            return new RestorePreflight(null, null, Array.Empty<SafetyIssue>(), Array.Empty<Compat.CompatFinding>(),
                "Image invalide ou incomplète : " + ex.Message + " Elle ne peut pas être utilisée comme sauvegarde.");
        }

        using (reader)
        {
            var dst = EngineSupport.Find(provider, destinationId);
            if (dst == null)
                return new RestorePreflight(reader.Metadata, null,
                    new[] { new SafetyIssue(IssueSeverity.Blocking, "DISK_MISSING", "Le disque destination n'est plus disponible.") },
                    Array.Empty<Compat.CompatFinding>(), null);

            var issues = SafetyGuard.CheckRestore(reader.SectorSize, reader.Metadata.Layout, dst);
            var compat = Compat.CompatibilityAnalyzer.Analyze(reader.Metadata, provider.GetMachineInfo(), dst);
            return new RestorePreflight(reader.Metadata, dst, issues, compat, null);
        }
    }

    public static Task<OperationResult> RunAsync(RestoreRequest req, IProgress<ProgressInfo>? progress, ILog log, CancellationToken ct)
        => Task.Run(() => Run(req, progress, log, ct), CancellationToken.None);

    public static OperationResult Run(RestoreRequest req, IProgress<ProgressInfo>? progress, ILog log, CancellationToken ct)
    {
        var t = new ProgressTracker(progress);
        bool destTouched = false;
        try
        {
            t.Phase(OperationPhase.Analysing, "Analyse de l'image…");
            log.Info($"Restauration : {req.ImagePath} -> {req.DestinationId}");
            var pre = Preflight(req.Provider, req.ImagePath, req.DestinationId);
            foreach (var i in pre.Issues) log.Info($"[{i.Severity}] {i.Code}: {i.Message}");
            if (pre.IsBlocked) return EngineSupport.Fail(t, pre.BlockingMessage);

            var warnings = pre.Issues.Where(i => i.Severity == IssueSeverity.Warning).Select(i => i.Message).ToList();
            warnings.AddRange(pre.Compatibility
                .Where(c => c.Severity != IssueSeverity.Info).Select(c => c.Title + " — " + c.Detail));

            EngineSupport.CheckConfirmation(req.ErasureConfirmed, req.ConfirmedDestinationId, req.DestinationId);

            using var reader = ImageReader.Open(req.ImagePath);
            var layout = reader.Metadata.Layout;
            long extent = reader.ExtentLength;
            bool gpt = layout.Style == PartitionStyle.Gpt;
            t.SetTotal(extent * (req.Verify ? 3 : 2));

            // 1) L'image doit être intègre AVANT d'effacer quoi que ce soit
            t.Phase(OperationPhase.Verifying, "Vérification de l'intégrité de l'image…");
            var iv = reader.Verify(t, ct, 0);
            if (!iv.IsValid)
                return EngineSupport.Fail(t, "L'image est corrompue : " + iv.Message + " Aucune donnée n'a été modifiée.");
            reader.Rewind();

            // 2) Écriture
            t.Phase(OperationPhase.Preparing, "Préparation du disque destination…");
            var hashes = new List<byte[]>();
            using (var dst = req.Provider.OpenWrite(req.DestinationId))
            {
                destTouched = true;
                TransferCore.ClearTail(dst, extent, log);

                t.Phase(OperationPhase.Working, "Restauration en cours…");
                var buf = new byte[reader.ChunkSize];
                long done = 0;
                while (reader.TryReadNext(buf, out var c))
                {
                    ct.ThrowIfCancellationRequested();
                    var span = buf.AsSpan(0, c.RawLength);
                    if (c.Offset == 0 && gpt)
                        GptFixer.AdjustPrimaryInPlace(span, reader.SectorSize, dst.Length / dst.SectorSize);
                    hashes.Add(SHA256.HashData(span));
                    TransferCore.Write(dst, c.Offset, span, log);
                    done += c.RawLength;
                    t.Report(extent + done);
                }

                t.Phase(OperationPhase.Finalizing, "Écriture des structures de démarrage…");
                if (gpt) GptFixer.WriteBackup(dst);
                dst.Flush();
            }

            string fingerprint = EngineSupport.Fingerprint(hashes);

            // 3) Vérification indépendante
            using (var chk = req.Provider.OpenRead(req.DestinationId))
            {
                if (req.Verify)
                {
                    t.Phase(OperationPhase.Verifying, "Vérification SHA-256 des données restaurées…");
                    TransferCore.Verify(chk, extent, hashes, reader.ChunkSize, t, log, ct, extent * 2);
                }
                else warnings.Add("Vérification d'intégrité désactivée : la restauration n'est pas contrôlée.");

                var actual = PartitionTableReader.Read(chk);
                var diff = EngineSupport.CompareLayouts(layout, actual);
                if (diff != null) throw new VerificationFailedException("Contrôle des partitions échoué : " + diff, 0);
                if (gpt && !GptFixer.IsBackupValid(chk))
                    throw new VerificationFailedException("Contrôle échoué : la GPT de secours est invalide.", 0);
            }

            t.Phase(OperationPhase.Finalizing, "Finalisation…");
            req.Provider.NotifyLayoutChanged(req.DestinationId);

            if (pre.Destination!.Size > reader.DiskSize)
                warnings.Add("L'espace supplémentaire de la destination est non alloué. Étendez la dernière partition avec la Gestion des disques de Windows si besoin.");
            warnings.Add("Le démarrage n'est pas garanti sur du matériel différent : validez-le avant de vous y fier.");

            string summary = $"Restauration terminée et vérifiée ({SafetyGuard.Fmt(extent)} restaurés).";
            log.Info(summary + " Empreinte : " + fingerprint);
            t.Final(OperationPhase.Completed, "Opération terminée");
            return new OperationResult(OperationOutcome.Success, summary, warnings, extent, t.Elapsed, fingerprint);
        }
        catch (OperationCanceledException)
        {
            log.Warn("Opération annulée par l'utilisateur.");
            return EngineSupport.Cancelled(t,
                destTouched
                    ? "Restauration annulée. Le disque destination est incomplet et ne démarrera pas : relancez la restauration."
                    : "Restauration annulée. Aucune donnée n'a été modifiée.", 0);
        }
        catch (InvalidOperationException ex) when (!destTouched)
        {
            log.Error(ex.Message);
            return EngineSupport.Fail(t, ex.Message);
        }
        catch (Exception ex)
        {
            log.Error("Échec : " + ex);
            string msg = EngineSupport.DescribeError(ex);
            if (destTouched) msg += " Le disque destination est dans un état incomplet : ne l'utilisez pas.";
            return EngineSupport.Fail(t, msg);
        }
        finally
        {
            if (destTouched)
            {
                try { req.Provider.NotifyLayoutChanged(req.DestinationId); } catch { /* meilleur effort */ }
            }
        }
    }
}
