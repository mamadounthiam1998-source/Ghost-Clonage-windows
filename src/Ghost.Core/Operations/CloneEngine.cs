using Ghost.Core.Devices;
using Ghost.Core.Disks;
using Ghost.Core.Partitions;
using Ghost.Core.Safety;

namespace Ghost.Core.Operations;

public sealed class CloneRequest
{
    public required IDiskProvider Provider { get; init; }
    public required string SourceId { get; init; }
    public required string DestinationId { get; init; }

    /// <summary>Doit valoir true ET <see cref="ConfirmedDestinationId"/> doit désigner la destination, sinon rien n'est écrit.</summary>
    public bool ErasureConfirmed { get; init; }
    public string? ConfirmedDestinationId { get; init; }

    public bool Verify { get; init; } = true;
    public int ChunkSize { get; init; } = TransferCore.DefaultChunk;
}

public static class CloneEngine
{
    public static PreflightResult Preflight(IDiskProvider provider, string sourceId, string destinationId)
    {
        var src = EngineSupport.Find(provider, sourceId);
        var dst = EngineSupport.Find(provider, destinationId);
        if (src == null || dst == null)
            return new PreflightResult(src, dst, null,
                new[] { new SafetyIssue(IssueSeverity.Blocking, "DISK_MISSING", "Un des disques sélectionnés n'est plus disponible.") });

        using var dev = provider.OpenRead(sourceId);
        var layout = PartitionTableReader.Read(dev);
        return new PreflightResult(src, dst, layout, SafetyGuard.CheckClone(src, dst, layout));
    }

    public static Task<OperationResult> RunAsync(CloneRequest req, IProgress<ProgressInfo>? progress, ILog log, CancellationToken ct)
        => Task.Run(() => Run(req, progress, log, ct), CancellationToken.None);

    public static OperationResult Run(CloneRequest req, IProgress<ProgressInfo>? progress, ILog log, CancellationToken ct)
    {
        var t = new ProgressTracker(progress);
        long done = 0;
        bool destTouched = false;
        try
        {
            t.Phase(OperationPhase.Analysing, "Analyse du disque…");
            log.Info($"Clonage demandé : {req.SourceId} -> {req.DestinationId}");
            var pre = Preflight(req.Provider, req.SourceId, req.DestinationId);
            foreach (var i in pre.Issues) log.Info($"[{i.Severity}] {i.Code}: {i.Message}");
            if (pre.IsBlocked)
                return EngineSupport.Fail(t, pre.BlockingMessage);

            var layout = pre.Layout!;
            var dstDesc = pre.Destination!;
            var warnings = pre.Issues.Where(i => i.Severity == IssueSeverity.Warning).Select(i => i.Message).ToList();

            EngineSupport.CheckConfirmation(req.ErasureConfirmed, req.ConfirmedDestinationId, req.DestinationId);

            long extent = layout.ExtentBytes;
            t.SetTotal(req.Verify ? extent * 2 : extent);
            bool gpt = layout.Style == PartitionStyle.Gpt;
            List<byte[]> hashes;

            t.Phase(OperationPhase.Preparing, "Préparation du disque destination…");
            using (var src = req.Provider.OpenRead(req.SourceId))
            using (var dst = req.Provider.OpenWrite(req.DestinationId))
            {
                destTouched = true;
                log.Info("Destination ouverte en écriture exclusive.");
                TransferCore.ClearTail(dst, extent, log);

                t.Phase(OperationPhase.Working, "Clonage en cours…");
                hashes = TransferCore.Copy(src, dst, extent, req.ChunkSize, t, log, ct, 0, gpt);

                t.Phase(OperationPhase.Finalizing, "Écriture des structures de démarrage…");
                if (gpt) GptFixer.WriteBackup(dst);
                dst.Flush();
            }

            string fingerprint = EngineSupport.Fingerprint(hashes);

            // Relecture indépendante via une nouvelle ouverture du disque (pas de cache d'écriture)
            using (var chk = req.Provider.OpenRead(req.DestinationId))
            {
                if (req.Verify)
                {
                    t.Phase(OperationPhase.Verifying, "Vérification SHA-256 des données copiées…");
                    TransferCore.Verify(chk, extent, hashes, req.ChunkSize, t, log, ct, extent);
                }
                else
                {
                    warnings.Add("Vérification d'intégrité désactivée : la copie n'est pas contrôlée.");
                }

                t.Phase(OperationPhase.Verifying, "Contrôle des partitions…");
                var actual = PartitionTableReader.Read(chk);
                var diff = EngineSupport.CompareLayouts(layout, actual);
                if (diff != null) throw new VerificationFailedException("Contrôle des partitions échoué : " + diff, 0);
                if (gpt && !GptFixer.IsBackupValid(chk))
                    throw new VerificationFailedException("Contrôle échoué : la GPT de secours est invalide.", 0);
            }

            t.Phase(OperationPhase.Finalizing, "Finalisation…");
            req.Provider.NotifyLayoutChanged(req.DestinationId);

            if (dstDesc.Size > layout.DiskLength)
                warnings.Add("L'espace supplémentaire de la destination est non alloué. Étendez la dernière partition avec la Gestion des disques de Windows si besoin.");
            warnings.Add("Le démarrage n'est garanti que sur le matériel d'origine. Sur un autre ordinateur, validez le démarrage avant de vous y fier.");

            string summary = $"Clonage terminé et vérifié ({SafetyGuard.Fmt(extent)} copiés).";
            log.Info(summary + " Empreinte : " + fingerprint);
            t.Final(OperationPhase.Completed, "Opération terminée");
            return new OperationResult(OperationOutcome.Success, summary, warnings, extent, t.Elapsed, fingerprint);
        }
        catch (OperationCanceledException)
        {
            log.Warn("Opération annulée par l'utilisateur.");
            return EngineSupport.Cancelled(t,
                destTouched
                    ? "Opération annulée. Le disque destination est dans un état incomplet et ne démarrera pas : relancez le clonage."
                    : "Opération annulée. Aucune donnée n'a été modifiée.", done);
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
