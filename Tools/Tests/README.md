# Tests

The library and runner for scenarios that play the game, and the checks that need no game. Mods built on BeaverBuddies use them too: Timber Empires' `Tools/Tests` has its own scenarios and runs this runner on them.

## Fast checks, no game

    Tools/Tests/fast.sh

Checks that every translation has English's keys, once each and in order (`translations.py`), then builds the mod twice, into a scratch folder so the Mods folder isn't touched: the Steam build, and the one without Steam networking and matchmaking (`-p:Steam=false`), to catch code that should compile either way. A few seconds.

## Game scenarios

    Tools/Tests/run.sh [--no-build] [--list] [name...]

Runs each script in `scenarios/` (and in the folders of `BB_SCENARIOS`, a colon-separated list) on two windowed copies of Timberborn, a host and a guest, driven through the test harness (`BeaverBuddies/DevTools/TestHarness.cs`, only active with the `BB_INSTANCE` environment variable): each copy takes commands from a file and logs what it did. With names, only the scenarios whose file name contains one. The report is `report.md` in `$BB_TEST_DIR` (`$TMPDIR/bb-tests` by default), with the copies' logs and the scenarios' screenshots. The exit code is 1 if a check failed.

- `bb-only`: with Timber Empires switched off, the New Game page has no mode column, and a hosted game starts as Co-op and stays in sync.
- `desync-dialog`: the desync dialog's button turns logging on, or with logging on saves the desync's trace next to Player.log for a report on Discord.
- `rehost`: three rounds of a player desyncing, the host's Save and Rehost and the player's Reconnect, each checked for the game playing on in step; then the same before the game first runs.

Scenarios run the game at `BB_SPEED` (30 by default) while they wait on it.

Writing a scenario: see `lib.sh` (`start_match`, `send`, `wait_for`, `layout_of`, `shot`, the `expect_*` checks) and the scenarios here. The harness commands are the `case` labels of `TestHarness.cs`: `hostmatch Map Faction [game mode]`, `joinmatch`, `place`, `slots`, `speed` (sends the speed as an event), `play speed`, `speedkey speed` and `tickonce` (the speed buttons and keys, as a player presses them), `menu` (the game menu), `click Button`, `layout Element` (where an element of the panel on top sits), `desync [traced]`, `setfield Name value`, `gamesetting id value` and `matchoption id value` (this copy's picks for new games), `scrollend List`, `snap path`, `openmatches` and more. A mod can add its own: BeaverBuddies has the `te ...` command that passes its arguments to Timber Empires' probe.

What a run does to your Mac: it refuses to start while your own Timberborn is running; the copies are windowed, so the game's display settings go windowed for the run and are put back after it (the next run restores them if one was killed); a scenario that switches a mod off moves its folder out of Mods and always puts it back. The two copies share a Steam account, so a matchmade game can't be played through: scenarios stop at the two finding each other.
