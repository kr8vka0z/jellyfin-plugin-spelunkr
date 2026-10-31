// cut.test.ts: the release decisions. bun test tools/release
import { expect, test } from "bun:test";
import { changeList, decideCut, isMaintenanceOnly, nextVersion, releaseBody, testBuildLine } from "./cut.ts";

const pr = (v: string, number = 7) => ({ number, url: `https://x/${number}`, headRefName: `release/v${v}` });
const base = { changed: true, prs: [], mainInDev: true, movedSinceCut: false, forcedVersion: "", versionIsLive: false };

test("versions stay 0.x: a feature raises the middle, anything else the last", () => {
  expect(nextVersion("0.3.4", ["fix: a", "feat(guide): b"])).toBe("0.4.0");
  expect(nextVersion("0.3.4", ["fix: a", "chore: feature flag"])).toBe("0.3.5");
});

test.each([
  ["no changes, no PR", { changed: false }, "nothing"],
  ["no changes leaves an open PR alone", { changed: false, prs: [pr("0.3.5")] }, "nothing"],
  ["first cut", {}, "new"],
  ["recut with nothing new", { prs: [pr("0.3.5")] }, "same"],
  ["recut after dev moved", { prs: [pr("0.3.5")], movedSinceCut: true }, "update"],
  ["main ahead of dev", { mainInDev: false }, "blocked"],
  ["two release PRs", { prs: [pr("0.3.5"), pr("0.3.6", 8)] }, "blocked"],
  ["a hotfix took the version", { prs: [pr("0.3.5")], movedSinceCut: true, versionIsLive: true }, "blocked"],
  ["forced version differs from the open PR", { prs: [pr("0.3.5")], forcedVersion: "0.4.0" }, "blocked"],
  ["forced version is not X.Y.Z", { forcedVersion: "1.0" }, "blocked"],
])("%s", (_name, over, action) => {
  expect(decideCut({ ...base, ...over }).action).toBe(action);
});

test("an update keeps the open PR's version and number", () => {
  const d = decideCut({ ...base, prs: [pr("0.3.5")], movedSinceCut: true });
  expect([d.version, d.prNumber]).toEqual(["0.3.5", 7]);
});

test("maintenance only is judged on files that ship", () => {
  expect(isMaintenanceOnly([".github/workflows/build.yml", "tools/release/cut.ts", "README.md"])).toBe(true);
  expect(isMaintenanceOnly(["README.md", "Jellyfin.Plugin.Spelunkr/Plugin.cs"])).toBe(false);
  expect(isMaintenanceOnly([])).toBe(false);
});

test("notes list only PRs in the release", () => {
  const merged = [
    { number: 1, title: "a", mergeCommit: { oid: "s1" } },
    { number: 2, title: "b", mergeCommit: { oid: "s2" } },
    { number: 3, title: "c", mergeCommit: null },
  ];
  expect(changeList(merged, new Set(["s2"])).map((p) => p.number)).toEqual([2]);
});

test("the first page holds what the standard asks for", () => {
  const body = releaseBody({
    changes: [{ number: 4, title: "feat(guide): add posters" }],
    maintenance: false,
    repo: "o/r",
    cutSha: "abcdef1234",
    testBuild: "line",
  });
  expect(body).toContain("- add posters (#4)");
  expect(body).toContain("https://raw.githubusercontent.com/o/r/dev/manifest-dev.json");
  expect(body).toContain("[`abcdef1`](https://github.com/o/r/commit/abcdef1234)");
  expect(body.match(/^- (Unknown|Does not apply):/gm)).toHaveLength(5);
  expect(body).not.toMatch(/^- Passed/m);
  expect(body).toContain("## How to undo");
});

test("the test build line never claims a build that holds other code", () => {
  const feed = { version: "0.3.4.7", changelog: "https://x/commit/a" };
  expect(testBuildLine(feed, true)).toContain("holds exactly this code");
  expect(testBuildLine(feed, false)).toContain("not on the test feed");
  expect(testBuildLine(undefined, false)).toContain("No test build");
});
