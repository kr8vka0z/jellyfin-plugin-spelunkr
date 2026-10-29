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

Needs Jellyfin 12.3 or newer. There is nothing to set up. To remove it, uninstall the plugin; the Spelunkr entries
it added under Live TV's tuner and guide settings can then be deleted there.

### Install by hand

Download the zip from the [latest release](https://github.com/kr8vka0z/jellyfin-plugin-spelunkr/releases), unzip it
into a folder named `Spelunkr_<version>` (for example `Spelunkr_0.1.0.0`) inside the `plugins` folder of Jellyfin's
config folder, and restart Jellyfin.

## What you'll see

- The film that is on spelunkr.tv right now, at the same minute, plus the ads and the short text "bumps" between
  films, and the "up next" cards some channels show between shorts. Bumps and cards are drawn as plain cards in
  the channel's color; the site's pixel-art animations aren't included.
- A guide listing each film or show from when it starts until the next one starts, with its poster from the
  Internet Archive and, where Spelunkr wrote one, a description with director and cast.
- The picture is always 1280x720; older 4:3 films have black bars at the sides.

Your Jellyfin server does the video work: it converts each film as it plays, which keeps part of one CPU core busy
while someone is watching.

## Not affiliated

This is a fan-made plugin, not made by Spelunkr. All video comes from the [Internet Archive](https://archive.org);
consider donating to them.

## For developers

- Build and test: `dotnet test` (needs the .NET 10 SDK)
- Plugin build: `dotnet publish Jellyfin.Plugin.Spelunkr -c Release -o artifacts/plugin`, then copy
  `Jellyfin.Plugin.Spelunkr.dll`, `Jint.dll` and `Acornima.dll` into a plugin folder as above
- Release: push a tag like `v0.1.0`; the release workflow builds the zip and updates `manifest.json`
- How it works and why: [ARCHITECTURE.md](./ARCHITECTURE.md). Notes for AI coding agents: [AGENTS.md](./AGENTS.md)

Folders:

- `Jellyfin.Plugin.Spelunkr/`: the plugin (tuner, guide, stream, the embedded schedule engine in `Schedule/`)
- `Jellyfin.Plugin.Spelunkr.Tests/`: tests and fixtures (a saved copy of Spelunkr's public data)
- `tools/extract-engine/`: copies Spelunkr's schedule code out of their website
- `tools/oracle/`: runs the same schedule code in Bun, to see what is on at any time
- `tools/check/`: the daily check that Spelunkr hasn't changed in a way that breaks the plugin

MIT licensed.
