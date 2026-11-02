# The game's lights at day and at night: what they are, and what their shadows cost on a big save at speed 3
file=${PERF_SAVE_FILE:-"$BB_SCENARIO_DIR/../saves/traveling-beaverburys-72.timber"}
name=$(basename "$file" .timber)
saves="$HOME/Documents/Timberborn/Saves/Perf"
mkdir -p "$saves" && cp "$file" "$saves/$name.timber"
export BB_HITCH_MS=${BB_HITCH_MS:-250}
secs=${PERF_VARIANT_SECONDS:-25}
bb_close
bb_launch host || { fail "the game starts" "no main menu"; return 1; }
send host "hostsave Perf|$name"
wait_for_watch host "Load time" 300 || { fail "the save loads" "never loaded"; return 1; }
sleep 10
send host dismiss
send host "speed 3"
sleep 25

report() {
    local log pace time
    log=$(bb_log host)
    pace=$(grep -a '\[Pace\] tick' "$log" | tail -1)
    time=$(grep -a '\[Pace\] time' "$log" | tail -1)
    printf '  %-28s avg %s ms, ticking %s ms | %s\n' "$1" \
        "$(echo "$pace" | sed -E 's/.* frames, avg ([0-9.]+) ms.*/\1/')" \
        "$(echo "$pace" | sed -E 's/.*ticking avg ([0-9.]+) ms.*/\1/')" \
        "$(echo "$time" | sed -E 's/.*(cpu main [0-9.]+ ms, render thread [0-9.]+ ms, gpu [0-9.]+ ms).*/\1/')"
}

phase() {
    send host lights
    sleep 1
    sleep "$secs"; report "$1: shadows as the game has"
    send host "render lampshadows off"; sleep "$secs"; report "$1: lamp shadows off"
    send host "render lampshadows on"
    send host "render shadows off"; sleep "$secs"; report "$1: all shadows off"
    send host "render shadows on"
}

phase "day"
send host tonight
sleep 3
phase "night"
grep -a '\[Lights\]' "$(bb_log host)" | sed 's/^/    /' | cut -c1-260
send host "speed 0"
bb_close
