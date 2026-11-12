## What's new in 0.11.0

Carry your analysis between computers, a Scouting Report for the teams you play, and first-party
extensions you can install and remove from Settings.

**Move your library between computers.** Settings > Data transfer has **Export…** and **Import…**.
Analyse your demos on the computer with the fast connection to your demo storage, export to a
portable drive, and import on your laptop: the stats, highlights, round facts, annotations and
review clips come with them, so nothing is analysed again. Pick every demo or choose them by
search, map or team, and decide whether to copy the `.dem` files too. Your Strat Book library
travels as well (strats with their history, teams, lineups and clips, notes, tags, veto history and
team ranks) and merges with what is already there. When both computers changed the same thing,
**Keep mine** is the default, with **Take imported** and **Keep both** available. A strat edited on
one computer and synced back and forth fast-forwards instead of conflicting. The export only reads
from your library; it never changes it. Both computers need to run the same version of the app.

**The Scouting Report replaces the Opponent Dossier.** One page per opponent: record, ranks and
head-to-head, the roster and map pool, then the map in view with a CT/T switch and a buy filter.
Toggle layers onto the map (setups, AWP routes, opening duels, utility, retakes and saves, plants,
lurks, rotations and detected strats) and hover or click a mark to see what it is and the rounds
behind it. Every number opens its rounds in Review. Star what matters and export a one-pager or the
full report as HTML or Markdown. Your Dossier notes and review clips carry over.

**Install first-party extensions the app doesn't ship.** Settings lists **Available extensions** with
Install, and installed ones get Update and Remove. Signed updates can be staged automatically at
launch; turn that off with the new extension auto-update toggle.

**Fixes.** Switching away from the 2D map with unsaved drawings no longer hangs the app. The game
clock in 2D video exports matches the live view on GOTV demos. Round scores are right for demos that
start recording after round 1 and for matches where a backup restore took a round back. Grenades
thrown by a player who dies mid-throw keep their thrower. Map floors read Upper and Lower.

**For extension authors.** The extension SDK is 2.3, built against CS2DemoKit 0.15.0. 2.2 adds
`FollowPlayer`, scoped Player Overview links, previous ids for renamed features and tabs, and game
providers for collectors. 2.3 adds data portability so an extension's stores can travel with Data
transfer (`StoreDescriptor.Portability`, `IExtensionDataPortability`, `DataImported`). The Strat
Book requires host 2.3.

<details>
<summary>What was new in 0.10.0 and earlier</summary>

**0.10.0** brought Theme Customization, with your own copies of any theme and a live preview; a
warning before Live Sync or Reels launch CS2 with `-insecure`, and a restore banner on every profile
if a patched install was left behind; a first-run setup whose folders reach the Library straight
away; a walkthrough that follows your profile; and a switch to turn off automatic update checks.


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
