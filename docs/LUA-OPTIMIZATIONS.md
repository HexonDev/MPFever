# Lua optimizations: UI polling and diagnostics

Branch: `codex/lua-optimizations`, based on `a7162b6`. This implements the selected items 1, 2, 3 and 8.

## Behavior

- UI `bindings.log` and `results.log` readers reuse `C.keptOpen` handles. They still run on every UI tick. Passing `retryDelay=0` avoids the common reader's default missing-file retry delay: a newly created result stream is checked on the next tick. Complete records are consumed once; partial records wait for their newline. An observed in-place truncation resets the offset, and binding truncation also clears the old maps. Explicit `RESET` records retain their existing behavior. The launcher already truncates these logs in place rather than replacing their files.
- The AI configuration is parsed once per value of `G.coAi` and `MPFEVER_AI`. The host-local `G.AIT` cache is rebuilt when either changes or the session cache is cleared. AI scheduling, company turns, level, color and starting-money behavior are preserved.
- `pending_build.txt` supplies display text only. Reads are limited to one per 0.1 units of the existing `os.clock()` clock; other ticks reuse the last text. Missing/empty files clear the display at the next read. Command results and entity bindings are not throttled.
- The recursive diagnostic state-size walk at every 3,000 simulation updates now requires `MPFEVER_STATEPROFILE=1`. Cheap event-rate logging and counter resets continue by default. This does not disable sync hashes or desync detection.

## Operation counts

The actual production Lua functions were run through Lua 5.2 with controlled clocks and live file-content mocks, comparing `a7162b6` with the modified code. Inputs: 600 ticks at a simulated 60 Hz, existing idle log files, and an unchanged AI configuration waiting for its scheduled turn.

| Operation | Baseline | Optimized |
| --- | ---: | ---: |
| Open `bindings.log` | 600 | 1 |
| Open `results.log` | 600 | 1 |
| Open `pending_build.txt` | 600 | 92 |
| Parse AI configuration | 600 | 1 |

The pending count depends on polling cadence and floating-point clock values; the limiter caps read frequency rather than promising an exact count. Missing log files continue to be retried each tick. These are operation counts, not measured FPS gains, wall-clock speedups or evidence of fewer desyncs.

## Verification

`tests/test_lua_optimizations.py` runs production functions and covers incomplete/appended records, callback ordering and errors, token filtering, duplicate results, rebinding, explicit resets, observed truncation, first appearance of missing logs, the common reader's unchanged default retry delay, pending text changes/clears, configuration invalidation and diagnostic gating. A real-file test also keeps Lua handles open while a separate writer appends and truncates files.

The full game-free suite passed 171/171, including the new Lua checks and the launcher's self-test. The real-file case was also run separately after it was added. Reproduce from the repository root with Python and lupa 2.8:

```powershell
python tests/test_lua_optimizations.py
python tests/run_all.py --exe launcher/bin/Release/MPFever.exe
```

## In-game comparison

Use separate complete baseline and test packages. Put this branch's `mod/mpfever_1` tree in the test package; its launcher installs that copy when starting. Keep the matching native DLL and the launcher's .NET runtime files with each package. Set `autoupdate=0` in the test package's `mpfever_settings.txt` while comparing builds.

1. Close both game instances before switching packages. Start the selected package with `MPFever.exe --dev` and click **Local test (2 games)**. Manual testing now starts two independent launchers/games, about 20 seconds apart, without hosting or connecting either one automatically.
2. In the first game's **Multiplayer** page choose the company mode, save and **Host**. In the second game's **Multiplayer** page choose **Join** and enter `127.0.0.1:28090` (or the port chosen on the host). Start from the host's in-game lobby: its save is sent and loaded through the normal multiplayer flow. Keep graphics, windows, AI configuration and simulation speed consistent between comparisons. The launcher's old direct **Start the game** button is hidden for this manual flow.
3. Run idle simulation and build/edit roads, stations, lines and vehicles from each instance. Check that callbacks arrive, new entity bindings work and pending text clears. Return to the menu and start another session to exercise resets; include a save with AI companies to exercise the configuration cache.
4. Compare `LAG` lines and visible pauses, hash costs/differences and automatic resyncs. Save reports from both instances after each run. Repeat from the original save; the two local games share machine resources, so this does not model internet latency.

For the detailed state-size diagnostic, set `$env:MPFEVER_STATEPROFILE = "1"` before starting the launcher. Leave it unset for normal performance comparisons. In-game performance and desync frequency have not yet been measured for this change.

The second manual player has a default name ending in `_2`, a separate session directory and a separate launcher lifecycle. Renaming it does not overwrite the primary player's saved name. It reuses the installed mod/native files, so it does not reinstall them while the first game is loading. Both launchers are in developer mode with automatic updates off. Closing either launcher affects its own game. Automated `--autotest` scenarios retain the original preconnected host/client setup.

If earlier tests set `MPFEVER_COMPANIES` or `MPFEVER_COMPANY_START` in PowerShell, use a fresh PowerShell window for testing choices from the lobby: explicit environment overrides still take precedence over those choices.
