# Frame times on the big save while paused, where the camera is all that moves.
# PERF_SAVE_FILE picks another .timber, PERF_MINUTES how long it sits paused (2 if unset)
file=${PERF_SAVE_FILE:-"$BB_SCENARIO_DIR/../saves/traveling-beaverburys-72.timber"}
name=$(basename "$file" .timber)
saves="$HOME/Documents/Timberborn/Saves/Perf"
mkdir -p "$saves" && cp "$file" "$saves/$name.timber"
export BB_HITCH_MS=${BB_HITCH_MS:-250}
bb_close
bb_launch host || { fail "the game starts" "no main menu"; return 1; }
send host "hostsave Perf|$name"
wait_for_watch host "Load time" 300 || { fail "the save loads" "never loaded"; return 1; }
sleep 10
send host dismiss
send host "speed 0"
sleep $((${PERF_MINUTES:-2} * 60))
log=$(bb_log host)
paces=$(grep -a '\[Pace\] tick' "$log" | tail -n +3)
echo "$paces" | sed 's/^/    /' | tail -4
grep -a '\[Pace\] time' "$log" | tail -2 | sed 's/^/    /'
expect_no_errors "no errors"
bb_close
