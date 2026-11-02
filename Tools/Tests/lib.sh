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
# The home the copies play in, a checkout's own (BB_HOME_NAME, its folder's name): everything in it links
# to the player's home but Documents/Timberborn/Mods, so a run builds its mods there and plays them,
# whatever the player's Mods folder holds, and another checkout's run never swaps them. The game finds
# its user folder from HOME. BB_HOME=$HOME plays from the player's own Mods folder as before
export BB_HOME=${BB_HOME:-"$HOME/Library/Caches/TimberbornHomes/${BB_HOME_NAME:-BeaverBuddies}"}
# How fast the copies play while a scenario waits on the game: 30x, as fast as two copies keep up on one Mac
BB_SPEED=${BB_SPEED:-30}
mkdir -p "$BB_TEST_DIR/shots"

# The game's display settings: windowed for the run, then the player's own back
# (and any other preference a mod's scenario changes: BB_PREF_KEYS_EXTRA, a space-free list separated by '|')
BB_PREF_KEYS=("FullScreen" "ResolutionWidth" "ResolutionHeight" "Screenmanager Fullscreen mode" \
    "Screenmanager Resolution Use Native" "Screenmanager Resolution Width" "Screenmanager Resolution Height" \
    "MasterVolume" "MusicVolume")
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
    defaults write $BB_PREFS_DOMAIN ResolutionWidth -int ${BB_RES_W:-1400}
    defaults write $BB_PREFS_DOMAIN ResolutionHeight -int ${BB_RES_H:-880}
    defaults write $BB_PREFS_DOMAIN "Screenmanager Fullscreen mode" -int 3
    defaults write $BB_PREFS_DOMAIN "Screenmanager Resolution Use Native" -int 0
    defaults write $BB_PREFS_DOMAIN "Screenmanager Resolution Width" -int ${BB_RES_W:-1400}
    defaults write $BB_PREFS_DOMAIN "Screenmanager Resolution Height" -int ${BB_RES_H:-880}
    # The copies run silent; BB_SOUND=1 leaves the player's volume, with the music off
    if [ -z "${BB_SOUND:-}" ]; then defaults write $BB_PREFS_DOMAIN MasterVolume -float 0; fi
    defaults write $BB_PREFS_DOMAIN MusicVolume -float 0
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

# The picks a new game starts from (BeaverBuddies.GameSetting.*, Timber Empires' among them): a scenario's
# gamesetting commands change them for the player's own games too. Saved with the rest, and put back after
# every scenario, so none leaks into the next one or into the player's games
BB_GAME_SETTINGS_SAVED="$BB_TEST_DIR/game-settings.saved"
game_settings() {
    defaults read $BB_PREFS_DOMAIN 2>/dev/null | sed -nE 's/^ *"?(BeaverBuddies\.GameSetting\.[^"= ]+)"? = .*/\1/p'
}

game_settings_save() {
    [ -f "$BB_GAME_SETTINGS_SAVED" ] && return
    local k
    for k in $(game_settings); do
        echo "$k|$(defaults read-type $BB_PREFS_DOMAIN "$k" | sed 's/^Type is //')|$(defaults read $BB_PREFS_DOMAIN "$k")"
    done > "$BB_GAME_SETTINGS_SAVED"
}

# keep: leave the saved file for a later restore (after a scenario), rather than the run's last
game_settings_restore() {
    [ -f "$BB_GAME_SETTINGS_SAVED" ] || return 0
    local k t v
    for k in $(game_settings); do grep -q "^$k|" "$BB_GAME_SETTINGS_SAVED" || defaults delete $BB_PREFS_DOMAIN "$k" 2>/dev/null; done
    while IFS='|' read -r k t v; do
        case "$t" in
            integer) defaults write $BB_PREFS_DOMAIN "$k" -int "$v" ;;
            float) defaults write $BB_PREFS_DOMAIN "$k" -float "$v" ;;
            *) defaults write $BB_PREFS_DOMAIN "$k" -string "$v" ;;
        esac
    done < "$BB_GAME_SETTINGS_SAVED"
    [ "${1:-}" == keep ] || rm -f "$BB_GAME_SETTINGS_SAVED"
}

