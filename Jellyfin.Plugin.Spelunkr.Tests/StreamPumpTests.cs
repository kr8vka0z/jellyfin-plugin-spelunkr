// StreamPumpTests: the loop that walks the schedule. When every fallback for a film fails after the stream has
// started, it must neither sit silent for the rest of the film nor retry in a tight loop.
using Jellyfin.Plugin.Spelunkr.Schedule;
using Jellyfin.Plugin.Spelunkr.Stream;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class StreamPumpTests
{
    private const long Start = 1_793_000_000_000;

    // The Long Night: 5816 s, so a stall here would mean waiting ~53 minutes.
    private static readonly Segment Film = new() { Kind = "show", StartMs = Start - 60_000, LengthMs = 5_816_000, Title = "The Long Night" };

    [Fact]
    public async Task Fallbacks_exhausted_mid_film_resumes_from_what_was_written_then_stops_when_nothing_plays()
    {
        var asked = new List<long>();
        var played = new List<(long OffsetMs, double OutputS)>();
        var waits = new List<TimeSpan>();

        var run = StreamPump.RunAsync(
            Start,
            () => Start, // the wall clock stands still: any wait the pump asks for is recorded, not slept
            (atMs, _) =>
            {
                asked.Add(atMs);
                return Task.FromResult(Film);
            },
            (segment, offsetMs, durationMs, outputS, _) =>
            {
                played.Add((offsetMs, outputS));
                // First attempt: 10 s written, then every fallback failed. Second attempt: nothing at all.
                return Task.FromResult(played.Count == 1 ? 10_000L : 0L);
            },
            (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<IOException>(() => run);

        Assert.Contains("The Long Night", ex.Message, StringComparison.Ordinal);
        Assert.Equal([Start, Start + 10_000], asked); // re-asks the schedule where the written part ended
        Assert.Equal((60_000L, 0.0), played[0]);
        Assert.Equal((70_000L, 10.0), played[1]); // continues the film and the stream clock from there
        Assert.Empty(waits); // never waits out the unwritten remainder
        Assert.Equal(2, played.Count); // and doesn't spin once nothing plays
    }
}
