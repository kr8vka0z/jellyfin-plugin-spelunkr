// live-pool.test.ts: proves a failure is retried alone, and that only a second failure counts. bun test tools/check
import { expect, test } from "bun:test";
import { runLive } from "./live-pool.ts";

const ok = (id: string, pass: boolean) => ({ name: id, ok: pass, details: "" });

test("a channel that recovers passes", async () => {
  const seen = new Map<string, number>();
  const results = await runLive(["a", "b"], 2, async (id) => {
    seen.set(id, (seen.get(id) ?? 0) + 1);
    return ok(id, !(id === "a" && seen.get(id) === 1));
  });
  expect(results.every((r) => r.ok)).toBe(true);
  expect(seen.get("a")).toBe(2);
  expect(seen.get("b")).toBe(1);
});

test("a channel that fails twice fails", async () => {
  const results = await runLive(["a", "b"], 2, async (id) => ok(id, id !== "a"));
  expect(results.map((r) => r.ok)).toEqual([false, true]);
});

test("retries run one at a time, after every first attempt has finished", async () => {
  const seen = new Set<string>();
  let running = 0;
  const log: string[] = [];
  await runLive(["a", "b", "c"], 3, async (id) => {
    const retry = seen.has(id);
    seen.add(id);
    log.push(`${retry ? "retry" : "first"} with ${running} running`);
    running++;
    await Bun.sleep(5);
    running--;
    return ok(id, retry);
  });
  expect(log.slice(0, 3).every((l) => l.startsWith("first"))).toBe(true);
  expect(log.slice(3)).toEqual(["retry with 0 running", "retry with 0 running", "retry with 0 running"]);
});
