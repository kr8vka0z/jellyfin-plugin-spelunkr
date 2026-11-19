// feed.test.ts: test builds must install over a live version, lose to the next release, and stay out of
// the public feed. bun test tools/release
import { expect, test } from "bun:test";
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { feedProblems, isAbove as above, isSuperseded, newestStable, releaseVersion, targetAbi, testVersion } from "./feed.ts";

const manifest = (...entries: [string, string][]) =>
  JSON.stringify([{ versions: entries.map(([version, sourceUrl]) => ({ version, sourceUrl })) }]);

test("a test build sits above live and below the next release", () => {
  const { tag, version } = testVersion(["v0.3.4-dev.9", "v0.3.4", "v0.3.3"], 12);
  expect(tag).toBe("v0.3.4-dev.12");
  expect(version).toBe("0.3.4.12");
  expect(above(version, "0.3.4.0")).toBe(true);
  expect(above("0.3.5.0", version)).toBe(true);
});

test("a later run outranks an earlier one", () => {
  expect(above(testVersion(["v0.3.4"], 10).version, testVersion(["v0.3.4"], 9).version)).toBe(true);
});

test("after a release test builds move up with it", () => {
  expect(testVersion(["v0.3.5", "v0.3.4-dev.12", "v0.3.4"], 13).version).toBe("0.3.5.13");
});

test("-dev tags are never the live version", () => {
  expect(newestStable(["v0.3.5-dev.2", "v0.3.4"])).toBe("0.3.4");
});

test("a re-run of an old build can't replace a newer one in the test feed", () => {
  const feed = manifest(["0.3.4.12", "https://x/p.zip"]);
  expect(isSuperseded(feed, 7)).toBe(true);
  expect(isSuperseded(feed, 12)).toBe(false); // re-running the newest build is fine
  expect(isSuperseded(feed, 13)).toBe(false);
  expect(isSuperseded("", 1)).toBe(false);
  // After a release, an old run's version would look newer. The run number still decides.
  expect(isSuperseded(manifest(["0.3.4.12", "https://x/p.zip"]), Number(testVersion(["v0.3.5"], 7).version.split(".")[3]))).toBe(true);
});

test("old code re-run after a release stays below that release", () => {
  const dir = mkdtempSync(join(tmpdir(), "feed-"));
  const git = (...args: string[]) =>
    execFileSync("git", ["-c", "user.name=t", "-c", "user.email=t@t", ...args], { cwd: dir, encoding: "utf8" });
  git("init", "-q");
  git("commit", "-q", "--allow-empty", "-m", "a");
  git("tag", "v0.3.4");
  git("commit", "-q", "--allow-empty", "-m", "old test build's code");
  const old = git("rev-parse", "HEAD").trim();
  git("switch", "-q", "-c", "hotfix", "v0.3.4");
  git("commit", "-q", "--allow-empty", "-m", "hotfix");
  git("tag", "v0.3.5"); // live now, but not in the old code
  git("checkout", "-q", old);
  const out = execFileSync("bun", [resolve("tools/release/feed.ts"), "test-version", "12"], { cwd: dir, encoding: "utf8" });
  expect(out).toContain("version=0.3.4.12");
  expect(above("0.3.5.0", "0.3.4.12")).toBe(true);
  git("merge", "-q", "--no-edit", "--no-ff", "v0.3.5"); // the copy-back brings the release in
  expect(execFileSync("bun", [resolve("tools/release/feed.ts"), "test-version", "13"], { cwd: dir, encoding: "utf8" })).toContain("version=0.3.5.13");
});

test("the public feed refuses test builds", () => {
  expect(feedProblems(manifest(["0.3.4.0", "https://x/v0.3.4/p_0.3.4.0.zip"]))).toEqual([]);
  expect(feedProblems(manifest(["0.3.4.7", "https://x/v0.3.4-dev.7/p.zip"]))).toHaveLength(1);
  expect(feedProblems(manifest(["0.3.5.0", "https://x/v0.3.4-dev.7/p.zip"]))).toHaveLength(1);
  expect(feedProblems(manifest())).toHaveLength(1);
});

