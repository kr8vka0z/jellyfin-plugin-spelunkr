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
- Build against the Jellyfin version in the csproj (`Jellyfin.Controller` 12.3.0); `targetAbi` in `release.yml` must match.
- The offered channels are the `EnabledChannels` list in `SpelunkrSource.cs`.
