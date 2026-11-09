// clip-helpers.test.ts: the bump-clip aliases in the embedded engine.js must name the right minified functions. The
// extractor finds them by regex, so a rebuild could make an anchor match a neighbour; this calls each one.
//   bun test tools/extract-engine
import { expect, test } from "bun:test";
import { clipAt, clipOf, clipSpan, holdStep, wordsHidden } from "../../Jellyfin.Plugin.Spelunkr/Schedule/engine.js";

test("the clip aliases behave as the site's clip helpers", () => {
  const src = "https://archive.org/download/a/b.mp4";
  const clip = clipOf({ slides: [{ text: "a" }, { text: "b", clip: src, look: "zoom" }, { text: "c", under: true }, { text: "d" }] });
  expect(clip).toMatchObject({ src, first: 1, motion: 2, look: "zoom", wordsOver: false });

  const entries = [0, 1, 2, 3].map((i) => ({ phase: "channel", startS: 10 + i, endS: 11 + i }));
  const span = clipSpan(entries, clip);
  expect(span).toEqual({ startS: 11, motionEndS: 13, endS: 14 });

  // A 5 s clip ends exactly as the motion run does, then holds just before its last frame.
  expect(clipAt(span, 5, 12.5)).toEqual({ t: 4.5, playing: true });
  expect(clipAt(span, 5, 13.5)).toEqual({ t: 4.95, playing: false });
  expect([1, 2, 3].map((i) => holdStep(clip, i))).toEqual([-1, -1, 0]);
  expect([1, 3].map((i) => wordsHidden(clip, i))).toEqual([true, false]);
});
