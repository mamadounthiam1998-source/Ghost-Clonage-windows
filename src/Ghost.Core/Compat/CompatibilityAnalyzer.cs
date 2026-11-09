using Ghost.Core.Disks;
using Ghost.Core.Imaging;
using Ghost.Core.Partitions;
using Ghost.Core.Safety;

namespace Ghost.Core.Compat;

public sealed record CompatFinding(IssueSeverity Severity, string Title, string Detail);

/// <summary>
/// Détecte les différences matérielles entre la machine d'origine de l'image et la machine courante,
/// et signale les risques. Ne garantit JAMAIS un démarrage : seule une validation réelle le prouve.
/// </summary>
public static class CompatibilityAnalyzer
{
    public static List<CompatFinding> Analyze(ImageMetadata img, MachineInfo current, DiskDescriptor target)
    {
        var f = new List<CompatFinding>();
        var layout = img.Layout;

        // Mode de démarrage
        if (layout.Style == PartitionStyle.Gpt && current.Firmware == "BIOS")
            f.Add(new(IssueSeverity.Warning, "Démarrage UEFI requis",
                "L'image utilise une table GPT (démarrage UEFI) mais cet ordinateur démarre en BIOS hérité (Legacy/CSM). Windows ne démarrera pas sans conversion ou sans activer l'UEFI."));
        else if (layout.Style == PartitionStyle.Mbr && current.Firmware == "UEFI")
            f.Add(new(IssueSeverity.Warning, "Démarrage BIOS hérité requis",
                "L'image utilise une table MBR (BIOS hérité) mais cet ordinateur démarre en UEFI. Activez le mode Legacy/CSM, sinon Windows ne démarrera pas."));
        else if (current.Firmware == "Inconnu")
            f.Add(new(IssueSeverity.Info, "Mode de démarrage non déterminé",
                "Le mode de démarrage de cet ordinateur (UEFI/BIOS) n'a pas pu être détecté."));

        // Changement de matériel
        bool sameMachine =
            string.Equals(img.Machine.Manufacturer, current.Manufacturer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(img.Machine.Product, current.Product, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(img.Machine.BoardProduct, current.BoardProduct, StringComparison.OrdinalIgnoreCase);
        if (!sameMachine)
        {
            f.Add(new(IssueSeverity.Warning, "Matériel différent détecté",
                $"Image créée sur « {img.Machine.Manufacturer} {img.Machine.Product} », restauration sur « {current.Manufacturer} {current.Product} ». Des pilotes (chipset, stockage, écran…) peuvent manquer ; Windows peut nécessiter une nouvelle détection du matériel."));
            f.Add(new(IssueSeverity.Warning, "Licence Windows",
                "Une licence OEM est généralement liée à la carte mère d'origine. Sur un autre ordinateur, Windows peut demander une réactivation et exiger une licence valide pour ce nouveau matériel."));
        }

        // Contrôleur de stockage
        if (!string.IsNullOrEmpty(img.SourceBusType) && !string.IsNullOrEmpty(target.BusType) &&
            !string.Equals(img.SourceBusType, target.BusType, StringComparison.OrdinalIgnoreCase) &&
            img.SourceBusType != "Virtuel" && target.BusType != "Virtuel")
        {
            f.Add(new(IssueSeverity.Warning, "Contrôleur de stockage différent",
                $"Disque d'origine en {img.SourceBusType}, destination en {target.BusType}. Si le pilote du contrôleur n'est pas déjà présent dans Windows, l'ordinateur peut afficher l'erreur INACCESSIBLE_BOOT_DEVICE (pilotes RAID/VMD Intel, par exemple)."));
        }

        // Chiffrement
        if (layout.HasBitLocker)
            f.Add(new(IssueSeverity.Warning, "Volumes BitLocker",
                "Des partitions BitLocker sont présentes. Elles sont restaurées chiffrées, sans contournement. Si la protection est liée au TPM de l'ordinateur d'origine, la clé de récupération BitLocker sera demandée."));

        if (img.SourceWasLiveSystem)
            f.Add(new(IssueSeverity.Warning, "Image d'un système en cours d'exécution",
                "Cette image a été prise pendant que Windows fonctionnait : des fichiers ouverts peuvent être incohérents (un contrôle de disque peut être nécessaire au premier démarrage)."));

        f.Add(new(IssueSeverity.Info, "Aucune garantie de démarrage",
            "Le démarrage sur un matériel différent ne peut pas être garanti : il doit être validé en pratique. Conservez l'image d'origine."));
        return f;
    }
}
