using System.Security.Cryptography;
using Ghost.Core.Devices;

namespace Ghost.Core.Operations;

public sealed class VerificationFailedException : Exception
{
    public long Offset { get; }
    public VerificationFailedException(string message, long offset) : base(message) => Offset = offset;
}

/// <summary>Primitives communes de lecture/écriture/vérification, avec contrôle d'erreurs.</summary>
internal static class TransferCore
{
    public const int DefaultChunk = 4 * 1024 * 1024;
    private const int ReadAttempts = 3;

    /// <summary>
    /// Lit un bloc avec nouvelles tentatives. En cas d'échec persistant, localise le secteur illisible
    /// et lève une <see cref="DeviceIoException"/> précise : la copie est alors interrompue (jamais de donnée inventée).
    /// </summary>
    public static void ReadResilient(IBlockDevice dev, long offset, Span<byte> buf, ILog log)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                dev.Read(offset, buf);
                return;
            }
            catch (DeviceIoException ex) when (attempt < ReadAttempts)
            {
                log.Warn($"Lecture échouée à {offset} (tentative {attempt}/{ReadAttempts}): {ex.Message}");
                Thread.Sleep(150 * attempt);
            }
            catch (DeviceIoException ex)
            {
                long bad = LocateBadSector(dev, offset, buf.Length);
                log.Error($"Secteur illisible à l'offset {bad}: {ex.Message}");
                throw new DeviceIoException($"Secteur illisible à l'offset {bad} sur {dev.Name}. Opération interrompue : {ex.Message}", bad, true, ex);
            }
        }
    }

    private static long LocateBadSector(IBlockDevice dev, long offset, int length)
    {
        var one = new byte[dev.SectorSize];
        for (long o = offset; o < offset + length; o += dev.SectorSize)
        {
            try { dev.Read(o, one); }
            catch (DeviceIoException) { return o; }
        }
        return offset;
    }

    public static void Write(IBlockDevice dev, long offset, ReadOnlySpan<byte> data, ILog log)
    {
        try
        {
            dev.Write(offset, data);
        }
        catch (DeviceIoException ex)
        {
            log.Error($"Écriture échouée à {offset}: {ex.Message}");
            throw;
        }
    }

    public static void ZeroRange(IBlockDevice dev, long offset, long length, ILog log)
    {
        if (length <= 0) return;
        var z = new byte[(int)Math.Min(length, DefaultChunk)];
        for (long o = 0; o < length; o += z.Length)
        {
            int n = (int)Math.Min(z.Length, length - o);
            Write(dev, offset + o, z.AsSpan(0, n), log);
        }
    }

    /// <summary>
    /// Efface les restes de structures de partitionnement en fin de disque destination
    /// (anciennes GPT de secours) lorsque cette zone n'est pas recouverte par la copie.
    /// </summary>
    public static void ClearTail(IBlockDevice dst, long extentBytes, ILog log)
    {
        long tail = Math.Min(dst.Length, 34L * dst.SectorSize);
        long start = Math.Max(extentBytes, dst.Length - tail);
        if (start < dst.Length) ZeroRange(dst, start, dst.Length - start, log);
    }

    /// <summary>Copie [0,length) de src vers dst et renvoie le SHA-256 de chaque bloc lu.</summary>
    public static List<byte[]> Copy(IBlockDevice src, IBlockDevice dst, long length, int chunk,
        ProgressTracker t, ILog log, CancellationToken ct, long progressBase = 0, bool adjustGpt = false)
    {
        var hashes = new List<byte[]>();
        var buf = new byte[chunk];
        for (long off = 0; off < length; off += chunk)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(chunk, length - off);
            var span = buf.AsSpan(0, n);
            ReadResilient(src, off, span, log);
            if (off == 0 && adjustGpt)
                Ghost.Core.Partitions.GptFixer.AdjustPrimaryInPlace(span, src.SectorSize, dst.Length / dst.SectorSize);
            hashes.Add(SHA256.HashData(span));
            Write(dst, off, span, log);
            t.Report(progressBase + off + n);
        }
        return hashes;
    }

    /// <summary>Relit dev et compare chaque bloc aux empreintes SHA-256 attendues.</summary>
    public static void Verify(IBlockDevice dev, long length, IReadOnlyList<byte[]> expected, int chunk,
        ProgressTracker t, ILog log, CancellationToken ct, long progressBase = 0)
    {
        var buf = new byte[chunk];
        int idx = 0;
        for (long off = 0; off < length; off += chunk, idx++)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(chunk, length - off);
            var span = buf.AsSpan(0, n);
            ReadResilient(dev, off, span, log);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(span), expected[idx]))
            {
                log.Error($"Vérification SHA-256 échouée dans le bloc commençant à l'offset {off}.");
                throw new VerificationFailedException(
                    $"Vérification échouée : les données écrites diffèrent des données lues à l'offset {off}.", off);
            }
            t.Report(progressBase + off + n);
        }
    }

    public static string Hex(byte[] h) => Convert.ToHexString(h).ToLowerInvariant();
}
