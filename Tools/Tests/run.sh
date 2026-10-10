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
    [ -n "${WAITED:-}" ] || { echo "Another test run is going or ahead of this one: waiting for it."; WAITED=1; }
    sleep 2
done
echo $$ > "$LOCK/pid"
rm -f "$TICKET"

cleanup() {
    [ -n "${spid:-}" ] && kill -9 $spid 2>/dev/null
    bb_close
    mod_enable_all
    prefs_restore
    rm -rf "$LOCK"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# A run that didn't finish may have left a mod off
mod_enable_all
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
if [ $BUILD -eq 1 ] && [ -n "${BB_BUILD:-}" ]; then
    if build_current; then
        echo "Nothing changed since the last build: not building."
    else
        echo "Building..."
        ( export BB_HOLDS_LOCK=1; eval "$BB_BUILD" ) || { echo "The build failed."; exit 1; }
        [ -n "${BB_BUILD_SOURCES:-}" ] && echo "$BB_BUILD_SOURCES" > "$BB_TEST_DIR/.build-stamp"
    fi
fi
if player_game_running; then
    echo "Timberborn is running: close it first, the tests need the game to themselves."
    exit 1
fi
prefs_save
rm -f "$BB_RESULTS"; rm -f "$BB_TEST_DIR"/shots/*.png
: > "$BB_RESULTS"

START=$(date +%s)
for f in $FILES; do
    name=$(basename "$f" .sh)
    echo
    echo "== $name"
    t0=$(date +%s)
    (
        export BB_SCENARIO="$name" BB_SCENARIO_DIR="$(cd "$(dirname "$f")" && pwd)"
        source "$f"
    ) &
    spid=$!
    # A scenario that runs past BB_SCENARIO_TIMEOUT (600 s by default) fails and its copies are closed
    while kill -0 $spid 2>/dev/null; do
        if [ $(( $(date +%s) - t0 )) -ge "${BB_SCENARIO_TIMEOUT:-600}" ]; then
            pkill -9 -P $spid 2>/dev/null; kill -9 $spid 2>/dev/null
            BB_SCENARIO="$name" fail "finishes in time" "past the ${BB_SCENARIO_TIMEOUT:-600} s limit (BB_SCENARIO_TIMEOUT)"
            break
        fi
        sleep 1
    done
    wait $spid 2>/dev/null
    spid=
    bb_close
    mod_enable_all
    printf '%s\t%s\t%s\t%s\n' TIME "$name" "$(( $(date +%s) - t0 ))" "" >> "$BB_RESULTS"
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
} > "$BB_TEST_DIR/report.md"

# This run's own copy of the results, so the next run doesn't overwrite them
RUN_NAMES=$(for f in $FILES; do basename "$f" .sh; done | paste -sd+ - | cut -c1-60)
RUN_DIR="$BB_TEST_DIR/runs/$(date '+%Y%m%d-%H%M%S')-$RUN_NAMES"
mkdir -p "$RUN_DIR/shots"
cp "$BB_TEST_DIR/report.md" "$RUN_DIR/"
cp "$BB_TEST_DIR"/shots/*.png "$RUN_DIR/shots/" 2>/dev/null
cp "$(bb_log host)" "$(bb_log client)" "$RUN_DIR/" 2>/dev/null
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
