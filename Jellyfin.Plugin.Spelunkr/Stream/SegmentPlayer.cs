// SegmentPlayer: plays one stretch of a segment, moving down the fallback list when a job fails, and resuming
// each fallback from where the last job stopped so the stream's timestamps stay continuous.
using Jellyfin.Plugin.Spelunkr.Schedule;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Spelunkr.Stream;

/// <summary>Runs the jobs for one segment.</summary>
internal static class SegmentPlayer
{
    /// <summary>Less than this left over counts as done (job start-up and progress reports are not that exact).</summary>
    public const long MinJobMs = 500;

    /// <summary>
    /// Plays <paramref name="durationMs"/> of <paramref name="segment"/> from <paramref name="offsetMs"/>.
    /// <paramref name="run"/> runs one ffmpeg job and returns how many ms it emitted. Returns the ms emitted in total:
    /// less than asked only when every fallback failed; StreamPump then carries on from what was written.
    /// </summary>
    public static async Task<long> PlayAsync(Segment segment, ChannelLook look, long offsetMs, long durationMs, double outputOffsetS, Func<FfmpegJob, CancellationToken, Task<long>> run, ILogger logger, CancellationToken ct)
    {
        var done = 0L;
        var level = 0;
        while (durationMs - done >= MinJobMs)
        {
            var jobs = FfmpegJobs.For(segment, look, offsetMs + done, durationMs - done, outputOffsetS + (done / 1000.0));
            if (level >= jobs.Count)
            {
                logger.LogError("Spelunkr: every fallback failed for {Kind} {Id} with {Seconds} s still to play", segment.Kind, segment.Id, (durationMs - done) / 1000);
                break;
            }

            done += Math.Clamp(await run(jobs[level], ct).ConfigureAwait(false), 0, durationMs - done);
            if (durationMs - done >= MinJobMs)
            {
                logger.LogWarning("Spelunkr: {Kind} {Id} stopped {Seconds} s short; trying the next fallback", segment.Kind, segment.Id, (durationMs - done) / 1000);
                level++;
            }
        }

        return done;
    }
}
