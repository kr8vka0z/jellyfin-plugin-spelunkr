// ChannelLogoTests: channel logos use spelunkr.tv's own palette rules. Expected colors were produced by running
// the site's palette code (cut from its bundle) on the same inputs, independent of this C# port.
using System.IO.Compression;
using Jellyfin.Plugin.Spelunkr.Guide;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class ChannelLogoTests
{
    [Theory]
    [InlineData("#E8DFC8", "#e8dfc8", "#8b8076", "#f6f2e1")] // gumshoe
    [InlineData("#2E8B57", "#2e8b57", "#16533f", "#7dd194")]
    [InlineData("#c0392b", "#c0392b", "#73111f", "#e6977b")]
    [InlineData("bad", "#666666", "#3d3d3d", "#c2c2c2")] // not a color: the site falls back to grey
    public void Palette_matches_the_site(string color, string c, string d, string l)
    {
        var palette = ChannelLogo.Palette(color);

        Assert.Equal(c, palette['c']);
        Assert.Equal(d, palette['d']);
        Assert.Equal(l, palette['l']);
        Assert.Equal("#0b0f26", palette['k']);
        Assert.Equal("#e8eaf0", palette['p']);
    }

    [Fact]
    public void Renders_a_square_png_with_transparent_dots_and_unknown_letters_as_p()
    {
        string[] rows = ["c.", "kz"]; // z isn't in the palette: the site draws it in the "p" color

        var png = ChannelLogo.RenderPng(rows, "#E8DFC8", size: 4);
        var pixels = DecodeRgba(png, out var width, out var height);

        Assert.Equal((4, 4), (width, height));
        Assert.Equal(new byte[] { 0xe8, 0xdf, 0xc8, 255 }, Pixel(pixels, width, 0, 0)); // c, scaled 2x
        Assert.Equal(new byte[] { 0xe8, 0xdf, 0xc8, 255 }, Pixel(pixels, width, 1, 1));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(pixels, width, 2, 0)); // "." is transparent
        Assert.Equal(new byte[] { 0x0b, 0x0f, 0x26, 255 }, Pixel(pixels, width, 0, 2)); // k
        Assert.Equal(new byte[] { 0xe8, 0xea, 0xf0, 255 }, Pixel(pixels, width, 3, 3)); // z -> p
    }

    private static byte[] Pixel(byte[] rgba, int width, int x, int y) => rgba.AsSpan(((y * width) + x) * 4, 4).ToArray();

    // Minimal PNG reader for the test: checks the signature, reads IHDR, inflates IDAT, undoes filter 0 rows.
    private static byte[] DecodeRgba(byte[] png, out int width, out int height)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        width = height = 0;
        using var idat = new MemoryStream();
        for (var p = 8; p < png.Length;)
        {
            var len = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            if (type == "IHDR")
            {
                width = (png[p + 8] << 24) | (png[p + 9] << 16) | (png[p + 10] << 8) | png[p + 11];
                height = (png[p + 12] << 24) | (png[p + 13] << 16) | (png[p + 14] << 8) | png[p + 15];
                Assert.Equal(new byte[] { 8, 6 }, png[(p + 16)..(p + 18)]); // 8-bit RGBA
            }
            else if (type == "IDAT")
            {
                idat.Write(png, p + 8, len);
            }

            p += 12 + len;
        }

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var bytes = raw.ToArray();
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, bytes[y * ((width * 4) + 1)]);
            Array.Copy(bytes, (y * ((width * 4) + 1)) + 1, rgba, y * width * 4, width * 4);
        }

        return rgba;
    }
}
