// check.ts: the daily "has Spelunkr changed under us?" check, run by .github/workflows/spelunkr-check.yml.
//   bun tools/check/check.ts [--channel <id>] [--report report.md] [--skip-live] [--simulate-break]
// Four checks, each reported separately, exit 1 if any fails:
//   data:    lineup, manifests and platform files of every channel the plugin offers (or just --channel) still
//            have every field the plugin reads, with the right types
//   engine:  lineup "engine" is still a number the embedded engine.js supports
//   extract: the live spelunkr.tv bundle still yields byte-for-byte the embedded engine.js
//   live:    for 3 channels a day (rotating through all of them, or just --channel), a headless browser on
//            spelunkr.tv/<channel> plays the same archive.org file, at the same moment (within 10 s), as the
//            embedded engine says should be on
import { readFileSync, writeFileSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { createChannel, addChannelVersion, segmentAt } from "../../Jellyfin.Plugin.Spelunkr/Schedule/schedule.js";
import { readText } from "../oracle/data.ts";

// Engine versions the embedded engine.js was extracted from and verified against.
const SUPPORTED_ENGINES = [5];
const LIVE_TOLERANCE_S = 10;

const args = process.argv.slice(2);
const flag = (name: string) => args.includes(name);
const opt = (name: string) => (args.includes(name) ? args[args.indexOf(name) + 1] : undefined);
const only = opt("--channel");
const LIVE_PER_DAY = 3;
const repo = resolve(import.meta.dir, "../..");

type Result = { name: string; ok: boolean; details: string };
const results: Result[] = [];
async function check(name: string, fn: () => Promise<string>) {
  try {
    results.push({ name, ok: true, details: await fn() });
  } catch (e) {
    results.push({ name, ok: false, details: e instanceof Error ? e.message : String(e) });
  }
}

// --- data -------------------------------------------------------------------------------------------------
const problems: string[] = [];
function expect(where: string, obj: any, field: string, type: "string" | "number" | "integer" | "array" | "object", optional = false) {
  const v = obj?.[field];
  if (v === undefined && optional) return;
  const ok = type === "array" ? Array.isArray(v) : type === "integer" ? Number.isInteger(v) : type === "object" ? v !== null && typeof v === "object" : typeof v === type;
  if (!ok) problems.push(`${where}.${field}: expected ${type}, got ${JSON.stringify(v)?.slice(0, 80)}`);
}

let lineup: any;
let channelIds: string[] = [];
const manifests = new Map<string, { pointer: any; text: string; platform: string | null }[]>();
// Same rule as SpelunkrSource.IsOffered in the plugin.
const offered = (c: any) => /^[a-z0-9-]+$/.test(c.id ?? "") && Number.isInteger(c.dial) && !c.comingSoon && !c.debugOnly && !c.audioOverlay;

await check("data", async () => {
  lineup = JSON.parse(await readText("lineup.json"));
  expect("lineup", lineup, "audioBase", "string");
  expect("lineup", lineup, "engine", "number");
  expect("lineup", lineup, "channels", "array");
  channelIds = only ? [only] : (lineup.channels ?? []).filter(offered).map((c: any) => c.id);
  if (!channelIds.length) throw new Error("no channels to check");
  const platforms = new Map<string, string>();
  for (const channelId of channelIds) {
    const ch = lineup.channels?.find((c: any) => c.id === channelId);
    if (!ch) {
      problems.push(`channel "${channelId}" is not in lineup.json any more`);
      continue;
    }
    for (const f of ["id", "name", "color"] as const) expect(`channel ${channelId}`, ch, f, "string");
    expect(`channel ${channelId}`, ch, "dial", "integer");
    expect(`channel ${channelId}`, ch, "versions", "array");
    expect(`channel ${channelId}`, ch, "logomark", "object", true);
    if (ch.logomark?.type === "pixel") expect(`channel ${channelId}.logomark`, ch.logomark, "rows", "array");
    if (!/^#[0-9a-fA-F]{6}$/.test(ch.color ?? "")) problems.push(`channel ${channelId}.color: not #RRGGBB: ${ch.color}`);
    if (!ch.versions?.length) problems.push(`channel ${channelId}.versions: empty`);
    const loaded: { pointer: any; text: string; platform: string | null }[] = [];
    for (const p of ch.versions ?? []) {
      const w = `${channelId} v${p.v}`;
      expect(w, p, "v", "integer");
      expect(w, p, "manifest", "string");
      expect(w, p, "epochMs", "integer");
      expect(w, p, "effectiveAtMs", "integer", true);
      expect(w, p, "startIndex", "integer", true);
      expect(w, p, "details", "string", true);
      const text = await readText(p.manifest);
      const m = JSON.parse(text);
      expect(w, m, "items", "array");
      expect(w, m, "bumps", "array");
      expect(w, m, "audio", "array");
      expect(w, m, "platform", "string");
      expect(w, m, "ads", "object", true);
      expect(w, m, "block", "object", true);
      for (const [i, it] of (m.items ?? []).entries()) {
        for (const f of ["id", "title"] as const) expect(`${w} items[${i}]`, it, f, "string");
        if (typeof (it.mp4 ?? it.file) !== "string") problems.push(`${w} items[${i}]: no mp4 or file name`);
        expect(`${w} items[${i}]`, it, "duration", "integer");
        expect(`${w} items[${i}]`, it, "inS", "number", true);
      }
      for (const [i, b] of (m.bumps ?? []).entries()) expect(`${w} bumps[${i}]`, b, "slides", "array");
      for (const [i, a] of (m.audio ?? []).entries()) expect(`${w} audio[${i}]`, a, "filename", "string");
      for (const [i, s] of (m.ads?.slices ?? []).entries()) {
        for (const f of ["tape", "file"] as const) expect(`${w} ads.slices[${i}]`, s, f, "string");
        for (const f of ["inMs", "lengthMs"] as const) expect(`${w} ads.slices[${i}]`, s, f, "integer");
      }
      for (const [i, st] of (m.block?.stings ?? []).entries()) {
        expect(`${w} block.stings[${i}]`, st, "track", "string");
        expect(`${w} block.stings[${i}]`, st, "startMs", "integer");
      }
      // The platform file holds the network-wide bumps; without it the engine silently drops bumps and ads.
      let platform: string | null = null;
      if (m.platform) {
        platform = platforms.get(m.platform) ?? (await readText(m.platform));
        if (!platforms.has(m.platform)) {
          platforms.set(m.platform, platform);
          const pl = JSON.parse(platform);
          expect(`${w} ${m.platform}`, pl, "bumps", "array");
          expect(`${w} ${m.platform}`, pl, "audio", "array");
        }
      }
      if (p.details) {
        const d = JSON.parse(await readText(p.details));
        expect(`${w} ${p.details}`, d, "items", "array");
        if (Array.isArray(d.items) && Array.isArray(m.items) && d.items.length !== m.items.length)
          problems.push(`${w} ${p.details}: ${d.items.length} entries for ${m.items.length} items (descriptions would go to the wrong films)`);
      }
      loaded.push({ pointer: p, text, platform });
    }
    manifests.set(channelId, loaded);
  }
  if (problems.length) throw new Error(problems.join("\n"));
  return `lineup generated ${lineup.generatedAt}; ${channelIds.length} channels: ${channelIds.join(", ")}`;
});

// --- engine -----------------------------------------------------------------------------------------------
await check("engine", async () => {
  const seen = new Set([lineup?.engine, ...[...manifests.values()].flat().map((m) => JSON.parse(m.text).engine)]);
  const bad = [...seen].filter((e) => !SUPPORTED_ENGINES.includes(e));
  if (bad.length) throw new Error(`Spelunkr now reports engine ${bad.join(", ")}; the embedded engine.js was verified for ${SUPPORTED_ENGINES.join(", ")}`);
  return `engine ${[...seen].join(", ")}`;
});

// --- extract ----------------------------------------------------------------------------------------------
await check("extract", async () => {
  const out = join(mkdtempSync(join(tmpdir(), "spelunkr-")), "engine.js");
  const run = Bun.spawnSync(["bun", join(repo, "tools/extract-engine/extract.ts"), "--out", out], { stderr: "pipe" });
  if (run.exitCode !== 0) throw new Error(`extract-engine failed:\n${run.stderr.toString()}`);
  const embedded = join(repo, "Jellyfin.Plugin.Spelunkr/Schedule/engine.js");
  if (readFileSync(out, "utf8") === readFileSync(embedded, "utf8")) return "live bundle yields the embedded engine.js";
  const diff = Bun.spawnSync(["diff", "-u", embedded, out]).stdout.toString();
  throw new Error(
    "spelunkr.tv's engine code differs from the embedded copy. This can be a harmless rebuild that only renamed " +
      "minified names; the live check below says whether the schedule actually changed.\n```diff\n" + diff.slice(0, 6000) + "\n```",
  );
});

// --- live -------------------------------------------------------------------------------------------------
// Today's sample: LIVE_PER_DAY channels, moving along the sorted list each day so every channel is seen in turn.
const day = Math.floor(Date.now() / 864e5);
const sorted = [...channelIds].sort();
const sample = only ? [only] : Array.from({ length: Math.min(LIVE_PER_DAY, sorted.length) }, (_, i) => sorted[(day * LIVE_PER_DAY + i) % sorted.length]);
if (!flag("--skip-live")) {
  const { chromium } = await import("playwright");
  const browser = await chromium.launch({ args: ["--autoplay-policy=no-user-gesture-required"] });
  try {
    for (const channelId of sample) {
      await check(`live: ${channelId}`, async () => {
        const channel = createChannel(channelId);
        for (const m of manifests.get(channelId) ?? []) addChannelVersion(channel, m.text, m.platform, JSON.stringify(m.pointer));
        const page = await browser.newPage();
        try {
          await page.goto(`https://spelunkr.tv/${channelId}`, { waitUntil: "domcontentloaded" });
          // Bumps and cards have no video file, so wait (up to 5 minutes) until the site plays an archive.org video.
          let lastSeen = "nothing";
          let mismatches = 0;
          for (let i = 0; i < 60; i++) {
            await page.waitForTimeout(5000);
            const s = await page.evaluate(() => {
              const v = [...document.querySelectorAll("video")].find((v) => !v.paused && v.readyState >= 2 && v.currentSrc.includes("archive.org"));
              return v ? { now: Date.now(), src: v.currentSrc, t: v.currentTime } : null;
            });
            if (!s) continue;
            const want = JSON.parse(segmentAt(channel, s.now, lineup.audioBase));
            const wantS = (want.inMs + want.offsetMs) / 1000;
            lastSeen = `site plays ${s.src} at ${s.t.toFixed(1)} s; engine says ${want.kind} ${want.url ?? want.id} at ${wantS.toFixed(1)} s`;
            if (want.kind !== "show" && want.kind !== "ad") continue; // caught a hand-over to a bump or card; look again
            if (s.src === want.url && Math.abs(s.t - wantS) <= LIVE_TOLERANCE_S) return lastSeen;
            // The site runs about a second behind the engine, so a sample taken at a hand-over (film to ad) can
            // disagree for a moment. Only a second disagreement 5 s later counts.
            if (mismatches++ > 0) throw new Error(lastSeen);
          }
          throw new Error(`no playing archive.org video on spelunkr.tv/${channelId} within 5 minutes (last: ${lastSeen})`);
        } finally {
          await page.close();
        }
      });
    }
  } finally {
    await browser.close();
  }
}

if (flag("--simulate-break")) results.push({ name: "simulated break", ok: false, details: "forced failure (workflow input simulate_break) to prove the issue path" });

const report = [
  `Spelunkr check, ${new Date().toISOString()}: data for ${channelIds.length} channels, live sample ${sample.join(", ")}`,
  "",
  ...results.map((r) => `### ${r.ok ? "PASS" : "FAIL"}: ${r.name}\n\n${r.details}\n`),
].join("\n");
console.log(report);
const reportPath = opt("--report");
if (reportPath) writeFileSync(reportPath, report);
process.exit(results.every((r) => r.ok) ? 0 : 1);
