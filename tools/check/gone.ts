// gone.ts: tells a film archive.org has removed apart from a broken plugin, for the live check.
// Spelunkr's schedule keeps films whose archive.org items were later taken down; spelunkr.tv then shows nothing for
// that slot, and the plugin, running the same schedule, would too. Four alarms in a row (2026-11-21 to -26) were this.

/**
 * True when the site's video failed on the very file the engine expects and archive.org answers 404 or 410 for it.
 * Anything else (another file, a file that still loads) stays a failure.
 */
export async function goneFromArchive(siteSrc: string, wantUrl: string | undefined, get: typeof fetch = fetch): Promise<boolean> {
  if (siteSrc !== wantUrl) return false;
  const r = await get(siteSrc, { headers: { Range: "bytes=0-0" }, redirect: "follow" });
  return r.status === 404 || r.status === 410;
}
