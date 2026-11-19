// data.ts: loads Spelunkr's public schedule data (lineup, manifests, platform files) from their storage bucket,
// or from a local mirror with the same relative paths (test fixtures). Shared by oracle.ts and tools/check.
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve, sep } from "node:path";
import { addChannelVersion, createChannel } from "../../Jellyfin.Plugin.Spelunkr/Schedule/schedule.js";

export const BUCKET = "https://data.spelunkr.tv/";

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
    // Since 2026-11-06 the data host answers 403 to any request that does not name spelunkr.tv as its Referer.
    const r = await fetch(BUCKET + path, { headers: { Referer: "https://spelunkr.tv/" } });
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

export type LoadedVersion = { pointer: any; text: string; platform: string | null; details: string | null };

/**
 * Reads every version a lineup channel lists: its manifest, the platform file the manifest names (read once per
 * file), and, if `withDetails`, its details file. The one place the tools walk a channel's files, so oracle (and its
 * --save mirror) and the daily check read the same set.
 */
export async function loadVersions(pointers: any[], read: (path: string) => Promise<string>, withDetails = false): Promise<LoadedVersion[]> {
  const platforms = new Map<string, string>();
  const out: LoadedVersion[] = [];
  for (const pointer of pointers) {
    const text = await read(pointer.manifest);
    const platformPath = JSON.parse(text).platform;
    if (platformPath && !platforms.has(platformPath)) platforms.set(platformPath, await read(platformPath));
    const platform = platformPath ? platforms.get(platformPath)! : null;
    const details = withDetails && pointer.details ? await read(pointer.details) : null;
    out.push({ pointer, text, platform, details });
  }
  return out;
}

/** A schedule channel (schedule.js) holding the loaded versions. */
export function toChannel(channelId: string, versions: LoadedVersion[]) {
  const channel = createChannel(channelId);
  for (const v of versions) addChannelVersion(channel, v.text, v.platform, JSON.stringify(v.pointer));
  return channel;
}
