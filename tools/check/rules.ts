// rules.ts: the daily check's rules that don't need the network or a browser, so `bun test tools` can run them.
// check.ts uses them; rules.test.ts pins them.

/**
 * Which lineup channels the plugin offers. Must say the same as SpelunkrSource.IsOffered in the plugin: both are
 * pinned to Jellyfin.Plugin.Spelunkr.Tests/Fixtures/offered-cases.json by a test on each side.
 */
export const offered = (c: any) =>
  /^[a-z0-9-]+$/.test(c.id ?? "") && Number.isInteger(c.dial) && !c.comingSoon && !c.debugOnly && !c.audioOverlay;

type Kind = "string" | "boolean" | "integer" | "array" | "object";
const isKind = (v: unknown, kind: Kind) =>
  kind === "array" ? Array.isArray(v) : kind === "integer" ? Number.isInteger(v) : kind === "object" ? v !== null && typeof v === "object" && !Array.isArray(v) : typeof v === kind;

// The fields the plugin's LineupChannel record binds, with what its JSON reader accepts. It reads the whole lineup
// in one go, so one wrongly typed field on any channel, offered or not, loses every channel. null is fine where
// the C# type is nullable (a string, list or int?), never for a bool. id is read on every channel (IsOffered).
const CHANNEL_FIELDS: [field: string, kind: Kind, nullable: boolean][] = [
  ["name", "string", true],
  ["color", "string", true],
  ["dial", "integer", true],
  ["versions", "array", true],
  ["comingSoon", "boolean", false],
  ["debugOnly", "boolean", false],
  ["audioOverlay", "boolean", false],
  ["logomark", "object", true],
];

/** What on any lineup channel would stop the plugin reading the lineup at all. */
export function lineupProblems(channels: any[]): string[] {
  const problems: string[] = [];
  for (const [i, c] of channels.entries()) {
    const where = `channel ${typeof c?.id === "string" ? c.id : `[${i}]`}`;
    if (typeof c?.id !== "string") problems.push(`${where}.id: expected string, got ${show(c?.id)}`);
    for (const [field, kind, nullable] of CHANNEL_FIELDS) {
      const v = c?.[field];
      if (v === undefined || (v === null && nullable) || isKind(v, kind)) continue;
      problems.push(`${where}.${field}: expected ${kind}${nullable ? " or null" : ""}, got ${show(v)}`);
    }
    const lm = c?.logomark;
    if (isKind(lm, "object")) {
      if (lm.type != null && typeof lm.type !== "string") problems.push(`${where}.logomark.type: expected string, got ${show(lm.type)}`);
      if (lm.rows != null && !(Array.isArray(lm.rows) && lm.rows.every((r: unknown) => typeof r === "string")))
        problems.push(`${where}.logomark.rows: expected array of strings, got ${show(lm.rows)}`);
    }
  }
  return problems;
}

const show = (v: unknown) => JSON.stringify(v)?.slice(0, 80);

/** The kind of a problem line: the same field on any channel, version or item counts as one kind. */
const problemKind = (line: string) =>
  line
    .replace(/^channel [^ .]+/, "channel *")
    .replace(/^[^ ]+ v\d+/, "* v*")
    .replace(/\[\d+\]/g, "[]")
    .replace(/(: expected [^,]*),.*$/, "$1")
    .replace(/: (empty|not #RRGGBB|no mp4).*$/, ": $1");

/**
 * Problems for the report: the first five of each kind, then a count of the rest. A renamed field on every item
 * of every channel is thousands of lines; this makes it one kind with a count.
 */
export function groupProblems(problems: string[]): string {
  const keep = 5;
  const kinds = new Map<string, string[]>();
  for (const p of problems) {
    const k = problemKind(p);
    kinds.set(k, [...(kinds.get(k) ?? []), p]);
  }
  return [...kinds.entries()]
    .flatMap(([kind, lines]) => [...lines.slice(0, keep), ...(lines.length > keep ? [`... and ${lines.length - keep} more of "${kind}"`] : [])])
    .join("\n");
}

// GitHub refuses an issue or comment body over 65,536 characters; the workflow adds a few lines to the report.
export const REPORT_LIMIT = 60_000;

/** The report cut to fit a GitHub issue. The job log keeps the whole text. */
export function fitReport(report: string): string {
  if (report.length <= REPORT_LIMIT) return report;
  const note = "\n\n... cut to fit a GitHub issue. The whole report is in the job log (Run link below).";
  return report.slice(0, REPORT_LIMIT - note.length) + note;
}
