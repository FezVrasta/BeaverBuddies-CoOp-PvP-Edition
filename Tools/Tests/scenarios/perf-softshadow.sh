# What the sun's soft shadow settings cost, and whether they change the picture: the game paused so only drawing differs,
# vsync off so frames aren't held to the display, a screenshot of the same frozen scene for each
file=${PERF_SAVE_FILE:-"$BB_SCENARIO_DIR/../saves/traveling-beaverburys-72.timber"}
name=$(basename "$file" .timber)
saves="$HOME/Documents/Timberborn/Saves/Perf"
mkdir -p "$saves" && cp "$file" "$saves/$name.timber"
export BB_HITCH_MS=${BB_HITCH_MS:-250}
secs=${PERF_VARIANT_SECONDS:-12}
bb_close
bb_launch host || { fail "the game starts" "no main menu"; return 1; }
send host "hostsave Perf|$name"
wait_for_watch host "Load time" 300 || { fail "the save loads" "never loaded"; return 1; }
sleep 10
send host dismiss
send host "speed 0"
send host "vsync off"
send host "sun dump"
sleep 15

report() {
    local log pace time
    log=$(bb_log host)
    pace=$(grep -a '\[Pace\] tick' "$log" | tail -1)
    time=$(grep -a '\[Pace\] time' "$log" | tail -1)
    printf '  %-34s avg %s ms | %s\n' "$1" \
        "$(echo "$pace" | sed -E 's/.* frames, avg ([0-9.]+) ms.*/\1/')" \
        "$(echo "$time" | sed -E 's/.*(cpu main [0-9.]+ ms, render thread [0-9.]+ ms, gpu [0-9.]+ ms).*/\1/')"
}

# try <label> <file name> <sun commands...>
try() {
    local label=$1 pic=$2; shift 2
    local c
    for c in "$@"; do send host "sun $c"; done
    sleep "$secs"
    report "$label"
    shot "$pic" host
    for c in "$@"; do send host "sun ${c%% *} restore"; done
}

sleep "$secs"; report "baseline"; shot "soft-baseline" host
try "baseline (again)" "soft-baseline2"
try "soft quality low" "soft-low" "m_SoftShadowQuality Low"
try "soft quality medium" "soft-medium" "m_SoftShadowQuality Medium"
try "soft quality high" "soft-high" "m_SoftShadowQuality High"
try "soft shadows off" "soft-off" "m_SoftShadowsSupported False"
try "shadow map quarter" "soft-quarter" "m_MainLightShadowmapResolution 1024"
try "shadow distance x0.5" "soft-dist" "m_ShadowDistance x0.5"
send host "render shadows off"; sleep "$secs"; report "no shadows at all"; shot "soft-none" host
grep -a '\[Sun\]' "$(bb_log host)" | sed 's/^/    /' | cut -c1-1400
send host "vsync on"
bb_close
