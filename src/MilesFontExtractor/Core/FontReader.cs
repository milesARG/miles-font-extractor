using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MilesFontExtractor.Core;

public sealed record FontInfo(string Family, string FullName, string Vendor, string Copyright, string Format);

/// <summary>
/// Reads just enough of a font file to prove it is a real font and to get its names.
/// Returns null for anything that is not a valid font, so renamed or corrupt files never get through.
/// </summary>
public static class FontReader
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf", ".otf", ".ttc", ".otc", ".woff", ".woff2", ".pfb", ".fon"
    };

    public static FontInfo? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            if (fs.Length < 32) return null;

            Span<byte> head = stackalloc byte[12];
            if (fs.Read(head) < 12) return null;
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(head);
            string fallback = Path.GetFileNameWithoutExtension(path);

            switch (tag)
            {
                case 0x00010000:
                case 0x74727565: // true
                case 0x4F54544F: // OTTO
                case 0x74797031: // typ1
                    return ReadSfnt(fs, 0, tag == 0x4F54544F ? "OpenType" : "TrueType", fallback);

                case 0x74746366: // ttcf
                {
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(head[8..]);
                    if (count is 0 or > 256) return null;
                    fs.Position = 12;
                    Span<byte> off = stackalloc byte[4];
                    if (fs.Read(off) < 4) return null;
                    uint first = BinaryPrimitives.ReadUInt32BigEndian(off);
                    if (first >= fs.Length) return null;
                    return ReadSfnt(fs, first, "Collection", fallback);
                }

                case 0x774F4646: // wOFF
                    return ReadWoff(fs, fallback);

                case 0x774F4632: // wOF2 (compressed with Brotli; names are not read, signature is enough)
                    return new FontInfo(fallback, fallback, "", "", "WOFF2");
            }

            // Type 1 (.pfb) starts with 0x80 0x01; Windows bitmap fonts (.fon) are MZ executables.
            string ext = Path.GetExtension(path);
            if (head[0] == 0x80 && head[1] == 0x01 && ext.Equals(".pfb", StringComparison.OrdinalIgnoreCase))
                return new FontInfo(fallback, fallback, "", "", "Type 1");
            if (head[0] == 'M' && head[1] == 'Z' && ext.Equals(".fon", StringComparison.OrdinalIgnoreCase))
                return new FontInfo(fallback, fallback, "", "", "Bitmap");
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static FontInfo? ReadSfnt(FileStream fs, long offset, string format, string fallback)
    {
        Span<byte> hdr = stackalloc byte[12];
        fs.Position = offset;
        if (fs.Read(hdr) < 12) return null;
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(hdr[4..]);
        if (numTables is < 1 or > 128) return null;

        var dir = new byte[numTables * 16];
        if (fs.Read(dir, 0, dir.Length) < dir.Length) return null;

        for (int i = 0; i < numTables; i++)
        {
            var e = dir.AsSpan(i * 16, 16);
            if (BinaryPrimitives.ReadUInt32BigEndian(e) != 0x6E616D65) continue; // 'name'
            long tOff = BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
            long tLen = BinaryPrimitives.ReadUInt32BigEndian(e[12..]);
            if (tLen is < 6 or > 2_000_000 || tOff + tLen > fs.Length) return Basic(format, fallback);
            var table = new byte[tLen];
            fs.Position = tOff;
            if (fs.Read(table, 0, table.Length) < table.Length) return Basic(format, fallback);
            return ParseNames(table, format, fallback);
        }
        // Valid sfnt without a name table (very rare): still a font.
        return Basic(format, fallback);
    }

    private static FontInfo? ReadWoff(FileStream fs, string fallback)
    {
        fs.Position = 12;
        Span<byte> b = stackalloc byte[2];
        if (fs.Read(b) < 2) return null;
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(b);
        if (numTables is < 1 or > 128) return null;

        fs.Position = 44;
        var dir = new byte[numTables * 20];
        if (fs.Read(dir, 0, dir.Length) < dir.Length) return null;

        for (int i = 0; i < numTables; i++)
        {
            var e = dir.AsSpan(i * 20, 20);
            if (BinaryPrimitives.ReadUInt32BigEndian(e) != 0x6E616D65) continue;
            long tOff = BinaryPrimitives.ReadUInt32BigEndian(e[4..]);
            long comp = BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
            long orig = BinaryPrimitives.ReadUInt32BigEndian(e[12..]);
            if (comp is < 6 or > 2_000_000 || tOff + comp > fs.Length || orig > 4_000_000) return Basic("WOFF", fallback);
            var raw = new byte[comp];
            fs.Position = tOff;
            if (fs.Read(raw, 0, raw.Length) < raw.Length) return Basic("WOFF", fallback);
            if (comp < orig)
            {
                using var zs = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                var outBuf = new byte[orig];
                int read = 0, n;
                while (read < outBuf.Length && (n = zs.Read(outBuf, read, outBuf.Length - read)) > 0) read += n;
                raw = outBuf;
            }
            return ParseNames(raw, "WOFF", fallback);
        }
        return Basic("WOFF", fallback);
    }

    private static FontInfo Basic(string format, string fallback) => new(fallback, fallback, "", "", format);

    private static FontInfo ParseNames(byte[] t, string format, string fallback)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(2));
        int strOff = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(4));
        var best = new Dictionary<int, (int score, string value)>();

        for (int i = 0; i < count; i++)
        {
            int p = 6 + i * 12;
            if (p + 12 > t.Length) break;
            int platform = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(p));
            int lang = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(p + 4));
            int id = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(p + 6));
            int len = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(p + 8));
            int off = BinaryPrimitives.ReadUInt16BigEndian(t.AsSpan(p + 10));
            if (id is not (0 or 1 or 4 or 8 or 16)) continue;
            if (strOff + off + len > t.Length) continue;

            var raw = t.AsSpan(strOff + off, len);
            string s = platform is 0 or 3
                ? Encoding.BigEndianUnicode.GetString(raw)
                : Encoding.Latin1.GetString(raw);
            s = s.Trim('\0', ' ');
            if (s.Length == 0) continue;

            int score = platform == 3 ? (lang == 0x409 ? 3 : 2) : 1;
            if (!best.TryGetValue(id, out var cur) || score > cur.score) best[id] = (score, s);
        }

        string Get(int id) => best.TryGetValue(id, out var v) ? v.value : "";
        string family = Get(16);
        if (family.Length == 0) family = Get(1);
        if (family.Length == 0) family = fallback;
        string full = Get(4);
        if (full.Length == 0) full = family;
        return new FontInfo(family, full, Get(8), Get(0), format);
    }
}
