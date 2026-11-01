# With T3MP and Performance Plus installed, BeaverBuddies turns its own optimizations off and the main menu
# says so in a dialog. Both come from the Workshop, downloaded without subscribing, into this run's Mods folder
# only, and leave it at the end
mods=$(bb_mods_dir)
workshop_get() {
    send host "workshopget $1"
    wait_for host "\[Test\] workshop $1 k_EResultOK at " 300 || return 1
    grep -aEo "\[Test\] workshop $1 k_EResultOK at .*" "$(bb_log host)" | tail -1 | sed 's/.* at //'
}
bb_close
bb_launch host || { fail "the game starts" "no main menu"; return; }
t3mp=$(workshop_get 3756656421) || { fail "T3MP downloads" "no download in the host log"; bb_close; return; }
plus=$(workshop_get 3830961792) || { fail "Performance Plus downloads" "no download in the host log"; bb_close; return; }
pass "T3MP and Performance Plus download"
bb_close
rm -rf "$mods/T3MP" "$mods/PerformancePlus"
# PERF_ONLY=t3mp or plus installs just that one, to tell them apart
[ "${PERF_ONLY:-}" != plus ] && cp -R "$t3mp" "$mods/T3MP"
[ "${PERF_ONLY:-}" != t3mp ] && cp -R "$plus" "$mods/PerformancePlus"
bb_launch host || { fail "the game starts with them" "no main menu"; rm -rf "$mods/T3MP" "$mods/PerformancePlus"; return; }
sleep 3
expect_log "our optimizations are off" host "Performance optimizations are off"
expect_log "the main menu warns about both" host "\[Perf\] .*T3MP.*Performance Plus.* is on: ours are off, warning shown|\[Perf\] .*Performance Plus.*T3MP.* is on: ours are off, warning shown"
shot warning
bb_close
rm -rf "$mods/T3MP" "$mods/PerformancePlus"