# pref_set <key> <int>: a game preference for this scenario only (a mod's setting, say), the player's
# own put back when the scenario is over. BeaverBuddies' "always trace" is the one that matters:
# a scenario that is about logging being off, or on, sets it, whatever this Mac has it at.
BB_PREFS_OVERRIDES="$BB_TEST_DIR/prefs.overrides"
pref_set() {
    local k=$1 v=$2 old
    old=$(defaults read $BB_PREFS_DOMAIN "$k" 2>/dev/null) || old=""
    # Only the first change of a key remembers the player's own value
    grep -q "^$k|" "$BB_PREFS_OVERRIDES" 2>/dev/null || echo "$k|$old" >> "$BB_PREFS_OVERRIDES"
    defaults write $BB_PREFS_DOMAIN "$k" -int "$v"
}

prefs_restore_overrides() {
    [ -f "$BB_PREFS_OVERRIDES" ] || return 0
    local k v
    while IFS='|' read -r k v; do
        if [ -z "$v" ]; then defaults delete $BB_PREFS_DOMAIN "$k" 2>/dev/null
        elif [[ "$v" =~ ^-?[0-9]+$ ]]; then defaults write $BB_PREFS_DOMAIN "$k" -int "$v"
        else defaults write $BB_PREFS_DOMAIN "$k" -string "$v"; fi
    done < "$BB_PREFS_OVERRIDES"
    rm -f "$BB_PREFS_OVERRIDES"
}

# ---- Mods: switching one off for a scenario, and always back on --------------

bb_mods_dir() { (cd "$BB_HOME/Documents/Timberborn/Mods" && pwd -P); }

# The mods a run builds itself, kept in its home's Mods folder; the player's others are linked in
BB_OWN_MODS="GameModes BeaverBuddies TimberEmpires"

