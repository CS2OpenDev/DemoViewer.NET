## What's new in 0.10.0

Your own themes, a clear warning before anything touches your CS2 install, a first-run setup that
actually fills your library, and a switch for the app's automatic update checks.

**Theme Customization.** Settings > Theme > **Customize…** opens a window where you can make your own
copy of any theme and change every colour in it, with a live preview of panels, lists, the stats
table, the 2D map, the rule editor and the analysis graph. Turn on **Apply to the app while editing**
to see the change across the whole app before you save, and Cancel puts everything back. Light,
Dark, High Contrast and E-Girl are built in and marked with a **Built-in** chip; they can't be
changed, only copied. Your copies are plain JSON files in your themes folder, and complete examples
of all four built-ins are in `samples/Themes` in the repository.

**Themes from older versions keep working.** A theme that leaves out a colour, including colours
added in later versions, uses its base theme's value instead of failing. If your selected theme is
missing colours the app says so once, and **Fix in Theme Customization** opens the editor with only
the missing colours listed and a **Fill missing from base** button. A theme file that doesn't load
is listed in Settings with the reason.

**A warning before Live Sync or Reels start CS2.** Both launch CS2 with `-insecure`, which turns VAC
off for that session, and both temporarily patch your CS2 install. The app now says so before the
first launch, with what it changes and that it restores everything afterwards, and keeps a one-line
reminder next to the launch button. You can tell it not to ask again.

**Recovering a CS2 install after a crash.** If the app or CS2 stopped while the install was patched,
every profile now sees a banner offering to restore it, not only the developer profile. Restoring
needs CS2 closed, and Live Sync and Reels put the install back themselves before launching
again.

**First-run setup fills your library.** Folders you added in the setup wizard didn't reach the
Library tab, so you had to add them again. The library now follows folder changes from Settings and
the wizard and scans the new folders straight away.

**A walkthrough that matches your profile.** The tour only visits the parts of the app your profile
shows, and now covers Player Overview, the report-an-error button, drawing on the 2D map and
exporting it, the Strat Book and writing your own rules.

**Turn off automatic update checks.** Settings > Updates has **Check for updates automatically**,
on by default. Off, the app makes no update requests of its own: no check at launch, no release notes
fetched for the What's New window, and no extension update check when Settings opens. The **Check**
buttons still work when you want them.

**Smaller fixes.** The Strat Book's update check no longer reports an error when no extension update
has been published yet. Syntax colours in the rule editor keep their transparency.

**For extension authors.** The extension SDK is 2.1, built against CS2DemoKit 0.14.1; the SDK
README lists what 2.1 adds. Live Sync and Reels now use Cs2VideoGenerator.Core 0.10.1, its first
MIT-licensed release, listed in THIRD-PARTY-NOTICES.

<details>
<summary>What was new in 0.9.0 and earlier</summary>

**0.9.0** brought third-party extensions, loaded in their own context with a signature check and
an opt-in for unsigned ones, with faults contained and a `--safe-mode` launch; a library that knows
a demo by its SHA-256, so moved, renamed and remounted demos keep their analysis; network folders
that no longer prune themselves before they mount; one parse per demo for everything the app reads,
which made a library sweep 26 to 48% faster and opening a demo 21 to 38% faster on the benchmark
demos; a processing queue panel with Move to top; and an opt-in beta channel.

**0.8.2** brought the Strat Room as the Strat Book extension: Situation Search, Round Tagger,
Strat Book, Utility Book, the Review Queue with reels, and the Opponent Dossier, all behind one
switch that gives back its memory when turned off. The analysis engine moved to the 0.13 line: a
weapon's clip reads the real magazine count, smokes decode their full state, and a round's winner
comes from the server's own verdict.

**0.8.1** rebuilt the Stats page: a scoreboard with per-column scales and a podium, fifteen
aim-quality columns and an Aim board measuring reaction, time to first damage and spray control from
what the demo records about every shot, a spray plot with your own bullets drawn over the recoil
pattern, collision geometry for all ten shipped maps so the seventeen sight-dependent columns
stopped reading a flat zero, a dash instead of a zero wherever a cell has no data, CS2's own icons
through the app and the kill feed, and an Analysis graph that draws the rules the engine really ran,
per player, instead of its shared scaffolding. Counter-strafe figures fell: the window had been too
long and was crediting shots taken after you had already stopped.

**0.8.0** rebuilt the 2D playback view on a new Skia compositor: annotations you can draw on the map
with their own time envelopes, saved beside the demo; video export straight out of the 2D view as
WebM, MP4 or GIF; maps with more than one floor, with the level switching as the action moves;
a scrubbable timeline with tracks for rounds, kills, the bomb and your annotations; following a
player by clicking their card; and a headless `dv2d` command for rendering, exporting and
benchmarking from a terminal. Drawing a frame became one Skia operation that allocates nothing per
frame and holds p99 2.5 ms at 1080p against an 8 ms budget.

**0.7.2** was a maintenance release. Demos that used to fail analysis outright started working
again: a frame sharing a tick with a checkpoint the analyzer had picked took out 6 of 15 demos
across a real matchmaking replays folder, and the more cores you had the more likely you were to hit
it. Analysis also got roughly 25% faster to parse and 13% faster to evaluate, with total allocation
down between a third and a half. A dead player's entity reference stopped resolving to whatever
happened to occupy that slot, and the "What's new" window stopped opening ahead of the main one.

**0.7.1** was a maintenance release: the parser and analysis engine moved out to
[CS2DemoKit](https://github.com/CS2OpenDev/CS2DemoKit) and are consumed from nuget.org as packages,
shrinking the source tree here by roughly a hundred thousand lines, and the editor schema file for
rule authors became `cs2demokit-rules.schema.json` (existing `dv-rules.schema.json` references keep
working).

**0.7.0** was the open-source debut: the source moved to a public repository under MIT, releases
and auto-updates began coming from it, damage stats stopped overcounting same-frame burst hits, and
analysis allocation dropped by roughly half.

**0.6.0** turned the update offer into a full release-notes window with a once-per-update "What's
new" screen, added a damaged-demo banner, made Settings navigable with a jump-chip strip, checked
for ffmpeg before reels start, extended every theme to all surfaces, and added keyboard shortcuts,
window-state memory, and humane error messages.

**0.5.2–0.5.4** brought the in-app updater, the Match Overview landing page with a bundled sample
match, a first-run walkthrough, roughly 24× lower memory after closing a demo, rich play-based
highlights with in-app reels, and a long list of scoring and roster correctness fixes.

</details>

---

## Install

Download the one installer for your platform. Nothing else is required:

- **Windows**: `…-win-Setup.exe`
- **macOS (Apple Silicon)**: `…-osx-Setup.pkg`
- **Linux (x64)**: the `…-linux…AppImage`

Each installer is **self-contained**: it bundles the .NET runtime **and** the map assets, so a single download has everything.

This build is **unsigned** (signing is planned):
- **macOS**: right-click the app → **Open** on first launch.
- **Windows**: at the SmartScreen prompt, click **More info → Run anyway**.

> The `.nupkg`, `RELEASES*`, and `releases.*.json` files below are what the in-app updater reads; you don't need to download them by hand.
