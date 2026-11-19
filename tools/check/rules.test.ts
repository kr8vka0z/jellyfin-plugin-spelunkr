// rules.test.ts: the daily check's offline rules. bun test tools/check
import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { fitReport, groupProblems, lineupProblems, offered, REPORT_LIMIT } from "./rules.ts";

const fixtures = join(import.meta.dir, "../../Jellyfin.Plugin.Spelunkr.Tests/Fixtures");

// The same list OfferedCasesTests.cs runs against SpelunkrSource.IsOffered.
const cases: { name: string; channel: any; offered: boolean }[] = JSON.parse(readFileSync(join(fixtures, "offered-cases.json"), "utf8"));
test.each(cases.map((c) => [c.name, c] as const))("offered: %s", (_name, c) => {
  expect(offered(c.channel)).toBe(c.offered);
});

const lineup = () => JSON.parse(readFileSync(join(fixtures, "spelunkr/lineup.json"), "utf8"));

test("today's lineup reads cleanly, and a wrong type on a channel the plugin doesn't offer is reported", () => {
  const l = lineup();
  expect(lineupProblems(l.channels)).toEqual([]);
  const hidden = l.channels.find((c: any) => c.id === "test-pattern");
  expect(offered(hidden)).toBe(false);
  hidden.dial = "TBD";
  hidden.logomark = "pixel";
  hidden.debugOnly = null;
  l.channels.push({ name: "no id" });
  expect(lineupProblems(l.channels)).toEqual([
    'channel test-pattern.dial: expected integer or null, got "TBD"',
    "channel test-pattern.debugOnly: expected boolean, got null",
    'channel test-pattern.logomark: expected object or null, got "pixel"',
    "channel [34].id: expected string, got undefined",
  ]);
});

test("thousands of problem lines become a few per kind, and the report fits a GitHub issue", () => {
  const problems = [
    ...Array.from({ length: 30 }, (_, c) =>
      Array.from({ length: 200 }, (_, i) => `channel-${c} v19 ads.slices[${i}].lengthMs: expected integer, got "${i}000"`),
    ).flat(),
    "channel gumshoe.color: not #RRGGBB: red",
  ];
  const grouped = groupProblems(problems).split("\n");
  expect(grouped).toEqual([
    ...problems.slice(0, 5),
    '... and 5995 more of "* v* ads.slices[].lengthMs: expected integer"',
    "channel gumshoe.color: not #RRGGBB: red",
  ]);

  const huge = `### FAIL: data\n\n${problems.join("\n")}`;
  expect(huge.length).toBeGreaterThan(65_536);
  const fitted = fitReport(huge);
  expect(fitted.length).toBeLessThanOrEqual(REPORT_LIMIT);
  expect(fitted).toEndWith("in the job log (Run link below).");
  expect(fitReport("short")).toBe("short");
});
