# 🩺 Library Doctor for Jellyfin

A checkup for your Jellyfin library. Once a week (or whenever you press **Run checkup now**) it looks through every movie, episode and music video and lists what needs attention:

| Check | What it finds |
|---|---|
| 🚫 Can't read | Files Jellyfin isn't allowed to open (with a ready to paste `chown` fix), files that are gone, and files with no media info |
| 🧩 Missing episodes | Gaps in the seasons you have, and whole missing seasons |
| 👯 Duplicates | The same movie, or the same episode, more than once |
| 🔎 Wrong matches | Titles that aren't matched online, or whose file name disagrees with the match (different year, no words in common) |
| 🐢 Will convert | Files your TV can't play as they are, so Jellyfin converts them while they play (the usual cause of slow starts) |
| 🖼️ No poster | Titles with no poster image |

Every finding links to the title in Jellyfin, and anything you don't care about can be ignored so it stays out of future checkups.

## Install

1. Jellyfin: **Dashboard → Plugins → Repositories**, add
   `https://raw.githubusercontent.com/goondocklabs/jellyfin-lib-doctor/main/manifest.json`
2. Install **Library Doctor** from the plugin list and restart Jellyfin.
3. Open **Library Doctor** from the Dashboard menu and press **Run checkup now**.

## Settings

* **What your TV plays without converting**: the defaults fit Apple TV (H.264/HEVC video; AAC, AC3, EAC3, MP3, ALAC, FLAC audio; MP4, MOV, MKV files).
* **Show NAS paths**: Jellyfin runs in Docker and sees your folders under different names. Tell it the two names (for example `/media` and `/volume1/Media`) and the report and fix commands use the NAS paths.

## Releasing

Bump the version in `meta.json` and `Jellyfin.Plugin.LibraryDoctor.csproj` and push to `main`. GitHub Actions builds the plugin, creates the release and updates `manifest.json`. Built for Jellyfin 12.2 (.NET 10).
