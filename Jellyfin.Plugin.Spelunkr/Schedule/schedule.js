// schedule.js: the small hand-written wrapper over engine.js. The plugin runs it in Jint and tools/oracle runs
// it in Bun, so both read the schedule through the same code. Data crosses in and out as JSON strings.
import { createRegistry, addVersion, lookup, walk, buildVersion, archiveUrl, slideMs, LEAD_IN_MS, TAIL_A_MS, TAIL_B_MS, GAP_MS } from "./engine.js";

/** An empty schedule for one channel; versions are added with addChannelVersion. */
export function createChannel(channelId) {
  return createRegistry(channelId, []);
}

/**
 * Registers one manifest version (same path as the site: lineup pointer + manifest + platform -> version).
 * The platform file is required when the manifest names one: without it the engine silently drops every
 * bump and ad and the timing is wrong. Returns false if that version was already registered.
 */
export function addChannelVersion(channel, manifestJson, platformJson, pointerJson) {
  const manifest = JSON.parse(manifestJson);
  if (manifest.platform && !platformJson) throw new Error(`manifest v${manifest.version} needs ${manifest.platform}`);
  return addVersion(channel, buildVersion(manifest, platformJson ? JSON.parse(platformJson) : null, JSON.parse(pointerJson)));
}

const slides = (b) => b.slides.map((s) => ({ text: s.text, ms: slideMs(s.text) }));
const audio = (a, base) => a && { file: a.filename, url: base + encodeURIComponent(a.filename), startOffsetS: a.startOffset };

/** One engine lookup result as a plain segment: what plays from startMs for lengthMs. */
function describe(q, audioBase) {
  const it = q.item;
  const base = { startMs: q.startMs, lengthMs: q.endMs - q.startMs, version: q.version, cycle: q.cycle, index: q.index };
  switch (it.type) {
    case "video":
      return { ...base, kind: "show", id: it.id, url: archiveUrl(it.id, it.mp4 ?? it.file), inMs: (it.inS ?? 0) * 1000, title: it.title, year: it.year };
    case "ad":
      return { ...base, kind: "ad", id: it.slice.id, url: archiveUrl(it.slice.tape, it.slice.file), inMs: it.slice.inMs, title: `${it.slice.tape} (${it.slice.year})`, gainDb: it.slice.gainDb ?? 0 };
    case "bump-break": {
      // A bump is text slides over music, no video. "open" = channel bump before an ad, "close" = network bump
      // after it, "full" = both back to back with no ad. Layout numbers are the engine's own constants.
      const out = { ...base, kind: "bump", half: it.half ?? "full", id: [it.channelBump?.id, it.platformBump?.id].filter(Boolean).join("+") };
      if (it.channelBump) out.channel = { id: it.channelBump.id, slides: slides(it.channelBump), audio: audio(it.channelAudio, audioBase) };
      if (it.platformBump) out.platform = { id: it.platformBump.id, slides: slides(it.platformBump), audio: audio(it.platformAudio, audioBase) };
      out.layout = { leadInMs: LEAD_IN_MS, tailMs: TAIL_A_MS + TAIL_B_MS, gapMs: GAP_MS };
      return out;
    }
    default:
      return { ...base, kind: it.type, id: it.key };
  }
}

/** The segment playing at atMs, plus how far into it atMs is. */
export function segmentAt(channel, atMs, audioBase) {
  const q = lookup(channel, atMs);
  return JSON.stringify({ atMs, offsetMs: atMs - q.startMs, ...describe(q, audioBase) });
}

/** Every segment from the one playing at fromMs up to (not including) the first that starts at or after toMs. */
export function segmentsBetween(channel, fromMs, toMs, audioBase) {
  const out = [];
  for (const q of walk(channel, fromMs)) {
    if (q.startMs >= toMs) break;
    out.push(describe(q, audioBase));
  }
  return JSON.stringify(out);
}
