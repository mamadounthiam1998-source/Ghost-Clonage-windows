using System.Security.Cryptography;
using Ghost.Core.Devices;
using Ghost.Core.Disks;
using Ghost.Core.Partitions;
using Ghost.Core.Safety;

namespace Ghost.Core.Operations;

public sealed record PreflightResult(
    DiskDescriptor? Source,
    DiskDescriptor? Destination,
    DiskLayout? Layout,
    IReadOnlyList<SafetyIssue> Issues)
{
    public bool IsBlocked => SafetyGuard.HasBlocking(Issues);
    public string BlockingMessage =>
        string.Join(Environment.NewLine, Issues.Where(i => i.Severity == IssueSeverity.Blocking).Select(i => i.Message));
}

internal static class EngineSupport
{
    public static DiskDescriptor? Find(IDiskProvider p, string id) => p.ListDisks().FirstOrDefault(d => d.Id == id);

    public static OperationResult Fail(ProgressTracker t, string summary, IReadOnlyList<string>? warnings = null, long done = 0)
    {
        t.Final(OperationPhase.Failed, summary);
        return new OperationResult(OperationOutcome.Failed, summary, warnings ?? Array.Empty<string>(), done, t.Elapsed);
    }

    public static OperationResult Cancelled(ProgressTracker t, string summary, long done)
    {
        t.Final(OperationPhase.Cancelled, summary);
        return new OperationResult(OperationOutcome.Cancelled, summary, Array.Empty<string>(), done, t.Elapsed);
    }

    public static string Fingerprint(IReadOnlyList<byte[]> hashes)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var x in hashes) h.AppendData(x);
        return TransferCore.Hex(h.GetHashAndReset());
    }

    /// <summary>Contrôle des partitions après écriture : la structure relue doit être identique à l'attendue.</summary>
    public static string? CompareLayouts(DiskLayout expected, DiskLayout actual)
    {
        if (actual.Style != expected.Style) return "Le style de table de partitions (GPT/MBR) diffère.";
        if (!actual.IsConsistent) return "La table de partitions relue sur la destination est invalide.";
        if (actual.Partitions.Count != expected.Partitions.Count) return "Le nombre de partitions diffère.";
        for (int i = 0; i < expected.Partitions.Count; i++)
        {
            var a = expected.Partitions[i];
            var b = actual.Partitions[i];
            if (a.StartOffset != b.StartOffset || a.Length != b.Length || a.TypeId != b.TypeId)
                return $"La partition {i + 1} diffère (début, taille ou type).";
        }
        return null;
    }

    public static string DescribeError(Exception ex) => ex switch
    {
        DeviceIoException d => d.Message,
        VerificationFailedException v => v.Message,
        UnauthorizedAccessException => "Accès refusé : exécutez le logiciel en tant qu'administrateur.",
        _ => ex.Message
    };

    public static void CheckConfirmation(bool confirmed, string? confirmedId, string destId)
    {
        if (!confirmed || confirmedId != destId)
            throw new InvalidOperationException(
                "Confirmation explicite d'effacement absente : aucune opération destructive n'est lancée.");
    }
}
