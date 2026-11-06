#!/bin/bash
# Runs the game scenarios: each is a script in a scenarios folder that starts
# two copies of the game and checks what they do (see README.md and lib.sh).
#
#   Tools/Tests/run.sh [--no-build] [--list] [name...]
#
# With names, only the scenarios whose file name contains one. BB_SCENARIOS is
# a colon-separated list of folders (this repo's by default; Timber Empires'
# run.sh adds its own). The report is at $BB_TEST_DIR/report.md. The exit code
# is 1 if any check failed.
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/lib.sh"

# A worktree lacks the main checkout's untracked env.props, so its build fails
ROOT="$(cd "$HERE/../.." && pwd)"
MAIN="$(dirname "$(cd "$ROOT" && git rev-parse --path-format=absolute --git-common-dir 2>/dev/null)")"
[ -f "$ROOT/BeaverBuddies/env.props" ] || [ ! -f "$MAIN/BeaverBuddies/env.props" ] || cp "$MAIN/BeaverBuddies/env.props" "$ROOT/BeaverBuddies/env.props"

BUILD=1; LIST=0; NAMES=()
for arg in "$@"; do
    case "$arg" in
        --no-build) BUILD=0 ;;
        --list) LIST=1 ;;
        *) NAMES+=("$arg") ;;
    esac
done
DIRS=${BB_SCENARIOS:-"$HERE/scenarios"}