# _bb_link_all <from> <to> <names kept out...>: each entry of a folder linked into another, but those
_bb_link_all() {
    local from=$1 to=$2 e n
    shift 2
    mkdir -p "$to"
    for e in "$from"/* "$from"/.[!.]*; do
        [ -e "$e" ] || [ -L "$e" ] || continue
        n=$(basename "$e")
        case " $* " in *" $n "*) continue ;; esac
        [ -e "$to/$n" ] || [ -L "$to/$n" ] || ln -s "$e" "$to/$n"
    done
    # Links to what's gone from the player's folder since
    for e in "$to"/* "$to"/.[!.]*; do [ -L "$e" ] && [ ! -e "$e" ] && rm -f "$e"; done
    return 0
}

# bb_home_prepare: the run's home laid out, a link for everything of the player's but its own Mods folder
bb_home_prepare() {
    [ "$BB_HOME" == "$HOME" ] && return 0
    _bb_link_all "$HOME" "$BB_HOME" Documents
    _bb_link_all "$HOME/Documents" "$BB_HOME/Documents" Timberborn
    _bb_link_all "$HOME/Documents/Timberborn" "$BB_HOME/Documents/Timberborn" Mods
    # shellcheck disable=SC2086
    _bb_link_all "$HOME/Documents/Timberborn/Mods" "$BB_HOME/Documents/Timberborn/Mods" $BB_OWN_MODS
}

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

# bb_keep_log <instance>: that copy's log moved into this run's logs folder, named after the
# scenario and numbered, before anything starts a new one over it. A scenario run on its own
# keeps its logs in runs/single
bb_keep_log() {
    local log dir n=1
    log=$(bb_log "$1")
    [ -s "$log" ] || { [ -n "$(bb_pid "$1")" ] || rm -f "$log"; return 0; }
    dir="${BB_RUN_DIR:-$BB_TEST_DIR/runs/single}/logs"
    mkdir -p "$dir"
    while [ -e "$dir/${BB_SCENARIO:-x}-$1-$n.log" ]; do n=$((n + 1)); done
    # A copy still running (kept open for the next scenario, see TE_PARKED in te-lib.sh) goes on writing to its
    # file, so the log is copied and emptied instead of moved
    if [ -n "$(bb_pid "$1")" ]; then cp "$log" "$dir/${BB_SCENARIO:-x}-$1-$n.log" && : > "$log"
    else mv "$log" "$dir/${BB_SCENARIO:-x}-$1-$n.log"; fi
}

# send <instance> <command...>: one harness command, once the last one's been taken
send() {
    local inst=$1 file n=0
    shift
    file="$BB_CMD_PREFIX-$inst"
    while [ -e "$file" ] && [ $n -lt 200 ]; do sleep 0.05; n=$((n + 1)); done
    # A copy that hasn't taken the last one in 10 s is stuck: its command would be lost under this one
    [ -e "$file" ] && echo "  (warning: $inst hadn't taken \"$(head -1 "$file")\", sending \"$*\" after it)" >&2
    # Written whole and then moved in: the game reads and deletes the file as soon as it sees it,
    # and could otherwise read one half written, empty, and drop the command
    if [ -e "$file" ]; then echo "$*" >> "$file"; else echo "$*" > "$file.part" && mv "$file.part" "$file"; fi
}

# wait_for <instance> <regex> [seconds]: gives up early if that copy has closed
wait_for() {
    local inst=$1 re=$2 t=${3:-60} i
    for ((i = 0; i < t * 10; i++)); do
        grep -aEq -- "$re" "$(bb_log "$inst")" 2>/dev/null && return 0
        [ $((i % 10)) == 9 ] && [ -z "$(bb_pid "$inst")" ] && return 1
        sleep 0.5
    done
    return 1
}

# The first exception either copy's log shows: a mod that fails to load (a binding for a type the
# game binds differently, say) is there at once, and waiting on the game only hides it
bb_load_error() {
    local e
    e=$(grep -aE "[A-Za-z]Exception\b" "$(bb_log host)" "$(bb_log client)" 2>/dev/null | grep -v "gpath.c" | head -1 | cut -c1-300)
    [ -n "$e" ] && echo "$e"
}

# wait_for that gives up at once on an exception in either log
wait_for_watch() {
    local inst=$1 re=$2 t=${3:-60} i e
    for ((i = 0; i < t * 2; i++)); do
        grep -aEq -- "$re" "$(bb_log "$inst")" 2>/dev/null && return 0
        e=$(bb_load_error) && { echo "  load error: $e"; return 1; }
        sleep 0.5
    done
    return 1
}

# answer <instance> <command...>: runs a harness command and prints what the copy logged for it
# (the lines from the command's own on, once it's been run)
answer() {
    local inst=$1 log from i
    shift
    log=$(bb_log "$inst")
    from=$(($(wc -l < "$log") + 1))
    send "$inst" "$*"
    for ((i = 0; i < 100; i++)); do
        tail -n +"$from" "$log" | grep -aqF "[Test] $*" && break
        sleep 0.05
    done
    sleep 0.1
    tail -n +"$from" "$log"
}

# standing <instance> <x y z> <name> [seconds]: waits for that copy to have a building of that name there
standing() {
    local i
    for ((i = 0; i < ${4:-10} * 4; i++)); do
        answer "$1" "check $2" | grep -aqF "[Test] $3" && return 0
    done
    return 1
}

# Which mods play, as preference flags ("ModEnabled.Local.BeaverBuddies.beaverbuddies=1", one per line) a runner
# that builds mods hands over in BB_MOD_FLAGS: set before every launch, so a scenario that never says which
# mods it plays still gets the local builds. The run puts the player's own back (BB_PREF_KEYS_EXTRA)
bb_mod_flags() {
    local flag on
    while IFS='=' read -r flag on; do
        [ -n "$flag" ] && defaults write "$BB_PREFS_DOMAIN" "$flag" -int "$on"
    done <<< "${BB_MOD_FLAGS:-}"
}

# bb_mods_off <folders>: the local builds of these mods (a regex of their folder names, "TimberEmpires|Archipelago")
# off for the copies this scenario launches. A scenario runs in its own subshell, so the next one has them back
bb_mods_off() {
    BB_MOD_FLAGS=$(echo "$BB_MOD_FLAGS" | sed -E "s/^(ModEnabled\.Local\.($1)\.[a-z]+)=1$/\1=0/")
}

# ---- Running the game until something happens ------------------------------------

# The last tick each copy logged, "<host tick> <client tick>"
last_ticks() {
    local inst t
    for inst in host client; do
        t=$(tail -c 300000 "$(bb_log $inst)" | grep -aEo "Tick [0-9]+ IO done" | tail -1)
        [ -n "$t" ] || t=$(grep -aEo "Tick [0-9]+ IO done" "$(bb_log $inst)" | tail -1)
        echo -n "${t//[!0-9]/} "
    done
}

# pause_game: the game paused, once both copies stand still at the same tick
pause_game() {
    send host "speed 0"
    settle
}

# settle: waits for both copies to stop at the same tick (after a pause)
settle() {
    local now last="" same=0 i
    for ((i = 0; i < 150; i++)); do
        now=$(last_ticks)
        if [ "$now" == "$last" ] && echo "$now" | awk '{exit !(NF == 0 || $1 == $2)}'; then
            same=$((same + 1)); [ $same -ge 3 ] && return 0
        else
            same=0
        fi
        last=$now
        sleep 0.1
    done
    return 1
}

# run_until <instance> <probe key...> <regex> [seconds] [ticks]: the game played at $BB_SPEED until
# what Timber Empires' probe says for "te <key>" on that copy matches the regex (or for that many
# ticks at most), then paused on both copies. The copy asks every frame, so a passing moment is
# caught; the regex is .NET's, on the probe's own answer (player IDs not shortened). UNTIL is the
# answer that matched, or the last one when it didn't, and it returns 1
run_until() { _until "$1" "${5:+ticks $5 }$2 :: $3" "${4:-60}"; }

# run_ticks <n> [seconds]: the game played at $BB_SPEED for n ticks, then paused (an in-game hour is
# about 32 ticks)
run_ticks() { _until host "ticks $1" "${2:-$(($1 / 25 + 30))}"; }

_until() {
    local inst=$1 log n i re="\[Test\] until (matched|timed out|stopped):"
    log=$(bb_log "$inst")
    n=$(grep -acE "$re" "$log")
    send "$inst" "until speed $BB_SPEED $2"
    for ((i = 0; i < $3 * 10; i++)); do
        [ "$(grep -acE "$re" "$log")" -gt "$n" ] && break
        sleep 0.1
    done
    if [ "$(grep -acE "$re" "$log")" -le "$n" ]; then
        send "$inst" "until off"
        send host "speed 0"
        for ((i = 0; i < 50; i++)); do [ "$(grep -acE "$re" "$log")" -gt "$n" ] && break; sleep 0.1; done
    fi
    settle
    UNTIL=$(grep -aE "$re" "$log" | tail -1)
    local result; result=$(echo "$UNTIL" | sed -E 's/.*\[Test\] until (matched|timed out|stopped):.*/\1/')
    UNTIL=$(echo "$UNTIL" | sed -E "s/.*$re *//" | sed -E 's/[0-9a-f]{8}-[0-9a-f-]{27}/P/g')
    [ "$result" == matched ]
}

