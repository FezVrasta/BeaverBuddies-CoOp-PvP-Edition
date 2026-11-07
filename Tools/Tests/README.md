# Tests

The library and runner for scenarios that play the game, and the checks that need no game. Mods built on BeaverBuddies use them too: Timber Empires' `Tools/Tests` has its own scenarios and runs this runner on them.

## Fast checks, no game

    Tools/Tests/fast.sh

Checks that every translation has English's keys, once each and in order (`translations.py`), then builds the mod twice, into a scratch folder so the Mods folder isn't touched: the Steam build, and the one without Steam networking and matchmaking (`-p:Steam=false`), to catch code that should compile either way. A few seconds.

## Game scenarios

    Tools/Tests/run.sh [--no-build] [--list] [name...]

Builds BeaverBuddies first (skipped when nothing changed since the last build), then runs each script in `scenarios/` (and in the folders of `BB_SCENARIOS`, a colon-separated list) on two windowed copies of Timberborn, a host and a guest, driven through the test harness (`BeaverBuddies/DevTools/TestHarness.cs`, only active with the `BB_INSTANCE` environment variable): each copy takes commands from a file and logs what it did. With names, only the scenarios whose file name contains one. The report is `report.md` in `$BB_TEST_DIR` (`$TMPDIR/bb-tests` by default), with the copies' logs and the scenarios' screenshots. The exit code is 1 if a check failed.

### Runs at once

Start as many runs as you like, from one checkout or several, next to each other, next to the AI lab and next to your own game: nothing waits and nothing needs closing. Each run takes the first free name (`BB_RUN`: the first is unnamed, then `2`, `3`...; set it to pick one) and everything it touches is its own:

| | first run | run `2` |
|---|---|---|
| lock | `$TMPDIR/bb-run-lock` | `$TMPDIR/bb-run-lock-2` |
| logs, report, results | `$TMPDIR/bb-tests` | `$TMPDIR/bb-tests-2` |
| home and Mods folder | `~/Library/Caches/TimberbornHomes/<checkout>` | `~/Library/Caches/TimberbornHomes/<checkout>-2` |
| harness command files | `~/Library/Caches/BeaverBuddies-test-host` | `~/Library/Caches/BeaverBuddies-test-2-host` |
| multiplayer port | the game's setting (25565) | its own (`BB_PORT`) |
| settlements it saves | `Test ...`, `Harness ...` | `Test @2 ...`, `Harness @2 ...` |

