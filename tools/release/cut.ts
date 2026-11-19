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
import { appendFileSync, existsSync, readFileSync } from "node:fs";
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

/**
 * A warning when an open release PR's version is now too low for what it holds (a feature merged after a patch
 * release was cut). The version can't change while the PR is open, so the fix is the maintainer's.
 */
export function versionWarning(version: string, latest: string, titles: string[]) {
  const wanted = nextVersion(latest, titles);
  return isAbove(wanted, version) ? `This release now holds a feature; the rule asks for v${wanted}, not v${version}. Close this PR and cut again to renumber.` : undefined;
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
export function releaseBody(p: { changes: Merged[]; maintenance: boolean; repo: string; cutSha: string; testBuild: string; warning?: string }) {
  const repoUrl = `https://github.com/${p.repo}`;
  return `${p.warning ? `> **Warning:** ${p.warning}\n\n` : ""}## What is changing

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
- Unknown: outside-service settings: the daily Spelunkr check is green on \`main\` and \`dev\`
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

type Run = (cmd: string, args: string[], input?: string) => string;

/**
 * Updates an open release PR in place: new body and title, then force-pushes its branch. `run` throws when a
 * command fails. Returns what to report; an error means the cut failed.
 *
 * WHY the body goes first: cut.ts reads the recorded cut from the branch. If the push came first and the body
 * write failed, the next cut would see the commit already recorded and change nothing, leaving old notes and checks
 * on new code. This way round a failed write leaves the old commit recorded, so the next cut redoes both.
 *
 * WHY a failed push is checked, not trusted: git can report failure after GitHub accepted the push (the reply was
 * lost). Restoring the old body then would put old notes on new code, and the next cut would see nothing to do. So
 * the old body goes back only when the branch is shown not to hold the new commit.
 */
export function updateReleasePr(run: Run, p: { repo: string; pr: number; version: string; body: string }): { level: "ok" | "warning" | "error"; message?: string } {
  const branch = `release/v${p.version}`;
  const prApi = `repos/${p.repo}/pulls/${p.pr}`;
  // REST, not `gh pr edit`: that command also reads project fields an App token may not be allowed to see.
  const oldBody = run("gh", ["api", prApi, "--jq", ".body"]);
  run("gh", ["api", "-X", "PATCH", prApi, "-f", `title=Release v${p.version}`, "-F", "body=@-"], p.body);
  try {
    // Force: the branch is rebuilt from dev on every cut. The push reruns the PR's checks.
    run("git", ["push", "--force", "origin", branch]);
    return { level: "ok" };
  } catch {}
  let remote: string;
  try {
    remote = run("git", ["ls-remote", "--exit-code", "origin", `refs/heads/${branch}`]);
  } catch {
    return { level: "error", message: "Release NOT confirmed: the push failed and we could not tell what the branch holds. The PR has the new description. Check that it matches the branch, then cut again." };
  }
  if (remote.split(/\s/)[0] === run("git", ["rev-parse", "HEAD"])) {
    return { level: "warning", message: "git reported a push failure, but the branch holds the new commit, so the update stands." };
  }
  try {
    run("gh", ["api", "-X", "PATCH", prApi, "-F", "body=@-"], oldBody);
  } catch {
    return { level: "error", message: "Release NOT updated: the push failed and the old description could not be put back. The PR describes a commit it doesn't hold. Cut again to repair it." };
  }
  return { level: "error", message: "Release NOT updated: the push failed, so the PR keeps its old commit and description. Cut again." };
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

  const titles = changes.map((c) => c.title);
  const version = d.version || nextVersion(latest, titles);
  const warning = versionWarning(version, latest, titles);
  if (warning) {
    console.log(`::warning::${warning}`);
    say(warning);
  }
  const repo = sh("gh", ["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"]);
  const bodyText = releaseBody({
    warning,
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
    const r = updateReleasePr(sh, { repo, pr: d.prNumber!, version, body: bodyText });
    if (r.level === "error") refuse(r.message!);
    if (r.level === "warning") console.log(`::warning::${r.message}`);
    url = prs[0].url;
  } else {
    // Force: a closed PR's branch may be left over. A fresh PR has no body to keep in step.
    git("push", "--force", "origin", branch);
    url = sh("gh", ["pr", "create", "--base", "main", "--head", branch, "--title", `Release v${version}`, "--body-file", "-"], bodyText);
  }
  say(`Release v${version} ready, cut from ${cutSha.slice(0, 7)}: ${url}`);
}

if (import.meta.main) main();
