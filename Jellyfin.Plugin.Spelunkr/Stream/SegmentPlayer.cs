// SegmentPlayer: plays one stretch of a segment. A job that played for a while and then stopped (a dropped download)
// is resumed where it stopped; one that fails before that moves down the fallback list. Every job starts where the
// last one stopped, so the stream's timestamps stay continuous.
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
    /// Real time a job must have played, beyond the pad a film job adds after its source ends, for a stop to count as
    /// a dropped connection worth resuming rather than a job that doesn't work.
    /// </summary>
    public const long ProgressMs = 10_000;

    /// <summary>How many times one call resumes a job at the same level after real progress, before moving down.</summary>
    public const int MaxRetries = 3;

    /// <summary>
    /// The longest a stand-in (the film with silence, the title slate) runs before handing back, so the stream asks
    /// the schedule again and tries the real film first: a source that was down for a minute isn't lost for the film.
    /// </summary>
    public const long StandInMs = 60_000;

    /// <summary>
    /// Plays up to <paramref name="durationMs"/> of <paramref name="segment"/> from <paramref name="offsetMs"/>.
    /// <paramref name="run"/> runs one ffmpeg job and returns how many ms it emitted. Returns the ms emitted in total,
    /// which is less than asked when every fallback failed or a stand-in ran its <see cref="StandInMs"/>; StreamPump
    /// then carries on from what was written.
    /// </summary>
    /// <remarks>
    /// <paramref name="originalUrl"/> and <paramref name="clip"/> are looked up once by the caller, never per job: see
    /// <see cref="FfmpegJobs.For"/>. A clip job that fails moves on to today's bump jobs like any other fallback.
    /// </remarks>
    public static async Task<long> PlayAsync(Segment segment, ChannelLook look, long offsetMs, long durationMs, double outputOffsetS, Func<FfmpegJob, CancellationToken, Task<long>> run, ILogger logger, CancellationToken ct, string? originalUrl = null, Picture? picture = null, Action? originalFailed = null, ReadyClip? clip = null)
    {
        var done = 0L;
        var level = 0;
        var retries = 0;
        var originalPlayed = false;
        // The original's jobs come first: one, or a GPU one and its CPU twin. A failed original goes straight to
        // Spelunkr's file: the twin would only wait on the same dead source, and that file has its own CPU twin.
        var originalLevels = originalUrl is null ? 0 : picture?.VaapiDevice is null ? 1 : 2;
        while (durationMs - done >= MinJobMs)
        {
            IReadOnlyList<FfmpegJob> Jobs(long ms) => FfmpegJobs.For(segment, look, offsetMs + done, ms, outputOffsetS + (done / 1000.0), originalUrl, picture, clip);
            var jobs = Jobs(durationMs - done);
            if (level >= jobs.Count)
            {
                logger.LogError("Spelunkr: every fallback failed for {Kind} {Id} with {Seconds} s still to play", segment.Kind, segment.Id, (durationMs - done) / 1000);
                break;
            }

            // The list's shape doesn't depend on the length, so a shorter stand-in sits at the same level.
            var askedMs = jobs[level].StandIn ? Math.Min(durationMs - done, StandInMs) : durationMs - done;
            var job = askedMs < durationMs - done ? Jobs(askedMs)[level] : jobs[level];
            var emitted = Math.Clamp(await run(job, ct).ConfigureAwait(false), 0, askedMs);
            done += emitted;
            if (askedMs - emitted < MinJobMs)
            {
                if (job.StandIn)
                {
                    break;
                }

                continue;
            }

            // A film job pads up to ShortfallPadS after its source ends, so only what it played beyond that is progress.
            var progressed = emitted - (FfmpegJobs.ShortfallPadS * 1000) > ProgressMs;
            originalPlayed |= progressed && level < originalLevels;
            if (progressed && retries < MaxRetries)
            {
                retries++;
                logger.LogWarning("Spelunkr: {Kind} {Id} stopped {Seconds} s short; resuming it from there", segment.Kind, segment.Id, (durationMs - done) / 1000);
                continue;
            }

            logger.LogWarning("Spelunkr: {Kind} {Id} stopped {Seconds} s short; trying the next fallback", segment.Kind, segment.Id, (durationMs - done) / 1000);
            if (level < originalLevels)
            {
                // Only an original that never played is set aside for hours: one that played and then dropped is
                // as likely archive.org's connection as the file.
                if (!originalPlayed)
                {
                    originalFailed?.Invoke();
                }

                level = originalLevels;
            }
            else
            {
                level++;
            }
        }

        return done;
    }
}
