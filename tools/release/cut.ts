// cut.ts: cuts a release. Opens the `Release vX.Y.Z` PR into main, updates the open one in place, or says
// why it did neither. release.yml runs it from a dev checkout with every branch and tag fetched.
// Rules: the "Releases" section of AGENTS.md.
//
// WHY files, not commits: releases reach main as squashes, so dev and main never share commits.
// WHY the recorded cut is a merge base: the release branch is one commit on top of the dev commit it was
// cut from, so nothing extra has to be stored.
// WHY notes start at a cut/* tag: v* tags sit on main, off dev's history. release.yml writes cut/vX.Y.Z on
// the dev commit that really shipped, so a cut that never merged stays in the next notes.
import { execFileSync } from "node:child_process";
import { appendFileSync, existsSync, readFileSync, writeFileSync } from "node:fs";
import { isAbove, newestStable } from "./feed.ts";

// dev alone has this feed file. It must never count as a change or reach main.
const NOT_FEED = [".", ":!manifest-dev.json"];

type Pr = { number: number; url: string; headRefName: string };
type Merged = { number: number; title: string; mergeCommit?: { oid: string } | null };

/** Only tests, tools, CI and docs differ, so nobody watching a channel should notice. */
export const isMaintenanceOnly = (files: string[]) =>
  files.length > 0 && files.every((f) => /^(\.github\/|tools\/|Jellyfin\.Plugin\.Spelunkr\.Tests\/)|\.md$/.test(f));

/** A feature raises the middle number, anything else the last. The first moves only by the version input. */
export function nextVersion(last: string, titles: string[]) {
  const [major, minor, patch] = last.split(".").map(Number);
  return titles.some((t) => /^feat(\(.*\))?!?:/.test(t)) ? `${major}.${minor + 1}.0` : `${major}.${minor}.${patch + 1}`;
}

const branchVersion = (pr: Pr) => /^release\/v(\d+\.\d+\.\d+)$/.exec(pr.headRefName)?.[1];

/**
 * Pure decision. `changed` = dev and main hold different files, feed file aside. `movedSinceCut` = dev's
 * code differs from the open PR's cut. `latest` = the newest live version.
 */
export function decideCut(input: {
  changed: boolean;
  prs: Pr[];
  mainInDev: boolean;
  movedSinceCut: boolean;
  forcedVersion: string;
  latest: string;
}): { action: "nothing" | "same" | "new" | "update" | "blocked"; message: string; version?: string; prNumber?: number } {
  const { changed, prs, mainInDev, movedSinceCut, forcedVersion, latest } = input;
  const blocked = (reason: string) => ({ action: "blocked" as const, message: `Release NOT cut: ${reason}` });
  if (prs.length > 1) return blocked(`only one release PR may be open, found ${prs.map((p) => p.url).join(" ")}. Close the extra ones.`);
  const [pr] = prs;
  if (!changed) {
    const left = pr ? ` Left the open release PR alone: ${pr.url}` : "";
    return { action: "nothing", message: `No changes: dev and main hold identical files, so no release PR.${left}` };
  }
  // A hotfix or a release whose copy-back hasn't landed. A cut now would rest on stale history.
  if (!mainInDev) return blocked(`main has a commit dev lacks, so it must be copied back into dev first, then cut again.`);
  if (forcedVersion && !/^\d+\.\d+\.\d+$/.test(forcedVersion)) return blocked(`version "${forcedVersion}" is not X.Y.Z.`);
  const version = pr ? branchVersion(pr) : forcedVersion;
  if (pr && !version) return blocked(`open release PR ${pr.url} has no version in its branch name.`);
  // GitHub cannot change a PR's head branch, so the version is fixed while the PR is open.
  if (pr && forcedVersion && forcedVersion !== version) {
    return blocked(`${pr.url} is open at v${version}, not v${forcedVersion}. Close it to use another version.`);
  }
  // A hotfix can pass the number while the PR is open, and Jellyfin never moves anyone to a lower version.
  if (version && !isAbove(version, latest)) {
    return blocked(`v${version} is not above live v${latest}.${pr ? ` Close ${pr.url} and cut again.` : ""}`);
  }
  if (!pr) return { action: "new", version: forcedVersion, message: "Cutting a new release." };
  if (!movedSinceCut) return { action: "same", version, prNumber: pr.number, message: `Nothing new since the last cut: ${pr.url}` };
  return { action: "update", version, prNumber: pr.number, message: `Updating ${pr.url} in place at v${version}.` };
}

