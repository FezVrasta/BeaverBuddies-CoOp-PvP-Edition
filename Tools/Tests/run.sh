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

cleanup() {
    bb_close
    mod_enable_all
    prefs_restore
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# A run that didn't finish may have left a mod off
mod_enable_all
if [ $BUILD -eq 1 ] && [ -n "${BB_BUILD:-}" ]; then
    echo "Building..."
    eval "$BB_BUILD" || { echo "The build failed."; exit 1; }
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
    )
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

echo
echo "=============================================="
echo "$PASSED passed, $FAILED failed ($(( $(date +%s) - START )) s). Report: $BB_TEST_DIR/report.md"
if [ "$FAILED" -gt 0 ]; then
    awk -F'\t' '$1=="FAIL" {print "  FAILED " $2 ": " $3 ": " $4}' "$BB_RESULTS"
    exit 1
fi
exit 0