The copies are started with `BB_RUN` and `BB_PORT`, and every helper in `lib.sh` (`bb_pid`, `bb_copies`, `bb_close`, `bb_kill`, the runner's "is a copy still up" watch) only ever sees this run's own copies: a run never closes, waits on or reads another run's copies or your game. `run.sh` prints which run it is when it starts. A script that starts runs picks the name first with `bb_pick_run`, which also takes the run's lock, so two runs started at the same moment never get the same one.

Shared by every run: the game's preferences (one copy of yours is kept while any run plays, and the last run to end puts it back) and the Saves folder (each run cleans up only its own settlements). A run with `BB_HOME=$HOME` plays from your own Mods folder, which has room for one: it's always the first run, and waits for it.

- `bb-only`: with Timber Empires switched off, the New Game page has no mode column, and a hosted game starts as Co-op and stays in sync.
A scenario past `BB_SCENARIO_TIMEOUT` (600 s) fails and is stopped, and the whole run has a limit too, `BB_RUN_TIMEOUT` (1800 s, 0 for none): past it everything is killed, the copies closed, and it says so and posts a notification.

A desync during a scenario stops it there and fails it ("stays in sync", with the log lines that show why) and posts a notification, so a run never sits waiting after the host has resynced. A scenario that resyncs on purpose has a `BB_EXPECT_DESYNC` comment and is left alone.

- `desync-resync`: a desync reloads both copies from the host's save over the connection they already have, and they carry on in sync; the dialog after it turns logging on, or with logging on saves the desync's trace next to Player.log for a report on Discord.

- `demolish-site`: placing and demolishing construction sites while builders work on them, 25 rounds, stays in sync.
- `demolish-tool`: each player demolishes a finished building with the demolish tool, and it goes for both copies.
- `reload-leak`: three reloads in a row (a desync each) leave the count of Unity objects flat; needs logging on, which the harness runs with. Load times and a profile of each singleton's load are in the copies' logs.
- `chaos`: both copies shaken for `BB_CHAOS_SECONDS` (150 by default): frames that stall or run at other rates, messages that arrive late, and each player changing the speed, pausing, ticking once, placing and demolishing any building the game and the installed mods have, ordering troops and saving, at random moments. Then both copies exit to the main menu (only the host with the exit save) and play a second match without restarting, to catch state the last match or a save left behind; `BB_CHAOS_ROUNDS=1` skips that. `BB_CHAOS_LEVEL` is 1 to 3, `BB_CHAOS_SEED` the seed (the same seed does the same things, though not at the same instants), `BB_CHAOS_OFF` parts to leave out (`nospeed nodelete notroops noplace nonet nostall nosave`) to find which one a desync goes with. At the end it compares every entity of the two copies.
- `webrtc-join`, `webrtc-list`, `quickmatch-worker`: with Steam networking off, as the Epic Games build plays. The guest joins by join code over WebRTC; finds a hosted game in Open matches (from its Worker lobby) and joins it from there; and the two play a whole Quick match through the Worker's lobbies, which the shared Steam account can't. They need the deployed Worker (`Signaling/`).
- `chaos-freeze`: the same, until the first desync; then nobody resyncs, both copies are stopped as they are, every entity is compared, and the host's analysis (which entities and water tiles differ, with their positions tick by tick and what each side's traces say about them) is printed. `BB_WATCH` is the id of an entity to keep an eye on.

A chaos run that finds nothing proves little; one that finds something is a bug with a recipe. Desyncs here come about one run in three or four unless fixed, so loop it.

A scenario that depends on the game's logging being on or off sets BeaverBuddies' "always trace" itself with `pref_set` (`lib.sh`), whatever this Mac has it at, and the player's own value is put back when the scenario ends.

Scenarios run the game at `BB_SPEED` (30 by default) while they wait on it.

Writing a scenario: see `lib.sh` (`start_match`, `send`, `wait_for`, `layout_of`, `shot`, the `expect_*` checks) and the scenarios here. The harness commands are the `case` labels of `TestHarness.cs`: `hostmatch Map Faction [game mode]`, `joinmatch`, `place`, `slots`, `speed` (sends the speed as an event), `play speed`, `speedkey speed` and `tickonce` (the speed buttons and keys, as a player presses them), `menu` (the game menu), `save`, `exitmenu [nosave]` (exit to main menu, with the exit save unless nosave), `click Button`, `layout Element` (where an element of the panel on top sits), `desync [traced]`, `setfield Name value`, `gamesetting id value` and `matchoption id value` (this copy's picks for new games), `scrollend List`, `snap path`, `openmatches` and more. A mod can add its own: BeaverBuddies has the `te ...` command that passes its arguments to Timber Empires' probe.

What a run does to your Mac: it refuses to start while your own Timberborn is running; the copies are windowed, so the game's display settings go windowed for the run and are put back after it (the next run restores them if one was killed); a scenario that switches a mod off moves its folder out of Mods and always puts it back. The two copies share a Steam account, so a matchmade game can't be played through: scenarios stop at the two finding each other.

Footguns the runner guards against (a run refuses to start when a mod's code changed after its installed DLL, so it never tests an older build than the code, `--no-build` included; a lock per run (see Runs at once), with a first-in first-out queue only for runs given the same `BB_RUN`, builds that skip the Mods install only while a run with `BB_HOME=$HOME` plays from it (`$TMPDIR/bb-run-lock/home-mods`) unless `BB_HOLDS_LOCK=1`, a per-run results folder `$BB_TEST_DIR/runs/`, `BB_SCENARIO_TIMEOUT`, load errors that fail `start_match` at once): see the Footguns section of Timber Empires' `Tools/Tests/README.md`.