# run_until_log <instance> <regex> <count> [seconds]: the game played at $BB_SPEED until that copy's
# log has more than count lines matching the regex, then paused (a few ticks after: the log is read
# from here)
run_until_log() {
    local log i ok=1
    log=$(bb_log "$1")
    send host "speed $BB_SPEED"
    for ((i = 0; i < ${4:-60} * 10; i++)); do
        [ "$(grep -acE -- "$2" "$log")" -gt "$3" ] && { ok=0; break; }
        sleep 0.1
    done
    pause_game
    return $ok
}

bb_launch() {
    bb_mod_flags
    bb_open "$1"
    wait_for_watch "$1" "Registering Main Menu Services" 120
}

# The pid of a test copy
bb_pid() {
    local p
    for p in $(pgrep -x Timberborn); do
        ps eww -p "$p" 2>/dev/null | grep -q "BB_INSTANCE=$1\b" && { echo "$p"; return; }
    done
}

# The guest's window put right above the host's, which stays wherever it is unless there's no
# room above it: then the host's goes down to make some, under the menu bar. Through macOS,
# which knows where the windows are (the game reports 0,0)
bb_stack_windows() {
    local host client at i
    host=$(bb_pid host); client=$(bb_pid client)
    [ -n "$host" ] && [ -n "$client" ] || return 0
    for i in 1 2 3 4 5 6 7 8 9 10; do
        at=$(osascript -e "tell application \"System Events\" to tell (first process whose unix id is $host) to get (position of window 1) & (size of window 1)" 2>/dev/null)
        [ -n "$at" ] && break
        sleep 0.5
    done
    [ -n "$at" ] || return 0
    IFS=', ' read -r x y _ h <<< "$at"
    local top=25
    if [ $((y - h)) -lt $top ]; then
        y=$((top + h))
        osascript -e "tell application \"System Events\" to tell (first process whose unix id is $host) to set position of window 1 to {$x, $y}" >/dev/null 2>&1 || true
    fi
    osascript -e "tell application \"System Events\" to tell (first process whose unix id is $client) to set position of window 1 to {$x, $((y - h))}" >/dev/null 2>&1 || true
}

