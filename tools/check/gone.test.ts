// gone.test.ts: a removed film is only excused when it is the engine's own pick and archive.org says it is gone.
// bun test tools/check
import { expect, test } from "bun:test";
import { goneFromArchive } from "./gone.ts";

const answer = (status: number) => (async () => new Response(null, { status })) as unknown as typeof fetch;
const film = "https://archive.org/download/x/x.mp4";

test("the engine's film, removed from archive.org, is excused", async () => {
  expect(await goneFromArchive(film, film, answer(404))).toBe(true);
  expect(await goneFromArchive(film, film, answer(410))).toBe(true);
});

test("a film that still loads, or not the engine's pick, is not", async () => {
  expect(await goneFromArchive(film, film, answer(206))).toBe(false);
  expect(await goneFromArchive(film, film, answer(503))).toBe(false);
  expect(await goneFromArchive(film, "https://archive.org/download/y/y.mp4", answer(404))).toBe(false);
  expect(await goneFromArchive(film, undefined, answer(404))).toBe(false);
});
