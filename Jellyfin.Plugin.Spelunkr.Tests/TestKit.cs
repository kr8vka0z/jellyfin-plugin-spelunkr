// TestKit: helpers the stream tests share (reading a job's arguments, the two stream modes, a temp folder), kept in
// one place so a change to the argument layout or the fixtures is made once.
using Jellyfin.Plugin.Spelunkr.Schedule;
using Jellyfin.Plugin.Spelunkr.Stream;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

internal static class TestKit
{
    /// <summary>The bump audio base the schedule tests pass to the engine.</summary>
    public const string AudioBase = "https://audio.spelunkr.tv/bumps/";

    /// <summary>A made-up VA-API device path: no test opens it.</summary>
    public const string Device = "/test/render-node";

    // Segment is an immutable record, so one engine run serves every test that asks.
    private static readonly Lazy<Segment> OpenBumpOnce = new(() => ScheduleEngineTests.GumshoeEngine()
        .SegmentsBetween("gumshoe", 1_793_220_818_819, 1_793_220_818_820, AudioBase)[0]);

    public static TheoryData<string> Modes => new() { "cpu", "gpu" };

    /// <summary>An "open" bump straight from the oracle (Fixtures/timeline-gumshoe-1793217600000.jsonl, the segment with index 173).</summary>
    public static Segment OpenBump() => OpenBumpOnce.Value;

    /// <summary>The value after the <paramref name="occurrence"/>th <paramref name="flag"/>.</summary>
    public static string Arg(IReadOnlyList<string> args, string flag, int occurrence = 0) =>
        args[args.Select((a, i) => (a, i)).Where(x => x.a == flag).ElementAt(occurrence).i + 1];

    public static string Arg(FfmpegJob job, string flag, int occurrence = 0) => Arg(job.Args, flag, occurrence);

    /// <summary>The job's filter graph.</summary>
    public static string Filter(FfmpegJob job) => Arg(job, "-filter_complex");

    /// <summary>Every input, in order.</summary>
    public static List<string> Inputs(IReadOnlyList<string> args) => args.Select((a, i) => (a, i)).Where(x => x.a == "-i").Select(x => args[x.i + 1]).ToList();

    /// <summary>The stream format for a mode named "cpu" or "gpu".</summary>
    public static Picture PictureFor(string mode) => mode == "gpu" ? Picture.Gpu(Device) : Picture.Cpu;

    /// <summary>A job's -t, in ms: what a fake runner returns for a job that runs to its end.</summary>
    public static long AskedMs(FfmpegJob job) => (long)Math.Round(double.Parse(Arg(job, "-t"), System.Globalization.CultureInfo.InvariantCulture) * 1000);

    /// <summary>A temp folder, deleted with everything in it.</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("spelunkr-test").FullName;

        public void Dispose() => Directory.Delete(Path, true);
    }
}
