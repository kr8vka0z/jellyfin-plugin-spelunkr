// PictureTests: GPU (VA-API, 1080p) versus CPU (720p) streams. Every job is joined into one continuous stream,
// so a single job at another size or encoder settings breaks playback mid-way; these pin that every kind of job,
// fallbacks included, matches the stream's mode, and that the mode follows Jellyfin's own setting.
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Spelunkr.Schedule;
using Jellyfin.Plugin.Spelunkr.Stream;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public partial class PictureTests
{
    private const string Device = "/test/render-node";
    private static readonly ChannelLook Look = new("HOMEROOM", "#336699");

    private static string Arg(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    private static IEnumerable<IReadOnlyList<FfmpegJob>> EveryKind(Picture picture)
    {
        var show = new Segment { Kind = "show", Url = "https://archive.org/download/x/x.mp4", Title = "X" };
        var upNext = new Segment { Kind = "up-next", LengthMs = 10_000, Next = "Y", Sting = new BumpAudio("s.mp3", "https://audio.example/s.mp3", 1) };
        yield return FfmpegJobs.For(show, Look, 0, 5_000, 0, "https://archive.org/download/x/x.m4v", picture);
        yield return FfmpegJobs.For(show with { Kind = "ad" }, Look, 0, 5_000, 0, null, picture);
        yield return FfmpegJobs.For(StreamPlanTests.OpenBump(), Look, 0, 5_000, 0, null, picture);
        yield return FfmpegJobs.For(upNext, Look, 0, 5_000, 0, null, picture);
        yield return FfmpegJobs.For(new Segment { Kind = "unknown", Title = "Z" }, Look, 0, 5_000, 0, null, picture);
    }

    public static TheoryData<string, int, int> Modes => new() { { "cpu", 1280, 720 }, { "gpu", 1920, 1080 } };

    [Theory]
    [MemberData(nameof(Modes))]
    public void Every_job_of_every_kind_outputs_the_streams_one_format(string mode, int width, int height)
    {
        var picture = mode == "gpu" ? Picture.Gpu(Device) : Picture.Cpu;

        foreach (var jobs in EveryKind(picture))
        {
            foreach (var job in jobs)
            {
                var all = string.Join(' ', job.Args);
                var sizes = Size().Matches(all).Select(m => (int.Parse(m.Groups["w"].Value), int.Parse(m.Groups["h"].Value))).ToList();
                Assert.NotEmpty(sizes);
                Assert.All(sizes, s => Assert.Equal((width, height), s));
                Assert.Equal("high", Arg(job.Args, "-profile:v"));
                Assert.Equal("4.1", Arg(job.Args, "-level:v"));
                Assert.Equal("60", Arg(job.Args, "-g"));
                Assert.Equal("0", Arg(job.Args, "-bf"));
            }
        }
    }

    [Fact]
    public void Gpu_streams_follow_every_gpu_job_with_its_cpu_twin_at_the_same_size()
    {
        foreach (var jobs in EveryKind(Picture.Gpu(Device)))
        {
            Assert.Equal(0, jobs.Count % 2);
            for (var i = 0; i < jobs.Count; i += 2)
            {
                var (gpu, cpu) = (jobs[i].Args, jobs[i + 1].Args);
                Assert.Equal("h264_vaapi", Arg(gpu, "-c:v"));
                Assert.Equal("vaapi=va:" + Device, Arg(gpu, "-init_hw_device"));
                Assert.Equal("libx264", Arg(cpu, "-c:v"));
                Assert.DoesNotContain("-init_hw_device", cpu);
                // The CPU twin is the same piece: same inputs, same text, same slot in the stream.
                Assert.Equal(Inputs(gpu), Inputs(cpu));
                Assert.Equal(jobs[i].TextFiles, jobs[i + 1].TextFiles);
                Assert.Equal(Arg(gpu, "-output_ts_offset"), Arg(cpu, "-output_ts_offset"));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void The_stream_is_declared_at_its_real_size_so_jellyfin_never_probes_or_caches_it(string mode, int width, int height)
    {
        var source = SpelunkrTunerHost.NewMediaSource("spelunkr_x", mode == "gpu" ? Picture.Gpu(Device) : Picture.Cpu);
        var video = source.MediaStreams.Single(s => s.Type == MediaStreamType.Video);

        // Jellyfin probes (and caches the result forever) unless probing is off or a stream has a real index.
        Assert.False(source.SupportsProbing);
        Assert.All(source.MediaStreams, s => Assert.NotEqual(-1, s.Index));
        Assert.Equal((width, height), (video.Width, video.Height));
        // Unset, Jellyfin's ffmpeg analyzes 200 s of the live stream before the picture starts.
        Assert.Equal(3000, source.AnalyzeDurationMs);
        // Jellyfin compares the container to "ts" to fix the AAC headers for HLS; anything else breaks web playback.
        Assert.Equal("ts", source.Container);
    }

    [Fact]
    public void Gpu_films_are_upscaled_but_cards_are_drawn_at_full_size()
    {
        var show = new Segment { Kind = "show", Url = "https://archive.org/download/x/x.mp4", Title = "X" };
        var film = FfmpegJobs.For(show, Look, 0, 5_000, 0, null, Picture.Gpu(Device))[0];
        var slate = FfmpegJobs.For(new Segment { Kind = "unknown", Title = "Z" }, Look, 0, 5_000, 0, null, Picture.Gpu(Device))[0];

        Assert.Contains("libplacebo=", Arg(film.Args, "-vf"), StringComparison.Ordinal);
        Assert.DoesNotContain(slate.Args, a => a.Contains("libplacebo", StringComparison.Ordinal));
        Assert.Contains("fontsize=72", Arg(slate.Args, "-filter_complex"), StringComparison.Ordinal); // 48 px at 720p, scaled
    }

    private const string Cabinet = "/test/cabinet.png";

    public static TheoryData<string> CabinetModes => new() { "cpu", "gpu" };

    private static Picture WithCabinet(string mode) => (mode == "gpu" ? Picture.Gpu(Device) : Picture.Cpu) with { Cabinet = Cabinet };

    [Theory]
    [MemberData(nameof(CabinetModes))]
    public void Every_job_of_every_kind_draws_the_cabinet_when_the_stream_has_one(string mode)
    {
        var picture = WithCabinet(mode);

        foreach (var job in EveryKind(picture).Append(FfmpegJobs.TuningIn(new Segment { Kind = "show", Title = "X" }, Look, 4_000, picture)).SelectMany(j => j))
        {
            // The cabinet is the last input, whatever else the job reads.
            var inputs = Inputs(job.Args);
            Assert.Equal(Cabinet, inputs[^1]);
            var filter = Arg(job.Args, "-filter_complex");
            Assert.Contains($"[{inputs.Count - 1}:v]", filter, StringComparison.Ordinal);
            // Decoded once and looped: doing it per frame cost a GPU film two thirds of its speed.
            Assert.Contains("loop=-1:1:0", filter, StringComparison.Ordinal);
            Assert.DoesNotContain("-vf", job.Args);
            // Pinned, or the wood changes shade with each film's color tags.
            Assert.Contains("bt709", filter, StringComparison.Ordinal);
            // Converted, never relabeled: setparams only changes the tag, which leaves other color spaces wrong.
            Assert.DoesNotContain("setparams=colorspace", filter, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Without_a_cabinet_no_job_reads_or_draws_one()
    {
        foreach (var job in EveryKind(Picture.Gpu(Device)).SelectMany(j => j))
        {
            Assert.DoesNotContain(Cabinet, job.Args);
            Assert.DoesNotContain(job.Args, a => a.Contains("loop=", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("gpu", 1920, 1080, 312, 54, 1296, 972)]
    [InlineData("cpu", 1280, 720, 208, 36, 864, 648)]
    public void The_film_goes_in_the_tube_and_the_finished_frame_is_the_streams_size(string mode, int width, int height, int x, int y, int tubeW, int tubeH)
    {
        var picture = WithCabinet(mode);
        Assert.Equal((x, y, tubeW, tubeH), (picture.TubeX, picture.TubeY, picture.TubeWidth, picture.TubeHeight));
        // Even offsets: 4:2:0 video can't start on an odd pixel.
        Assert.Equal(0, (x % 2) + (y % 2));

        var show = new Segment { Kind = "show", Url = "https://archive.org/download/x/x.mp4" };
        var film = FfmpegJobs.For(show, Look, 0, 5_000, 0, null, picture)[0];
        var filter = Arg(film.Args, "-filter_complex");
        if (mode == "gpu")
        {
            Assert.Contains($"w={width}:h={height}", filter, StringComparison.Ordinal);
            // Its own shape: contain, not a stretch to the tube.
            Assert.Contains("fit_mode=contain", filter, StringComparison.Ordinal);
            Assert.Contains($"pos_w=if(eq(in_idx\\,0)\\,{tubeW}\\,{width})", filter, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"scale={tubeW}:{tubeH}:force_original_aspect_ratio=decrease", filter, StringComparison.Ordinal);
            Assert.Contains($"pad={width}:{height}", filter, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Cards_are_tube_sized_and_wrap_to_fit_it()
    {
        const string Title = "A REALLY QUITE LONG FILM TITLE OF THIRTY-TWO LETTERS";
        var job = FfmpegJobs.For(new Segment { Kind = "unknown", Title = Title }, Look, 0, 5_000, 0, null, WithCabinet("gpu"))[0];

        Assert.Contains(job.Args, a => a.Contains("s=1296x972", StringComparison.Ordinal));
        Assert.Contains("fontsize=60", Arg(job.Args, "-filter_complex"), StringComparison.Ordinal);
        Assert.All(job.TextFiles.Values.Single().Split('\n'), line => Assert.True(line.Length <= 30, line));
    }

    public static TheoryData<string, HardwareAccelerationType, bool, string, bool, bool> Settings => new()
    {
        { "VA-API on", HardwareAccelerationType.vaapi, true, Device, true, true },
        { "no hardware acceleration", HardwareAccelerationType.none, true, Device, true, false },
        { "another kind of GPU", HardwareAccelerationType.qsv, true, Device, true, false },
        { "hardware encoding switched off", HardwareAccelerationType.vaapi, false, Device, true, false },
        { "no device set", HardwareAccelerationType.vaapi, true, string.Empty, true, false },
        { "GPU can't share frames with Vulkan", HardwareAccelerationType.vaapi, true, Device, false, false },
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public void Gpu_mode_follows_jellyfins_hardware_acceleration_setting(string because, HardwareAccelerationType type, bool encode, string device, bool vulkan, bool gpu)
    {
        var options = new EncodingOptions { HardwareAccelerationType = type, EnableHardwareEncoding = encode, VaapiDevice = device };

        var picture = SpelunkrTunerHost.PictureFor(options, vulkan, NullLogger.Instance);

        Assert.True(gpu == (picture.VaapiDevice is not null), because);
        Assert.Equal(gpu ? Picture.Gpu(device) : Picture.Cpu, picture);
    }

    private static List<string> Inputs(IReadOnlyList<string> args) => args.Select((a, i) => (a, i)).Where(x => x.a == "-i").Select(x => args[x.i + 1]).ToList();

    // Every place a job sets a frame size: the CPU scale/pad, the color background, and libplacebo.
    [GeneratedRegex(@"(?:scale=|pad=|s=|w=)(?<w>\d+)(?::|x|:h=)(?<h>\d+)")]
    private static partial Regex Size();
}
