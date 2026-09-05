using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SysCmd.Core.Theming;

/// <summary>
/// A minimal indexed-colour PNG writer.
///
/// Backdrops have to be recoloured for whichever palette is showing, and forty-odd palettes times
/// thirty backdrops is far too many files to keep on disk - so they are encoded on demand, the same
/// way dtwm re-tints the pixmap when it loads it. Indexed PNG is the natural output: the image is
/// already a palette plus indices, so encoding is a palette table and one filter byte per row, and
/// deflate handles the rest. Everything needed is in the framework, so this costs no dependency.
/// </summary>
public static class IndexedPng
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a];

    public static byte[] Encode(int width, int height, byte[] pixels, IReadOnlyList<Rgb> palette)
    {
        if (palette.Count is 0 or > 256) throw new ArgumentException("a PNG palette holds 1 to 256 entries", nameof(palette));

        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bits per index
        header[9] = 3;  // colour type 3: indexed
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlacing
        WriteChunk(output, "IHDR", header);

        var plte = new byte[palette.Count * 3];
        for (var i = 0; i < palette.Count; i++)
        {
            // The 16-bit channels narrow the way X does, by taking the high byte.
            plte[i * 3] = (byte)(palette[i].R >> 8);
            plte[i * 3 + 1] = (byte)(palette[i].G >> 8);
            plte[i * 3 + 2] = (byte)(palette[i].B >> 8);
        }
        WriteChunk(output, "PLTE", plte);

        WriteChunk(output, "IDAT", Deflate(width, height, pixels));
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    /// <summary>
    /// zlib-wrapped scanlines, each introduced by a filter-type byte. Filter 0 (none) is the right
    /// choice for indexed data: the bytes are palette indices, so the differences a filter would
    /// take are meaningless and usually compress worse.
    /// </summary>
    private static byte[] Deflate(int width, int height, byte[] pixels)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[width + 1];
            for (var y = 0; y < height; y++)
            {
                row[0] = 0;
                pixels.AsSpan(y * width, width).CopyTo(row.AsSpan(1));
                zlib.Write(row);
            }
        }
        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var name = Encoding.ASCII.GetBytes(type);
        output.Write(name);
        output.Write(data);

        var crc = Crc32.Of(name, data);
        Span<byte> check = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(check, crc);
        output.Write(check);
    }
}

/// <summary>The CRC-32 PNG puts on every chunk.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static uint Of(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var c = 0xffffffffu;
        foreach (var b in first) c = Table[(c ^ b) & 0xff] ^ (c >> 8);
        foreach (var b in second) c = Table[(c ^ b) & 0xff] ^ (c >> 8);
        return c ^ 0xffffffffu;
    }
}
