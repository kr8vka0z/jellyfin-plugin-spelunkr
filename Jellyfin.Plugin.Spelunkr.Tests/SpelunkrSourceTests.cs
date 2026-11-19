// SpelunkrSourceTests: the data layer reads Spelunkr's lineup the way the live bucket serves it (here: the
// fixture mirror), offers the channels spelunkr.tv lists, and loads their schedules.
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Spelunkr.Schedule;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class SpelunkrSourceTests
{
    internal static SpelunkrSource FromFixtures(List<string>? reads = null) => new(
        (path, _) =>
        {
            reads?.Add(path);
            return Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path)));
        },
        NullLogger<SpelunkrSource>.Instance);

    /// <summary>The fixture lineup.json, changed by <paramref name="edit"/>.</summary>
    internal static string LineupWith(Action<JsonObject> edit)
    {
        var lineup = JsonNode.Parse(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, "lineup.json")))!.AsObject();
        edit(lineup);
        return lineup.ToJsonString();
    }

    /// <summary>Drops every channel but the two the fixture mirror holds, so a refresh has nothing failing.</summary>
    internal static void OnlyLoadable(JsonObject lineup)
    {
        var channels = lineup["channels"]!.AsArray();
        foreach (var c in channels.Where(c => (string?)c!["id"] is not ("gumshoe" or "homeroom")).ToList())
        {
            channels.Remove(c);
        }
    }

    internal static JsonArray Versions(JsonObject lineup, string channelId)
        => lineup["channels"]!.AsArray().Single(c => (string?)c!["id"] == channelId)!["versions"]!.AsArray();

    /// <summary>A gumshoe version pointer for a relabelled copy of the fixture's version (see <see cref="Reader"/>).</summary>
    internal static JsonNode GumshoePointer(int v, long epochMs, long effectiveAtMs)
        => JsonNode.Parse($"{{\"v\":{v},\"manifest\":\"manifests/gumshoe/v{v}.json\",\"epochMs\":{epochMs},\"effectiveAtMs\":{effectiveAtMs},\"startIndex\":0}}")!;

    /// <summary>
    /// Serves the fixture mirror with <paramref name="lineup"/> as lineup.json, any other gumshoe manifest as a copy
    /// of v19 relabelled (a new version Spelunkr published), and throws for paths <paramref name="fail"/> picks.
    /// </summary>
    internal static Func<string, CancellationToken, Task<string>> Reader(Func<string> lineup, List<string>? reads = null, Func<string, bool>? fail = null)
        => (path, _) =>
        {
            reads?.Add(path);
            if (fail?.Invoke(path) == true)
            {
                throw new HttpRequestException("unavailable");
            }

            if (path == "lineup.json")
            {
                return Task.FromResult(lineup());
            }

            if (path.StartsWith("manifests/gumshoe/v", StringComparison.Ordinal) && path != "manifests/gumshoe/v19.json")
            {
                var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, "manifests/gumshoe/v19.json")))!;
                manifest["version"] = int.Parse(path["manifests/gumshoe/v".Length..^".json".Length], System.Globalization.CultureInfo.InvariantCulture);
                return Task.FromResult(manifest.ToJsonString());
            }

            return Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path)));
        };

    [Fact]
    public void Offers_every_numbered_channel_that_is_not_coming_soon_debug_or_audio_overlay()
    {
        var lineup = System.Text.Json.JsonSerializer.Deserialize<Lineup>(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, "lineup.json")), ScheduleEngine.Json)!;

        var left = lineup.Channels.Where(c => !SpelunkrSource.IsOffered(c)).Select(c => c.Id).Order();

        // rocket-punch: debug only; oops-all-bumps and test-pattern: no number (debug in this saved lineup);
        // vacation-slides: its music overlay isn't built yet.
        Assert.Equal(["oops-all-bumps", "rocket-punch", "test-pattern", "vacation-slides"], left);
        Assert.Equal(30, lineup.Channels.Count(SpelunkrSource.IsOffered));
    }

    [Fact]
    public async Task A_channel_whose_files_fail_to_load_is_left_out_without_stopping_the_rest()
    {
        // The fixture mirror only holds gumshoe's and homeroom's manifests; every other channel's fetch fails.
        var channels = await FromFixtures().GetChannelsAsync(CancellationToken.None);

        Assert.Equal(["gumshoe", "homeroom"], channels.Select(c => c.Id).Order());
        var gumshoe = channels.Single(c => c.Id == "gumshoe");
        Assert.Equal("GUMSHOE", gumshoe.Name);
        Assert.Equal(101, gumshoe.Dial);
        Assert.Equal("#E8DFC8", gumshoe.Color);
    }

    // The data host answers 403 to any request that does not name spelunkr.tv as its Referer (issue #27).
    [Fact]
    public async Task Incident_2026_11_06_data_referer_every_bucket_read_names_spelunkr_tv_as_the_referer()
    {
        var handler = new FixtureHandler();
        var source = new SpelunkrSource(new OneHandlerFactory(handler), null!, NullLogger<SpelunkrSource>.Instance);

        var channels = await source.GetChannelsAsync(CancellationToken.None);

        Assert.Contains(channels, c => c.Id == "gumshoe");
        Assert.NotEmpty(handler.Referers);
        Assert.All(handler.Referers, r => Assert.Equal("https://spelunkr.tv/", r));
    }

    private sealed class OneHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Serves the fixture mirror as the bucket would, 404 for files it doesn't hold, and notes each Referer.</summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        public List<string?> Referers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Referers)
            {
                Referers.Add(request.Headers.Referrer?.ToString());
            }

            var file = Path.Combine(ScheduleEngineTests.FixtureDir, request.RequestUri!.AbsolutePath.TrimStart('/'));
            return Task.FromResult(File.Exists(file)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(file)) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Answers_what_is_on_and_reads_the_lineup_once_an_hour()
    {
        var reads = new List<string>();
        var source = FromFixtures(reads);

        var seg = await source.SegmentAtAsync("gumshoe", 1_793_000_000_000, CancellationToken.None);
        await source.SegmentAtAsync("gumshoe", 1_793_000_100_000, CancellationToken.None);

        Assert.Equal("D.o.a.VideoQualityUpgrade", seg.Id);
        Assert.Equal(1, reads.Count(p => p == "lineup.json"));
        Assert.Contains("platform/294b0bf5ae4d1917.json", reads); // the network-wide bumps file, needed for timing
    }

    [Fact]
    public async Task A_new_version_in_the_hourly_lineup_takes_over_and_refreshes_the_guide()
    {
        // Spelunkr publishes a new version ahead of time; from its effectiveAtMs the schedule switches to it.
        // Simulated with a copy of v19 relabelled v20, taking over at 2026-10-31T00:00Z.
        const long takeover = 1_793_404_800_000;
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(1_793_000_000_000));
        var lineupV19 = File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, "lineup.json"));
        var lineupV20 = lineupV19.Replace(
            "\"versions\":[{\"v\":19,\"manifest\":\"manifests/gumshoe/",
            $"\"versions\":[{{\"v\":20,\"manifest\":\"manifests/gumshoe/v20.json\",\"epochMs\":{takeover},\"effectiveAtMs\":{takeover},\"startIndex\":0}},{{\"v\":19,\"manifest\":\"manifests/gumshoe/",
            StringComparison.Ordinal);
        Assert.NotEqual(lineupV19, lineupV20); // the fixture still has the shape the Replace expects
        var published = false;
        var guideRefreshes = 0;
        var source = new SpelunkrSource(
            (path, _) => Task.FromResult(path switch
            {
                "lineup.json" => published ? lineupV20 : lineupV19,
                "manifests/gumshoe/v20.json" => File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, "manifests/gumshoe/v19.json")).Replace("\"version\":19", "\"version\":20", StringComparison.Ordinal),
                _ => File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path)),
            }),
            NullLogger<SpelunkrSource>.Instance,
            () => guideRefreshes++,
            clock);

        Assert.Equal(19, (await source.SegmentAtAsync("gumshoe", takeover + 60_000, CancellationToken.None)).Version);
        Assert.Equal(0, guideRefreshes); // the first load isn't news; Registration queues the first guide refresh

        published = true;
        clock.Now += TimeSpan.FromMinutes(61);
        await source.SegmentAtAsync("gumshoe", takeover + 60_000, CancellationToken.None); // starts the background refresh
        for (var i = 0; i < 500 && guideRefreshes == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        var after = await source.SegmentAtAsync("gumshoe", takeover + 60_000, CancellationToken.None);
        var before = await source.SegmentAtAsync("gumshoe", takeover - 60_000, CancellationToken.None);

        Assert.Equal(20, after.Version);
        Assert.Equal(19, before.Version);
        Assert.Equal(1, guideRefreshes);
    }

    [Fact]
    public async Task A_schedule_refresh_never_makes_a_channel_wait()
    {
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(1_793_000_000_000));
        var slowLineup = new TaskCompletionSource<string>();
        var loaded = false;
        var source = new SpelunkrSource(
            (path, _) => loaded && path == "lineup.json" ? slowLineup.Task : Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path))),
            NullLogger<SpelunkrSource>.Instance,
            () => { },
            clock);
        await source.GetChannelsAsync(CancellationToken.None);
        loaded = true;
        clock.Now += TimeSpan.FromMinutes(61);

        // The refresh is stuck on slowLineup, and the answer still comes from the copy already loaded.
        var channels = await source.GetChannelsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await source.SegmentAtAsync("gumshoe", 1_793_000_000_000, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.NotEmpty(channels);
        slowLineup.SetResult(await File.ReadAllTextAsync(Path.Combine(ScheduleEngineTests.FixtureDir, "lineup.json"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("manifests/gumshoe/v19.json#/../../../.github/workflows/build.yml")]
    [InlineData("manifests/../../secrets.json")]
    [InlineData("/etc/passwd")]
    [InlineData("platform/x.json?y=1")]
    [InlineData("https://elsewhere.example/x.json")]
    public async Task Refuses_data_paths_that_leave_the_bucket(string badPath)
    {
        // A manifest or platform path comes from Spelunkr's data and is appended to the bucket URL.
        var fixture = File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, "lineup.json"));
        var lineup = fixture.Replace("\"manifests/gumshoe/v19.json\"", System.Text.Json.JsonSerializer.Serialize(badPath), StringComparison.Ordinal);
        Assert.NotEqual(fixture, lineup); // the fixture still has the path the Replace expects, so the bad path is really served
        var reads = new List<string>();
        var source = new SpelunkrSource(
            (path, _) =>
            {
                reads.Add(path);
                return path == "lineup.json" ? Task.FromResult(lineup) : throw new FileNotFoundException(path);
            },
            NullLogger<SpelunkrSource>.Instance);

        var channels = await source.GetChannelsAsync(CancellationToken.None);

        Assert.DoesNotContain(badPath, reads); // never fetched
        Assert.DoesNotContain(channels, c => c.Id == "gumshoe"); // and the channel is left out
    }

    [Fact]
    public async Task Refuses_a_platform_path_that_leaves_the_bucket()
    {
        var manifest = File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, "manifests/gumshoe/v19.json"));
        var badManifest = manifest.Replace("\"platform/294b0bf5ae4d1917.json\"", "\"platform/../../x.json\"", StringComparison.Ordinal);
        Assert.NotEqual(manifest, badManifest); // the fixture still has the platform path the Replace expects
        Func<string, CancellationToken, Task<string>> source = (path, _) => path switch
            {
                "manifests/gumshoe/v19.json" => Task.FromResult(badManifest),
                "lineup.json" => Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path))),
                _ => throw new FileNotFoundException(path),
            };
        var reads = new List<string>();

        var channels = await new SpelunkrSource(
            (path, ct) =>
            {
                reads.Add(path);
                return source(path, ct);
            },
            NullLogger<SpelunkrSource>.Instance).GetChannelsAsync(CancellationToken.None);

        Assert.DoesNotContain(reads, p => p.Contains("..", StringComparison.Ordinal));
        Assert.DoesNotContain(channels, c => c.Id == "gumshoe");
    }

    [Theory]
    [InlineData("../escaped")]
    [InlineData("/etc/passwd")]
    [InlineData("Gumshoe")]
    [InlineData("a/b")]
    public void Channels_with_ids_that_are_not_one_plain_name_are_not_offered(string id)
    {
        Assert.False(SpelunkrSource.IsOffered(new LineupChannel(id, "X", "#000000", 1, [])));
        Assert.True(SpelunkrSource.IsOffered(new LineupChannel("late-nite-local", "X", "#000000", 11, [])));
    }

    [Theory]
    [InlineData("../escaped")]
    [InlineData("/tmp/rooted")]
    public void Logo_files_stay_in_the_logo_folder_whatever_the_channel_id(string id)
    {
        var dir = Directory.CreateTempSubdirectory("spelunkr-logos").FullName;
        try
        {
            var channel = new LineupChannel(id, "X", "#E8DFC8", 1, [], Logomark: new Logomark("pixel", ["c."]));

            var path = SpelunkrTunerHost.LogoFile(dir, channel, NullLogger.Instance);

            Assert.Equal(dir, Path.GetDirectoryName(path));
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_fetch_that_times_out_only_costs_that_channel_or_its_descriptions()
    {
        // HttpClient reports its own timeout as TaskCanceledException while the caller's token is still live.
        var source = new SpelunkrSource(
            (path, _) => path is "manifests/homeroom/v21.json" or "details/gumshoe/v19.json"
                ? throw new TaskCanceledException("timed out", new TimeoutException())
                : Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path))),
            NullLogger<SpelunkrSource>.Instance);

        var channels = await source.GetChannelsAsync(CancellationToken.None);
        var film = await source.SegmentAtAsync("gumshoe", 1_793_215_002_819, CancellationToken.None);

        Assert.Equal(["gumshoe"], channels.Select(c => c.Id)); // homeroom's manifest timed out; gumshoe unaffected
        Assert.Null(source.Details("gumshoe", film)); // its details timed out: titles only
    }

    [Fact]
    public async Task A_real_cancellation_still_stops_the_load()
    {
        using var cts = new CancellationTokenSource();
        var source = new SpelunkrSource(
            (path, ct) =>
            {
                if (path.StartsWith("manifests/", StringComparison.Ordinal))
                {
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                }

                return Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path)));
            },
            NullLogger<SpelunkrSource>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetChannelsAsync(cts.Token));
    }

    // 2026-10-31T00:00Z, after the fixture's v19 epoch: when a relabelled gumshoe version takes over in these tests.
    private const long Takeover = 1_793_404_800_000;

    [Fact]
    public async Task The_refresh_timer_picks_up_a_new_version_within_10_minutes_with_nobody_tuned_in()
    {
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(1_793_000_000_000));
        var lineup = LineupWith(OnlyLoadable);
        var reads = new List<string>();
        var guideRefreshes = 0;
        var source = new SpelunkrSource(Reader(() => lineup, reads), NullLogger<SpelunkrSource>.Instance, () => guideRefreshes++, clock);
        var waits = new List<TimeSpan>();
        using var stop = new CancellationTokenSource();

        // Only the timer calls the source. Its first tick loads the lineup; Spelunkr then publishes v20.
        await source.RunRefreshTimerAsync(
            () => true,
            (wait, _) =>
            {
                waits.Add(wait);
                if (waits.Count == 1)
                {
                    lineup = LineupWith(l =>
                    {
                        OnlyLoadable(l);
                        Versions(l, "gumshoe").Insert(0, GumshoePointer(20, Takeover, Takeover));
                    });
                }
                else
                {
                    stop.Cancel();
                }

                clock.Now += wait;
                return Task.CompletedTask;
            },
            stop.Token);

        Assert.Equal(TimeSpan.FromMinutes(10), waits[0]);
        Assert.Equal(2, reads.Count(p => p == "lineup.json"));
        Assert.Contains("manifests/gumshoe/v20.json", reads);
        Assert.Equal(1, guideRefreshes); // and Jellyfin is asked to re-read the guide
        Assert.Equal(20, (await source.SegmentAtAsync("gumshoe", Takeover + 60_000, CancellationToken.None)).Version);
    }

    [Fact]
    public async Task A_channel_whose_file_failed_to_load_is_back_within_5_minutes()
    {
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(1_793_000_000_000));
        var lineup = LineupWith(OnlyLoadable);
        var reads = new List<string>();
        var homeroomDown = true;
        var source = new SpelunkrSource(Reader(() => lineup, reads, p => homeroomDown && p == "manifests/homeroom/v21.json"), NullLogger<SpelunkrSource>.Instance, () => { }, clock);

        Assert.Equal(TimeSpan.FromMinutes(5), await source.RefreshIfDueAsync(CancellationToken.None));
        Assert.Equal(["gumshoe"], (await source.GetChannelsAsync(CancellationToken.None)).Select(c => c.Id));

        homeroomDown = false;
        clock.Now += TimeSpan.FromMinutes(4);
        await source.RefreshIfDueAsync(CancellationToken.None);
        Assert.Equal(1, reads.Count(p => p == "lineup.json")); // not due yet

        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(TimeSpan.FromMinutes(10), await source.RefreshIfDueAsync(CancellationToken.None)); // nothing failed: back to 10
        Assert.Equal(["gumshoe", "homeroom"], (await source.GetChannelsAsync(CancellationToken.None)).Select(c => c.Id).Order());
    }

    [Fact]
    public async Task Film_descriptions_that_failed_once_load_at_the_next_refresh_and_update_the_guide()
    {
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(1_793_000_000_000));
        var lineup = LineupWith(OnlyLoadable);
        var detailsDown = true;
        var guideRefreshes = 0;
        var source = new SpelunkrSource(Reader(() => lineup, fail: p => detailsDown && p.StartsWith("details/", StringComparison.Ordinal)), NullLogger<SpelunkrSource>.Instance, () => guideRefreshes++, clock);
        var film = await source.SegmentAtAsync("gumshoe", 1_793_215_002_819, CancellationToken.None);
        Assert.Null(source.Details("gumshoe", film));

        detailsDown = false;
        clock.Now += TimeSpan.FromMinutes(10);
        await source.RefreshIfDueAsync(CancellationToken.None);

        Assert.NotNull(source.Details("gumshoe", film)?.Description);
        Assert.Equal(1, guideRefreshes);
    }

    [Theory]
    [InlineData("rocket-punch", "dial", "\"TBD\"")] // a channel the plugin doesn't even offer
    [InlineData("rocket-punch", "logomark", "\"pixel\"")]
    [InlineData("gumshoe", "comingSoon", "\"soon\"")]
    [InlineData("gumshoe", "id", "null")]
    [InlineData("gumshoe", "versions", "null")]
    public async Task A_malformed_channel_is_skipped_and_the_rest_still_listed(string channelId, string field, string json)
    {
        var lineup = LineupWith(l => l["channels"]!.AsArray().Single(c => (string?)c!["id"] == channelId)![field] = JsonNode.Parse(json));
        var source = new SpelunkrSource(Reader(() => lineup), NullLogger<SpelunkrSource>.Instance);

        var channels = (await source.GetChannelsAsync(CancellationToken.None)).Select(c => c.Id).ToList();

        Assert.Contains("homeroom", channels);
        Assert.Equal(channelId != "gumshoe", channels.Contains("gumshoe"));
    }

    [Fact]
    public async Task Versions_the_lineup_stops_listing_are_forgotten_but_never_while_their_replacement_fails_to_load()
    {
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(1_793_000_000_000));
        var lineup = LineupWith(OnlyLoadable);
        var v20Down = true;
        var source = new SpelunkrSource(Reader(() => lineup, fail: p => v20Down && p == "manifests/gumshoe/v20.json"), NullLogger<SpelunkrSource>.Instance, () => { }, clock);
        await source.RefreshIfDueAsync(CancellationToken.None);

        // Spelunkr replaces v19 with v20 in the lineup, and v20 fails to load: v19 must stay, or the channel has nothing.
        lineup = LineupWith(l =>
        {
            OnlyLoadable(l);
            var versions = Versions(l, "gumshoe");
            versions.Clear();
            versions.Add(GumshoePointer(20, Takeover, Takeover));
        });
        clock.Now += TimeSpan.FromMinutes(10);
        await source.RefreshIfDueAsync(CancellationToken.None);
        Assert.Equal(19, (await source.SegmentAtAsync("gumshoe", Takeover - 60_000, CancellationToken.None)).Version);

        // v20 loads at the retry; v19 is gone, so even before v20's start the engine answers from v20, as the site does.
        v20Down = false;
        clock.Now += TimeSpan.FromMinutes(5);
        await source.RefreshIfDueAsync(CancellationToken.None);
        Assert.Equal(20, (await source.SegmentAtAsync("gumshoe", Takeover - 60_000, CancellationToken.None)).Version);
        Assert.Equal(20, (await source.SegmentAtAsync("gumshoe", Takeover + 60_000, CancellationToken.None)).Version);
    }

    [Theory]
    [InlineData("\"http://audio.example/bumps/\"")]
    [InlineData("\"file:///etc/\"")]
    [InlineData("\"bumps/\"")]
    [InlineData("null")]
    public async Task A_lineup_whose_music_address_is_not_https_is_refused_and_the_earlier_copy_kept(string audioBase)
    {
        var clock = new Clock(DateTimeOffset.FromUnixTimeMilliseconds(1_793_000_000_000));
        var lineup = LineupWith(OnlyLoadable);
        var reads = new List<string>();
        var source = new SpelunkrSource(Reader(() => lineup, reads), NullLogger<SpelunkrSource>.Instance, () => { }, clock);
        await source.RefreshIfDueAsync(CancellationToken.None);

        lineup = LineupWith(l =>
        {
            OnlyLoadable(l);
            l["audioBase"] = JsonNode.Parse(audioBase);
            Versions(l, "gumshoe").Insert(0, GumshoePointer(20, Takeover, Takeover));
        });
        clock.Now += TimeSpan.FromMinutes(10);

        Assert.Equal(TimeSpan.FromMinutes(5), await source.RefreshIfDueAsync(CancellationToken.None));
        Assert.DoesNotContain("manifests/gumshoe/v20.json", reads); // nothing from the refused lineup was loaded
        var bump = await source.SegmentAtAsync("gumshoe", 1_793_220_818_820, CancellationToken.None);
        Assert.StartsWith("https://audio.spelunkr.tv/bumps/", bump.Channel!.Audio!.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_data_read_that_stalls_gives_up_after_its_own_limit_and_costs_only_that_channel()
    {
        // The data host accepts the request for homeroom's manifest and never answers.
        var reader = SpelunkrSource.BucketReader(new OneHandlerFactory(new StallingHandler("/manifests/homeroom/")), TimeSpan.FromMilliseconds(200));
        var source = new SpelunkrSource(reader, NullLogger<SpelunkrSource>.Instance);

        var channels = await source.GetChannelsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Contains(channels, c => c.Id == "gumshoe");
        Assert.DoesNotContain(channels, c => c.Id == "homeroom");
    }

    [Fact]
    public async Task The_refresh_timer_never_runs_alongside_another_refresh_and_stops_at_once_on_shutdown()
    {
        var gate = new TaskCompletionSource<string>();
        var lineupReads = 0;
        var source = new SpelunkrSource(
            (path, _) => path == "lineup.json" ? (Interlocked.Increment(ref lineupReads) == 1 ? gate.Task : throw new InvalidOperationException("second refresh")) : Task.FromResult(File.ReadAllText(Path.Combine(ScheduleEngineTests.FixtureDir, path))),
            NullLogger<SpelunkrSource>.Instance);
        var first = source.RefreshIfDueAsync(CancellationToken.None); // a refresh in progress, stuck on its download
        using var stop = new CancellationTokenSource();

        var timer = source.RunRefreshTimerAsync(() => true, Task.Delay, stop.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await timer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // shutdown doesn't wait for the stuck download

        Assert.Equal(1, lineupReads);
        gate.SetResult(LineupWith(OnlyLoadable));
        await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    /// <summary>Serves the fixture mirror, but never answers for paths containing <paramref name="stall"/>.</summary>
    private sealed class StallingHandler(string stall) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains(stall, StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var file = Path.Combine(ScheduleEngineTests.FixtureDir, path.TrimStart('/'));
            return File.Exists(file)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(await File.ReadAllTextAsync(file, cancellationToken)) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
