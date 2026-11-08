# .NET 10 launcher optimizations

Baseline: `3ea540d` (the .NET 10 migration merged into main). These measurements compare two builds on the **same .NET 10 runtime**, so they measure the code changes, not the Framework-to-.NET runtime upgrade.

## Changes

- `NetReader.ReadLine` searches for LF with span `IndexOf` and decodes complete buffered lines directly. Only lines spanning reads allocate a temporary stream; decoding its buffer avoids `ToArray`. Binary bytes following a line stay buffered, UTF-8 is decoded after the whole line arrives, one trailing CR is removed, and incomplete lines at EOF are discarded as before.
- `LuaLit` parses numbers from a character span instead of allocating a substring per number.
- `SaveDelta.CommonLength` uses `CommonPrefixLength` instead of comparing eight bytes at a time. SHA-256 uses the static `HashData` APIs, with a stack buffer for checking reconstructed saves.

The delta format, hashes, compression settings, message order, file-based game connection, Lua mod and native module are unchanged. The compatibility checks require identical delta bytes on the same runtime and test reconstruction in both directions between the builds.

## Measurements

Representative local run: .NET 10.0.12, Windows x64, 12 logical CPUs. Each entry is the median of seven samples after three warmup invocations. Input generation, delegate binding and correctness checks are outside timing. Allocation counts cover the whole process, including parallel delta workers. GC is forced before warmup, not between measured samples; this measures sustained operation rather than deliberately clearing caches between calls.

| Synthetic workload | Baseline ms | Optimized ms | Managed allocations per invocation |
| --- | ---: | ---: | --- |
| 20,000 short TCP lines | 5.580 | 1.034 | 9,667,472 -> 1,668,808 bytes (-83%) |
| 12 lines of 850,000 bytes | 28.343 | 12.538 | 59,204,456 -> 49,003,428 bytes (-17%) |
| 500 UTF-8 lines, reads capped at 37 bytes | 3.247 | 2.999 | 3,118,120 -> 2,590,056 bytes (-17%) |
| Lua table with 1,000 numeric entries | 0.787 | 0.640 | 246,056 -> 174,216 bytes (-29%) |
| 4,000 prefix comparisons, 64 KiB each | 118.863 | 12.995 | No allocations in either version |
| Make delta, 12 MiB with sparse edits and an insertion | 24.549 | 21.044 | Essentially unchanged |
| Make delta, 12 MiB with changes throughout | 86.105 | 83.632 | Essentially unchanged |

The first five rows isolate launcher operations. Delta rows include the whole `SaveDelta.Make` operation, but exclude save decoding, network transfer and game reload. Timings vary with machine load, tiered compilation and GC; small differences are not evidence of a material improvement. These are synthetic inputs, not a game-session or FPS benchmark.

An experiment pooling compression buffers reduced warmed-up allocations for the widespread-edit delta from about 15.6 MB to 3.0 MB, but repeated runs increased computation time (for example, 72.1 ms to 91.8 ms). It is not included. Pooling can also retain buffers and loses its allocation benefit if the GC trims the pool between infrequent resyncs.

## Reproduce

Run from the repository root with the .NET 10 SDK and Windows desktop runtime. The comparison tool has no external packages and does not start a game or open sockets. Exporting a baseline avoids switching branches or modifying the working tree:

```powershell
New-Item -ItemType Directory -Force tools/LauncherPerf/bin | Out-Null
git archive --format=zip --output=tools/LauncherPerf/bin/baseline.zip 3ea540d launcher
Expand-Archive tools/LauncherPerf/bin/baseline.zip tools/LauncherPerf/bin/baseline-source -Force
dotnet build tools/LauncherPerf/bin/baseline-source/launcher/MPFever.csproj -c Release
dotnet build launcher/MPFever.csproj -c Release
dotnet run --project tools/LauncherPerf -c Release -- --baseline tools/LauncherPerf/bin/baseline-source/launcher/bin/Release/MPFever.dll --json tools/LauncherPerf/bin/results.json
```

`--checks-only` runs only the correctness checks. `--filter delta` selects matching benchmark names; correctness checks still run. Without `--baseline`, the tool checks and measures the candidate against itself, which is useful for checking a single build but is not a before/after comparison. `--candidate` selects another launcher DLL; the default is `launcher/bin/Release/MPFever.dll`.

Correctness checks cover fragmented UTF-8, empty LF/CRLF lines, CRLF at 64 KiB boundaries, large lines, binary data after a line, truncated input, randomized prefix offsets/limits, empty and multi-segment saves, cross-build delta reconstruction, identical output and corrupt SHA-256 rejection. The launcher's normal `--selftest` also includes the stream boundary cases.

Run the repository's full game-free suite separately:

```powershell
python tests/run_all.py --exe launcher/bin/Release/MPFever.exe
```

Three existing source checks (`test_installlock0711`, `test_launcher074`, `test_modcopies079`) also failed on the exported baseline: they still expected `InstallMod()` after GOG support changed the call to `InstallMod(gameDir)`. Their assertions now check the actual call, preserving the checks for install locking, background work and stale-mod detection.

Validation: both Release builds completed with zero warnings and errors, the comparison tool's compatibility checks passed, and the full game-free suite passed **170/170**, including `MPFever.exe --selftest`. No real-game multiplayer test was run.

## Further work

Profile real saves and sessions before broadening this work. Likely next targets are the file-tail reader's temporary buffers and the managed Zstandard decoder. HTTP migration needs tests for GitHub headers, timeouts, UPnP proxy bypass and the server list's IPv4 behavior. Async TCP and bounded queues need explicit ordering and slow-peer tests; their value is resource usage and cancellation, not lower internet latency.

The release packaging and CI SDK setup still need the separate follow-up identified during the .NET 10 migration.
