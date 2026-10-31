// feed.ts: test build version numbers, and the guard that keeps test builds out of the public feed.
//   bun tools/release/feed.ts test-version <run number>   prints tag=... and version=... for GitHub Actions
//   bun tools/release/feed.ts check <branch>              fails if manifest.json holds anything but real releases
import { existsSync, readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";

export const STABLE_TAG = /^v(\d+\.\d+\.\d+)$/;

/** The newest real release in tags sorted newest first, without its v. -dev tags don't count. */
export const newestStable = (tags: string[]) => tags.map((t) => STABLE_TAG.exec(t)?.[1]).find(Boolean);

/**
 * WHY the live version with a rising fourth number: Jellyfin compares all four parts. 0.3.4.N is above
 * the live 0.3.4.0, so a test build installs, and below the real 0.3.5.0, so a tester moves onto the
 * release. Building on the next version instead (0.3.5.N) would outrank that release forever.
 */
export function testVersion(tags: string[], run: number) {
  const live = newestStable(tags);
  if (!live) throw new Error("no release tag to base a test build on");
  if (!Number.isInteger(run) || run < 1) throw new Error(`bad run number ${run}`);
  return { tag: `v${live}-dev.${run}`, version: `${live}.${run}` };
}

/** What is wrong with the text of a public manifest.json. Real releases end in .0; test builds never do. */
export function feedProblems(text: string) {
  const versions: { version: string; sourceUrl: string }[] = JSON.parse(text)[0]?.versions ?? [];
  if (!versions.length) return ["manifest.json lists no versions"];
  return versions.flatMap((v) =>
    /^\d+\.\d+\.\d+\.0$/.test(v.version) && !v.sourceUrl.includes("-dev.") ? [] : [`test build in the public feed: ${v.version}`],
  );
}

if (import.meta.main) {
  const [command, arg] = process.argv.slice(2);
  if (command === "test-version") {
    const tags = execFileSync("git", ["tag", "--list", "v*", "--sort=-v:refname"], { encoding: "utf8" }).split("\n");
    const { tag, version } = testVersion(tags, Number(arg));
    console.log(`tag=${tag}\nversion=${version}`);
  } else if (command === "check") {
    const problems = feedProblems(readFileSync("manifest.json", "utf8"));
    if (arg === "main" && existsSync("manifest-dev.json")) problems.push("manifest-dev.json belongs on dev only");
    if (problems.length) {
      console.error(problems.join("\n"));
      process.exit(1);
    }
    console.log("manifest.json: real releases only");
  } else {
    console.error("usage: feed.ts test-version <run number> | check <branch>");
    process.exit(2);
  }
}
