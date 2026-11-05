# A map's starts round trip: a map with a start per player saves them all on player 1's, the
# game's own start, so a game without BeaverBuddies sees one; the editor and a game with it
# see every start again. Needs Forgotten Atolls (Archipelago's official map) from the Workshop.
atolls=$(ls "$HOME/Library/Application Support/Steam/steamapps/workshop/content/1062090/3833507519/"*.timber 2>/dev/null | head -1)
[ -n "$atolls" ] || { fail "Forgotten Atolls is downloaded" "not in the Workshop folder"; return; }
maps="$HOME/Documents/Timberborn/Maps"
cp "$atolls" "$maps/StartsBefore.timber"
rm -f "$maps/StartsFolded.timber"
starts() {
    rm -f "$BB_TEST_DIR/entities.txt"
    send host "dumpentities $BB_TEST_DIR/entities.txt"
    wait_for_watch host "Dumped the entities to $BB_TEST_DIR/entities.txt" 30 >/dev/null
    grep -c " StartingLocation" "$BB_TEST_DIR/entities.txt"
}

bb_close
bb_launch host || { fail "the game starts" "no main menu"; return; }
send host "editmap user:StartsBefore"
wait_for_watch host "Load time" 300 || { fail "the old map opens in the editor" "never loaded"; return; }
sleep 3
expect_eq "the old map's three starts are in the editor" "$(starts)" 3
send host "savemap StartsFolded"
wait_for_watch host "savemap StartsFolded saved" 60 || fail "the map saves" "no save"
expect_log "the other starts are saved on player 1's" host "Saved 2 more starting locations on player 1's"
python3 -I - "$maps/StartsFolded.timber" > "$BB_TEST_DIR/folded.txt" <<'PY'
import json, sys, zipfile
world = json.loads(zipfile.ZipFile(sys.argv[1]).read("world.json"))
starts = [e for e in world["Entities"] if e["Template"] == "StartingLocation"]
print(len(starts), len(starts[0]["Components"].get("BeaverBuddies.OtherStartingLocations", {}).get("Starts", [])))
PY
expect_eq "the file has one start of the game's own, carrying the other two" "$(cat "$BB_TEST_DIR/folded.txt")" "1 2"

bb_close
bb_launch host || { fail "the game starts again" "no main menu"; return; }
send host "editmap user:StartsFolded"
wait_for_watch host "Loaded 2 more starting locations" 300 || fail "the saved map opens with its starts" "no starts loaded"
sleep 5
expect_eq "the saved map's three starts are back in the editor" "$(starts)" 3
shot "editor-starts" host

bb_close
bb_launch host || { fail "the game starts again" "no main menu"; return; }
send host "newgame user:StartsFolded Folktails"
wait_for_watch host "Map supports players: 3" 300 || fail "a game on the saved map sees three starts" "$(grep -a 'Map supports players' "$(bb_log host)" | tail -1)"
expect_no_errors "no errors"
rm -f "$maps/StartsBefore.timber" "$maps/StartsFolded.timber"
bb_close
