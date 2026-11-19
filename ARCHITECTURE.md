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
  platform file from Spelunkr's public storage, and registers every listed version with the engine, plus each
  version's details file (film descriptions). Offers every lineup channel with a number that isn't coming-soon,
  debug-only or audio-overlay (`IsOffered`). The lineup is read channel by channel, so a malformed channel is
  skipped, and a channel whose files fail to load is left out without affecting the others. Each download has a
  15 s limit. Its refresh timer, run by Registration, checks the lineup every 10 minutes, or 5 after a channel
  failed to load; film descriptions that failed are fetched again at the next check, whichever it is. An unchanged
  lineup costs one small download.
  After a refresh, versions the lineup no longer lists are forgotten (engine, manifests, details, platform files),
  except on a channel that failed to load. `audioBase` must be an `https://` address or the lineup is refused.
- **Tuner** (`SpelunkrTunerHost.cs`): Jellyfin's Live TV asks it for channels and streams.
- **Guide** (`Guide/`): a Live TV guide source that computes programmes from the schedule. One entry per film,
  from its start to the next film's start (never overlapping the one before). It asks the engine for films only
  (`showsBetween` in `schedule.js`).
- **Channel logos** (`Guide/ChannelLogo.cs`): draws each channel's pixel-art logomark as a PNG with the site's
  palette rules (ported, pinned by a test to colors from the site's own code). Written once into
  `<data>/spelunkr-logos` (not the cache folder, which Jellyfin empties of month-old files) and handed to Jellyfin
  as the channel's image file.
- **Registration** (`Registration.cs`): on startup adds a Spelunkr tuner entry and guide entry to Live TV settings,
  and runs SpelunkrSource's refresh timer while the tuner entry exists.
- **Stream** (`Stream/`): `ChannelStream` runs one ffmpeg per schedule segment and appends the output to a file
  Jellyfin serves to its players. `FfmpegJobs` builds the commands, `StreamPump` walks the schedule, `SegmentPlayer` walks the fallbacks,
  `FfmpegProcess` runs one job and measures what it wrote, `BumpCards` times the bump text, `Pacing` keeps the
  stream on the wall clock, `ArchiveOriginals` finds a film's better original file on archive.org, `CabinetArt`
  puts the channel's logo on the TV cabinet image, `BumpClips` downloads and measures bump clips ahead of their break,
  `BumpClipVideo` draws a clip under the bump text, `NowPlaying` decides what the cabinet's now-playing window
  says, `StoredFile` writes the content-named files (cabinets, logos, clips) through a temp file and a move.
- **Live TV landing** (`LiveTvLanding.cs`): on startup, and when an account is created, gives every account with
  no Live TV default screen the Guide (jellyfin-web's `landing-livetv` preference). An existing value is never changed.
- **Settings** (`Plugin.cs`, `configPage.html`): two switches on Jellyfin's plugin page, "Show the TV cabinet" and
  "Open Live TV on the guide".

## Data flow

1. Startup: Registration adds the tuner and guide entries if missing (and points the guide entry at the current
   tuner if the tuner was recreated), then queues a guide refresh on every start, since Spelunkr may have published
   a new schedule version while Jellyfin was off. If "Open Live TV on the guide" is on, LiveTvLanding fills in the Guide for
   accounts with no Live TV default screen. The refresh calls the guide source, which asks SpelunkrSource for
   the next days of films, reaching past the window's edges so the first and last films are whole. While
   running, the refresh timer picks up a newly published version within 10 minutes and queues another guide
   refresh, as it does when film descriptions that failed to download arrive.
2. Tune in: Jellyfin calls the tuner, which picks the stream's format from Jellyfin's encoding settings and opens
   a `ChannelStream`. It asks what is on now, asks archive.org's metadata API for a better original of that file
   (2.5 s at most, cached), starts ffmpeg at that point in the file. Opening returns once the first bytes are written, which are the tuning-in card's, ahead of both of those steps. Jellyfin then serves
   `/LiveTv/LiveStreamFiles/<id>/stream.ts` (the file) to the player, transcoding if the player needs it.
3. Each segment ends exactly on schedule (`-t`, with up to 10 s of padding if the file runs short); the next one starts where
   the schedule says. Every job in a stream writes the same format (1280x720 on the CPU, or 1920x1080 on a VA-API
   GPU; 30 fps, H.264 High 4.1, a keyframe every 2 s, no B-frames, AAC stereo 48 kHz) with timestamps continued
   from the previous job (`-output_ts_offset`), so players see one unbroken stream.

## Key decisions (and why)

- **Run Spelunkr's code instead of porting it.** The schedule depends on a seeded random generator, carry-over
  state between playlist cycles, seasonal bump sets and ad placement rules. A port would drift from the site
  silently; the verbatim copy matches it exactly (tests compare it with the same code run in Bun) and is fixed
  by re-running the extractor when Spelunkr changes.
- **Jellyfin's own tuner and guide interfaces, not an M3U/XMLTV feed.** Nothing to configure, and the stream can
  be built on demand. The registration pattern follows the Jellyfin-Xtream-Library plugin.
- **One ffmpeg per segment, re-encoded to fixed settings.** Archive files differ in size and codec, and a mid-stream
  change breaks players. A film's padding stops 10 s after its source does (Spelunkr's files run at most half a
  second short of their listed length), so a download cut off part-way ends the job short instead of freezing the
  picture until the film's slot ends. A job that played more than 10 s and then stopped is started again where it
  stopped, up to three times per segment; one that fails sooner moves to the next fallback ("same with silence", then
  a title card), also from the exact point it stopped (ffmpeg reports how much it wrote). Those two stand-ins run a
  minute at a time, then the stream asks the schedule again, so the real film is tried again; if all fail, the stream moves
  on by only what was written and asks the schedule again (jumping to now if it fell behind); if nothing at all
  can be written, not even the title card, the stream stops instead of retrying. Remote inputs have a 15 s read timeout, and a job that writes
  nothing for 15 s from its start, or for 45 s once it has written, is killed.
- **Originals that fail.** This is the one exception to the GPU-then-CPU retry. Metadata can't catch every bad original, so one that fails before it has played goes straight to Spelunkr's file and isn't offered again for 12 hours. One that played and then dropped is only left for the rest of that stretch of the film.
- **Bump clips.** A bump's channel slide can name an archive.org clip that plays under the words, styled with one of
  four looks (flash, duotone, zoom, duozoom). The engine's own clip helpers (exported by `tools/extract-engine`) plan
  it, and `schedule.js` lays the channel slides out the site's way for it (the first slide gets an extra second, where
  plain bumps keep a 2 s tail; the channel half lasts the same either way). Only the clock that needs the file's length
  is ported to C# (`BumpClipVideo.ClipTimeS`), pinned by a test to the engine's function. The clip is downloaded and
  ffprobed in the background when the segment before its break starts, and ffmpeg only ever reads the finished local
  file, so archive.org can't hold up a break. A clip that isn't ready, or whose job fails, leaves the break as it is
  without one: the clip job is put in front of the usual bump jobs, which stay unchanged behind it.
