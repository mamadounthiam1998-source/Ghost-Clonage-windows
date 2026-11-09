using Ghost.Core.Disks;
using Ghost.Core.Partitions;
using Ghost.Core.Safety;

namespace Ghost.App.ViewModels;

public sealed class DiskItemViewModel
{
    public DiskDescriptor Disk { get; }
    public DiskItemViewModel(DiskDescriptor d) => Disk = d;

    public string Id => Disk.Id;
    public string Title => Disk.DisplayName;
    public string Model => Disk.Model;
    public string Capacity => SafetyGuard.Fmt(Disk.Size);
    public string Used => Disk.UsedBytes >= 0 ? SafetyGuard.Fmt(Disk.UsedBytes) : "Inconnu";
    public string MediaType => Disk.Media switch { MediaKind.Ssd => "SSD", MediaKind.Hdd => "HDD", _ => "Inconnu" };
    public string Partitions => Disk.PartitionCount.ToString();
    public string Table => Disk.Style switch { PartitionStyle.Gpt => "GPT", PartitionStyle.Mbr => "MBR", _ => "Aucune" };
    public string Bus => Disk.BusType;
    public bool IsSystem => Disk.IsSystemDisk;
    public bool CanBeDestination => !Disk.IsSystemDisk;

    /// <summary>Texte repris dans la confirmation d'effacement.</summary>
    public string Describe() =>
        $"{Disk.DisplayName} — {Disk.Model} — {Capacity} — {Disk.PartitionCount} partition(s)";
}
