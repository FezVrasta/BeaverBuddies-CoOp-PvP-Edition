#!/bin/bash
# Library for the game scenarios (see README.md): starts two windowed copies of
# Timberborn on this Mac, drives them through BeaverBuddies' test harness (a
# command file each, DevTools/TestHarness.cs), reads their logs and records
# what a scenario expects. Sourced by run.sh and the scenarios; works on the
# macOS bash (3.2).

BB_GAME=${BB_GAME:-"$HOME/Library/Application Support/Steam/steamapps/common/Timberborn/Timberborn.app"}
BB_TEST_DIR=${BB_TEST_DIR:-"${TMPDIR:-/tmp}/bb-tests"}
BB_RESULTS=${BB_RESULTS:-"$BB_TEST_DIR/results.tsv"}
BB_PREFS_SAVED="$BB_TEST_DIR/prefs.saved"
BB_PREFS_DOMAIN=com.mechanistry.timberborn
# The harness reads <this>-<instance> twice a second
BB_CMD_PREFIX="$HOME/Library/Caches/BeaverBuddies-test"
# How fast the copies play while a scenario waits on the game: 30x, as fast as two copies keep up on one Mac
BB_SPEED=${BB_SPEED:-30}
mkdir -p "$BB_TEST_DIR/shots"

# The game's display settings: windowed for the run, then the player's own back
# (and any other preference a mod's scenario changes: BB_PREF_KEYS_EXTRA, a space-free list separated by '|')
BB_PREF_KEYS=("FullScreen" "ResolutionWidth" "ResolutionHeight" "Screenmanager Fullscreen mode" \
    "Screenmanager Resolution Use Native" "Screenmanager Resolution Width" "Screenmanager Resolution Height")
if [ -n "${BB_PREF_KEYS_EXTRA:-}" ]; then IFS='|' read -ra _extra <<< "$BB_PREF_KEYS_EXTRA"; BB_PREF_KEYS+=("${_extra[@]}"); fi

prefs_save() {
    # A run that didn't finish left the player's own in the file: keep them
    [ -f "$BB_PREFS_SAVED" ] && return
    local k v
    for k in "${BB_PREF_KEYS[@]}"; do
        v=$(defaults read $BB_PREFS_DOMAIN "$k" 2>/dev/null) && echo "$k|$v" || echo "$k|"
    done > "$BB_PREFS_SAVED"
}

prefs_windowed() {
    defaults write $BB_PREFS_DOMAIN FullScreen -int 0
    defaults write $BB_PREFS_DOMAIN ResolutionWidth -int 1400
    defaults write $BB_PREFS_DOMAIN ResolutionHeight -int 880
    defaults write $BB_PREFS_DOMAIN "Screenmanager Fullscreen mode" -int 3
    defaults write $BB_PREFS_DOMAIN "Screenmanager Resolution Use Native" -int 0
    defaults write $BB_PREFS_DOMAIN "Screenmanager Resolution Width" -int 1400
    defaults write $BB_PREFS_DOMAIN "Screenmanager Resolution Height" -int 880
}

prefs_restore() {
    [ -f "$BB_PREFS_SAVED" ] || return 0
    local k v
    while IFS='|' read -r k v; do
        if [ -z "$v" ]; then defaults delete $BB_PREFS_DOMAIN "$k" 2>/dev/null
        elif [[ "$v" =~ ^-?[0-9]+$ ]]; then defaults write $BB_PREFS_DOMAIN "$k" -int "$v"
        else defaults write $BB_PREFS_DOMAIN "$k" -string "$v"; fi
    done < "$BB_PREFS_SAVED"
    rm -f "$BB_PREFS_SAVED"
}

# ---- Mods: switching one off for a scenario, and always back on --------------

bb_mods_dir() { (cd "$HOME/Documents/Timberborn/Mods" && pwd -P); }

mod_disable() {
    local mods; mods=$(bb_mods_dir)
    [ -d "$mods/$1" ] || return 0
    mv "$mods/$1" "$mods/../$1.disabled-by-tests" && echo "$1" >> "$BB_TEST_DIR/disabled-mods"
}

mod_enable_all() {
    local mods name; mods=$(bb_mods_dir)
    [ -f "$BB_TEST_DIR/disabled-mods" ] || return 0
    while read -r name; do
        [ -d "$mods/../$name.disabled-by-tests" ] && [ ! -e "$mods/$name" ] && mv "$mods/../$name.disabled-by-tests" "$mods/$name"
    done < "$BB_TEST_DIR/disabled-mods"
    rm -f "$BB_TEST_DIR/disabled-mods"
}

mod_installed() { [ -d "$(bb_mods_dir)/$1" ]; }

# ---- Game copies ----------------------------------------------------------------

bb_log() { echo "$BB_TEST_DIR/$1.log"; }

