using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ghost.Core.Imaging;

public sealed class ImageWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly IncrementalHash _overall = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly int _quality;
    private readonly byte[] _compressBuf;
    private readonly long _extentLength;
    private long _chunks;
    private long _rawTotal;
    private bool _completed;

    public string Path { get; }

    public ImageWriter(string path, int sectorSize, int chunkSize, long diskSize, long extentLength, int brotliQuality = 4)
    {
        Path = path;
        _quality = brotliQuality;
        _extentLength = extentLength;
        _compressBuf = new byte[BrotliEncoder.GetMaxCompressedLength(chunkSize)];
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);

        var h = new byte[ImageFormat.HeaderSize];
        ImageFormat.Magic.CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), ImageFormat.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), (uint)sectorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), (uint)chunkSize);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(24), diskSize);
        BinaryPrimitives.WriteInt64LittleEndian(h.AsSpan(32), extentLength);
        _fs.Write(h);
    }

    /// <summary>Ajoute un bloc de données brutes. Retourne le SHA-256 du bloc.</summary>
    public byte[] WriteChunk(ReadOnlySpan<byte> raw)
    {
        var hash = SHA256.HashData(raw);
        _overall.AppendData(raw);

        byte type;
        ReadOnlySpan<byte> payload;
        if (!raw.ContainsAnyExcept((byte)0))
        {
            type = ImageFormat.ChunkZero;
            payload = ReadOnlySpan<byte>.Empty;
        }
        else if (BrotliEncoder.TryCompress(raw, _compressBuf, out int written, _quality, 22) && written < raw.Length)
        {
            type = ImageFormat.ChunkBrotli;
            payload = _compressBuf.AsSpan(0, written);
        }
        else
        {
            type = ImageFormat.ChunkRaw;
            payload = raw;
        }

        Span<byte> hdr = stackalloc byte[ImageFormat.ChunkHeaderSize];
        hdr[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[1..], (uint)raw.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[5..], (uint)payload.Length);
        hash.CopyTo(hdr[9..]);
        _fs.Write(hdr);
        _fs.Write(payload);

        _chunks++;
        _rawTotal += raw.Length;
        return hash;
    }

    /// <summary>Écrit marqueur de fin, métadonnées et pied. Retourne le SHA-256 global des données.</summary>
    public byte[] Complete(ImageMetadata meta)
    {
        if (_rawTotal != _extentLength)
            throw new InvalidOperationException($"Taille écrite ({_rawTotal}) différente de la taille annoncée ({_extentLength}).");

        _fs.WriteByte(ImageFormat.EndMarker);
        long metaOffset = _fs.Position;
        var json = JsonSerializer.SerializeToUtf8Bytes(meta, ImageJson.Options);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)json.Length);
        _fs.Write(len);
        _fs.Write(json);

        var overall = _overall.GetHashAndReset();
        var footer = new byte[ImageFormat.FooterSize];
        ImageFormat.FooterMagic.CopyTo(footer, 0);
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(8), _chunks);
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(16), _rawTotal);
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(24), metaOffset);
        overall.CopyTo(footer.AsSpan(32));
        SHA256.HashData(footer.AsSpan(0, 64)).CopyTo(footer.AsSpan(64));
        _fs.Write(footer);

        _fs.Flush(true);
        _completed = true;
        return overall;
    }

    public void Dispose()
    {
        _overall.Dispose();
        _fs.Dispose();
        _ = _completed;
    }
}

internal static class ImageJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
