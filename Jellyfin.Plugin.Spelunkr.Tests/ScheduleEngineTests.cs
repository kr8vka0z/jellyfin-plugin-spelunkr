// ScheduleEngineTests: the engine run in Jint must give the same schedule as the same code run in Bun (tools/oracle).
// Fixtures/spelunkr is a mirror of Spelunkr's public data; the expected files were made with
//   bun tools/oracle/oracle.ts --data Jellyfin.Plugin.Spelunkr.Tests/Fixtures/spelunkr timeline <fromMs> 24 --channel <channel>
//   (saved as timeline-<channel>-<fromMs>.jsonl; homeroom covers "up next" cards between films)
//   bun tools/oracle/oracle.ts --data Jellyfin.Plugin.Spelunkr.Tests/Fixtures/spelunkr now <atMs>
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Spelunkr.Schedule;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class ScheduleEngineTests
{
    private const string AudioBase = "https://audio.spelunkr.tv/bumps/";

    internal static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "spelunkr");

    internal static ScheduleEngine GumshoeEngine() => Engine("gumshoe");

    internal static ScheduleEngine Engine(string channelId)
    {
        var engine = new ScheduleEngine();
        var lineup = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDir, "lineup.json")))!;
        var channel = lineup["channels"]!.AsArray().First(c => (string?)c!["id"] == channelId)!;
        foreach (var pointer in channel["versions"]!.AsArray())
        {
            var manifest = File.ReadAllText(Path.Combine(FixtureDir, (string)pointer!["manifest"]!));
            var platform = File.ReadAllText(Path.Combine(FixtureDir, (string)JsonNode.Parse(manifest)!["platform"]!));
            engine.AddVersion(channelId, manifest, platform, pointer.ToJsonString());
        }

        return engine;
    }

    public static TheoryData<string> TimelineFiles() =>
        new(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "timeline-*.jsonl").Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(TimelineFiles))]
    public void Timeline_matches_oracle(string file)
    {
        // timeline-<channel>-<fromMs>.jsonl
        var dash = file.LastIndexOf('-');
        var channelId = file["timeline-".Length..dash];
        var fromMs = long.Parse(file[(dash + 1)..^".jsonl".Length], System.Globalization.CultureInfo.InvariantCulture);
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        var actual = Engine(channelId).SegmentsBetweenJson(channelId, fromMs, fromMs + (24 * 3_600_000L), AudioBase);

        var actualLines = JsonNode.Parse(actual)!.AsArray().Select(n => n!.ToJsonString()).ToArray();
        Assert.Equal(expected.Length, actualLines.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected[i]), JsonNode.Parse(actualLines[i])), $"segment {i}:\n oracle {expected[i]}\n jint   {actualLines[i]}");
        }
    }

    [Fact]
    public void Now_matches_oracle_at_every_fixture_time()
    {
        var engine = GumshoeEngine();
        foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "now.jsonl")))
        {
            var expected = JsonNode.Parse(line)!;
            var actual = engine.SegmentAtJson("gumshoe", (long)expected["atMs"]!, AudioBase);
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(actual)), $"oracle {line}\n jint   {actual}");
        }
    }

    [Fact]
    public void Typed_lookup_names_the_film_and_position()
    {
        // Known-good values read off the oracle (Fixtures/now.jsonl), independent of this code.
        var seg = GumshoeEngine().SegmentAt("gumshoe", 1_793_000_000_000, AudioBase);

        Assert.Equal("show", seg.Kind);
        Assert.Equal("D.o.a.VideoQualityUpgrade", seg.Id);
        Assert.Equal("D.O.A.", seg.Title);
        Assert.Equal(4_200_811, seg.OffsetMs);
        Assert.Equal(1_792_995_799_189 + 5_004_000, seg.EndMs);
    }

    [Fact]
    public void Unknown_channel_fails_loudly()
    {
        Assert.Throws<InvalidOperationException>(() => new ScheduleEngine().SegmentAt("nope", 0, AudioBase));
    }
}
