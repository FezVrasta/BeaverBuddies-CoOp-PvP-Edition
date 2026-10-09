# A desync gets everyone back in sync without leaving the game: the copy that desynced tells the host,
# the host saves and sends the save over the connection they already have, and both load it. Once
# back, the dialog's button turns logging on, or with logging on saves the desync's trace next to
# Player.log for a report on Discord. Nothing is posted anywhere.
bb_close
start_match Folktails Folktails || { fail "a match starts" "the copies never got into the game"; return; }
pass "a match starts"
send host "speed 1"; sleep 6
send client "desync traced"
wait_for host "Sending the resync save" 30 && pass "the host resyncs" || fail "the host resyncs" "it never sent its save"
wait_for client "Received the host's resync save" 60 && pass "the save reaches the client over the same connection" \
    || fail "the save reaches the client over the same connection" "it never arrived"
wait_for host "Back in sync after a resync" 120 && wait_for client "Back in sync after a resync" 120 \
    && pass "both copies load it" || fail "both copies load it" "one never finished loading"
expect_no_log "nobody left the game" client "Closing EventIO"
sleep 3
shot "resynced-host" host
shot "resynced-client" client
send host "click InfoButton"; sleep 2
send client "click InfoButton"; sleep 2
expect_log "with logging on, the trace is saved" client "Desync trace saved to .*BeaverBuddies-desync-"
expect_log "and Discord is where to report it" client "Would open https://discord"
expect_no_log "without logging, nothing is saved" host "Desync trace saved"
trace=$(grep -aoE "Desync trace saved to .*" "$(bb_log client)" | head -1 | sed 's/^Desync trace saved to //')
if [ -n "$trace" ] && [ -f "$trace" ]; then pass "the trace file is there"; rm -f "$trace"; else fail "the trace file is there" "no file at '$trace'"; fi
send host dismiss; send client dismiss; sleep 2
send host "speed 1"; sleep 15
expect_in_sync "the game carries on in sync"
expect_no_log "nothing posted" host "api.airtable.com"
