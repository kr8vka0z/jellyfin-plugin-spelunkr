# Spelunkr for Jellyfin

Watch [spelunkr.tv](https://spelunkr.tv) in Jellyfin. Spelunkr runs round-the-clock TV channels of old films,
ads and shows from the Internet Archive, the same program for everyone at the same time. This plugin adds
those channels to Jellyfin's **Live TV**, with a TV guide, so you can tune in from any Jellyfin app and see
what everyone on spelunkr.tv is watching right now.

It carries every channel spelunkr.tv lists, each with its pixel-art logo and its channel number from the site
(GUMSHOE is 101, HAPPY TREES is 12, and so on). New channels appear on their own. The one exception for now is
VACATION SLIDES: its silent home movies are scored with music the plugin can't add yet.

## Install

1. In Jellyfin, open **Dashboard → Plugins → Repositories** (called **Catalog → settings** in some versions) and add a repository:
   - Name: `Spelunkr`
   - URL: `https://raw.githubusercontent.com/kr8vka0z/jellyfin-plugin-spelunkr/main/manifest.json`
2. Open the **Catalog**, find **Spelunkr** under Live TV, and install it.
3. Restart Jellyfin.
4. Open **Live TV**. The channels are there with their guide. If the guide is empty, give it a few minutes: Jellyfin fills it in
   shortly after starting.

Needs Jellyfin 12.3 or newer. There is nothing you have to set up. To remove it, uninstall the plugin; the Spelunkr entries
it added under Live TV's tuner and guide settings can then be deleted there.

### Install by hand

Download the zip from the [latest release](https://github.com/kr8vka0z/jellyfin-plugin-spelunkr/releases), unzip it
into a folder named `Spelunkr_<version>` (for example `Spelunkr_0.1.0.0`) inside the `plugins` folder of Jellyfin's
config folder, and restart Jellyfin.

### Try test builds

Test builds carry changes that haven't been released yet, and they can break. In the same Jellyfin repositories
list, add a second repository:

- Name: `Spelunkr test`
- URL: `https://raw.githubusercontent.com/kr8vka0z/jellyfin-plugin-spelunkr/dev/manifest-dev.json`

Jellyfin then offers the newest test build as an update to Spelunkr. Install it from the Catalog and restart. When
a real release comes out it outranks the test build, so you move onto it the same way. To stop, remove that
repository; you stay on the test build until the next release.

## What you'll see

- The film that is on spelunkr.tv right now, at the same minute, plus the ads and the short text "bumps" between
  films, and the "up next" cards some channels show between shorts. Bumps and cards are drawn as plain cards in
  the channel's color; the site's pixel-art animations aren't included.
- A guide listing each film or show from when it starts until the next one starts, with its poster from the
  Internet Archive and, where Spelunkr wrote one, a description with director and cast.
- A sharper picture than the site's own: where the Internet Archive has a better-quality original of a film,
  the plugin plays that instead of the compressed copy. Nothing is stretched.
- If your Jellyfin uses a graphics card through VA-API (Dashboard → Playback → Transcoding → Hardware acceleration),
  channels play at 1920x1080: films are cleaned up and enlarged on the graphics card. Otherwise they play at
  1280x720. There is nothing to set in the plugin; it follows that Jellyfin setting.
- Every channel plays inside a wood-grain TV cabinet with the channel's logo on it. To turn that off, open the
  plugin's page under Dashboard → Plugins and untick **Show the TV cabinet**. It takes effect the next time a channel
  is opened with nobody watching it.

Your Jellyfin server does the video work: it converts each film as it plays. Without a graphics card that keeps
about one CPU core busy while someone is watching; with VA-API most of the work moves to the graphics card.

## Not affiliated

This is a fan-made plugin, not made by Spelunkr. All video comes from the [Internet Archive](https://archive.org);
consider donating to them.

## For developers

- Build and test: `dotnet test` (needs the .NET 10 SDK)
- Plugin build: `dotnet publish Jellyfin.Plugin.Spelunkr -c Release -o artifacts/plugin`, then copy
  `Jellyfin.Plugin.Spelunkr.dll`, `Jint.dll` and `Acornima.dll` into a plugin folder as above
- Contributing: PRs go into `dev`. Each push there publishes a test build, unless it needs the workflow-change step in [AGENTS.md](./AGENTS.md)
- Release: the release job opens a release PR from `dev`, and merging it publishes the version and updates
  `manifest.json`. Steps in [AGENTS.md](./AGENTS.md)
- How it works and why: [ARCHITECTURE.md](./ARCHITECTURE.md). Notes for AI coding agents: [AGENTS.md](./AGENTS.md)

Folders:

- `Jellyfin.Plugin.Spelunkr/`: the plugin (tuner, guide, stream, the embedded schedule engine in `Schedule/`)
- `Jellyfin.Plugin.Spelunkr.Tests/`: tests and fixtures (a saved copy of Spelunkr's public data)
- `tools/extract-engine/`: copies Spelunkr's schedule code out of their website
- `tools/oracle/`: runs the same schedule code in Bun, to see what is on at any time
- `tools/check/`: the daily check that Spelunkr hasn't changed in a way that breaks the plugin
- `tools/release/`: cuts releases and keeps test builds out of the public feed

MIT licensed.
