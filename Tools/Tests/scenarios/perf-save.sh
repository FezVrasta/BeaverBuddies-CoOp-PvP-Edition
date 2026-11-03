# Frame times on a big late-game save, hosted alone at speed 3, with our optimizations and then without.
# PERF_SAVE_FILE picks another .timber, PERF_MINUTES how long each half plays (2 if unset)
file=${PERF_SAVE_FILE:-"$BB_SCENARIO_DIR/../saves/castori-di-montagna-fabri.timber"}
name=$(basename "$file" .timber)
saves="$HOME/Documents/Timberborn/Saves/Perf"
mkdir -p "$saves" && cp "$file" "$saves/$name.timber"
noperf="$BB_HOME/Documents/Timberborn/BeaverBuddies-noperf"
export BB_HITCH_MS=${BB_HITCH_MS:-250}
# PERF_PREFS="ShadowQuality=0 LightQuality=0" sets game preferences for the run, the player's own put back after
for kv in ${PERF_PREFS:-}; do pref_set "${kv%%=*}" "${kv#*=}"; done

play() {
    bb_close
    bb_launch host || { fail "the game starts ($1)" "no main menu"; return 1; }
    send host "hostsave Perf|$name"
    wait_for_watch host "Load time" 300 || { fail "the save loads ($1)" "never loaded"; return 1; }
    sleep 10
    send host dismiss
    local log speed; log=$(bb_log host)
    echo "  $1: $(grep -a 'Load time' "$log" | tail -1 | sed 's/.*Load time/load time/')"
    # PERF_SPEEDS="0 1 3" plays each speed in turn (3 if unset), each for PERF_MINUTES
    for speed in ${PERF_SPEEDS:-3}; do
        local before; before=$(grep -ac '\[Pace\] tick' "$log")
        send host "speed $speed"
        # Halfway: the game's own FPS counter (bottom right) as the window shows it
        sleep $((${PERF_MINUTES:-2} * 30))
        shot "perf-fps-${1// /-}-speed-$speed" host
        sleep $((${PERF_MINUTES:-2} * 30))
        # The first window after a change has the last speed (or the load) in it
        local paces; paces=$(grep -a '\[Pace\] tick' "$log" | tail -n +$((before + 2)))
        echo "  $1, speed $speed: $(echo "$paces" | grep -c .) windows of 10 s"
        echo "$paces" | sed 's/^/    /' | tail -3
        echo "$paces" | awk -v what="$1, speed $speed" '{for (i = 1; i <= NF; i++) { if ($i == "avg" && $(i-1) ~ /frames,/) { s += $(i+1); n++ } if ($i == "ticking") t += $(i+2) } }
            END { if (n) printf "  %s: avg frame %.1f ms (%.0f fps), ticking %.1f ms\n", what, s / n, 1000 / (s / n), t / n }'
    done
    send host "speed 0"
    expect_no_errors "no errors ($1)"
}

# PERF_ONLY=on plays just the optimized half; PERF_ORDER=off-first plays without them first, to rule out the order
if [ "${PERF_ONLY:-}" = on ]; then
    rm -f "$noperf"; play "optimizations on"
elif [ "${PERF_ORDER:-}" = off-first ]; then
    touch "$noperf"; play "optimizations off" || return
    rm -f "$noperf"; play "optimizations on"
else
    rm -f "$noperf"; play "optimizations on" || return
    touch "$noperf"; play "optimizations off"
fi
rm -f "$noperf"
bb_close