# send <instance> <command...>: one harness command, once the last one's been taken
send() {
    local inst=$1 file n=0
    shift
    file="$BB_CMD_PREFIX-$inst"
    while [ -e "$file" ] && [ $n -lt 40 ]; do sleep 0.25; n=$((n + 1)); done
    echo "$*" > "$file"
}

# wait_for <instance> <regex> [seconds]
wait_for() {
    local inst=$1 re=$2 t=${3:-60} i
    for ((i = 0; i < t * 2; i++)); do
        grep -aEq -- "$re" "$(bb_log "$inst")" 2>/dev/null && return 0
        sleep 0.5
    done
    return 1
}

bb_launch() {
    bb_open "$1"
    wait_for "$1" "Registering Main Menu Services" 120
}

# bb_open <instance>: starts a copy without waiting for it, so two can load side by side
bb_open() {
    local inst=$1
    rm -f "$BB_CMD_PREFIX-$inst" "$(bb_log "$inst")"
    [ "$inst" == host ] && rm -f "$BB_CMD_PREFIX-window"
    prefs_windowed
    open -g -n -a "$BB_GAME" --env SteamAppId=1062090 --env SteamGameId=1062090 --env BB_INSTANCE="$inst" \
        --args -skipModManager -logFile "$(bb_log "$inst")" -screen-fullscreen 0 -screen-width 1400 -screen-height 880
}

# Whether the player's own Timberborn is running (not one of the test copies)
player_game_running() {
    local p
    for p in $(pgrep -x Timberborn); do
        ps eww -p "$p" 2>/dev/null | grep -q "BB_INSTANCE=" || return 0
    done
    return 1
}

# Closes the test copies, and only them: the player's own game isn't touched
bb_close() {
    local p
    for p in $(pgrep -x Timberborn); do
        ps eww -p "$p" 2>/dev/null | grep -q "BB_INSTANCE=" && kill -9 "$p" 2>/dev/null
    done
    rm -f "$BB_CMD_PREFIX-host" "$BB_CMD_PREFIX-client"
    local n=0
    while lsof -nP -iTCP -sTCP:LISTEN 2>/dev/null | grep -qi timberborn && [ $n -lt 40 ]; do sleep 1; n=$((n + 1)); done
}

# bb_kill <instance>: closes one test copy
bb_kill() {
    local p
    for p in $(pgrep -x Timberborn); do
        ps eww -p "$p" 2>/dev/null | grep -q "BB_INSTANCE=$1 " && kill -9 "$p" 2>/dev/null
    done
    sleep 3
}

# start_match <host faction> <client faction> [game mode id]: both copies in one
# match game, dialogs dismissed. Returns 1 if it never got there. BB_HOST_SETTINGS and
# BB_CLIENT_SETTINGS ("id=value ...") are each copy's picks for new games' settings
start_match() {
    local hostf=${1:-Folktails} clientf=${2:-IronTeeth} mode=${3:-} pick
    bb_close
    # One after the other: copies starting together race on the player's data file
    bb_launch host || return 1
    bb_launch client || return 1
    for pick in ${BB_HOST_SETTINGS:-}; do send host "gamesetting ${pick%%=*} ${pick#*=}"; done
    for pick in ${BB_CLIENT_SETTINGS:-}; do send client "gamesetting ${pick%%=*} ${pick#*=}"; done
    send host "hostmatch ${BB_MAP:-Waterfalls} $hostf $mode"
    wait_for host "Server started listening" 120 || return 1
    send client "joinmatch $clientf"
    local i
    for ((i = 0; i < 180; i++)); do
        grep -aq "Registering Co-op services" "$(bb_log host)" \
            && sed -n '/Registering Co-op/,$p' "$(bb_log client)" | grep -aq "Load time" \
            && sed -n '/Registering Co-op/,$p' "$(bb_log host)" | grep -aq "Load time" && break
        sleep 1
    done
    [ $i -lt 180 ] || return 1
    sleep 3
    send host dismiss; send client dismiss
    sleep 2
    return 0
}

# place_starts <host faction> <client faction>: each player's free District Center, where the map's open
place_starts() {
    send host "place DistrictCenter.$1 ${BB_HOST_START:-79 46 5}"
    sleep 3
    send client "place DistrictCenter.$2 ${BB_CLIENT_START:-89 68 5}"
    sleep 6
    send host dismiss; send client dismiss
    sleep 3
}

# ---- Asking the game ------------------------------------------------------------

# probe <instance> <key...>: what Timber Empires' harness probe says for "te <key>" on that copy
# (DevTools/HarnessProbe.cs), with player IDs shortened to P
probe() {
    local inst=$1 key=$2 log before after i
    log=$(bb_log "$inst")
    before=$(grep -aFc "[Test] te $key:" "$log")
    send "$inst" "te $key"
    for i in $(seq 1 60); do
        after=$(grep -aFc "[Test] te $key:" "$log")
        [ "$after" -gt "$before" ] && break
        sleep 0.25
    done
    grep -aF "[Test] te $key:" "$log" | tail -1 | sed "s/.*\[Test\] te $key: *//" | sed -E 's/[0-9a-f]{8}-[0-9a-f-]{27}/P/g'
}

