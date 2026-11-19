// TunerRefusalTests: Jellyfin asks every tuner about every channel, so the Spelunkr tuner must turn other tuners'
// channels away the way Jellyfin expects (an empty list, FileNotFoundException) without touching Spelunkr's data,
// or a Spelunkr outage breaks every other tuner. Also: channel logos live in the data folder, not the cache.
using System.Reflection;
using Jellyfin.Plugin.Spelunkr.Stream;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public sealed class TunerRefusalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("spelunkr-tuner").FullName;
    private readonly List<string> _reads = [];

    public void Dispose() => Directory.Delete(_root, true);

    private SpelunkrTunerHost Tuner()
    {
        var paths = Stub<IApplicationPaths>.Of(m => m.Name switch
        {
            "get_CachePath" => Path.Combine(_root, "cache"),
            "get_DataPath" => Path.Combine(_root, "data"),
            _ => null,
        });
        var config = Stub<IConfigurationManager>.Of(m => m.Name switch
        {
            "get_CommonApplicationPaths" => paths,
            "GetConfiguration" => new LiveTvOptions { TunerHosts = [new TunerHostInfo { Id = "t", Type = SpelunkrTunerHost.TunerType }] },
            _ => null,
        });
        var http = Stub<IHttpClientFactory>.Of(_ => throw new InvalidOperationException("no network in this test"));
        return new SpelunkrTunerHost(
            SpelunkrSourceTests.FromFixtures(_reads),
            new ArchiveOriginals(http, NullLogger<ArchiveOriginals>.Instance),
            config,
            Stub<IMediaEncoder>.Of(_ => null),
            Stub<IServerApplicationHost>.Of(_ => null),
            NullLogger<SpelunkrTunerHost>.Instance);
    }

    [Fact]
    public async Task Another_tuners_channel_is_refused_without_reading_spelunkrs_data()
    {
        var tuner = Tuner();

        Assert.Empty(await tuner.GetChannelStreamMediaSources("hdhr_5", CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(() => tuner.GetChannelStream("hdhr_5", "s", [], CancellationToken.None));
        Assert.Empty(_reads);
    }

    [Fact]
    public async Task Channel_logos_are_kept_in_the_data_folder()
    {
        // Jellyfin's cache cleaner deletes files over a month old, and the channel keeps only the logo's path.
        var channels = await Tuner().GetChannels(false, CancellationToken.None);

        var logo = channels.Single(c => c.Id == "spelunkr_gumshoe").ImagePath;
        Assert.StartsWith(Path.Combine(_root, "data") + Path.DirectorySeparatorChar, logo, StringComparison.Ordinal);
        Assert.True(File.Exists(logo));
    }

    /// <summary>An interface whose every member answers through one function (the test project has no mocking library).</summary>
    public class Stub<T> : DispatchProxy
        where T : class
    {
        private Func<MethodInfo, object?> _answer = _ => null;

        public static T Of(Func<MethodInfo, object?> answer)
        {
            var proxy = Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy)._answer = answer;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => _answer(targetMethod!) ?? (targetMethod!.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType) : null);
    }
}
