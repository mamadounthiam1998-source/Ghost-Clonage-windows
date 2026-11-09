using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ghost.Core.Devices;
using Microsoft.Win32.SafeHandles;

namespace Ghost.Windows;

/// <summary>Disque physique (\\.\PhysicalDriveN) lu/écrit via les API Win32 natives.</summary>
[SupportedOSPlatform("windows")]
public sealed class PhysicalDiskDevice : IBlockDevice
{
    private readonly SafeFileHandle _handle;
    private readonly bool _writable;
    private readonly List<SafeFileHandle> _volumeLocks;

    public string Name { get; }
    public long Length { get; }
    public int SectorSize { get; }

    internal PhysicalDiskDevice(string name, SafeFileHandle handle, long length, int sectorSize, bool writable,
        List<SafeFileHandle>? volumeLocks = null)
    {
        Name = name; _handle = handle; Length = length; SectorSize = sectorSize; _writable = writable;
        _volumeLocks = volumeLocks ?? new();
    }

    private void CheckRange(long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), "Accès hors des limites du disque.");
        if (offset % SectorSize != 0 || count % SectorSize != 0)
            throw new ArgumentException("Accès non aligné sur la taille de secteur.");
    }

    public void Read(long offset, Span<byte> buffer)
    {
        CheckRange(offset, buffer.Length);
        if (buffer.Length == 0) return;
        if (!NativeMethods.SetFilePointerEx(_handle, offset, out _, 0))
            throw new DeviceIoException($"Positionnement impossible à {offset}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}", offset, true);

        int total = 0;
        while (total < buffer.Length)
        {
            ref byte first = ref MemoryMarshal.GetReference(buffer[total..]);
            if (!NativeMethods.ReadFile(_handle, ref first, buffer.Length - total, out int n, IntPtr.Zero))
                throw new DeviceIoException(
                    $"Erreur de lecture à l'offset {offset + total}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}",
                    offset + total, true);
            if (n == 0)
                throw new DeviceIoException($"Lecture tronquée à l'offset {offset + total}.", offset + total, true);
            total += n;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> buffer)
    {
        if (!_writable) throw new InvalidOperationException("Disque ouvert en lecture seule.");
        CheckRange(offset, buffer.Length);
        if (buffer.Length == 0) return;
        if (!NativeMethods.SetFilePointerEx(_handle, offset, out _, 0))
            throw new DeviceIoException($"Positionnement impossible à {offset}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}", offset, false);

        int total = 0;
        while (total < buffer.Length)
        {
            ref byte first = ref Unsafe.AsRef(in MemoryMarshal.GetReference(buffer[total..]));
            if (!NativeMethods.WriteFile(_handle, ref first, buffer.Length - total, out int n, IntPtr.Zero))
                throw new DeviceIoException(
                    $"Erreur d'écriture à l'offset {offset + total}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}",
                    offset + total, false);
            if (n == 0)
                throw new DeviceIoException($"Écriture tronquée à l'offset {offset + total}.", offset + total, false);
            total += n;
        }
    }

    public void Flush()
    {
        if (_writable && !NativeMethods.FlushFileBuffers(_handle))
            throw new DeviceIoException($"Vidage des tampons impossible: {new Win32Exception(Marshal.GetLastWin32Error()).Message}", 0, false);
    }

    public void Dispose()
    {
        _handle.Dispose();
        foreach (var v in _volumeLocks) v.Dispose();   // libère les verrous de volumes
        _volumeLocks.Clear();
    }
}
