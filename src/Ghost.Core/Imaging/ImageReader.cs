using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Ghost.Core.Operations;

namespace Ghost.Core.Imaging;

public readonly record struct ChunkInfo(long Offset, int RawLength, byte[] Hash, bool IsZero);

public sealed record ImageVerifyResult(bool IsValid, string Message);

public sealed class ImageReader : IDisposable
{
    private readonly FileStream _fs;
    private readonly byte[] _storedBuf;
    private long _nextOffset;

    public string Path { get; }
    public int SectorSize { get; }
    public int ChunkSize { get; }
    public long DiskSize { get; }
    public long ExtentLength { get; }
    public long ChunkCount { get; }
    public long FileSize { get; }
    public byte[] OverallHash { get; }
    public ImageMetadata Metadata { get; }

    private ImageReader(string path, FileStream fs, int ss, int cs, long disk, long extent, long chunks,
        byte[] overall, ImageMetadata meta)
    {
        Path = path; _fs = fs; SectorSize = ss; ChunkSize = cs; DiskSize = disk; ExtentLength = extent;
        ChunkCount = chunks; OverallHash = overall; Metadata = meta; FileSize = fs.Length;
        _storedBuf = new byte[BrotliEncoder.GetMaxCompressedLength(cs) + 64];
        _fs.Position = ImageFormat.HeaderSize;
    }

