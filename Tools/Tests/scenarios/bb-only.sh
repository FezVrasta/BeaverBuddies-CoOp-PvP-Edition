# BeaverBuddies on its own, with Timber Empires switched off: the New Game page is the game's own
# with no mode to pick, and a hosted game starts as Co-op and runs in sync.
if mod_installed TimberEmpires; then mod_disable TimberEmpires; fi

bb_close
bb_launch host || { fail "the game starts" "no main menu"; return; }
new_game_page host
expect_nonempty "the page is up: its difficulties are laid out" "$(layout_of host Modes)"
l=$(layout_of host BeaverBuddiesMatchOptions)
expect_eq "no Multiplayer mode column with only Co-op to pick" "$l" ""
shot "new-game-page" host
bb_close

start_match Folktails Folktails || { fail "a match starts" "the copies never got into the game"; return; }
pass "a match starts"
expect_log "the game starts as Co-op" host "Starting a game with options: bb.gamemode=bb.coop"
send host "speed 3"; sleep 25
expect_in_sync "both copies in sync"
if mod_installed TimberEmpires; then fail "Timber Empires is off" "its folder is still in Mods"; else pass "Timber Empires is off"; fi
expect_no_log "nothing of Timber Empires loaded" host "\[TimberEmpires\]"
expect_no_errors "no errors"
