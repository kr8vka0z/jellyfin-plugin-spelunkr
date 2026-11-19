// StoredFile: files the plugin keeps on disk under a name made from what is in them (cabinet images, channel logos,
// bump clips). Each is written beside its final name and moved into place, so a reader (Jellyfin reading a logo, a
// stream opening at the same time) never sees half a file.
using System.Globalization;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>Writes content-named files.</summary>
internal static class StoredFile
{
    /// <summary>A file name stem from <paramref name="parts"/>: the first 16 hex digits of their SHA-256.</summary>
    public static string Name(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
        {
            hash.AppendData(part);
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..16];
    }

    /// <summary>
    /// Returns <paramref name="path"/>, first making it if it is missing: <paramref name="write"/> fills a temp file
    /// beside it, which is then moved into place. Anything it throws is passed on, and the temp file is removed.
    /// </summary>
    public static async Task<string> SaveAsync(string path, Func<string, Task> write)
    {
        if (!File.Exists(path))
        {
            var part = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".part";
            try
            {
                await write(part).ConfigureAwait(false);
                File.Move(part, path, true);
            }
            finally
            {
                File.Delete(part);
            }
        }

        return path;
    }

    /// <summary><see cref="SaveAsync"/> for content already in memory.</summary>
    public static string Save(string path, byte[] content) =>
        // Completes without waiting: the write is synchronous.
        SaveAsync(path, part =>
        {
            File.WriteAllBytes(part, content);
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
}
