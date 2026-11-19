// CabinetArt: makes the image of one channel's TV cabinet: the shipped art with that channel's logo on the brass plate.
// It runs before a stream's first job, so it is quick and it never stops a channel opening: no logo, or a logo that
// can't be added, gives the empty cabinet. Files are named by a hash of what's in them, so new art or a redrawn logo
// simply makes a new file, and two streams opening at once can't read a half-written one.
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.Spelunkr.Guide;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>Finds or makes cabinet images.</summary>
internal static class CabinetArt
{
    // The plate's inside at 1080p; it scales with the picture. Must match the art (tools/cabinet/render.ts).
    private const int PlateX = 79;
    private const int PlateY = 99;
    private const int PlateSize = 120;

    private static readonly TimeSpan LogoTimeout = TimeSpan.FromSeconds(5);

    // The shipped art for each stream size and its file name, read and hashed once rather than at every tune-in.
    private static readonly ConcurrentDictionary<int, (byte[] Png, string Name)> Arts = new();

    /// <summary>
    /// The path of <paramref name="channel"/>'s cabinet image for a stream <paramref name="height"/> lines tall, or
    /// null if even the empty cabinet can't be written (the stream then plays full frame).
    /// </summary>
    public static async Task<string?> ForChannelAsync(string ffmpeg, string dir, int height, LineupChannel channel, ILogger logger, CancellationToken ct)
    {
        (byte[] Png, string Name) art;
        string plain;
        try
        {
            Directory.CreateDirectory(dir);
            art = Arts.GetOrAdd(height, h =>
            {
                var png = Art(h);
                return (png, StoredFile.Name(png));
            });
            plain = StoredFile.Save(Path.Combine(dir, art.Name + ".png"), art.Png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write the TV cabinet image");
            return null;
        }

        if (channel.Logomark is not { Type: "pixel", Rows: { Count: > 0 } rows })
        {
            return plain;
        }

        var scale = height / 1080.0;
        var plate = (int)(PlateSize * scale);
        var logo = ChannelLogo.RenderPng(rows, channel.Color, plate);
        var finished = Path.Combine(dir, StoredFile.Name(Encoding.ASCII.GetBytes(art.Name), logo) + ".png");
        var logoFile = Path.Combine(dir, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".logo.png");
        try
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(LogoTimeout);
                return await StoredFile.SaveAsync(finished, async made =>
                {
                    await File.WriteAllBytesAsync(logoFile, logo, limit.Token).ConfigureAwait(false);
                    // Output in RGB with its alpha kept: overlay's default works in YUV, which would drop the see-through tube.
                    var place = string.Create(CultureInfo.InvariantCulture, $"overlay=x={(int)(PlateX * scale)}+({plate}-overlay_w)/2:y={(int)(PlateY * scale)}+({plate}-overlay_h)/2:format=rgb,format=rgba");
                    var (code, _) = await FfmpegProcess.RunToExitAsync(ffmpeg, ["-hide_banner", "-loglevel", "quiet", "-nostdin", "-y", "-i", plain, "-i", logoFile, "-filter_complex", place, "-frames:v", "1", "-c:v", "png", "-f", "image2", "-update", "1", made], limit.Token).ConfigureAwait(false);
                    if (code != 0)
                    {
                        throw new IOException($"ffmpeg exited {code}");
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(logoFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // A hung ffmpeg is stopped at LogoTimeout: a viewer is waiting on this.
            logger.LogWarning(ex, "Spelunkr: could not add the logo to the cabinet for channel {Channel}", channel.Id);
            return plain;
        }
    }

    private static byte[] Art(int height)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(string.Create(CultureInfo.InvariantCulture, $"cabinet-{height}.png"))
            ?? throw new IOException("no cabinet art for this picture size");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        return bytes.ToArray();
    }
}
