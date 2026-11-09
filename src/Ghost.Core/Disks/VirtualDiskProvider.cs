using Ghost.Core.Devices;
using Ghost.Core.Partitions;

namespace Ghost.Core.Disks;

/// <summary>
/// Fournisseur de disques virtuels (fichiers .img). Permet de valider tous les moteurs
/// sur des disques virtuels avant toute utilisation sur du matériel réel.
/// </summary>
public sealed class VirtualDiskProvider : IDiskProvider
{
    private readonly Dictionary<string, string> _paths = new();   // id -> chemin
    public HashSet<string> SystemDiskIds { get; } = new();
    public Func<string, IBlockDevice, IBlockDevice>? WriteWrapper { get; set; }
    public Func<string, IBlockDevice, IBlockDevice>? ReadWrapper { get; set; }
    public List<string> LayoutNotifications { get; } = new();
    public MachineInfo Machine { get; set; } =
        new("Virtuel", "Disque virtuel", "N/A", "UEFI", "Test", "X64");

    public string Add(string id, string path)
    {
        _paths[id] = path;
        return id;
    }

    public IReadOnlyList<DiskDescriptor> ListDisks()
    {
        var list = new List<DiskDescriptor>();
        foreach (var (id, path) in _paths)
        {
            using var dev = FileBlockDevice.Open(path, false, SectorSizeFor(id));
            var layout = PartitionTableReader.Read(dev);
            list.Add(new DiskDescriptor(id, id, Path.GetFileName(path), dev.Length, dev.SectorSize,
                MediaKind.Ssd, layout.Style, layout.Partitions.Count, -1,
                SystemDiskIds.Contains(id), false, "Virtuel"));
        }
        return list;
    }

    public Dictionary<string, int> SectorSizes { get; } = new();
    private int SectorSizeFor(string id) => SectorSizes.TryGetValue(id, out var s) ? s : 512;

    public IBlockDevice OpenRead(string id)
    {
        IBlockDevice d = FileBlockDevice.Open(_paths[id], false, SectorSizeFor(id));
        return ReadWrapper?.Invoke(id, d) ?? d;
    }

    public IBlockDevice OpenWrite(string id)
    {
        if (SystemDiskIds.Contains(id))
            throw new InvalidOperationException("Écriture refusée sur le disque système.");
        IBlockDevice d = FileBlockDevice.Open(_paths[id], true, SectorSizeFor(id));
        return WriteWrapper?.Invoke(id, d) ?? d;
    }

    public void NotifyLayoutChanged(string id) => LayoutNotifications.Add(id);

    public bool IsPathOnDisk(string filePath, string diskId) => false;

    public MachineInfo GetMachineInfo() => Machine;
}
