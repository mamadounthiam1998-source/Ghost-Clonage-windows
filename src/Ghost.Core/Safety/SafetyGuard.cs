using Ghost.Core.Disks;
using Ghost.Core.Partitions;

namespace Ghost.Core.Safety;

public enum IssueSeverity { Info, Warning, Blocking }

public sealed record SafetyIssue(IssueSeverity Severity, string Code, string Message);

/// <summary>Contrôles de sécurité exécutés avant toute opération. Tout problème "Blocking" interdit l'opération.</summary>
public static class SafetyGuard
{
    public static List<SafetyIssue> CheckClone(DiskDescriptor src, DiskDescriptor dst, DiskLayout srcLayout)
    {
        var l = new List<SafetyIssue>();

        if (src.Id == dst.Id)
            l.Add(Block("SAME_DISK", "Le disque source et le disque destination sont identiques."));
        if (dst.IsSystemDisk)
            l.Add(Block("DEST_SYSTEM_DISK",
                "Le disque destination contient le Windows en cours d'exécution : il ne peut pas être effacé depuis ce système. Utilisez le support de démarrage (WinPE)."));
        if (src.SectorSize != dst.SectorSize)
            l.Add(Block("SECTOR_SIZE",
                $"Taille de secteur différente (source {src.SectorSize} octets, destination {dst.SectorSize} octets) : une copie fidèle est impossible sans conversion, non prise en charge."));
        AddLayoutChecks(l, srcLayout, dst.Size, "destination");

        if (src.IsSystemDisk)
            l.Add(Warn("LIVE_SYSTEM",
                "Le disque source contient le Windows en cours d'exécution. Les fichiers ouverts peuvent changer pendant la copie : la copie n'est pas garantie cohérente. Pour une copie fiable, utilisez le support de démarrage (WinPE)."));
        if (dst.IsRemovable)
            l.Add(Warn("DEST_REMOVABLE", "Le disque destination est amovible (USB)."));
        if (dst.PartitionCount > 0)
            l.Add(Info("DEST_HAS_DATA",
                $"Le disque destination contient {dst.PartitionCount} partition(s) qui seront définitivement effacées."));
        return l;
    }

    public static List<SafetyIssue> CheckImageSource(DiskDescriptor src, DiskLayout srcLayout)
    {
        var l = new List<SafetyIssue>();
        if (!srcLayout.IsConsistent)
            foreach (var n in srcLayout.Notes)
                l.Add(Block("SOURCE_LAYOUT", "Table de partitions de la source invalide : " + n));
        if (src.IsSystemDisk)
            l.Add(Warn("LIVE_SYSTEM",
                "Le disque source contient le Windows en cours d'exécution. L'image peut être incohérente (fichiers ouverts). Pour une image fiable, utilisez le support de démarrage (WinPE)."));
        AddBitLockerNote(l, srcLayout);
        return l;
    }

    public static List<SafetyIssue> CheckRestore(int imageSectorSize, DiskLayout imageLayout, long requiredBytes, DiskDescriptor dst)
    {
        var l = new List<SafetyIssue>();
        if (dst.IsSystemDisk)
            l.Add(Block("DEST_SYSTEM_DISK",
                "Le disque destination contient le Windows en cours d'exécution : il ne peut pas être écrasé depuis ce système. Utilisez le support de démarrage (WinPE)."));
        if (imageSectorSize != dst.SectorSize)
            l.Add(Block("SECTOR_SIZE",
                $"Taille de secteur différente (image {imageSectorSize} octets, destination {dst.SectorSize} octets) : restauration impossible sans conversion, non prise en charge."));
        if (dst.Size < requiredBytes)
            l.Add(Block("CAPACITY",
                $"Capacité insuffisante : {Fmt(requiredBytes)} nécessaires, destination {Fmt(dst.Size)}."));
        if (dst.PartitionCount > 0)
            l.Add(Info("DEST_HAS_DATA",
                $"Le disque destination contient {dst.PartitionCount} partition(s) qui seront définitivement effacées."));
        AddBitLockerNote(l, imageLayout);
        return l;
    }

    private static void AddLayoutChecks(List<SafetyIssue> l, DiskLayout layout, long destSize, string who)
    {
        if (!layout.IsConsistent)
            foreach (var n in layout.Notes)
                l.Add(Block("SOURCE_LAYOUT", "Table de partitions de la source invalide : " + n));
        if (destSize < layout.RequiredBytesOnDestination)
            l.Add(Block("CAPACITY",
                $"Capacité insuffisante : {Fmt(layout.RequiredBytesOnDestination)} nécessaires, {who} {Fmt(destSize)}."));
        else if (destSize > layout.DiskLength)
            l.Add(Info("DEST_LARGER",
                "La destination est plus grande que la source : l'espace supplémentaire restera non alloué (les partitions ne sont pas redimensionnées automatiquement)."));
        AddBitLockerNote(l, layout);
    }

    private static void AddBitLockerNote(List<SafetyIssue> l, DiskLayout layout)
    {
        if (layout.HasBitLocker)
            l.Add(Warn("BITLOCKER",
                "Une ou plusieurs partitions sont chiffrées avec BitLocker. Elles sont copiées telles quelles (chiffrées), sans jamais contourner le chiffrement. Sur un autre ordinateur, la clé de récupération BitLocker sera nécessaire."));
    }

    public static bool HasBlocking(IEnumerable<SafetyIssue> issues) => issues.Any(i => i.Severity == IssueSeverity.Blocking);

    private static SafetyIssue Block(string c, string m) => new(IssueSeverity.Blocking, c, m);
    private static SafetyIssue Warn(string c, string m) => new(IssueSeverity.Warning, c, m);
    private static SafetyIssue Info(string c, string m) => new(IssueSeverity.Info, c, m);

    public static string Fmt(long bytes)
    {
        string[] u = { "o", "Ko", "Mo", "Go", "To", "Po" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }
}
