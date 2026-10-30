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
    /// How long the tuning-in card runs. It is drawn locally in under a second, so the player has a picture at once,
    /// and the film connects during it (about 3 s to the archive). Longer only delays the film.
    /// </summary>
    public const long TuningInMs = 4_000;

    /// <summary>Plays the tuning-in card. Returns the ms written, 0 if no job could: the stream then just starts without it.</summary>
    public static async Task<long> PlayTuningInAsync(Segment segment, ChannelLook look, long durationMs, Func<FfmpegJob, CancellationToken, Task<long>> run, CancellationToken ct, Picture? picture = null)
    {
        foreach (var job in FfmpegJobs.TuningIn(segment, look, Math.Min(TuningInMs, durationMs), picture))
        {
            var emitted = await run(job, ct).ConfigureAwait(false);
            if (emitted > 0)
            {
                return emitted;
            }
        }

        return 0;
    }

    /// <summary>
    /// Plays <paramref name="durationMs"/> of <paramref name="segment"/> from <paramref name="offsetMs"/>.
    /// <paramref name="run"/> runs one ffmpeg job and returns how many ms it emitted. Returns the ms emitted in total:
    /// less than asked only when every fallback failed; StreamPump then carries on from what was written.
    /// </summary>
    /// <remarks><paramref name="originalUrl"/> is looked up once by the caller, never per job: see <see cref="FfmpegJobs.For"/>.</remarks>
    public static async Task<long> PlayAsync(Segment segment, ChannelLook look, long offsetMs, long durationMs, double outputOffsetS, Func<FfmpegJob, CancellationToken, Task<long>> run, ILogger logger, CancellationToken ct, string? originalUrl = null, Picture? picture = null, Action? originalFailed = null)
    {
        var done = 0L;
        var level = 0;
        // The original's jobs come first: one, or a GPU one and its CPU twin. A failed original goes straight to
        // Spelunkr's file: the twin would only wait on the same dead source, and that file has its own CPU twin.
        var originalLevels = originalUrl is null ? 0 : picture?.VaapiDevice is null ? 1 : 2;
        while (durationMs - done >= MinJobMs)
        {
            var jobs = FfmpegJobs.For(segment, look, offsetMs + done, durationMs - done, outputOffsetS + (done / 1000.0), originalUrl, picture);
            if (level >= jobs.Count)
            {
                logger.LogError("Spelunkr: every fallback failed for {Kind} {Id} with {Seconds} s still to play", segment.Kind, segment.Id, (durationMs - done) / 1000);
                break;
            }

            done += Math.Clamp(await run(jobs[level], ct).ConfigureAwait(false), 0, durationMs - done);
            if (durationMs - done >= MinJobMs)
            {
                logger.LogWarning("Spelunkr: {Kind} {Id} stopped {Seconds} s short; trying the next fallback", segment.Kind, segment.Id, (durationMs - done) / 1000);
                if (level < originalLevels)
                {
                    originalFailed?.Invoke();
                    level = originalLevels;
                }
                else
                {
                    level++;
                }
            }
        }

        return done;
    }
}
