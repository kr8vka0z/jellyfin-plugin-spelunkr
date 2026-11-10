// extract.test.ts: the extractor must find its anchors when minified names contain `$` (a rebuild named one `$s`).
//   bun test tools/extract-engine
import { expect, test } from "bun:test";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

// A tiny stand-in bundle with every anchor extract.ts looks for, each name using `$`, laid out like the 2026-11-09
// build: the bump-clip helpers come first and the timing constants sit mid-way in their `const`.
const bundle = [
  "var x$=0;",
  'const L$=["flash","duotone","zoom","duozoom"];function $k(e){return L$[0]}',
  'function $c(e){return(e==null?void 0:e.under)===!0}function $p(e,t){return e.filter(s=>s.phase==="channel").map(s=>({motionEndS:s.endS}))}',
  "function $d(e,t,n,r=null){return{t:0,playing:!1}}const $h=(e,t)=>!e||t<e.first+e.motion?-1:t,$f=(e,t)=>$c(e,t)&&!e.wordsOver;",
  "const P$=(e,t)=>e+t,",
  "A$=2500,B$=1500,C$=55,$g=300,D$=2e3,$e=3500,$s=1e3,",
  "F$=1;function $m(e){return Math.max(A$,B$+e.length*C$)}const $t=1e3,G$=2;",
  "function $r(e,t=[]){const n={channelId:e,versions:[]};for(const r of t)$a(n,r);return n}",
  "function*$w(e,t){let n=t;for(;;){const r=$q(e,n);yield r,n=r.endMs}}",
  "function $u(e,t){return`https://archive.org/download/${e}/${encodeURIComponent(t)}`}",
  "function $b(e,t,n){const r=e.bumps||[],s=(t==null?void 0:t.bumps)||[];return{engine:e.engine}}",
].join("");

test("extracts a bundle whose names contain $", () => {
  const dir = mkdtempSync(join(tmpdir(), "extract-test-"));
  writeFileSync(join(dir, "bundle.js"), bundle);
  const out = join(dir, "engine.js");
  const run = Bun.spawnSync(["bun", join(import.meta.dir, "extract.ts"), "--bundle", join(dir, "bundle.js"), "--out", out]);
  expect(run.stderr.toString()).not.toContain("matched 0 times");
  expect(run.exitCode).toBe(0);
  expect(readFileSync(out, "utf8")).toContain(
    "export { $r as createRegistry, $a as addVersion, $q as lookup, $w as walk, $b as buildVersion, $u as archiveUrl, " +
      "$m as slideMs, $e as LEAD_IN_MS, $s as TAIL_A_MS, $t as TAIL_B_MS, $g as GAP_MS };",
  );
});

// Issue #40: the bump planner started calling the bump-clip helpers, which sat before the cut, so the engine threw
// "ReferenceError: lv is not defined". The cut must begin at the helpers and keep the constants' `const` intact.
test("incident-2026-11-09-bump-clip-slice: the cut starts at the bump-clip helpers", () => {
  const dir = mkdtempSync(join(tmpdir(), "extract-test-"));
  writeFileSync(join(dir, "bundle.js"), bundle);
  const out = join(dir, "engine.js");
  expect(Bun.spawnSync(["bun", join(import.meta.dir, "extract.ts"), "--bundle", join(dir, "bundle.js"), "--out", out]).exitCode).toBe(0);
  const engine = readFileSync(out, "utf8");
  expect(engine).toContain('\nconst L$=["flash","duotone","zoom","duozoom"];function $k(e)');
  expect(engine).toContain("const P$=(e,t)=>e+t,A$=2500,");
  expect(engine).not.toContain("x$");
});

// The 2026-11-10 build opened the looks' `const` with the clip's word positions, added a fifth look ("widen") and gave
// the clip span a third parameter. The cut must still start at that `const` (the planner reads the word positions).
test("incident-2026-11-10-widen-look: the cut keeps lists before the looks and a longer looks list", () => {
  const dir = mkdtempSync(join(tmpdir(), "extract-test-"));
  const widened = bundle
    .replace('const L$=["flash","duotone","zoom","duozoom"];', 'const W$=["centre","lower"],L$=["flash","duotone","zoom","duozoom","widen"];')
    .replace("function $p(e,t){", "function $p(e,t,n=null){");
  writeFileSync(join(dir, "bundle.js"), widened);
  const out = join(dir, "engine.js");
  const run = Bun.spawnSync(["bun", join(import.meta.dir, "extract.ts"), "--bundle", join(dir, "bundle.js"), "--out", out]);
  expect(run.stderr.toString()).not.toContain("anchor");
  expect(run.exitCode).toBe(0);
  const engine = readFileSync(out, "utf8");
  expect(engine).toContain('\nconst W$=["centre","lower"],L$=["flash","duotone","zoom","duozoom","widen"];');
  expect(engine).toContain("$p as clipSpan");
});
