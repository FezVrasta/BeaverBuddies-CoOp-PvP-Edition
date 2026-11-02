# Micro-benchmarks on the loaded save's own animated models (see RenderToggles.Bench)
file=${PERF_SAVE_FILE:-"$BB_SCENARIO_DIR/../saves/traveling-beaverburys-72.timber"}
name=$(basename "$file" .timber)
saves="$HOME/Documents/Timberborn/Saves/Perf"
mkdir -p "$saves" && cp "$file" "$saves/$name.timber"
bb_close
bb_launch host || { fail "the game starts" "no main menu"; return 1; }
send host "hostsave Perf|$name"
wait_for_watch host "Load time" 300 || { fail "the save loads" "never loaded"; return 1; }
sleep 10
send host dismiss
send host "speed 0"
sleep 5
send host bench
wait_for_watch host "per call, microseconds" 120 || { fail "the bench finishes" "no result"; return 1; }
grep -a '\[Bench\]' "$(bb_log host)" | sed 's/^/    /'
bb_close
