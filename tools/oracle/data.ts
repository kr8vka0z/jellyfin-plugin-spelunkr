// data.ts: loads Spelunkr's public schedule data (lineup, manifests, platform files) from their storage bucket,
// or from a local mirror with the same relative paths (test fixtures). Shared by oracle.ts and tools/check.
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";

export const BUCKET = "https://jbuopkuluyfjwgsrigry.supabase.co/storage/v1/object/public/broadcast/";

/** Reads `path` (e.g. "lineup.json") from dataDir if given, else from the bucket; mirrors it into saveDir if given. */
export async function readText(path: string, dataDir?: string, saveDir?: string): Promise<string> {
  let text: string;
  if (dataDir) text = await Bun.file(join(dataDir, path)).text();
  else {
    const r = await fetch(BUCKET + path);
    if (!r.ok) throw new Error(`GET ${BUCKET + path} -> HTTP ${r.status}`);
    text = await r.text();
  }
  if (saveDir) {
    mkdirSync(dirname(join(saveDir, path)), { recursive: true });
    writeFileSync(join(saveDir, path), text);
  }
  return text;
}
