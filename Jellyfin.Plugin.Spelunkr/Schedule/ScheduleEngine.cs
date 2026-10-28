// ScheduleEngine: runs Spelunkr's own schedule code (engine.js, wrapped by schedule.js) inside Jint, in-process.
// Decision: embed their JavaScript verbatim instead of porting it to C#, so a change on spelunkr.tv is fixed by
// re-running tools/extract-engine, not by re-deriving their algorithm. Data crosses the boundary as JSON strings.
using System.Reflection;
using System.Text.Json;
using Jint;
using Jint.Native;

namespace Jellyfin.Plugin.Spelunkr.Schedule;

/// <summary>Answers "what is on channel X at time T" exactly as spelunkr.tv does.</summary>
public sealed class ScheduleEngine
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Jint engines are not thread-safe; every call goes through this lock. A lookup costs well under a
    // millisecond once a cycle is built, so one lock for all channels is fine.
    private readonly Lock _lock = new();
    private readonly Engine _js;
    private readonly JsFunctions _fn;
    private readonly Dictionary<string, JsValue> _channels = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="ScheduleEngine"/> class.</summary>
    public ScheduleEngine()
    {
        _js = new Engine();
        _js.Modules.Add("./engine.js", ReadResource("engine.js"));
        _js.Modules.Add("schedule", ReadResource("schedule.js"));
        var ns = _js.Modules.Import("schedule");
        _fn = new JsFunctions(ns.Get("createChannel"), ns.Get("addChannelVersion"), ns.Get("segmentAt"), ns.Get("segmentsBetween"));
    }

    /// <summary>Registers one manifest version for a channel. Returns false if it was already registered.</summary>
    public bool AddVersion(string channelId, string manifestJson, string? platformJson, string pointerJson)
    {
        lock (_lock)
        {
            var channel = _channels.TryGetValue(channelId, out var existing) ? existing : _js.Invoke(_fn.CreateChannel, channelId);
            var added = _js.Invoke(_fn.AddVersion, channel, manifestJson, platformJson is null ? JsValue.Null : platformJson, pointerJson).AsBoolean();
            // Only keep a channel once a version loaded, so HasChannel never reports an empty schedule.
            _channels[channelId] = channel;
            return added;
        }
    }

    /// <summary>Whether at least one version of the channel's schedule is loaded.</summary>
    public bool HasChannel(string channelId)
    {
        lock (_lock)
        {
            return _channels.ContainsKey(channelId);
        }
    }

    /// <summary>The segment playing at <paramref name="atMs"/> (Unix ms).</summary>
    public Segment SegmentAt(string channelId, long atMs, string audioBase)
        => JsonSerializer.Deserialize<Segment>(SegmentAtJson(channelId, atMs, audioBase), Json)!;

    /// <summary>Every segment from the one playing at <paramref name="fromMs"/> until <paramref name="toMs"/>.</summary>
    public IReadOnlyList<Segment> SegmentsBetween(string channelId, long fromMs, long toMs, string audioBase)
        => JsonSerializer.Deserialize<List<Segment>>(SegmentsBetweenJson(channelId, fromMs, toMs, audioBase), Json)!;

    internal string SegmentAtJson(string channelId, long atMs, string audioBase)
    {
        lock (_lock)
        {
            return _js.Invoke(_fn.SegmentAt, Channel(channelId), atMs, audioBase).AsString();
        }
    }

    internal string SegmentsBetweenJson(string channelId, long fromMs, long toMs, string audioBase)
    {
        lock (_lock)
        {
            return _js.Invoke(_fn.SegmentsBetween, Channel(channelId), fromMs, toMs, audioBase).AsString();
        }
    }

    private JsValue Channel(string channelId)
        => _channels.TryGetValue(channelId, out var c) ? c : throw new InvalidOperationException($"No schedule loaded for channel {channelId}");

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record JsFunctions(JsValue CreateChannel, JsValue AddVersion, JsValue SegmentAt, JsValue SegmentsBetween);
}
