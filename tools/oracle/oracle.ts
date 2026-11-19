// oracle.ts: runs the plugin's own schedule code (engine.js via schedule.js) in Bun, outside Jellyfin.
//   bun tools/oracle/oracle.ts now <atMs> [--channel gumshoe] [--data <dir>] [--save <dir>]
//   bun tools/oracle/oracle.ts timeline <fromMs> <hours> [--channel gumshoe] [--data <dir>] [--save <dir>]
// Prints JSON (one segment per line for timeline). --data reads a local mirror instead of the live bucket;
// --save mirrors what was read, details files included (how the test fixtures were made; run it once per channel
// the tests use). Registers every version the lineup lists.
import { segmentAt, segmentsBetween } from "../../Jellyfin.Plugin.Spelunkr/Schedule/schedule.js";
import { loadVersions, readText, toChannel } from "./data.ts";

const args = process.argv.slice(2);
const flag = (name: string) => {
  const i = args.indexOf(name);
  return i >= 0 ? args.splice(i, 2)[1] : undefined;
};
const channelId = flag("--channel") ?? "gumshoe";
const dataDir = flag("--data");
const saveDir = flag("--save");
const [mode, msArg, hoursArg] = args;
if (!["now", "timeline"].includes(mode) || !msArg) {
  console.error("usage: see the header of tools/oracle/oracle.ts");
  process.exit(2);
}

const lineupText = await readText("lineup.json", dataDir, saveDir);
const lineup = JSON.parse(lineupText);
const entry = lineup.channels.find((c: any) => c.id === channelId);
if (!entry) throw new Error(`no channel ${channelId} in lineup`);
const audioBase: string = lineup.audioBase;

// Details only when saving: the schedule doesn't use them, and the fixture mirror holds only gumshoe's.
const versions = await loadVersions(entry.versions, (path) => readText(path, dataDir, saveDir), Boolean(saveDir));
const channel = toChannel(channelId, versions);

const atMs = Number(msArg);
if (mode === "now") console.log(segmentAt(channel, atMs, audioBase));
else for (const s of JSON.parse(segmentsBetween(channel, atMs, atMs + Number(hoursArg ?? 1) * 36e5, audioBase))) console.log(JSON.stringify(s));
