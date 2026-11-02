# What the sun's shadow settings cost on a big save at speed 3: the render pipeline asset's shadow fields, one at a time
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
send host "sun dump"
sleep 25

report() {
    local log pace time
    log=$(bb_log host)
    pace=$(grep -a '\[Pace\] tick' "$log" | tail -1)
    time=$(grep -a '\[Pace\] time' "$log" | tail -1)
    printf '  %-34s avg %s ms, ticking %s ms | %s\n' "$1" \
        "$(echo "$pace" | sed -E 's/.* frames, avg ([0-9.]+) ms.*/\1/')" \
        "$(echo "$pace" | sed -E 's/.*ticking avg ([0-9.]+) ms.*/\1/')" \
        "$(echo "$time" | sed -E 's/.*(cpu main [0-9.]+ ms, render thread [0-9.]+ ms, gpu [0-9.]+ ms).*/\1/')"
}

# try <label> <field> <value>
try() {
    send host "sun $2 $3"
    sleep "$secs"
    report "$1"
    send host "sun $2 restore"
}

sleep "$secs"; report "baseline"
try "cascades 2" m_ShadowCascadeCount 2
try "cascades 1" m_ShadowCascadeCount 1
try "shadow map half" m_MainLightShadowmapResolution 2048
try "shadow map quarter" m_MainLightShadowmapResolution 1024
try "soft shadows off" m_SoftShadowsSupported False
try "shadow distance x0.5" m_ShadowDistance x0.5
try "shadow distance x0.25" m_ShadowDistance x0.25
send host "render shadows off"; sleep "$secs"; report "no shadows at all"
send host "render shadows on"; sleep "$secs"; report "baseline again"
grep -a '\[Sun\]' "$(bb_log host)" | sed 's/^/    /' | cut -c1-900
send host "speed 0"
bb_close