// The release job's first step: a mistyped tag must stop it before anything is published.
test.each([
  ["v0.6.2", "0.6.2.0"],
  ["v0.6", undefined],
  ["v0.6.2.1", undefined],
  ["0.6.2", undefined],
  ["v0.6.2-dev.3", undefined],
])("release tag %s", (tag, version) => {
  if (version) expect(releaseVersion(tag)).toBe(version);
  else expect(() => releaseVersion(tag)).toThrow("is not vX.Y.Z");
});

test("the zip's targetAbi is the Jellyfin version the projects build against", () => {
  const ref = (pkg: string, v: string) => `<PackageReference Include="Jellyfin.${pkg}" Version="${v}" ExcludeAssets="runtime" />`;
  expect(targetAbi([ref("Controller", "12.3.0") + ref("Model", "12.3.0"), ref("Controller", "12.3.0")])).toBe("12.3.0.0");
  expect(() => targetAbi([ref("Controller", "12.4.0") + ref("Model", "12.3.0")])).toThrow("12.4.0, 12.3.0");
  expect(() => targetAbi(["<Project />"])).toThrow("none");
  expect(execFileSync("bun", [resolve("tools/release/feed.ts"), "target-abi"], { encoding: "utf8" })).toMatch(/^\d+\.\d+\.\d+\.0\n$/);
});

// Both workflows build their feed entry with feed-entry.jq. A re-run of the release job must replace its entry.
test("a feed entry goes first; the public feed keeps older versions and never lists one twice", () => {
  const entry = (feed: string, v: string, keep: boolean) =>
    JSON.parse(execFileSync("jq", ["--arg", "v", v, "--arg", "abi", "12.3.0.0", "--arg", "url", `https://x/${v}.zip`, "--arg", "md5", "m",
      "--arg", "log", "https://x/log", "--argjson", "keep", String(keep), "-f", "tools/release/feed-entry.jq"], { input: feed, encoding: "utf8" }));
  const feed = JSON.stringify([{ name: "Spelunkr", versions: [{ version: "0.3.4.0" }, { version: "0.3.3.0" }] }]);
  const once = entry(feed, "0.3.5.0", true);
  expect(once[0].name).toBe("Spelunkr");
  expect(once[0].versions.map((v: any) => v.version)).toEqual(["0.3.5.0", "0.3.4.0", "0.3.3.0"]);
  expect(Object.keys(once[0].versions[0])).toEqual(["version", "changelog", "targetAbi", "sourceUrl", "checksum", "timestamp"]);
  expect(once[0].versions[0].timestamp).toMatch(/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/);
  expect(entry(JSON.stringify(once), "0.3.5.0", true)[0].versions.map((v: any) => v.version)).toEqual(["0.3.5.0", "0.3.4.0", "0.3.3.0"]);
  expect(entry(feed, "0.3.4.7", false)[0].versions.map((v: any) => v.version)).toEqual(["0.3.4.7"]);
});

test("this repo's public feed passes", () => {
  expect(feedProblems(readFileSync("manifest.json", "utf8"))).toEqual([]);
});

test("the test build workflow can only write the dev feed", () => {
  const yml = readFileSync(".github/workflows/test-build.yml", "utf8");
  expect(yml).toContain("branches: [dev]");
  expect(yml).toContain("> manifest-dev.json");
  expect(yml.match(/feed\.ts fresh/g)).toHaveLength(2); // before anything is published, and on the current feed
  expect(yml).not.toMatch(/>\s*manifest\.json/);
  const pushes = yml.split("\n").filter((l) => l.includes("git push"));
  expect(pushes.length).toBeGreaterThan(0);
  for (const line of pushes) expect(line).toContain("git push origin dev");
});
