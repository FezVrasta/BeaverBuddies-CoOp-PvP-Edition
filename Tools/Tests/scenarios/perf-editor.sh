# The map editor on PerfEditor512 (in the Maps folder), paused and then at its top speed, with where each frame goes
export BB_HITCH_MS=${BB_HITCH_MS:-250}
secs=${PERF_VARIANT_SECONDS:-30}
bb_close
bb_launch host || { fail "the game starts" "no main menu"; return 1; }
send host "editmap user:${PERF_MAP:-PerfEditor512}"
wait_for_watch host "Load time" 300 || { fail "the map loads" "never loaded"; return 1; }
sleep 10
for speed in 0 ${PERF_SPEEDS:-3}; do
    send host "speed $speed"
    sleep "$secs"
    echo "  == speed $speed"
    grep -a '\[Pace\] tick' "$(bb_log host)" | tail -1 | sed 's/^/    /'
    grep -a '\[Pace\] time' "$(bb_log host)" | tail -1 | sed 's/.*totals: //' | tr ',;' '\n\n' | head -20 | sed 's/^/      /'
    shot "editor-speed-$speed" host
done
pass "the editor runs"
bb_close
