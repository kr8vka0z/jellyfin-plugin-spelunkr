// extract.test.ts: the extractor must find its anchors when minified names contain `$` (a rebuild named one `$s`).
//   bun test tools/extract-engine
import { expect, test } from "bun:test";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

// A tiny stand-in bundle with every anchor extract.ts looks for, each name using `$`.
const bundle = [
  "var x$=0;",
  "const A$=2500,B$=1500,C$=55,$g=300,D$=2e3,$e=3500,$s=1e3,",
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