/** The PRs merged into dev inside `shas`. */
export const changeList = (merged: Merged[], shas: Set<string>) => merged.filter((p) => p.mergeCommit && shas.has(p.mergeCommit.oid));

/** The PR's first page: enough for the maintainer to decide. Checks start Unknown; only a real check changes one. */
export function releaseBody(p: { changes: Merged[]; maintenance: boolean; repo: string; cutSha: string; testBuild: string }) {
  const repoUrl = `https://github.com/${p.repo}`;
  return `## What is changing

${p.maintenance ? "**Maintenance only:** only tests, tools, CI and docs changed.\n\n" : ""}${p.changes
    .map((c) => `- ${c.title.replace(/^\w+(\(.*?\))?!?:\s*/, "")} (#${c.number})`)
    .join("\n")}

## Where it stands
- Test feed: https://raw.githubusercontent.com/${p.repo}/dev/manifest-dev.json
- Cut from commit: [\`${p.cutSha.slice(0, 7)}\`](${repoUrl}/commit/${p.cutSha})
- Test build at that moment: ${p.testBuild}

## Pre-release checks
The agent marks each one Passed or Failed. Anything not checked stays Unknown.
- Unknown: a clean Codex review on every PR in this release
- Does not apply: production database changes. A plugin has no database of its own.
- Unknown: outside-service settings: the daily Spelunkr check is green on \`main\` and \`dev\`, and \`targetAbi\` still matches the Jellyfin version in the csproj
- Unknown: instead of a checkout, which a plugin doesn't have: this test build installed on Jellyfin and a channel opened and played
- Unknown: the "Undoing a release" steps in AGENTS.md still fit this release

## How to undo
Everyone who updated keeps the version until a newer one appears, so roll forward: revert the bad PR in a PR to dev, check it on the test feed, then release again. To stop new installs at once, remove the version from \`manifest.json\` in a PR into main, which the maintainer merges. Full steps: AGENTS.md "Undoing a release".

Squash-merging this PR tags the version, publishes the GitHub release, adds it to the feed everyone uses, and copies main back into dev.
`;
}

/** Says whether a test build holds the code being released. */
export function testBuildLine(feed: { version: string; changelog: string } | undefined, sameCode: boolean) {
  if (!feed) return "**No test build exists yet.**";
  const link = `[${feed.version}](${feed.changelog})`;
  return sameCode ? `${link} holds exactly this code.` : `**This code is not on the test feed yet.** The latest is ${link}.`;
}

const sh = (cmd: string, args: string[], input?: string) =>
  execFileSync(cmd, args, { encoding: "utf8", stdio: ["pipe", "pipe", "inherit"], input }).trim();
const git = (...args: string[]) => sh("git", args);
const sameFiles = (a: string, b: string) => {
  try {
    execFileSync("git", ["diff", "--quiet", a, b, "--", ...NOT_FEED]);
    return true;
  } catch {
    return false;
  }
};

function say(message: string) {
  console.log(message);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${message}\n`);
}

function refuse(message: string): never {
  console.log(`::error::${message}`);
  process.exit(1);
}

function notesStart() {
  try {
    return execFileSync("git", ["describe", "--tags", "--match", "cut/v*", "--abbrev=0", "HEAD"], { encoding: "utf8", stdio: "pipe" }).trim();
  } catch {
    return git("merge-base", "origin/main", "HEAD");
  }
}

function testBuild() {
  if (!existsSync("manifest-dev.json")) return testBuildLine(undefined, false);
  const feed = JSON.parse(readFileSync("manifest-dev.json", "utf8"))[0].versions[0];
  const built = /commit\/([0-9a-f]{40})/.exec(feed.changelog)?.[1];
  return testBuildLine(feed, Boolean(built) && sameFiles(built!, "HEAD"));
}

function main() {
  // Same-repo only: a fork's branch can be named release/anything.
  const prs: Pr[] = JSON.parse(sh("gh", ["pr", "list", "--base", "main", "--state", "open", "--limit", "100", "--json", "number,url,headRefName,isCrossRepository"]))
    .filter((p: Pr & { isCrossRepository: boolean }) => p.headRefName.startsWith("release/") && !p.isCrossRepository);
  const forcedVersion = process.env.FORCE_VERSION ?? "";
  const latest = newestStable(git("tag", "--list", "v*", "--sort=-v:refname").split("\n"));
  if (!latest) refuse("Release NOT cut: no earlier release tag found.");
  const d = decideCut({
    changed: !sameFiles("origin/main", "HEAD"),
    prs,
    mainInDev: git("merge-base", "origin/main", "HEAD") === git("rev-parse", "origin/main"),
    movedSinceCut: prs.length === 1 && !sameFiles(git("merge-base", "HEAD", `origin/${prs[0].headRefName}`), "HEAD"),
    forcedVersion,
    latest,
  });

  say(d.message);
  if (d.action === "blocked") refuse(d.message);
  if (d.action !== "new" && d.action !== "update") return;

  const cutSha = git("rev-parse", "HEAD");
  const shas = new Set(git("rev-list", "--first-parent", `${notesStart()}..HEAD`).split("\n"));
  const merged: Merged[] = JSON.parse(sh("gh", ["pr", "list", "--base", "dev", "--state", "merged", "--limit", "300", "--json", "number,title,mergeCommit"]));
  const changes = changeList(merged, shas);
  if (!changes.length) refuse("Release NOT cut: dev and main hold different files but no PR is listed since the last release. Compare them with git diff.");

  const version = d.version || nextVersion(latest, changes.map((c) => c.title));
  const repo = sh("gh", ["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"]);
  const bodyText = releaseBody({
    changes,
    maintenance: isMaintenanceOnly(git("diff", "--name-only", "origin/main", "HEAD", "--", ...NOT_FEED).split("\n").filter(Boolean)),
    repo,
    cutSha,
    testBuild: testBuild(),
  });

  // main's feed always wins: the release commit drops dev's feed file and pins main's manifest.json.
  // --allow-empty: before the first test build this commit changes nothing, and the cut must still record.
  const branch = `release/v${version}`;
  git("switch", "-C", branch);
  git("rm", "-q", "--ignore-unmatch", "manifest-dev.json");
  git("checkout", "origin/main", "--", "manifest.json");
  git("-c", "user.name=kr8vka0z-release-bot", "-c", "user.email=kr8vka0z-release-bot@users.noreply.github.com", "commit", "--allow-empty", "-m", `chore(release): v${version}`);

  let url: string;
  if (d.action === "update") {
    const file = `${process.env.RUNNER_TEMP ?? "/tmp"}/release-body.md`;
    writeFileSync(file, bodyText);
    sh("bash", ["tools/release/update-release-pr.sh", repo, String(d.prNumber), version, file]);
    url = prs[0].url;
  } else {
    // Force: a closed PR's branch may be left over. A fresh PR has no body to keep in step.
    git("push", "--force", "origin", branch);
    url = sh("gh", ["pr", "create", "--base", "main", "--head", branch, "--title", `Release v${version}`, "--body-file", "-"], bodyText);
  }
  say(`Release v${version} ready, cut from ${cutSha.slice(0, 7)}: ${url}`);
}

if (import.meta.main) main();
