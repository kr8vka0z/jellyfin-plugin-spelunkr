// extract.ts: cuts Spelunkr's schedule engine VERBATIM out of the spelunkr.tv JavaScript bundle into engine.js.
//   bun tools/extract-engine/extract.ts [--bundle <file or URL>] [--out <path>]
// With no --bundle it reads https://spelunkr.tv/, finds the main bundle in index.html and downloads it.
// Default --out is the plugin's embedded copy. Exits non-zero, naming the anchor, if anything is not found.
//
// Why verbatim: the plugin must schedule exactly like the site. Copying their code (instead of porting it)
// means a change on their side is fixed by re-running this script, not by re-deriving the algorithm.
//
// ponytail: the anchors below are regexes over minified code. They lean on values and string literals that
// survive a rebuild (2500/1500/55 slide timing, "channelId", archive.org URL, "engine:e.engine"), but a rebuild
// that renames locals or reorders code can still break a match. That failure is loud on purpose: re-read the
// bundle, adjust the regex, re-run. The exported names are stable aliases, so schedule.js never changes for a rename.
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";

const args = process.argv.slice(2);
const opt = (name: string) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const SITE = "https://spelunkr.tv/";
const out = opt("--out") ?? resolve(import.meta.dir, "../../Jellyfin.Plugin.Spelunkr/Schedule/engine.js");

function fail(msg: string): never {
  console.error(`extract-engine: ${msg}`);
  process.exit(1);
}

async function fetchText(url: string) {
  const r = await fetch(url);
  if (!r.ok) fail(`GET ${url} -> HTTP ${r.status}`);
  return r.text();
}

async function loadBundle(): Promise<string> {
  const given = opt("--bundle");
  if (given && !/^https?:/.test(given)) return Bun.file(given).text();
  if (given) return fetchText(given);
  const html = await fetchText(SITE);
  const m = html.match(/<script[^>]+type="module"[^>]+src="([^"]+\.js)"/);
  if (!m) fail(`no <script type="module" src=...> in ${SITE}`);
  const url = new URL(m[1], SITE).href;
  console.error(`extract-engine: bundle ${url}`);
  return fetchText(url);
}

const src = await loadBundle();

// Every anchor must match exactly once; two matches means the regex is no longer specific enough.
function once(label: string, re: RegExp): RegExpExecArray {
  const all = [...src.matchAll(new RegExp(re.source, "g"))];
  if (all.length !== 1) fail(`anchor "${label}" matched ${all.length} times (want 1): ${re.source}`);
  return all[0] as RegExpExecArray;
}

// Slice 1: constants, RNG, bump/ad planners, version registry, cycle builder, lookup (Qt) and walker (to).
const start1 = once("slide-timing constants", /const \w+=2500,\w+=1500,\w+=55,(\w+)=300,\w+=2e3,(\w+)=3500,(\w+)=1e3,/);
const walker = once("schedule walker", /function\*(\w+)\(e,t\)\{let n=t;for\(;;\)\{const r=(\w+)\(e,n\);yield r,n=r\.endMs\}\}/);
// Slice 2: archive.org download URL.
const url = once("archive.org url", /function (\w+)\(e,t\)\{return`https:\/\/archive\.org\/download\/\$\{e\}\/\$\{encodeURIComponent\(t\)\}`\}/);
// Slice 3: manifest + platform + lineup pointer -> version object.
const build = once("version builder", /function (\w+)\(e,t,n\)\{const r=e\.bumps\|\|\[\],s=\(t==null\?void 0:t\.bumps\)\|\|\[\];/);
const buildEnd = src.indexOf("engine:e.engine}}", build.index);
if (buildEnd < 0) fail(`version builder end "engine:e.engine}}" not found`);

const end1 = walker.index + walker[0].length;
if (!(start1.index < end1)) fail("slide-timing constants come after the walker; bundle layout changed");
const slice1 = src.slice(start1.index, end1);
const slice2 = url[0];
const slice3 = src.slice(build.index, buildEnd + "engine:e.engine}}".length);

// Names the glue needs, looked up inside slice 1 so they refer to the code we actually cut.
const inSlice1 = (label: string, re: RegExp) => {
  const m = slice1.match(re);
  if (!m) fail(`anchor "${label}" not found in slice 1: ${re.source}`);
  return m;
};
const registry = inSlice1("registry", /function (\w+)\(e,t=\[\]\)\{const n=\{channelId:e,versions:\[\]\};for\(const r of t\)(\w+)\(n,r\);return n\}/);
const slideMs = inSlice1("slide length", /function (\w+)\(e\)\{return Math\.max\(\w+,\w+\+e\.length\*\w+\)\}const (\w+)=1e3,/);

const [, gapName, leadInName, tailAName] = start1;
const names = {
  createRegistry: registry[1],
  addVersion: registry[2],
  lookup: walker[2],
  walk: walker[1],
  buildVersion: build[1],
  archiveUrl: url[1],
  slideMs: slideMs[1],
  LEAD_IN_MS: leadInName,
  TAIL_A_MS: tailAName,
  TAIL_B_MS: slideMs[2],
  GAP_MS: gapName,
};

const exportList = Object.entries(names).map(([alias, name]) => `${name} as ${alias}`).join(", ");
const text = [
  "// engine.js: Spelunkr's schedule engine, cut VERBATIM from the spelunkr.tv bundle by tools/extract-engine.",
  "// Generated file: do not edit by hand. Re-run `bun tools/extract-engine/extract.ts` instead.",
  slice1,
  slice2,
  slice3 + ";",
  `export { ${exportList} };`,
  "",
].join("\n");

writeFileSync(out, text);
console.error(`extract-engine: wrote ${out} (${text.length} bytes)`);
