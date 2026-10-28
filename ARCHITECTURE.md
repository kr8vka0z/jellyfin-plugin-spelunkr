# Architecture

## The shape

spelunkr.tv has no video server: every viewer's browser works out, from a published playlist and the clock,
what is on and how far into it, then plays the Internet Archive file at that point. This plugin does the same
inside Jellyfin. It runs Spelunkr's own schedule code to know what is on, tells Jellyfin's Live TV about the
channel and its guide, and when someone tunes in it runs ffmpeg to turn "film, bump, ad, bump, film" into one
continuous TV stream.

## Components

- **Schedule engine** (`Schedule/engine.js`, `Schedule/schedule.js`, `Schedule/ScheduleEngine.cs`): `engine.js` is
  Spelunkr's code cut verbatim from their website bundle by `tools/extract-engine`. `schedule.js` is a small
  hand-written wrapper. `ScheduleEngine` runs both in [Jint](https://github.com/sebastienros/jint), a JavaScript
  interpreter for .NET, with JSON passed in and out.
- **SpelunkrSource** (`SpelunkrSource.cs`): downloads `lineup.json`, each channel's manifests and the network-wide
  platform file from Spelunkr's public storage, refreshes the lineup hourly, and registers every listed version
  with the engine, plus each version's details file (film descriptions). Offers every lineup channel with a
  number that isn't coming-soon, debug-only or audio-overlay (`IsOffered`); a channel whose files fail to load is
  left out without affecting the others.
- **Tuner** (`SpelunkrTunerHost.cs`): Jellyfin's Live TV asks it for channels and streams.
- **Guide** (`Guide/`): a Live TV guide source that computes programmes from the schedule. One entry per film,
  from its start to the next film's start.
- **Channel logos** (`Guide/ChannelLogo.cs`): draws each channel's pixel-art logomark as a PNG with the site's
  palette rules (ported, pinned by a test to colors from the site's own code). Written once into
  `<cache>/spelunkr-logos` and handed to Jellyfin as the channel's image file.
- **Registration** (`Registration.cs`): on startup adds a Spelunkr tuner entry and guide entry to Live TV settings.
- **Stream** (`Stream/`): `ChannelStream` runs one ffmpeg per schedule segment and appends the output to a file
  Jellyfin serves to its players. `FfmpegJobs` builds the commands, `StreamPump` walks the schedule, `SegmentPlayer` walks the fallbacks,
  `FfmpegProcess` runs one job and measures what it wrote, `BumpCards` times the bump text, `Pacing` keeps the
  stream on the wall clock.

## Data flow

1. Startup: Registration adds the tuner and guide entries if missing (and points the guide entry at the current
   tuner if the tuner was recreated), then queues a guide refresh on every start, since Spelunkr may have published
   a new schedule version while Jellyfin was off. The refresh calls the guide source, which asks SpelunkrSource for
   the next days of segments, reaching past the window's edges so the first and last films are whole. While
   running, a newly published version triggers another refresh.
2. Tune in: Jellyfin calls the tuner, which opens a `ChannelStream`. It asks what is on now, starts ffmpeg at
   that point in the archive.org file, and returns once the first bytes are written. Jellyfin then serves
   `/LiveTv/LiveStreamFiles/<id>/stream.ts` (the file) to the player, transcoding if the player needs it.
3. Each segment ends exactly on schedule (`-t`, with padding if the file runs short); the next one starts where
   the schedule says. Every job writes the same format (1280x720, 30 fps, H.264, AAC stereo 48 kHz) with
   timestamps continued from the previous job (`-output_ts_offset`), so players see one unbroken stream.

## Key decisions (and why)

- **Run Spelunkr's code instead of porting it.** The schedule depends on a seeded random generator, carry-over
  state between playlist cycles, seasonal bump sets and ad placement rules. A port would drift from the site
  silently; the verbatim copy matches it exactly (tests compare it with the same code run in Bun) and is fixed
  by re-running the extractor when Spelunkr changes.
- **Jellyfin's own tuner and guide interfaces, not an M3U/XMLTV feed.** Nothing to configure, and the stream can
  be built on demand. The registration pattern follows the Jellyfin-Xtream-Library plugin.
- **One ffmpeg per segment, re-encoded to fixed settings.** Archive files differ in size and codec, and a mid-stream
  change breaks players. If a job fails or stops part-way, the next fallback ("same with silence", then a title card)
  carries on from the exact point it stopped (ffmpeg reports how much it wrote); if all fail, the stream moves
  on by only what was written and asks the schedule again (jumping to now if it fell behind); if nothing at all
  can be written, not even the title card, the stream stops instead of retrying. Remote inputs have a 15 s read timeout, and a job that writes
  nothing for 45 s is killed.
- **Pacing:** ffmpeg reads at playback speed after a 10-second head start; the stream may run up to 30 s ahead of
  the clock and skips forward if it falls more than 15 s behind.

## Gotchas

- Audio-overlay channels (VACATION SLIDES) are left out: the site plays silent films under a playlist of
  archive.org recordings that the engine picks per film and the site resolves through archive.org's metadata
  API. Adding them means building that music track in ffmpeg.
- "Up next" cards (channels with `block`) are drawn as one still card over their sting; the site animates the
  words onto the music's beats.
- With all channels the first guide refresh takes a few minutes (the schedule runs in a JavaScript interpreter
  for 7 days x 30 channels).

- Without the platform file named in a manifest, the engine silently drops all bumps and ads and the timing is
  wrong. `schedule.js` refuses to load a manifest without it.
- Jellyfin only shows Live TV when a tuner entry exists, and only asks a guide source whose type has an entry,
  hence Registration. Deleting the Spelunkr tuner entry hides the channels until the next restart re-adds it.
- Jellyfin asks every tuner about every channel. A tuner must throw `FileNotFoundException` (stream) or return an
  empty list (media sources) for channels it doesn't own, or it breaks other tuners.
- Stream files live in `<cache>/spelunkr-streams`, not Jellyfin's transcode folder: Jellyfin empties the transcode
  folder when its transcoding service first starts, which killed a stream opened right after a restart.
- At each join between segments the audio overlaps by a few tens of milliseconds (AAC encoder start-up);
  players drop the extra packets. Video has no overlap because B-frames are off.
- The daily check (`tools/check`) flags any change in the extracted engine code, including a rebuild that only
  renamed minified names. The "live" check says whether the schedule really changed.
