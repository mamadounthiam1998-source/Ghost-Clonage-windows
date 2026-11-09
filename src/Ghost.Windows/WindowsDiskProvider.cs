using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Ghost.Core.Devices;
using Ghost.Core.Disks;
using Ghost.Core.Partitions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using static Ghost.Windows.NativeMethods;

namespace Ghost.Windows;

/// <summary>
/// Détection et accès aux disques physiques Windows par API natives (DeviceIoControl).
/// Les droits administrateur sont requis pour lire/écrire les disques bruts.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiskProvider : IDiskProvider
{
    private const int MaxDisks = 128;

    private sealed record VolumeInfo(string GuidPath, long UsedBytes, IReadOnlyList<int> DiskNumbers);

    // ---------- Énumération ----------

    public IReadOnlyList<DiskDescriptor> ListDisks()
    {
        var volumes = EnumerateVolumes();
        var systemDisks = GetSystemDiskNumbers();
        var list = new List<DiskDescriptor>();

        for (int n = 0; n < MaxDisks; n++)
        {
            var d = Describe(n, volumes, systemDisks);
            if (d != null) list.Add(d);
        }
        return list;
    }

    private static DiskDescriptor? Describe(int n, List<VolumeInfo> volumes, HashSet<int> systemDisks)
    {
        using var h = CreateFileW(PhysicalPath(n), 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;

        // Géométrie : taille et taille de secteur
        var geo = new byte[256];
        if (!IoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, geo, out _)) return null;
        int sector = BinaryPrimitives.ReadInt32LittleEndian(geo.AsSpan(20, 4));
        long size = BinaryPrimitives.ReadInt64LittleEndian(geo.AsSpan(24, 8));
        if (size <= 0 || sector <= 0) return null;

        // Descripteur de stockage : fabricant, modèle, bus, amovible
        string vendor = "", product = "", bus = "Inconnu";
        bool removable = false;
        var q = new byte[12]; // STORAGE_PROPERTY_QUERY : PropertyId=0 (StorageDeviceProperty), QueryType=0 (Standard)
        var desc = new byte[1024];
        if (IoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, q, desc, out _))
        {
            removable = desc[10] != 0;
            vendor = ReadAscii(desc, BinaryPrimitives.ReadInt32LittleEndian(desc.AsSpan(12, 4)));
            product = ReadAscii(desc, BinaryPrimitives.ReadInt32LittleEndian(desc.AsSpan(16, 4)));
            bus = BusName(BinaryPrimitives.ReadInt32LittleEndian(desc.AsSpan(28, 4)));
        }

        // Type de support : SSD si pas de pénalité de recherche
        var media = MediaKind.Unknown;
        var sq = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(sq.AsSpan(0, 4), 7); // StorageDeviceSeekPenaltyProperty
        var sp = new byte[64];
        if (IoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, sq, sp, out int ret) && ret >= 9)
            media = sp[8] == 0 ? MediaKind.Ssd : MediaKind.Hdd;
        if (bus == "NVMe") media = MediaKind.Ssd;

        // Table de partitions (lecture réelle)
        PartitionStyle style = PartitionStyle.Unknown;
        int parts = 0;
        try
        {
            using var dev = OpenInternal(n, false, size, sector);
            var layout = PartitionTableReader.Read(dev);
            style = layout.Style;
            parts = layout.Partitions.Count;
        }
        catch { /* droits insuffisants ou disque illisible : valeurs inconnues */ }

        long used = 0;
        bool anyUsed = false;
        foreach (var v in volumes.Where(v => v.DiskNumbers.Contains(n)))
        {
            used += v.UsedBytes;
            anyUsed = true;
        }

        string model = $"{vendor} {product}".Trim();
        if (model.Length == 0) model = "Disque inconnu";
        return new DiskDescriptor($"PhysicalDrive{n}", $"Disque {n}", model, size, sector, media, style, parts,
            anyUsed ? used : -1, systemDisks.Contains(n), removable || bus == "USB", bus);
    }

    private static string ReadAscii(byte[] buf, int offset)
    {
        if (offset <= 0 || offset >= buf.Length) return "";
        int end = Array.IndexOf(buf, (byte)0, offset);
        if (end < 0) end = buf.Length;
        return Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
    }

    private static string BusName(int t) => t switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 6 => "Fibre Channel", 7 => "USB",
        8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC",
        14 => "Virtuel", 15 => "Virtuel", 16 => "Espaces de stockage", 17 => "NVMe", 18 => "SCM", 19 => "UFS",
        _ => "Inconnu"
    };

    private static string PhysicalPath(int n) => $@"\\.\PhysicalDrive{n}";

    private static int ParseId(string id)
    {
        const string prefix = "PhysicalDrive";
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(id[prefix.Length..], out int n) || n < 0)
            throw new ArgumentException("Identifiant de disque invalide.", nameof(id));
        return n;
    }

    // ---------- Volumes ----------

    private static List<VolumeInfo> EnumerateVolumes()
    {
        var result = new List<VolumeInfo>();
        var sb = new StringBuilder(260);
        IntPtr find = FindFirstVolumeW(sb, sb.Capacity);
        if (find == new IntPtr(-1)) return result;
        try
        {
            do
            {
                string guidPath = sb.ToString();                   // \\?\Volume{GUID}\
                var disks = DiskNumbersOfVolume(guidPath.TrimEnd('\\'));
                long used = 0;
                if (GetDiskFreeSpaceExW(guidPath, out _, out ulong total, out ulong free))
                    used = (long)(total - free);
                result.Add(new VolumeInfo(guidPath, used, disks));
            } while (FindNextVolumeW(find, sb, sb.Capacity));
        }
        finally { FindVolumeClose(find); }
        return result;
    }

    private static List<int> DiskNumbersOfVolume(string volumePathNoSlash)
    {
        var res = new List<int>();
        using var h = CreateFileW(volumePathNoSlash, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return res;
        var buf = new byte[8 + 24 * 32];
        if (!IoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, buf, out _)) return res;
        int count = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(0, 4));
        for (int i = 0; i < count && 8 + (i + 1) * 24 <= buf.Length; i++)
            res.Add(BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8 + i * 24, 4)));
        return res;
    }

    /// <summary>Disques hébergeant le Windows en cours d'exécution (volume système et disque entier).</summary>
    private static HashSet<int> GetSystemDiskNumbers()
    {
        var set = new HashSet<int>();
        string? root = Path.GetPathRoot(Environment.SystemDirectory);   // ex. C:\
        if (!string.IsNullOrEmpty(root))
            foreach (var n in DiskNumbersOfVolume(@"\\.\" + root.TrimEnd('\\'))) set.Add(n);
        return set;
    }

    // ---------- Ouverture ----------

    public IBlockDevice OpenRead(string id)
    {
        int n = ParseId(id);
        var (size, sector) = Geometry(n);
        return OpenInternal(n, false, size, sector);
    }

    private static (long size, int sector) Geometry(int n)
    {
        using var h = CreateFileW(PhysicalPath(n), 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) throw LastError($"Disque {n} introuvable");
        var geo = new byte[256];
        if (!IoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, geo, out _)) throw LastError($"Géométrie du disque {n} illisible");
        return (BinaryPrimitives.ReadInt64LittleEndian(geo.AsSpan(24, 8)), BinaryPrimitives.ReadInt32LittleEndian(geo.AsSpan(20, 4)));
    }

    private static PhysicalDiskDevice OpenInternal(int n, bool write, long size, int sector, List<SafeFileHandle>? locks = null)
    {
        uint access = write ? GENERIC_READ | GENERIC_WRITE : GENERIC_READ;
        uint flags = write ? FILE_FLAG_WRITE_THROUGH : 0;
        var h = CreateFileW(PhysicalPath(n), access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var ex = LastError($"Ouverture du disque {n} impossible (droits administrateur requis ?)");
            if (ex.NativeErrorCode == 5) throw new UnauthorizedAccessException("Accès refusé au disque " + n, ex);
            throw ex;
        }
        return new PhysicalDiskDevice(PhysicalPath(n), h, size, sector, write, locks);
    }

    /// <summary>
    /// Ouverture en écriture exclusive : verrouille et démonte chaque volume du disque, supprime l'ancienne
    /// table de partitions, puis ouvre le disque brut. N'est appelé qu'après confirmation explicite.
    /// </summary>
    public IBlockDevice OpenWrite(string id)
    {
        int n = ParseId(id);

        // Défense en profondeur : jamais d'écriture sur le disque système
        if (GetSystemDiskNumbers().Contains(n))
            throw new InvalidOperationException("Écriture refusée : ce disque contient le Windows en cours d'exécution.");

        var (size, sector) = Geometry(n);
        var locks = new List<SafeFileHandle>();
        try
        {
            foreach (var v in EnumerateVolumes().Where(v => v.DiskNumbers.Contains(n)))
            {
                var vh = CreateFileW(v.GuidPath.TrimEnd('\\'), GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (vh.IsInvalid) continue;   // volume absent / non montable : rien à verrouiller

                bool locked = false;
                for (int attempt = 0; attempt < 10 && !locked; attempt++)
                {
                    locked = IoControl(vh, FSCTL_LOCK_VOLUME, null, Array.Empty<byte>(), out _);
                    if (!locked) Thread.Sleep(300);
                }
                if (!locked)
                {
                    vh.Dispose();
                    throw new IOException("Un volume du disque destination est utilisé par un programme. Fermez les programmes qui l'utilisent (Explorateur, antivirus…) et recommencez.");
                }
                IoControl(vh, FSCTL_DISMOUNT_VOLUME, null, Array.Empty<byte>(), out _);
                locks.Add(vh);
            }

            // Suppression de l'ancienne table (les écritures sur un disque dont une partition est montée sont refusées par Windows)
            using (var lh = CreateFileW(PhysicalPath(n), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                       IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (lh.IsInvalid) throw LastError($"Ouverture du disque {n} impossible");
                IoControl(lh, IOCTL_DISK_DELETE_DRIVE_LAYOUT, null, Array.Empty<byte>(), out _);
                IoControl(lh, IOCTL_DISK_UPDATE_PROPERTIES, null, Array.Empty<byte>(), out _);
            }

            return OpenInternal(n, true, size, sector, locks);
        }
        catch
        {
            foreach (var l in locks) l.Dispose();
            throw;
        }
    }

    public void NotifyLayoutChanged(string id)
    {
        int n = ParseId(id);
        using var h = CreateFileW(PhysicalPath(n), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return;
        IoControl(h, IOCTL_DISK_UPDATE_PROPERTIES, null, Array.Empty<byte>(), out _);
    }

    public bool IsPathOnDisk(string filePath, string diskId)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(filePath));
            if (string.IsNullOrEmpty(root)) return false;
            if (root.StartsWith(@"\\")) return false; // chemin réseau
            int n = ParseId(diskId);
            return DiskNumbersOfVolume(@"\\.\" + root.TrimEnd('\\')).Contains(n);
        }
        catch { return false; }
    }

    // ---------- Machine ----------

    public MachineInfo GetMachineInfo()
    {
        string Read(string name)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                return (k?.GetValue(name) as string)?.Trim() ?? "";
            }
            catch { return ""; }
        }

        string fw = "Inconnu";
        try
        {
            if (GetFirmwareType(out int t)) fw = t == 1 ? "BIOS" : t == 2 ? "UEFI" : "Inconnu";
        }
        catch { /* API indisponible */ }

        return new MachineInfo(Read("SystemManufacturer"), Read("SystemProductName"), Read("BaseBoardProduct"),
            fw, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString());
    }
}