# The last tick both copies logged, as "<tick> <host line> | <client line>" hashes
sync_state() {
    local h c n
    h=$(grep -aEo "Tick [0-9]+ IO done" "$(bb_log host)" | tail -1 | grep -oE "[0-9]+")
    c=$(grep -aEo "Tick [0-9]+ IO done" "$(bb_log client)" | tail -1 | grep -oE "[0-9]+")
    [ -n "$h" ] && [ -n "$c" ] || { echo "no ticks"; return 1; }
    n=$((10#$h < 10#$c ? 10#$h : 10#$c))
    local hh ch
    hh=$(grep -aE "Tick 0*$n IO done" "$(bb_log host)" | tail -1 | sed 's/.*IO done//')
    ch=$(grep -aE "Tick 0*$n IO done" "$(bb_log client)" | tail -1 | sed 's/.*IO done//')
    [ "$hh" == "$ch" ] && echo "tick $n in sync" || echo "tick $n DIFFERS: host$hh client$ch"
}

# shot <name> [instance]: a screenshot of that copy, kept with the report
shot() {
    send "${2:-host}" "snap $BB_TEST_DIR/shots/${BB_SCENARIO:-x}-$1.png"
    sleep 2
}

# ---- What a scenario expects -----------------------------------------------------

_record() { printf '%s\t%s\t%s\t%s\n' "$1" "${BB_SCENARIO:-?}" "$2" "$3" >> "$BB_RESULTS"; }

pass() { echo "  ok    $1"; _record PASS "$1" ""; }
fail() { echo "  FAIL  $1: $2"; _record FAIL "$1" "$2"; }

# expect_eq <name> <actual> <wanted>
expect_eq() { if [ "$2" == "$3" ]; then pass "$1"; else fail "$1" "got '$2', wanted '$3'"; fi; }

# expect_match <name> <actual> <regex>
expect_match() { if echo "$2" | grep -aEq -- "$3"; then pass "$1"; else fail "$1" "got '$2', wanted it to match '$3'"; fi; }

# expect_no_match <name> <actual> <regex>
expect_no_match() { if echo "$2" | grep -aEq -- "$3"; then fail "$1" "got '$2', wanted it not to match '$3'"; else pass "$1"; fi; }

# expect_nonempty <name> <value>: guards a comparison against passing because both sides are empty
expect_nonempty() { if [ -n "$2" ]; then pass "$1"; else fail "$1" "nothing came back"; fi; }

# expect_log <name> <instance> <regex>: the copy's log has such a line
expect_log() { if grep -aEq -- "$3" "$(bb_log "$2")"; then pass "$1"; else fail "$1" "no line like '$3' in the $2 log"; fi; }

# expect_no_log <name> <instance> <regex>
expect_no_log() {
    local line; line=$(grep -aE -- "$3" "$(bb_log "$2")" | head -1 | cut -c1-200)
    if [ -z "$line" ]; then pass "$1"; else fail "$1" "$2 log has: $line"; fi
}

# expect_in_sync <name>: both copies' state hashes agree at the last tick they share
expect_in_sync() {
    local s; s=$(sync_state)
    if echo "$s" | grep -q "in sync"; then pass "$1 ($s)"; else fail "$1" "$s"; fi
}

# expect_no_errors <name>: no exception in either log
expect_no_errors() {
    local e
    e=$(grep -aE "NullReferenceException|Exception:|desync|Desync" "$(bb_log host)" "$(bb_log client)" 2>/dev/null | grep -v "gpath.c" | head -2 | cut -c1-200)
    if [ -z "$e" ]; then pass "$1"; else fail "$1" "$e"; fi
}

# ---- The menus ---------------------------------------------------------------------

# new_game_page <instance> [host|match]: the New Game screens' last page, as a player gets there
# from the main menu to host a game (the default) or to find a match
new_game_page() {
    local button=HostNewGameMenuButton c
    [ "${2:-host}" == match ] && button=FindMatchMenuButton
    for c in "click MultiplayerButton" "click $button" "click NextButton" "click NextButton"; do
        send "$1" "$c"; sleep 2
    done
}

# layout_of <instance> <element name>: the element and its children as the harness lays them out
# (position, size, and the text of any label), one per line
layout_of() {
    local log L
    log=$(bb_log "$1")
    send "$1" "layout $2"; sleep 2
    L=$(grep -an "\[Test\] Layout $2:" "$log" | tail -1 | cut -d: -f1)
    [ -n "$L" ] && sed -n "$((L + 1)),\$p" "$log" | awk '/^\[[0-9]+-[0-9]+-[0-9]+\./ {exit} {print}'
}

# button_ys <layout>: the top of each button, comma separated
button_ys() { echo "$1" | grep NineSliceButton | sed -E 's/.* y=([0-9]+) .*/\1/' | paste -sd, -; }
