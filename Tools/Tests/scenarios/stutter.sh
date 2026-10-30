# Profiles a BeaverBuddies-only co-op game at the speeds people play at: every frame over
# BB_HITCH_MS (50 here) is a [Hitch] line in each copy's log saying where it went, and every 10 s
# a [Pace] line says how the frames went. The guest keeps pace with the host without stopping to
# wait for its ticks, which shows as the game stuttering.
if mod_installed TimberEmpires; then mod_disable TimberEmpires; fi
[ -n "${BB_STUTTER_NOSTEAM:-}" ] && pref_set ModSetting.beaverbuddies.Settings.EnableSteamConnection 0
# Tracing (a developer setting) costs megabytes of garbage a tick: off, as in play, unless asked for
pref_set ModSetting.beaverbuddies.Settings.AlwaysTrace ${BB_STUTTER_TRACE:-0}
export BB_HITCH_MS=${BB_HITCH_MS:-50}
start_match Folktails Folktails || { fail "a match starts" "the copies never got into the game"; return; }
pass "a match starts"
for s in ${BB_STUTTER_SPEEDS:-1 2 3}; do send host "speed $s"; sleep ${BB_STUTTER_SECONDS:-60}; done
for i in host client; do
    echo "  $i: $(grep -c "\[Hitch\]" "$(bb_log $i)") hitches"
    grep -aE "\[(Hitch|Pace)\]" "$(bb_log $i)" | sed 's/.*\[/    [/' | cut -c1-260 | tail -40
done
# The first two stretches are the load and the first ticks
stalls=$(grep -aoE "\[Pace\] tick [0-9]+ speed [1-9].* ([0-9]+) stalled" "$(bb_log client)" | tail -n +3 | grep -oE "[0-9]+ stalled" | awk '{s += $1} END {print s + 0}')
if [ "$stalls" -le 5 ]; then pass "the guest doesn't stop for the host's ticks ($stalls frames)"; else fail "the guest doesn't stop for the host's ticks" "$stalls frames stalled"; fi
expect_in_sync "both copies in sync"
expect_no_errors "no errors"
