// ChannelLogo: draws a channel's pixel-art logomark as a PNG, with spelunkr.tv's own palette rules.
// The palette is ported from the site's logo renderer: six fixed colors plus c (the channel color), d (a shade
// of it) and l (a light tint), derived in HSV. Letters outside the palette draw in the "p" color, as on the site.
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Jellyfin.Plugin.Spelunkr.Guide;

/// <summary>Renders channel logos.</summary>
public static class ChannelLogo
{
    // ponytail: hand-ported (about 30 lines) rather than run through Jint like the schedule: it's presentation only,
    // and the test pins it to colors produced by the site's own code. If Spelunkr retunes its palette, re-derive
    // the expected values in ChannelLogoTests from the bundle and update these numbers.
    private static readonly Dictionary<char, string> Fixed = new()
    {
        ['k'] = "#0b0f26", ['n'] = "#2a3580", ['s'] = "#8899cc", ['p'] = "#e8eaf0", ['g'] = "#ffd740", ['o'] = "#d2500c",
    };

    /// <summary>The palette for a channel color ("#RRGGBB"; anything else falls back to grey, as on the site).</summary>
    public static Dictionary<char, string> Palette(string? color)
    {
        var c = color is { Length: 7 } && color[0] == '#' && int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
            ? color.ToLowerInvariant()
            : "#666666";
        var (h, s, v) = ToHsv(c);
        return new Dictionary<char, string>(Fixed)
        {
            ['c'] = c,
            ['d'] = FromHsv(StepHue(h, 255, 14), Math.Min(100, s * 1.1), v * 0.6),
            ['l'] = FromHsv(StepHue(h, 50, 10), s * 0.6, v + ((100 - v) * 0.6)),
        };
    }

    /// <summary>A square PNG of <paramref name="rows"/> scaled to <paramref name="size"/> with hard pixel edges.</summary>
    public static byte[] RenderPng(IReadOnlyList<string> rows, string? color, int size = 256)
    {
        var palette = Palette(color);
        var n = rows.Count;
        var scale = Math.Max(1, size / n);
        var side = n * scale;
        // One filter byte (0 = none) before each row of RGBA pixels.
        var raw = new byte[side * ((side * 4) + 1)];
        for (var y = 0; y < side; y++)
        {
            var row = rows[y / scale];
            for (var x = 0; x < side; x++)
            {
                var ch = (x / scale) < row.Length ? row[x / scale] : '.';
                if (ch == '.')
                {
                    continue;
                }

                var hex = palette.TryGetValue(ch, out var p) ? p : palette['p'];
                var at = (y * ((side * 4) + 1)) + 1 + (x * 4);
                raw[at] = Convert.ToByte(hex[1..3], 16);
                raw[at + 1] = Convert.ToByte(hex[3..5], 16);
                raw[at + 2] = Convert.ToByte(hex[5..7], 16);
                raw[at + 3] = 255;
            }
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, side);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), side);
        ihdr[8] = 8; // bits per channel
        ihdr[9] = 6; // RGBA
        Chunk(png, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zlib = new ZLibStream(z, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            Chunk(png, "IDAT", z.ToArray());
        }

        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(System.IO.Stream png, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        png.Write(len);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typeAndData);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(typeAndData));
        png.Write(len);
    }

    // The CRC-32 PNG requires (polynomial 0xEDB88320). The BCL only ships one in an extra package.
    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    // The site's y1: RGB to hue (0-360), saturation and value (0-100).
    private static (double H, double S, double V) ToHsv(string hex)
    {
        double r = Convert.ToByte(hex[1..3], 16) / 255.0, g = Convert.ToByte(hex[3..5], 16) / 255.0, b = Convert.ToByte(hex[5..7], 16) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min, h = 0;
        if (delta > 0)
        {
            h = max == r ? (((g - b) / delta) + (g < b ? 6 : 0)) / 6 : max == g ? (((b - r) / delta) + 2) / 6 : (((r - g) / delta) + 4) / 6;
        }

        return (h * 360, max > 0 ? delta / max * 100 : 0, max * 100);
    }

    // The site's $d: move hue h toward target by at most step degrees, the short way round.
    private static double StepHue(double h, double target, double step)
    {
        var diff = ((target - h + 540) % 360) - 180;
        return (h + (Math.Sign(diff) * Math.Min(step, Math.Abs(diff))) + 360) % 360;
    }

    // The site's Id: HSV (s, v in 0-100) to "#rrggbb", rounding each channel like Math.round.
    private static string FromHsv(double h, double s, double v)
    {
        s /= 100;
        v /= 100;
        string Part(double n)
        {
            var k = (n + (h / 60)) % 6;
            var value = 255 * (v - (v * s * Math.Max(0, Math.Min(k, Math.Min(4 - k, 1)))));
            return ((int)Math.Floor(value + 0.5)).ToString("x2", CultureInfo.InvariantCulture);
        }

        return "#" + Part(5) + Part(3) + Part(1);
    }
}