# bb_open <instance>: starts a copy without waiting for it, so two can load side by side
bb_open() {
    local inst=$1
    rm -f "$BB_CMD_PREFIX-$inst"
    bb_keep_log "$inst"
    prefs_windowed
    bb_home_prepare
    open -g -n -a "$BB_GAME" --env SteamAppId=1062090 --env SteamGameId=1062090 --env BB_INSTANCE="$inst" --env HOME="$BB_HOME" \
        ${BB_RTC_CODE:+--env BB_RTC_CODE="$BB_RTC_CODE"} \
        ${BB_HITCH_MS:+--env BB_HITCH_MS="$BB_HITCH_MS"} ${BB_HITCH_TRACE:+--env BB_HITCH_TRACE=1} ${BB_HITCH_GARBAGE:+--env BB_HITCH_GARBAGE=1} \
        ${BB_LEAK_ROOTS:+--env BB_LEAK_ROOTS="$BB_LEAK_ROOTS"} ${BB_LEAK_SIZES:+--env BB_LEAK_SIZES=1} ${BB_LEAK_WALK:+--env BB_LEAK_WALK=1} \
        --args -skipModManager -logFile "$(bb_log "$inst")" -screen-fullscreen 0 -screen-width ${BB_RES_W:-1400} -screen-height ${BB_RES_H:-880}
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
    rm -f "$BB_CMD_PREFIX-host" "$BB_CMD_PREFIX-client" "$BB_CMD_PREFIX-third" "$BB_TEST_DIR/.parked"
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
    local hostf=${1:-Folktails} clientf=${2:-IronTeeth} mode=${3:-} pick e
    bb_close
    # One after the other: copies starting together race on the player's data file
    bb_launch host || return 1
    bb_launch client || return 1
    bb_stack_windows
    for pick in ${BB_HOST_SETTINGS:-}; do send host "gamesetting ${pick%%=*} ${pick#*=}"; done
    for pick in ${BB_CLIENT_SETTINGS:-}; do send client "gamesetting ${pick%%=*} ${pick#*=}"; done
    # BB_HOST_DIFFICULTY (Easy, Hard) is the game's difficulty, Normal otherwise
    [ -n "${BB_HOST_DIFFICULTY:-}" ] && send host "difficulty $BB_HOST_DIFFICULTY"
    send host "hostmatch ${BB_MAP:-Waterfalls} $hostf $mode"
    wait_for_watch host "Server started listening" 120 || return 1
    # BB_JOIN_ADDRESS joins some other way than 127.0.0.1, rtc:CODE over WebRTC say
    send client "joinmatch $clientf ${BB_JOIN_ADDRESS:-}"
    local i
    for ((i = 0; i < 360; i++)); do
        grep -aq "Registering Co-op services" "$(bb_log host)" \
            && sed -n '/Registering Co-op/,$p' "$(bb_log client)" | grep -aq "Load time" \
            && sed -n '/Registering Co-op/,$p' "$(bb_log host)" | grep -aq "Load time" && break
        e=$(bb_load_error) && { echo "  load error: $e"; return 1; }
        sleep 1
    done
    [ $i -lt 360 ] || return 1
    sleep 3
    send host dismiss; send client dismiss
    sleep 1
    return 0
}

# rematch <host faction> <client faction>: both copies exit to the main menu, the host with the exit save
# and the client without, so state a save leaves behind differs between them,
# and start a new match without restarting, so whatever the last one left behind is still there.
# Returns 1 if it never got there
rematch() {
    local hostf=${1:-Folktails} clientf=${2:-IronTeeth} c i n_host n_client
    n_host=$(grep -ac "Registering Main Menu Services" "$(bb_log host)")
    n_client=$(grep -ac "Registering Main Menu Services" "$(bb_log client)")
    send client "exitmenu nosave"; send host "exitmenu"
    for ((i = 0; i < 240; i++)); do
        [ "$(grep -ac "Registering Main Menu Services" "$(bb_log host)")" -gt "$n_host" ] \
            && [ "$(grep -ac "Registering Main Menu Services" "$(bb_log client)")" -gt "$n_client" ] && break
        sleep 0.5
    done
    [ $i -lt 240 ] || return 1
    sleep 3
    n_host=$(grep -ac "Load time" "$(bb_log host)"); n_client=$(grep -ac "Load time" "$(bb_log client)")
    c=$(grep -ac "Server started listening" "$(bb_log host)")
    send host "hostmatch ${BB_MAP:-Waterfalls} $hostf"
    for ((i = 0; i < 240; i++)); do [ "$(grep -ac "Server started listening" "$(bb_log host)")" -gt "$c" ] && break; sleep 0.5; done
    [ $i -lt 240 ] || return 1
    send client "joinmatch $clientf"
    for ((i = 0; i < 180; i++)); do
        [ "$(grep -ac "Load time" "$(bb_log host)")" -gt "$n_host" ] && [ "$(grep -ac "Load time" "$(bb_log client)")" -gt "$n_client" ] && break
        sleep 1
    done
    [ $i -lt 180 ] || return 1
    sleep 3
    send host dismiss; send client dismiss
    sleep 2
}

