using Ghost.Core.Imaging;
using Ghost.Core.Safety;

namespace Ghost.App.ViewModels;

public sealed class RestorePointItem
{
    public RestorePoint Point { get; }
    public RestorePointItem(RestorePoint p) => Point = p;

    public string Path => Point.Path;
    public string Label => Point.Label;
    public string DateText => $"{Point.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm}  ·  {SafetyGuard.Fmt(Point.FileSize)}";
    public string Summary => $"{Point.PartitionCount} partition(s) : {Point.PartitionSummary}";
    public string Origin => $"{Point.SourceModel} — {SafetyGuard.Fmt(Point.SourceDiskSize)}{(Point.FullDisk ? " — disque entier" : "")}";
}
