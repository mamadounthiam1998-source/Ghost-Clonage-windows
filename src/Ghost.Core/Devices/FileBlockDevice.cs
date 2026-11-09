using Microsoft.Win32.SafeHandles;

namespace Ghost.Core.Devices;

/// <summary>
/// Disque virtuel adossé à un fichier (fichier creux). Sert aux tests et à la validation
/// des moteurs avant toute utilisation sur un disque physique.
/// Applique les mêmes contraintes d'alignement qu'un disque physique.
/// </summary>
public sealed class FileBlockDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;
    private readonly bool _writable;

    public string Name { get; }
    public long Length { get; }
    public int SectorSize { get; }

    private FileBlockDevice(string path, SafeFileHandle handle, long length, int sectorSize, bool writable)
    {
        Name = path;
        _handle = handle;
        Length = length;
        SectorSize = sectorSize;
        _writable = writable;
    }

    public static FileBlockDevice Create(string path, long length, int sectorSize = 512)
    {
        if (length <= 0 || length % sectorSize != 0)
            throw new ArgumentException("La taille doit être un multiple positif de la taille de secteur.", nameof(length));
        var h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        RandomAccess.SetLength(h, length);
        return new FileBlockDevice(path, h, length, sectorSize, true);
    }

    public static FileBlockDevice Open(string path, bool writable, int sectorSize = 512)
    {
        var h = File.OpenHandle(path, FileMode.Open,
            writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);
        long len = RandomAccess.GetLength(h);
        if (len % sectorSize != 0)
        {
            h.Dispose();
            throw new InvalidDataException("La taille du fichier n'est pas un multiple de la taille de secteur.");
        }
        return new FileBlockDevice(path, h, len, sectorSize, writable);
    }

    private void CheckRange(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Accès hors des limites du périphérique.");
        if (offset % SectorSize != 0 || count % SectorSize != 0)
            throw new ArgumentException("Accès non aligné sur la taille de secteur.");
    }

    public void Read(long offset, Span<byte> buffer)
    {
        CheckRange(offset, buffer.Length);
        try
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = RandomAccess.Read(_handle, buffer[total..], offset + total);
                if (n == 0)
                {
                    buffer[total..].Clear(); // zone creuse au-delà de la fin physique du fichier
                    break;
                }
                total += n;
            }
        }
        catch (IOException ex)
        {
            throw new DeviceIoException($"Erreur de lecture à l'offset {offset}: {ex.Message}", offset, true, ex);
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!_writable) throw new InvalidOperationException("Périphérique ouvert en lecture seule.");
        CheckRange(offset, buffer.Length);
        try
        {
            RandomAccess.Write(_handle, buffer, offset);
        }
        catch (IOException ex)
        {
            throw new DeviceIoException($"Erreur d'écriture à l'offset {offset}: {ex.Message}", offset, false, ex);
        }
    }

    public void Flush()
    {
        if (_writable) RandomAccess.FlushToDisk(_handle);
    }

    public void Dispose() => _handle.Dispose();
}
