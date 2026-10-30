# A packet sent over Steam to the test account itself comes back through the Steam packet listener's thread
# and out of the socket's read, and the listener's thread ends with the socket.
bb_close
bb_launch host || { fail "the game starts" "no main menu"; return 1; }
send host steamloop
wait_for host "steamloop (arrived|never)" 30 || { fail "the loopback finishes" "no answer"; return 1; }
expect_log "the packet comes back through the listener" host "steamloop arrived"
expect_no_errors "no errors"
bb_close
