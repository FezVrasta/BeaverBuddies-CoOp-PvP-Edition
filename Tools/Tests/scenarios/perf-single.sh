# The big save loaded alone, as the Load game screen does, not hosted: paused, then at speed 3
file=${PERF_SAVE_FILE:-"$BB_SCENARIO_DIR/../saves/traveling-beaverburys-72.timber"}
name=$(basename "$file" .timber)
saves="$HOME/Documents/Timberborn/Saves/Perf"
mkdir -p "$saves" && cp "$file" "$saves/$name.timber"
export BB_HITCH_MS=${BB_HITCH_MS:-250}
secs=${PERF_VARIANT_SECONDS:-30}
bb_close
bb_launch host || { fail "the game starts" "no main menu"; return 1; }
send host "loadsave Perf|$name"
wait_for_watch host "Load time" 300 || { fail "the save loads" "never loaded"; return 1; }
sleep 10
send host dismiss
send host "speed 0"
# PERF_FRONT=1 brings the copy to the front, as a game being played is: a window nobody can see isn't composited
if [ -n "${PERF_FRONT:-}" ]; then
    pid=$(bb_pid host)
    osascript -e "tell application \"System Events\" to set frontmost of (first process whose unix id is $pid) to true" >/dev/null 2>&1
fi
sleep "$secs"

report() {
    local log pace time
    log=$(bb_log host)
    pace=$(grep -a '\[Pace\] tick' "$log" | tail -1)
    time=$(grep -a '\[Pace\] time' "$log" | tail -1)
    printf '  %-22s avg %s ms, ticking %s ms | %s\n' "$1" \
        "$(echo "$pace" | sed -E 's/.* frames, avg ([0-9.]+) ms.*/\1/')" \
        "$(echo "$pace" | sed -E 's/.*ticking avg ([0-9.]+) ms.*/\1/')" \
        "$(echo "$time" | sed -E 's/.*(cpu main [0-9.]+ ms, render thread [0-9.]+ ms, gpu [0-9.]+ ms).*/\1/')"
}
report "single player, paused"
shot "single-paused" host
echo "    where the paused time goes:"
grep -a '\[Pace\] time' "$(bb_log host)" | tail -1 | sed 's/.*totals: //' | tr ',;' '\n\n' | head -14 | sed 's/^/      /'
send host "speed 3"
sleep "$secs"
report "single player, speed 3"
grep -a '\[Pace\] time' "$(bb_log host)" | tail -1 | sed 's/.*totals: //' | tr ',;' '\n\n' | head -14 | sed 's/^/      /'
bb_close
