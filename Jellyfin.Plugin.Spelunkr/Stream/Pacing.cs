// Pacing: keeps the stream's position on the schedule tied to the wall clock.
namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>Decides where the next segment starts and whether to wait first.</summary>
public static class Pacing
{
    /// <summary>Further behind than this (archive.org slow, a retry), skip ahead to what is on now.</summary>
    public const long MaxBehindMs = 15_000;

    /// <summary>Further ahead than this, wait: there is no point rendering much more than the player buffers.</summary>
    public const long MaxAheadMs = 30_000;

    /// <summary>
    /// Given the schedule time the stream has reached (<paramref name="contentMs"/>) and the real time, returns
    /// the schedule time to render next and how long to wait before starting it.
    /// </summary>
    public static (long ContentMs, long WaitMs) Next(long contentMs, long wallMs)
    {
        if (contentMs < wallMs - MaxBehindMs)
        {
            return (wallMs, 0);
        }

        return (contentMs, Math.Max(0, contentMs - wallMs - MaxAheadMs));
    }
}
