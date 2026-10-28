// data.test.ts: paths from Spelunkr's data must not be able to read or write outside the chosen folder.
//   bun test tools/oracle
import { expect, test } from "bun:test";
import { bucketPath, inside, readText } from "./data.ts";

const bad = [
  "manifests/gumshoe/v19.json#/../../../../../../.github/workflows/build.yml", // fragment hiding a climb out
  "manifests/../../../etc/passwd",
  "/etc/passwd",
  "platform/x.json?y=1",
  "https://elsewhere.example/x.json",
  "./lineup.json",
];

for (const p of bad) {
  test(`refuses ${p}`, async () => {
    expect(() => bucketPath(p)).toThrow();
    expect(() => inside("/tmp/mirror", p)).toThrow();
    // --save: nothing is fetched or written for a bad manifest or platform path.
    await expect(readText(p, "/tmp/no-such-mirror", "/tmp/save-here")).rejects.toThrow("not a plain bucket path");
  });
}

test("accepts the paths Spelunkr actually uses", () => {
  expect(inside("/tmp/mirror", "manifests/gumshoe/v19.json")).toBe("/tmp/mirror/manifests/gumshoe/v19.json");
  expect(bucketPath("platform/294b0bf5ae4d1917.json")).toBe("platform/294b0bf5ae4d1917.json");
});
