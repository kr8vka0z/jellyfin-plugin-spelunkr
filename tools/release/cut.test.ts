// cut.test.ts: the release decisions. bun test tools/release
import { expect, test } from "bun:test";
import { changeList, decideCut, isMaintenanceOnly, nextVersion, releaseBody, testBuildLine, updateReleasePr, versionWarning } from "./cut.ts";

const pr = (v: string, number = 7) => ({ number, url: `https://x/${number}`, headRefName: `release/v${v}` });
const base = { changed: true, prs: [], mainInDev: true, movedSinceCut: false, forcedVersion: "", latest: "0.3.4" };

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
  ["a hotfix took the version", { prs: [pr("0.3.5")], movedSinceCut: true, latest: "0.3.5" }, "blocked"],
  ["a hotfix passed the version", { prs: [pr("0.3.5")], latest: "0.3.6" }, "blocked"],
  ["forced version already live", { forcedVersion: "0.3.4" }, "blocked"],
  ["forced version below live", { forcedVersion: "0.2.0" }, "blocked"],
  ["forced version above live", { forcedVersion: "1.0.0" }, "new"],
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

test("an open patch release that now holds a feature is flagged, at the top of the PR", () => {
  const warning = versionWarning("0.3.5", "0.3.4", ["fix: a", "feat: b"]);
  expect(warning).toContain("v0.4.0");
  expect(versionWarning("0.3.5", "0.3.4", ["fix: a"])).toBeUndefined();
  expect(versionWarning("0.4.0", "0.3.4", ["feat: b"])).toBeUndefined();
  expect(releaseBody({ changes: [], maintenance: false, repo: "o/r", cutSha: "a", testBuild: "", warning })).toStartWith(`> **Warning:** ${warning}`);
});

// Updating an open release PR. Each case: which commands fail, what the remote branch holds, and the outcome.
const NEW = "new-sha";
test.each([
  ["the push works", { push: true }, "ok", undefined, false],
  ["the push fails and the branch can't be read", { lsRemote: false }, "error", "could not tell what the branch holds", false],
  ["the push fails but the branch holds the new commit", { remote: NEW }, "warning", "the update stands", false],
  ["the push fails, the branch is old, the old body goes back", { remote: "old-sha" }, "error", "keeps its old commit and description", true],
  ["the push fails, the branch is old, the old body can't go back", { remote: "old-sha", restore: false }, "error", "could not be put back", true],
] as const)("release PR update: %s", (_name, world: { push?: boolean; lsRemote?: boolean; remote?: string; restore?: boolean }, level, message, restored) => {
  const calls: string[] = [];
  let patches = 0;
  const run = (cmd: string, args: string[], input?: string) => {
    const call = [cmd, ...args].join(" ");
    calls.push(input === undefined ? call : `${call} <${input}`);
    if (call.includes("PATCH") && ++patches === 2 && world.restore === false) throw new Error("restore failed");
    if (args[0] === "push" && !world.push) throw new Error("push failed");
    if (args[0] === "ls-remote") {
      if (world.lsRemote === false) throw new Error("no answer");
      return `${world.remote}\trefs/heads/release/v0.3.5`;
    }
    if (args[0] === "rev-parse") return NEW;
    if (args.includes("--jq")) return "old notes";
    return "";
  };
  const r = updateReleasePr(run, { repo: "o/r", pr: 7, version: "0.3.5", body: "new notes" });
  expect(r.level).toBe(level);
  if (message) expect(r.message).toContain(message);
  else expect(r.message).toBeUndefined();
  // The new body is written before the push; the old one goes back only once the branch is shown to be old.
  expect(calls.slice(0, 3)).toEqual([
    "gh api repos/o/r/pulls/7 --jq .body",
    "gh api -X PATCH repos/o/r/pulls/7 -f title=Release v0.3.5 -F body=@- <new notes",
    "git push --force origin release/v0.3.5",
  ]);
  expect(calls.includes("gh api -X PATCH repos/o/r/pulls/7 -F body=@- <old notes")).toBe(restored);
  if (restored) expect(calls.indexOf("git ls-remote --exit-code origin refs/heads/release/v0.3.5")).toBeLessThan(calls.length - 1);
});