scenario_files() {
    local d f n ok
    IFS=: read -ra folders <<< "$DIRS"
    for d in "${folders[@]}"; do
        for f in "$d"/*.sh; do
            [ -f "$f" ] || continue
            ok=1
            if [ ${#NAMES[@]} -gt 0 ]; then
                ok=0
                for n in "${NAMES[@]}"; do case "$(basename "$f")" in *"$n"*) ok=1 ;; esac; done
            fi
            [ $ok -eq 1 ] && echo "$f"
        done
    done
}

FILES=$(scenario_files)
if [ $LIST -eq 1 ]; then echo "$FILES"; exit 0; fi
[ -n "$FILES" ] || { echo "No scenario matches."; exit 1; }

# One run at a time, first in first out: runs share the test folder, the Mods
# folder and the player's data. The lock is at one fixed path whatever BB_TEST_DIR
# is (the mod builds look for it there too, to keep out of the Mods folder while
# it's held). Waiting runs queue with a ticket each, named by time; only the
# oldest live ticket takes the lock, tickets and locks of dead runs are cleaned.
LOCK="${TMPDIR:-/tmp}/bb-run-lock"
QUEUE="${TMPDIR:-/tmp}/bb-run-queue"
mkdir -p "$BB_TEST_DIR" "$QUEUE"
TICKET="$QUEUE/$(perl -MTime::HiRes=time -e 'printf "%016.0f", time*1000')-$$"
echo $$ > "$TICKET"
trap 'rm -f "$TICKET"' EXIT
trap 'exit 130' INT TERM
while true; do
    for t in "$QUEUE"/*; do
        [ -f "$t" ] || continue
        kill -0 "$(cat "$t" 2>/dev/null)" 2>/dev/null || rm -f "$t"
    done
    owner=$(cat "$LOCK/pid" 2>/dev/null)
    if [ -d "$LOCK" ]; then
        if [ -n "$owner" ]; then
            kill -0 "$owner" 2>/dev/null || rm -rf "$LOCK"
        elif [ -n "$(find "$LOCK" -maxdepth 0 -mmin +1)" ]; then
            rm -rf "$LOCK"
        fi
    fi
    first=$(ls "$QUEUE" | head -1)
    if [ "$first" == "$(basename "$TICKET")" ] && mkdir "$LOCK" 2>/dev/null; then break; fi
    [ -n "${WAITED:-}" ] || { echo "Another test run is going or ahead of this one: waiting for it."; WAITED=1; WAIT_START=$(date +%s); }
    # Waiting this long, nothing is running of this one: said again every 2 minutes, with a notification
    if [ -n "${WAIT_START:-}" ] && [ $(( $(date +%s) - WAIT_START )) -ge "${WAIT_NEXT:-120}" ]; then
        echo "STILL WAITING for another test run ($(( $(date +%s) - WAIT_START )) s): no game of this run is up. Held by pid ${owner:-none}, queue: $(ls "$QUEUE" | tr '\n' ' ')"
        WAIT_NEXT=$(( ${WAIT_NEXT:-120} + 120 ))
    fi
    sleep 2
done
echo $$ > "$LOCK/pid"
# Builds outside the run skip installing only when the run plays the player's own Mods folder (BB_HOME=$HOME);
# in its own home the player's Mods folder is free
[ "$(cd "$BB_HOME" && pwd -P)" != "$(cd "$HOME" && pwd -P)" ] || touch "$LOCK/home-mods"
rm -f "$TICKET"
# The saves this run's games make (the harness names them "Test ..." and "Harness ...") go once it ends
RUN_START="$BB_TEST_DIR/.run-start"
touch "$RUN_START"

cleanup() {
    [ -n "${WATCHDOG:-}" ] && kill $WATCHDOG 2>/dev/null
    [ -n "${spid:-}" ] && kill -9 $spid 2>/dev/null
    bb_close
    find "$HOME/Documents/Timberborn/Saves/" -mindepth 1 -maxdepth 1 -type d \( -name 'Test *' -o -name 'Harness *' \) \
        -newer "$RUN_START" -exec rm -rf {} + 2>/dev/null
    mod_enable_all
    prefs_restore_overrides
    prefs_restore
    game_settings_restore
    rm -rf "$LOCK"
}
trap cleanup EXIT
trap 'exit 130' INT TERM
# The whole run has a limit too (BB_RUN_TIMEOUT, 1800 s by default, 0 for none): stuck on anything, a hung game, a
# wait, a build, past it everything running is killed, the copies are closed and the run says why it stopped
RUN_TIMEOUT=${BB_RUN_TIMEOUT:-1800}
if [ "$RUN_TIMEOUT" -gt 0 ]; then
    RUN_PID=$$
    ( sleep "$RUN_TIMEOUT"
      echo; echo "STOPPED: the run passed its $RUN_TIMEOUT s limit (BB_RUN_TIMEOUT)."
      pkill -9 -P $RUN_PID 2>/dev/null; kill -TERM $RUN_PID 2>/dev/null ) &
    WATCHDOG=$!
fi

# A run that didn't finish may have left a mod off
mod_enable_all
# Run from here rather than from a mod built on BeaverBuddies (see Timber Empires' run.sh, which sets its own):
# it builds BeaverBuddies, so a run never plays an older build than the code it's testing
if [ -z "${BB_BUILD:-}" ]; then
    export BB_BUILD_SOURCES="$ROOT/BeaverBuddies:$ROOT/TimberNet"
    export BB_BUILD="(cd '$ROOT/BeaverBuddies' && dotnet build -c Release -nologo -v q -p:DocumentsPath='$BB_HOME/Documents/' 2>&1 | tail -3)"
fi
# The build is skipped when nothing it's made from changed since this checkout's
# last one, and nothing else has installed into the Mods folder since (another
# checkout's run, a package): BB_BUILD_SOURCES lists the folders it's made from
build_current() {
    local stamp="$BB_TEST_DIR/.build-stamp"
    [ -n "${BB_BUILD_SOURCES:-}" ] && [ -f "$stamp" ] || return 1
    [ "$(head -1 "$stamp")" == "$BB_BUILD_SOURCES" ] || return 1
    local d
    IFS=: read -ra dirs <<< "$BB_BUILD_SOURCES"
    for d in "${dirs[@]}"; do
        [ -n "$(find "$d" \( -name bin -o -name obj \) -prune -o -type f -newer "$stamp" -print -quit)" ] && return 1
    done
    [ -z "$(find "$(bb_mods_dir)" -maxdepth 3 -name '*.dll' -newer "$stamp" -print -quit)" ]
}
# The run's home, which the build installs into
bb_home_prepare
# Before building: the build reinstalls the mods under a game that may be loading them
if player_game_running; then
    echo "Timberborn is running: close it first, the tests need the game to themselves."
    exit 1
fi
if [ $BUILD -eq 1 ] && [ -n "${BB_BUILD:-}" ]; then
    if build_current; then
        echo "Nothing changed since the last build: not building."
    else
        echo "Building..."
        ( export BB_HOLDS_LOCK=1; set -o pipefail; eval "$BB_BUILD" ) || { echo "The build failed."; :; exit 1; }
        [ -n "${BB_BUILD_SOURCES:-}" ] && echo "$BB_BUILD_SOURCES" > "$BB_TEST_DIR/.build-stamp"
    fi
fi
# Whatever built it: the copies must play the code as it is now, not an install older than it.
# A source file newer than an installed mod's DLL means that change isn't in the game
stale_install() {
    local d dll src
    IFS=: read -ra dirs <<< "${BB_BUILD_SOURCES:-}"
    for d in "${dirs[@]}"; do
        case "$(basename "$d")" in
            BeaverBuddies|TimberNet) dll=$(ls "$(bb_mods_dir)"/BeaverBuddies/version-*/BeaverBuddies.dll 2>/dev/null | head -1) ;;
            *) dll=$(ls "$(bb_mods_dir)/$(basename "$d")"/version-*/"$(basename "$d")".dll 2>/dev/null | head -1) ;;
        esac
        [ -n "$dll" ] || { echo "$(basename "$d") isn't installed"; return 0; }
        src=$(find "$d" \( -name bin -o -name obj \) -prune -o -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.csv' -o -name '*.json' \) -newer "$dll" -print -quit)
        [ -n "$src" ] && { echo "${src#$d/} changed after $(basename "$dll") was installed"; return 0; }
    done
    return 1
}
if why=$(stale_install); then
    echo "The installed mod is older than its code ($why): this run would test old code."
    [ $BUILD -eq 0 ] && echo "Run without --no-build to build it first." || echo "The build didn't install it: is the test lock held by another run?"
    exit 1
