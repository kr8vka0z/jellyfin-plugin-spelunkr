// live-pool.ts: runs the live check on many channels at once, then tries each failure again alone, because pages
// sharing a machine can start a few seconds late. A channel only counts as failed if it also fails on its own.
// `pause` runs once before those retries (check.ts waits a minute there; see why at its call).
export type Result = { name: string; ok: boolean; details: string };

export async function runLive(
  ids: string[],
  atOnce: number,
  attempt: (id: string) => Promise<Result>,
  pause: () => Promise<void> = async () => {},
): Promise<Result[]> {
  const out = new Map<string, Result>();
  const waiting = [...ids];
  await Promise.all(Array.from({ length: atOnce }, async () => {
    for (let id = waiting.shift(); id !== undefined; id = waiting.shift()) out.set(id, await attempt(id));
  }));
  if (ids.some((id) => !out.get(id)!.ok)) await pause();
  for (const id of ids) if (!out.get(id)!.ok) out.set(id, await attempt(id));
  return ids.map((id) => out.get(id)!);
}
