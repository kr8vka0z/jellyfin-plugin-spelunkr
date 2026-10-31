// CabinetArt: makes the image of one channel's TV cabinet: the shipped art with that channel's logo on the brass plate.
// It runs before a stream's first job, so it is quick and it never stops a channel opening: no logo, or a logo that
// can't be added, gives the empty cabinet. Files are named by a hash of what's in them, so new art or a redrawn logo
// simply makes a new file, and two streams opening at once can't read a half-written one.
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
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

    /// <summary>
    /// The path of <paramref name="channel"/>'s cabinet image for a stream <paramref name="height"/> lines tall, or
    /// null if even the empty cabinet can't be written (the stream then plays full frame).
    /// </summary>
    public static async Task<string?> ForChannelAsync(string ffmpeg, string dir, int height, LineupChannel channel, ILogger logger, CancellationToken ct)
    {
        byte[] art;
        string plain;
        try
        {
            Directory.CreateDirectory(dir);
            art = Art(height);
            plain = Saved(dir, art);
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
        var finished = Path.Combine(dir, Hash(art, logo) + ".png");
        if (File.Exists(finished))
        {
            return finished;
        }

        var logoFile = Path.Combine(dir, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".logo.png");
        var made = Path.Combine(dir, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".new.png");
        try
        {
            try
            {
                await File.WriteAllBytesAsync(logoFile, logo, ct).ConfigureAwait(false);
                // Output in RGB with its alpha kept: overlay's default works in YUV, which would drop the see-through tube.
                var place = string.Create(CultureInfo.InvariantCulture, $"overlay=x={(int)(PlateX * scale)}+({plate}-overlay_w)/2:y={(int)(PlateY * scale)}+({plate}-overlay_h)/2:format=rgb,format=rgba");
                if (await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "quiet", "-nostdin", "-y", "-i", plain, "-i", logoFile, "-filter_complex", place, "-frames:v", "1", "-c:v", "png", "-f", "image2", "-update", "1", made], ct).ConfigureAwait(false))
                {
                    File.Move(made, finished, true);
                    return finished;
                }
            }
            finally
            {
                File.Delete(logoFile);
                File.Delete(made);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            logger.LogWarning(ex, "Spelunkr: could not add the logo to the cabinet for channel {Channel}", channel.Id);
            return plain;
        }

        logger.LogWarning("Spelunkr: ffmpeg could not add the logo to the cabinet for channel {Channel}", channel.Id);
        return plain;
    }

    private static byte[] Art(int height)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(string.Create(CultureInfo.InvariantCulture, $"cabinet-{height}.png"))
            ?? throw new IOException("no cabinet art for this picture size");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static string Saved(string dir, byte[] png)
    {
        var path = Path.Combine(dir, Hash(png) + ".png");
        if (!File.Exists(path))
        {
            var part = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            File.WriteAllBytes(part, png);
            File.Move(part, path, true);
        }

        return path;
    }

    private static string Hash(params byte[][] parts) => Convert.ToHexString(SHA256.HashData(parts.SelectMany(p => p).ToArray()))[..16];

    // True if ffmpeg finished cleanly in time. A hung run is stopped: a viewer is waiting on this.
    private static async Task<bool> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new IOException("could not start " + exe);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(LogoTimeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            ct.ThrowIfCancellationRequested();
            return false;
        }
    }
}
