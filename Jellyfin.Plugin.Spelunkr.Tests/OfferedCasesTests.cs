// OfferedCasesTests: the plugin and the daily check (tools/check/rules.ts) must agree on which channels are offered,
// or the check watches a different set of channels than the plugin serves. Both read Fixtures/offered-cases.json.
using System.Text.Json;
using Jellyfin.Plugin.Spelunkr.Schedule;
using Xunit;

namespace Jellyfin.Plugin.Spelunkr.Tests;

public class OfferedCasesTests
{
    public static TheoryData<string, string, bool> Cases()
    {
        var data = new TheoryData<string, string, bool>();
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "offered-cases.json")));
        foreach (var c in cases.RootElement.EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!, c.GetProperty("channel").GetRawText(), c.GetProperty("offered").GetBoolean());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsOffered_matches_the_shared_cases(string name, string channel, bool offered)
    {
        var c = JsonSerializer.Deserialize<LineupChannel>(channel, ScheduleEngine.Json)!;

        Assert.True(SpelunkrSource.IsOffered(c) == offered, name);
    }
}
