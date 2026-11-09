namespace Ghost.Core.Devices;

/// <summary>
/// Périphérique bloc (disque physique, disque virtuel fichier…).
/// Tous les offsets et tailles doivent être alignés sur <see cref="SectorSize"/>,
/// comme pour un vrai disque physique sous Windows.
/// </summary>
public interface IBlockDevice : IDisposable
{
    string Name { get; }
    long Length { get; }
    int SectorSize { get; }

    /// <summary>Lit exactement buffer.Length octets. Lève <see cref="DeviceIoException"/> en cas d'erreur.</summary>
    void Read(long offset, Span<byte> buffer);

    /// <summary>Écrit exactement buffer.Length octets. Lève <see cref="DeviceIoException"/> en cas d'erreur.</summary>
    void Write(long offset, ReadOnlySpan<byte> buffer);

    void Flush();
}

public sealed class DeviceIoException : IOException
{
    public long Offset { get; }
    public bool IsRead { get; }

    public DeviceIoException(string message, long offset, bool isRead, Exception? inner = null)
        : base(message, inner)
    {
        Offset = offset;
        IsRead = isRead;
    }
}
