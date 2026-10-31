# AGENTS.md: instructions for AI coding agents

> Big picture: [README.md](./README.md) and [ARCHITECTURE.md](./ARCHITECTURE.md). This file is operational only.

## Build / test

- `dotnet test` (net10.0 SDK; `global.json` opts into Microsoft.Testing.Platform). Warnings are errors.
- `bun tools/oracle/oracle.ts now <unixMs>`: what is on now, per the embedded engine, from live data.
- `bun tools/check/check.ts --skip-live`: the daily check without the browser part (run `bun install` in `tools/check` first for the full one).

## Rules

- **Never edit `Jellyfin.Plugin.Spelunkr/Schedule/engine.js` by hand.** Regenerate it:
  `bun tools/extract-engine/extract.ts` (live site) or `--bundle <file>`. If an anchor stops matching, fix the
  regex in `extract.ts`, keep the exported aliases the same, and leave `schedule.js` alone where possible.
- After regenerating `engine.js` or refreshing fixtures, regenerate the expected files with the commands in
  the header of `Jellyfin.Plugin.Spelunkr.Tests/ScheduleEngineTests.cs`, then run `dotnet test`. Fixtures come
  from `bun tools/oracle/oracle.ts now <ms> --save Jellyfin.Plugin.Spelunkr.Tests/Fixtures/spelunkr`.
- Public repo: no server paths, hostnames, IPs or personal details anywhere.
- Build against the Jellyfin version in the csproj (`Jellyfin.Controller` 12.3.0); `TARGET_ABI` in `.github/actions/build-plugin/action.yml` must match.
- Which channels are offered: `SpelunkrSource.IsOffered` (mirrored in `tools/check/check.ts`; keep them the same).
- `bun test tools/oracle tools/release` runs the Bun-side tests (path safety, release decisions, the feed guard).

## Branches

New work: branch from `dev`, PR into `dev`. Each push to `dev` makes a test build (one exception under The two feeds). Only three things reach `main`:
a release PR, a breakage fix, and the bots' own feed commits. Never PR `dev` itself into `main`.

## Releases

A release starts only when the maintainer says so.

- Cut: `gh workflow run release.yml` (`-f version=X.Y.Z` only to raise the first number). `tools/release/cut.ts`
  opens or updates `release/vX.Y.Z`, or stops with a reason. A feature raises the middle number, anything else
  the last. The first stays 0 until the maintainer says otherwise.
- The maintainer squash-merges the release PR. Agents can't. `release.yml` then tags it, publishes it, adds it to
  `manifest.json` on `main` and merges `main` back into `dev`.
- Breakage fix, the one lane with no click: `hotfix/<slug>` from `origin/main`, a PR into `main` linking an open
  `broken-by-spelunkr` issue, a clean Codex review, agent merge, then push the next patch tag. The steps
  live in the `spelunkr-fix` skill.
- A push that changes `.github/workflows/` can be refused for the release bot, which has no workflow permission.
  If a cut or copy-back fails that way, do that step by hand with an account that has it.

## The two feeds

- `manifest.json`: the public feed. Real releases only, and only the release job writes it, on `main`. `dev` gets
  it by the copy-back and never edits it.
- `manifest-dev.json`: the test feed, on `dev` only, holding just the newest test build. `test-build.yml` writes it.
  The release commit removes it, so it never reaches `main`, and it is ignored when comparing `dev` with `main`.
- Test builds: `v<base>-dev.<run>` becomes Jellyfin version `<base>.<run>`, where base is the newest release the
  built code contains. That is above that release and below the next (`testVersion` in `tools/release/feed.ts`
  says why). Until a release or hotfix is copied back into `dev`, test builds stay below it, because they lack it.
- A re-run of an old test build stops itself instead of replacing a newer one in the feed.
- A branch operation that changes no files starts no test build. Any push that changes a file outside the two
  manifests starts one, including a reset.
- While `dev`'s `.github/workflows/` differ from `main`'s, GitHub refuses the job's token the new tag, so the test
  build goes red. To recover, take the tag from the failed run, have an account with workflow permission run
  `gh release create <tag> --prerelease --latest=false --target <commit> --notes "Test build of <commit>."`, then
  re-run the job: it uploads the zip to that release and updates the feed.
- `tools/release/feed.ts check` runs in CI and before the release job's push. Keep it passing.

## Undoing a release

Everyone who updated keeps that version until a newer one appears, so roll forward: revert the bad PR in a PR to
`dev`, check it on the test feed, release again. If it can't wait for that and it is breakage, use the hotfix lane.
To stop new installs at once, remove the version's entry from `manifest.json` in a PR into `main`, which the maintainer
merges. Leave its GitHub release up: existing installs may still point at the zip.
