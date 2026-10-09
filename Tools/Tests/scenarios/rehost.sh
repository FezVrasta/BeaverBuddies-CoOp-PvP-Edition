# Rehosting, again and again: a player desyncs, the host saves and hosts the game again, the
# player reconnects, and the game plays on in step from where it was. Three rounds, at speed.
# Then a player who comes back before the game first runs, and the host hosting a game nobody
# has unpaused yet a second time.

# How many times a line is in a copy's log
count() { grep -acE -- "$2" "$(bb_log "$1")"; }

# Waits for a copy's count of a line to pass what it was
wait_more() { # copy pattern before seconds
    local i
    for ((i = 0; i < ${4:-90} * 2; i++)); do [ "$(count "$1" "$2")" -gt "$3" ] && return 0; sleep 0.5; done
    return 1
}

# The host's play button and the box asking if it's time (nobody can join after)
play() { send host "play 1"; sleep 2; send host dismiss; sleep 1; send host "speed $BB_SPEED"; }

# One round: a desync, the host's Save and Rehost, the player's Reconnect, the game on again
rehost_round() { # label
    local label=$1 hosts loads ticks
    hosts=$(count host "Server started listening"); loads=$(count client "Load time")
    send client "desync"; sleep 3
    shot "$label-desynced" host
    # Save and Rehost on the host, Reconnect on the player: the boxes' confirm buttons
    send host dismiss
    wait_more host "Server started listening" "$hosts" 60 || { fail "$label: the host hosts again" "no new server"; return 1; }
    sleep 2
    send client dismiss
    wait_more client "Load time" "$loads" 120 || { fail "$label: the player gets the game again" "the map never loaded"; return 1; }
    pass "$label: the player is back in"
    sleep 5
    send host dismiss; send client dismiss; sleep 2
    ticks=$(count client "IO done")
    play; sleep 12
    [ "$(count client "IO done")" -gt "$ticks" ] && pass "$label: the game plays on for the player" || fail "$label: the game plays on for the player" "no new ticks"
    expect_in_sync "$label: in step"
}

start_match Folktails IronTeeth || { fail "a match starts" "the copies never got into the game"; return; }
pass "a match starts"
if mod_installed TimberEmpires; then place_starts Folktails IronTeeth; fi
play; sleep 10
expect_in_sync "before any rehost: in step"
for round in 1 2 3; do rehost_round "rehost $round" || break; done

# Before the game first runs: the host saves and hosts it again, and the player comes back
hosts=$(count host "Server started listening"); loads=$(count client "Load time")
send host "speed 0"; sleep 2
send client "desync"; sleep 3
send host dismiss
wait_more host "Server started listening" "$hosts" 60 && sleep 2 && send client dismiss
if wait_more client "Load time" "$loads" 120; then pass "paused: the player is back in"; else fail "paused: the player is back in" "the map never loaded"; fi
sleep 5; send host dismiss; send client dismiss; sleep 2
play; sleep 12
expect_in_sync "paused rehost: in step"

e=$(grep -aE "NullReferenceException|Exception:" "$(bb_log host)" "$(bb_log client)" 2>/dev/null | grep -v "gpath.c" | head -2 | cut -c1-200)
if [ -z "$e" ]; then pass "no errors"; else fail "no errors" "$e"; fi
bb_close