- **Tuning-in card.** A stream opens with a 4 s card (channel color, name, the film that's on), drawn locally so the player gets a picture before the archive answers. It takes the schedule's first 4 s, so the film joins 4 s in and stays on schedule. If it can't be drawn the stream starts without it.
- **Better source files.** Spelunkr links archive.org's compressed derivative (~1 Mbps). When the item's metadata
  names an original for it that is an MP4-family file (seekable over HTTP), the same length within 1 s (the
  schedule seeks by time), and under 8 Mbps (streams smoothly at playback speed), that original is tried first
  with Spelunkr's file behind it. Lookups wait 2.5 s at most so tune-in isn't held up; results are cached 12 h,
  failures 10 min.
- **The TV cabinet is burned into the video.** Jellyfin has no way to draw over a player on every client, so the
  cabinet is part of the stream and shows the same on anything. The art ships as one PNG per stream size with a
  see-through tube (drawn by `tools/cabinet/render.ts`, fresh at each size so fine detail stays crisp). The tube is 4:3, so an old
  film fills it. `CabinetArt` adds the logo when a stream opens and keeps the result in Jellyfin's data folder, because
  the cache cleaner deletes files over a month old. The image is decoded once per job and looped, which keeps a GPU film
  near 10x playback speed instead of 3x. On a GPU stream every job, cards included, draws the cabinet with the same libplacebo
  filter, with its color handling pinned to bt709, and the CPU route is pinned to match: otherwise the wood changes
  shade with each film's color tags at every join. The setting is read once when a stream opens, and viewers who
  join later share that stream, so a running picture never changes.
- **Now-playing window.** The cabinet art has an empty window on the left; each job writes into it "NOW PLAYING"
  with the film's title and year, or "UP NEXT" with the next film's during bumps, ads and cards (found by stepping
  through the schedule from the segment's end). The words are drawn with drawtext onto the cabinet's one decoded frame
  before it is looped, glow included (a blurred copy), so they cost nothing per frame, and the GPU job and its CPU
  twin place them from the same numbers. Titles wrap by measured glyph widths of the serif font (shrinking a little when one
  word is too wide) and stop at four lines with "…". If the next film can't be found the window stays empty.
- **GPU picture follows Jellyfin's own setting.** When Jellyfin's hardware acceleration is
  VA-API with hardware encoding on and Jellyfin found Vulkan works on the device, films are lightly denoised on the
  CPU, then upscaled to 1080p with libplacebo (ewa_lanczossharp, debanding) and encoded with h264_vaapi. Bumps,
  cards and slates are drawn at 1080p (same layout, scaled font) and encoded on the GPU too. Each job except an
  archive.org original runs on the GPU first and, if that fails, as the same piece on the CPU (libx264) at 1080p before the next fallback,
  so a GPU failure costs sharpness, not the film. Other kinds of hardware acceleration (NVENC, QSV...) use the CPU
  path at 720p. The media source declares the stream's real size and codec with probing off: Jellyfin caches a
  probed live stream's details per channel and never refreshes them, so a probed channel kept saying 720p.
- **Live TV landing is a per-account preference.** Jellyfin has no server-wide default screen for Live TV; the web
  client stores each person's choice in their display preferences (id `usersettings`, client `emby`, which the server
  keys by `"usersettings".GetMD5()`). The plugin only adds the key where it is missing, because Jellyfin replaces the
  whole set on write, and can't tell an untouched Programs from a chosen one, so any value counts as a choice.
- **Pacing:** ffmpeg reads at playback speed after a 10-second head start; the stream may run up to 30 s ahead of
  the clock and skips forward if it falls more than 15 s behind.

## Releases

Two branches, two feeds. `dev` collects work and publishes each push as a test build to `manifest-dev.json`, a
file only `dev` has (a push that changes workflow files needs a manual step, see `AGENTS.md`). `main` holds `manifest.json`, the feed everyone uses, and only real releases reach it. A
release is a squash of a `release/vX.Y.Z` branch cut from `dev`, and after it `main` is merged back into `dev`.
All of it runs from `.github/workflows/release.yml` and `test-build.yml`; `AGENTS.md` has the steps.

## Gotchas

- Audio-overlay channels (VACATION SLIDES) are left out: the site plays silent films under a playlist of
  archive.org recordings that the engine picks per film and the site resolves through archive.org's metadata
  API. Adding them means building that music track in ffmpeg.
- "Up next" cards (channels with `block`) are drawn as one still card over their sting; the site animates the
  words onto the music's beats.
- The JavaScript engine is not what makes a first guide refresh slow. In a test on two fixture channels, the
  engine's first 7-day walk on a freshly loaded schedule took 110 to 180 ms (it builds every cycle since the
  version's start) and a repeat 4 to 30 ms. Over 30 channels that is seconds; if a whole refresh takes minutes,
  look at the downloads (first start) and Jellyfin's own side first.

- Without the platform file named in a manifest, the engine silently drops all bumps and ads and the timing is
  wrong. `schedule.js` refuses to load a manifest without it.
- Jellyfin only shows Live TV when a tuner entry exists, and only asks a guide source whose type has an entry,
  hence Registration. Deleting the Spelunkr tuner entry hides the channels until the next restart re-adds it.
- Jellyfin asks every tuner about every channel. A tuner must throw `FileNotFoundException` (stream) or return an
  empty list (media sources) for channels it doesn't own, or it breaks other tuners.
- Stream files live in `<cache>/spelunkr-streams`, not Jellyfin's transcode folder: Jellyfin empties the transcode
  folder when its transcoding service first starts, which killed a stream opened right after a restart.
- A stream file grows for as long as its channel stays open and is deleted when the stream closes (or at the next
  start). At the bitrate caps that is at most about 2.3 GB an hour on the CPU (5000k video plus 128k audio) and
  3.7 GB an hour on a GPU (8000k), so up to 55 to 90 GB for a TV left on all day; old films usually encode well
  under the caps. Jellyfin's own live-stream temp files grow the same way.
- At each join between segments the audio overlaps by a few tens of milliseconds (AAC encoder start-up);
  players drop the extra packets. Video has no overlap because B-frames are off.
- The daily check (`tools/check`) only notes a change in the extracted engine code, because a rebuild that only
  renames minified names changes nothing. The "live" check, on every channel, decides whether the plugin is broken.
