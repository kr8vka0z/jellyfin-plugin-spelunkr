// StreamPump: the loop behind a ChannelStream. Asks the schedule what is on, plays it, and moves on by exactly
// what was written, so the stream never claims time it didn't fill.
using Jellyfin.Plugin.Spelunkr.Schedule;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>Walks the schedule for one open stream.</summary>
internal static class StreamPump
{
    /// <summary>
    /// Runs until cancelled, or throws <see cref="IOException"/> when nothing at all could be played (not even
    /// the title slate), which means ffmpeg itself is broken: retrying would only spin.
    /// </summary>
    /// <param name="startMs">Schedule time to start at (normally now).</param>
    /// <param name="wallMs">The real clock, Unix ms.</param>
    /// <param name="segmentAt">What is on at a schedule time.</param>
    /// <param name="play">Plays (segment, offsetMs, durationMs, outputOffsetS) and returns the ms written.</param>
    /// <param name="delay">Waits (Task.Delay in production).</param>
    /// <param name="ct">Stops the loop.</param>
    public static async Task RunAsync(
        long startMs,
        Func<long> wallMs,
        Func<long, CancellationToken, Task<Segment>> segmentAt,
        Func<Segment, long, long, double, CancellationToken, Task<long>> play,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        // contentMs: the schedule time the stream has reached. outputS: the stream's own timestamp clock.
        // Both move only by what was written; if that leaves the stream behind the wall clock, Pacing jumps
        // contentMs to now on the next turn while outputS keeps counting, so there is no gap in the timestamps.
        var contentMs = startMs;
        var outputS = 0.0;
        while (!ct.IsCancellationRequested)
        {
            (contentMs, var waitMs) = Pacing.Next(contentMs, wallMs());
            if (waitMs > 0)
            {
                await delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
            }

            var segment = await segmentAt(contentMs, ct).ConfigureAwait(false);
            var durationMs = segment.EndMs - contentMs;
            if (durationMs < SegmentPlayer.MinJobMs)
            {
                // Joined at the very end of a segment: not worth starting ffmpeg for.
                contentMs = segment.EndMs;
                continue;
            }

            var emittedMs = await play(segment, contentMs - segment.StartMs, durationMs, outputS, ct).ConfigureAwait(false);
            if (emittedMs <= 0)
            {
                throw new IOException($"nothing could be played for {segment.Kind} {segment.Title ?? segment.Id}, not even the title slate");
            }

            contentMs += emittedMs;
            outputS += emittedMs / 1000.0;
        }
    }
}