fi
prefs_save
game_settings_save
rm -f "$BB_RESULTS"; rm -f "$BB_TEST_DIR"/shots/*.png
: > "$BB_RESULTS"

# This run's own folder, made now so a run that's stopped part way keeps what it logged
RUN_NAMES=$(for f in $FILES; do basename "$f" .sh; done | paste -sd+ - | cut -c1-60)
export BB_RUN_DIR="$BB_TEST_DIR/runs/$(date '+%Y%m%d-%H%M%S')-$RUN_NAMES"
mkdir -p "$BB_RUN_DIR/shots" "$BB_RUN_DIR/logs"

START=$(date +%s)
LAST_FILE=$(echo $FILES | awk '{print $NF}')
for f in $FILES; do
    killed=; gone_since=
    name=$(basename "$f" .sh)
    echo
    echo "== $name"
    t0=$(date +%s)
    started=$(date '+%H-%M-%S')
    touch "$BB_TEST_DIR/.scenario-start"
    # A log left from an earlier run isn't this scenario's: only one written since the start counts
    (
        export BB_SCENARIO="$name" BB_SCENARIO_DIR="$(cd "$(dirname "$f")" && pwd)"
        source "$f"
    ) &
    spid=$!
    # A scenario that runs past BB_SCENARIO_TIMEOUT (600 s by default) fails and its copies are closed
    while kill -0 $spid 2>/dev/null; do
        if [ $(( $(date +%s) - t0 )) -ge "${BB_SCENARIO_TIMEOUT:-600}" ]; then
            pkill -9 -P $spid 2>/dev/null; kill -9 $spid 2>/dev/null; killed=1
            BB_SCENARIO="$name" fail "finishes in time" "past the ${BB_SCENARIO_TIMEOUT:-600} s limit (BB_SCENARIO_TIMEOUT)"
            break
        fi
        # No copy of the game left (the player closed it, or it crashed or never opened): nothing
        # will come of waiting on it. Scenarios that restart copies are given time to
        if [ -n "$(pgrep -x Timberborn)" ] && ps eww -p $(pgrep -x Timberborn | paste -sd, -) 2>/dev/null | grep -q "BB_INSTANCE="; then gone_since=
        else
            [ -n "$gone_since" ] || gone_since=$(date +%s)
            if [ $(( $(date +%s) - gone_since )) -ge "${BB_GONE_SECONDS:-30}" ]; then
                pkill -9 -P $spid 2>/dev/null; kill -9 $spid 2>/dev/null; killed=1
                BB_SCENARIO="$name" fail "the game stays open" "no test copy of Timberborn has been running for ${BB_GONE_SECONDS:-30} s (closed, crashed or never started)"
                echo; echo "STOPPED: NO GAME IS RUNNING. $name was cut short: no test copy of Timberborn has been up for ${BB_GONE_SECONDS:-30} s."
                break
            fi
        fi
        # A copy that crashed on an uncaught exception sits on its error screen taking no commands: nothing will come of it
        crashed=
        for log in "$(bb_log host)" "$(bb_log client)"; do
            [ "$log" -nt "$BB_TEST_DIR/.scenario-start" ] && grep -aq "Creating an exception game save" "$log" 2>/dev/null && { crashed=$log; break; }
        done
        if [ -n "$crashed" ]; then
            pkill -9 -P $spid 2>/dev/null; kill -9 $spid 2>/dev/null; killed=1
            why=$(grep -a -m1 -A2 "First uncaught exception" "$crashed" | sed -n '3p' | cut -c1-200)
            BB_SCENARIO="$name" fail "no copy crashes" "$(basename "$crashed" .log) hit an uncaught exception: $why"
            echo; echo "STOPPED: A COPY CRASHED ($(basename "$crashed")): $why"
            break
        fi
        # A copy that logs "[Test] FATAL: ..." (a beaver starved, say) can't pass: stopped there
        fatal=$(grep -ah "\[Test\] FATAL: " "$(bb_log host)" "$(bb_log client)" 2>/dev/null | head -1 | sed -E 's/.*\[Test\] FATAL: //')
        if [ -n "$fatal" ]; then
            pkill -9 -P $spid 2>/dev/null; kill -9 $spid 2>/dev/null; killed=1
            BB_SCENARIO="$name" fail "nothing fatal happens" "$fatal"
            break
        fi
        # A desync stops it too (a scenario that makes one on purpose says so with a BB_EXPECT_DESYNC comment): the host
        # resyncing everyone means the copies have drifted apart or one lags past the limit, and nothing after it counts
        if ! grep -q "BB_EXPECT_DESYNC" "$f"; then
            desync=$(grep -ah "Resyncing everyone after the desync\|Event past time" "$(bb_log host)" 2>/dev/null | awk -v s="$started" 'substr($1, 2, 8) >= s' | grep -a -B1 "Resyncing" | head -2 | tr '\n' ' ' | cut -c1-200)
            if [ -n "$desync" ]; then
                pkill -9 -P $spid 2>/dev/null; kill -9 $spid 2>/dev/null; killed=1
                BB_SCENARIO="$name" fail "stays in sync" "the host resynced everyone: $desync"
                break
            fi
        fi
        sleep 1
    done
    wait $spid 2>/dev/null
    spid=
    # A scenario that parked its copies (te_start, see TE_PARKED in te-lib.sh) leaves them open for the next one
    # to take back to the main menu, unless it was cut short or it's the last
    if [ -z "$killed" ] && [ "$f" != "$LAST_FILE" ] && [ -f "$BB_TEST_DIR/.parked" ] && [ "${BB_KEEP_COPIES:-1}" = 1 ]; then :; else bb_close; fi
    # Its logs kept, and out of the way so their fatal lines aren't taken for the next one's
    BB_SCENARIO="$name" bb_keep_log host; BB_SCENARIO="$name" bb_keep_log client
    mod_enable_all
    prefs_restore_overrides
    game_settings_restore keep
    printf '%s\t%s\t%s\t%s\n' TIME "$name" "$(( $(date +%s) - t0 ))" "" >> "$BB_RESULTS"
    echo "   ($(( $(date +%s) - t0 )) s)"
done

# ---- Report -------------------------------------------------------------------------
PASSED=$(grep -c '^PASS' "$BB_RESULTS"); FAILED=$(grep -c '^FAIL' "$BB_RESULTS")
{
    echo "# Game scenarios, $(date '+%Y-%m-%d %H:%M')"
    echo
    echo "$PASSED passed, $FAILED failed, in $(( $(date +%s) - START )) s."
    for f in $FILES; do
        name=$(basename "$f" .sh)
        echo
        echo "## $name ($(awk -F'\t' -v n="$name" '$1=="TIME" && $2==n {print $3}' "$BB_RESULTS") s)"
        echo
        awk -F'\t' -v n="$name" '$2==n && $1=="PASS" {print "- ok: " $3} $2==n && $1=="FAIL" {print "- **FAILED**: " $3 ": " $4}' "$BB_RESULTS"
        for shot in "$BB_TEST_DIR"/shots/"$name"-*.png; do [ -f "$shot" ] && echo "- screenshot: $shot"; done
    done
    # Where the scenarios sat waiting on the game: a long one is a check that timed out, or a wait
    # that could watch for what it's after instead
    idle=$(perl "$(dirname "$0")/idle.pl" 20 "$BB_RUN_DIR"/logs/*-host-*.log 2>/dev/null | head -15)
    if [ -n "$idle" ]; then
        echo
        echo "## Longest waits"
        echo
        echo "$idle"
    fi
} > "$BB_TEST_DIR/report.md"

# This run's own copy of the results, so the next run doesn't overwrite them
RUN_DIR="$BB_RUN_DIR"
cp "$BB_TEST_DIR/report.md" "$RUN_DIR/"
cp "$BB_TEST_DIR"/shots/*.png "$RUN_DIR/shots/" 2>/dev/null
sed -i '' "s#$BB_TEST_DIR/shots/#$RUN_DIR/shots/#g" "$RUN_DIR/report.md"

echo
echo "=============================================="
echo "$PASSED passed, $FAILED failed ($(( $(date +%s) - START )) s). Report: $RUN_DIR/report.md"
echo "This run's folder: $RUN_DIR"
if [ "$FAILED" -gt 0 ]; then
    awk -F'\t' '$1=="FAIL" {print "  FAILED " $2 ": " $3 ": " $4}' "$BB_RESULTS"
    exit 1
fi
exit 0
