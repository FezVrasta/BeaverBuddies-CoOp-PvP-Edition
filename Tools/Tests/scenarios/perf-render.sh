# What each part of the picture costs on a big save at speed 3: one window of frames per variant, with
# shadows, post-processing, the UI, the animated models, everything the cameras draw, or the resolution switched off.
# PERF_SAVE_FILE picks another .timber, PERF_VARIANT_SECONDS how long each variant plays (25 if unset)
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
sleep 30

# The last full window: pace and where the main thread's time went
report() {
    local log pace time
    log=$(bb_log host)
    pace=$(grep -a '\[Pace\] tick' "$log" | tail -1)
    time=$(grep -a '\[Pace\] time' "$log" | tail -1)
    printf '  %-26s avg %s ms, ticking %s ms | %s\n' "$1" \
        "$(echo "$pace" | sed -E 's/.* frames, avg ([0-9.]+) ms.*/\1/')" \
        "$(echo "$pace" | sed -E 's/.*ticking avg ([0-9.]+) ms.*/\1/')" \
        "$(echo "$time" | sed -E 's/.*(cpu main [0-9.]+ ms, render thread [0-9.]+ ms, gpu [0-9.]+ ms).*/\1/')"
}

variant() {
    local label=$1 on=$2 off=$3
    [ -n "$off" ] && for c in "$off"; do send host "$c"; done
    sleep "$secs"
    report "$label"
    [ -n "$on" ] && for c in "$on"; do send host "$c"; done
}

variant "baseline" "" ""
log=$(bb_log host)
echo "  where the behavior manager's time goes (baseline window):"
grep -a '\[Pace\] time' "$log" | tail -1 | grep -oE '(behavior|executor) [A-Za-z0-9_<>]+ [0-9]+ ms/[0-9]+' | sed 's/^/    /' | head -16
variant "shadows off" "render shadows on" "render shadows off"
variant "post-processing off" "render post on" "render post off"
variant "UI hidden" "render ui on" "render ui off"
variant "animated models hidden" "render animated on" "render animated off"
variant "half resolution" "render res on" "render res off 1728 1084"
send host "render shadows off"; send host "render post off"; sleep "$secs"; report "shadows + post off"
send host "render shadows on"; send host "render post on"
variant "nothing drawn (cull)" "render cull on" "render cull off"
variant "baseline again" "" ""

log=$(bb_log host)
grep -a '\[Render\]' "$log" | sed 's/^/    /'
echo "  entity ticks:"; grep -a '\[Entities\]' "$log" | tail -2 | sed 's/^/    /'
send host "speed 0"
expect_no_errors "no errors"
bb_close
