// data.ts: loads Spelunkr's public schedule data (lineup, manifests, platform files) from their storage bucket,
// or from a local mirror with the same relative paths (test fixtures). Shared by oracle.ts and tools/check.
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve, sep } from "node:path";

export const BUCKET = "https://jbuopkuluyfjwgsrigry.supabase.co/storage/v1/object/public/broadcast/";

/**
 * Paths come from Spelunkr's own data (manifest and platform names), so treat them as untrusted: only plain
 * relative paths (letters, digits, . _ - and /), no "." or ".." parts, no query, fragment or scheme.
 */
export function bucketPath(path: string): string {
  if (typeof path !== "string" || !/^[A-Za-z0-9._-]+(\/[A-Za-z0-9._-]+)*$/.test(path) || path.split("/").some((p) => p === "." || p === "..")) {
    throw new Error(`"${path}" is not a plain bucket path`);
  }
  return path;
}

/** `path` inside `dir`, refusing anything that would resolve outside it. */
export function inside(dir: string, path: string): string {
  const root = resolve(dir);
  const full = resolve(root, bucketPath(path));
  if (!full.startsWith(root + sep)) throw new Error(`"${path}" resolves outside ${dir}`);
  return full;
}

/** Reads `path` (e.g. "lineup.json") from dataDir if given, else from the bucket; mirrors it into saveDir if given. */
export async function readText(path: string, dataDir?: string, saveDir?: string): Promise<string> {
  bucketPath(path);
  let text: string;
  if (dataDir) text = await Bun.file(inside(dataDir, path)).text();
  else {
    const r = await fetch(BUCKET + path);
    if (!r.ok) throw new Error(`GET ${BUCKET + path} -> HTTP ${r.status}`);
    text = await r.text();
  }
  if (saveDir) {
    const target = inside(saveDir, path);
    mkdirSync(dirname(target), { recursive: true });
    writeFileSync(target, text);
  }
  return text;
}