# place_starts <host faction> <client faction>: each player's free District Center, where the map's open
place_starts() {
    local host_at=${BB_HOST_START:-79 46 5} client_at=${BB_CLIENT_START:-89 68 5}
    send host "place DistrictCenter.$1 $host_at"
    standing client "$host_at" "DistrictCenter.$1"
    send client "place DistrictCenter.$2 $client_at"
    standing host "$client_at" "DistrictCenter.$2"
    standing client "$client_at" "DistrictCenter.$2"
    send host dismiss; send client dismiss
    sleep 1
}

# ---- Asking the game ------------------------------------------------------------

# probe <instance> <key...>: what Timber Empires' harness probe says for "te <key>" on that copy
# (DevTools/HarnessProbe.cs), with player IDs shortened to P
probe() {
    local inst=$1 key=$2 log before after i
    log=$(bb_log "$inst")
    before=$(grep -aFc -e "[Test] te $key:" -e "[Test] te $key failed:" "$log")
    send "$inst" "te $key"
    for ((i = 0; i < 300; i++)); do
        after=$(grep -aFc -e "[Test] te $key:" -e "[Test] te $key failed:" "$log")
        [ "$after" -gt "$before" ] && break
        sleep 0.05
    done
    grep -aF "[Test] te $key:" "$log" | tail -1 | sed "s/.*\[Test\] te $key: *//" | sed -E 's/[0-9a-f]{8}-[0-9a-f-]{27}/P/g'
}

# The last tick both copies logged, as "<tick> <host line> | <client line>" hashes
sync_state() {
    local h c n
    h=$(grep -aEo "Tick [0-9]+ IO done" "$(bb_log host)" | tail -1 | grep -oE "[0-9]+")
    c=$(grep -aEo "Tick [0-9]+ IO done" "$(bb_log client)" | tail -1 | grep -oE "[0-9]+")
    [ -n "$h" ] && [ -n "$c" ] || { echo "no ticks"; return 1; }
    # The tick before the last both logged: a tick's line goes out as it starts, and events played while
    # paused land after the host's line but can reach the guest before its own, so the last one's can differ
    n=$((10#$h < 10#$c ? 10#$h : 10#$c))
    n=$((n - 1))
    local hh ch
    hh=$(grep -aE "Tick 0*$n IO done" "$(bb_log host)" | tail -1 | sed 's/.*IO done//')
    ch=$(grep -aE "Tick 0*$n IO done" "$(bb_log client)" | tail -1 | sed 's/.*IO done//')
    [ "$hh" == "$ch" ] && echo "tick $n in sync" || echo "tick $n DIFFERS: host$hh client$ch"
}

# shot <name> [instance]: a screenshot of that copy, kept with the report
shot() {
    local file="$BB_TEST_DIR/shots/${BB_SCENARIO:-x}-$1.png" i
    rm -f "$file"
    send "${2:-host}" "snap $file"
    for ((i = 0; i < 30; i++)); do [ -s "$file" ] && break; sleep 0.1; done
    sleep 0.2
}

# ---- What a scenario expects -----------------------------------------------------

_record() { printf '%s\t%s\t%s\t%s\n' "$1" "${BB_SCENARIO:-?}" "$2" "$3" >> "$BB_RESULTS"; }

pass() { echo "  ok    $1"; _record PASS "$1" ""; }
# A mod's own tests can define bb_on_fail <check> to keep more of the copies' state at a failure
fail() { echo "  FAIL  $1: $2"; _record FAIL "$1" "$2"; declare -F bb_on_fail > /dev/null && bb_on_fail "$1"; return 0; }

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

# expect_in_sync <name>: both copies' state hashes agree at the last whole tick they share
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