    /// <summary>Ouvre une image. Lève <see cref="InvalidDataException"/> si elle est incomplète, tronquée ou corrompue.</summary>
    public static ImageReader Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        try
        {
            if (fs.Length < ImageFormat.HeaderSize + 1 + 4 + ImageFormat.FooterSize)
                throw new InvalidDataException("Fichier trop court : image incomplète.");

            var h = new byte[ImageFormat.HeaderSize];
            fs.ReadExactly(h);
            if (!h.AsSpan(0, 8).SequenceEqual(ImageFormat.Magic))
                throw new InvalidDataException("Ce fichier n'est pas une image GHOST CLONAGE.");
            uint ver = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8));
            if (ver != ImageFormat.Version)
                throw new InvalidDataException($"Version d'image non prise en charge ({ver}).");
            int ss = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12));
            int cs = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16));
            long disk = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(24));
            long extent = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(32));
            if (ss is not (512 or 1024 or 2048 or 4096) || cs <= 0 || cs % ss != 0 || cs > 256 * 1024 * 1024 || extent <= 0)
                throw new InvalidDataException("En-tête d'image invalide.");

            fs.Position = fs.Length - ImageFormat.FooterSize;
            var f = new byte[ImageFormat.FooterSize];
            fs.ReadExactly(f);
            if (!f.AsSpan(0, 8).SequenceEqual(ImageFormat.FooterMagic))
                throw new InvalidDataException("Pied d'image absent : l'image est incomplète ou a été interrompue. Elle n'est pas une sauvegarde valide.");
            if (!SHA256.HashData(f.AsSpan(0, 64)).AsSpan().SequenceEqual(f.AsSpan(64, 32)))
                throw new InvalidDataException("Pied d'image corrompu.");

            long chunks = BinaryPrimitives.ReadInt64LittleEndian(f.AsSpan(8));
            long fExtent = BinaryPrimitives.ReadInt64LittleEndian(f.AsSpan(16));
            long metaOff = BinaryPrimitives.ReadInt64LittleEndian(f.AsSpan(24));
            if (fExtent != extent)
                throw new InvalidDataException("En-tête et pied incohérents.");
            if (metaOff < ImageFormat.HeaderSize || metaOff + 4 > fs.Length - ImageFormat.FooterSize)
                throw new InvalidDataException("Offset des métadonnées invalide.");

            fs.Position = metaOff;
            var lenBuf = new byte[4];
            fs.ReadExactly(lenBuf);
            uint metaLen = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
            if (metaLen == 0 || metaOff + 4 + metaLen != fs.Length - ImageFormat.FooterSize)
                throw new InvalidDataException("Métadonnées d'image invalides.");
            var json = new byte[metaLen];
            fs.ReadExactly(json);
            var meta = JsonSerializer.Deserialize<ImageMetadata>(json, ImageJson.Options)
                       ?? throw new InvalidDataException("Métadonnées illisibles.");
            if (meta.Layout == null || meta.Machine == null)
                throw new InvalidDataException("Métadonnées incomplètes.");

            return new ImageReader(path, fs, ss, cs, disk, extent, chunks, f.AsSpan(32, 32).ToArray(), meta);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public void Rewind()
    {
        _fs.Position = ImageFormat.HeaderSize;
        _nextOffset = 0;
    }

    /// <summary>Lit le bloc suivant dans <paramref name="buffer"/> (≥ ChunkSize). Faux à la fin des blocs.</summary>
    public bool TryReadNext(byte[] buffer, out ChunkInfo info)
    {
        int type = _fs.ReadByte();
        if (type == ImageFormat.EndMarker)
        {
            info = default;
            return false;
        }
        if (type < 0) throw new InvalidDataException("Fin de fichier inattendue dans les blocs.");

        var hdr = new byte[ImageFormat.ChunkHeaderSize - 1];
        _fs.ReadExactly(hdr);
        int raw = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(0, 4));
        int stored = (int)BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4, 4));
        var hash = hdr.AsSpan(8, 32).ToArray();
        if (raw <= 0 || raw > ChunkSize || raw > buffer.Length || stored < 0 || stored > _storedBuf.Length)
            throw new InvalidDataException("En-tête de bloc invalide.");

        switch (type)
        {
            case ImageFormat.ChunkZero:
                if (stored != 0) throw new InvalidDataException("Bloc nul invalide.");
                buffer.AsSpan(0, raw).Clear();
                break;
            case ImageFormat.ChunkRaw:
                if (stored != raw) throw new InvalidDataException("Bloc brut invalide.");
                _fs.ReadExactly(buffer.AsSpan(0, raw));
                break;
            case ImageFormat.ChunkBrotli:
                _fs.ReadExactly(_storedBuf.AsSpan(0, stored));
                if (!BrotliDecoder.TryDecompress(_storedBuf.AsSpan(0, stored), buffer.AsSpan(0, raw), out int w) || w != raw)
                    throw new InvalidDataException("Échec de décompression d'un bloc.");
                break;
            default:
                throw new InvalidDataException($"Type de bloc inconnu ({type}).");
        }

        info = new ChunkInfo(_nextOffset, raw, hash, type == ImageFormat.ChunkZero);
        _nextOffset += raw;
        return true;
    }

    /// <summary>
    /// Vérification complète : relit toute l'image, contrôle le SHA-256 de chaque bloc et le SHA-256 global.
    /// </summary>
    public ImageVerifyResult Verify(ProgressTracker? tracker, CancellationToken ct, long progressBase = 0)
    {
        Rewind();
        var buf = new byte[ChunkSize];
        using var overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0, total = 0;
        try
        {
            while (TryReadNext(buf, out var c))
            {
                ct.ThrowIfCancellationRequested();
                var span = buf.AsSpan(0, c.RawLength);
                if (!SHA256.HashData(span).AsSpan().SequenceEqual(c.Hash))
                    return new(false, $"Bloc {count} corrompu (offset {c.Offset}).");
                overall.AppendData(span);
                count++;
                total += c.RawLength;
                tracker?.Report(progressBase + total);
            }
        }
        catch (InvalidDataException ex)
        {
            return new(false, ex.Message);
        }
        catch (EndOfStreamException)
        {
            return new(false, "Fin de fichier inattendue.");
        }

        if (count != ChunkCount) return new(false, "Nombre de blocs incohérent avec le pied de l'image.");
        if (total != ExtentLength) return new(false, "Taille des données incohérente avec le pied de l'image.");
        if (!overall.GetHashAndReset().AsSpan().SequenceEqual(OverallHash))
            return new(false, "Le SHA-256 global ne correspond pas : l'image est corrompue.");
        return new(true, "Image intègre.");
    }

    public void Dispose() => _fs.Dispose();
}
