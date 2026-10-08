# The desync dialog: without logging, its button turns logging on; with logging on, it saves the
# desync's trace next to Player.log for a report on Discord. Nothing is posted anywhere.
bb_close
start_match Folktails Folktails || { fail "a match starts" "the copies never got into the game"; return; }
pass "a match starts"
send host "desync traced"; sleep 4
shot "desync-host" host
shot "desync-client" client
send host "click InfoButton"; sleep 2
send client "click InfoButton"; sleep 2
expect_log "with logging on, the trace is saved" host "Desync trace saved to .*BeaverBuddies-desync-"
expect_log "and Discord is where to report it" host "Would open https://discord"
expect_no_log "without logging, nothing is saved" client "Desync trace saved"
shot "desync-host-reported" host
shot "desync-client-logging" client
trace=$(grep -aoE "Desync trace saved to .*" "$(bb_log host)" | head -1 | sed 's/^Desync trace saved to //')
if [ -n "$trace" ] && [ -f "$trace" ]; then pass "the trace file is there"; rm -f "$trace"; else fail "the trace file is there" "no file at '$trace'"; fi
expect_no_log "nothing posted" host "api.airtable.com"
