// clip-music.test.ts: since the 2026-11-09 engine, a bump whose slide has an archive.org `clip` and names a `track`
// plays that track, starting at `trackAt`; a track the channel doesn't have falls back to the usual random pick.
//   bun test tools/oracle
import { expect, test } from "bun:test";
import { cpSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const fixtures = join(import.meta.dir, "../../Jellyfin.Plugin.Spelunkr.Tests/Fixtures/spelunkr");
const named = "slowerpace-settling-the-score.mp3";

test("a clip bump plays the track it names, from trackAt; an unknown track falls back", () => {
  const dir = mkdtempSync(join(tmpdir(), "clip-music-"));
  cpSync(fixtures, dir, { recursive: true });
  const path = join(dir, "manifests/gumshoe/v19.json");
  const manifest = JSON.parse(readFileSync(path, "utf8"));
  const clip = (track: string) => ({ text: "x", clip: "https://archive.org/download/a/b.mp4", track, trackAt: 0.25 });
  for (const b of manifest.bumps) {
    if (b.id === "gs-075") b.slides.unshift(clip(named));
    if (b.id === "gs-065") b.slides.unshift(clip("no-such-track.mp3"));
  }
  writeFileSync(path, JSON.stringify(manifest));

  const run = Bun.spawnSync(
    ["bun", join(import.meta.dir, "oracle.ts"), "--data", dir, "timeline", "1793217600000", "24", "--channel", "gumshoe"],
  );
  expect(run.exitCode).toBe(0);
  const bumps = run.stdout.toString().trim().split("\n").map((l) => JSON.parse(l)).filter((s) => s.kind === "bump" && s.channel);
  const audioOf = (id: string) => bumps.find((s) => s.channel.id === id)?.channel.audio;

  expect(audioOf("gs-075")).toMatchObject({ file: named, startOffsetS: 0.25 });
  const fallback = audioOf("gs-065");
  expect(manifest.audio.map((a: { filename: string }) => a.filename)).toContain(fallback.file);
  expect(Number.isInteger(fallback.startOffsetS)).toBe(true);
});
