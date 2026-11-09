# Named pipe ping/pong experiment

In the release archive, close existing games/launchers and run `start-pipe-probe.bat` next to `MPFever.exe`.
The normal launcher leaves the experiment disabled. The build instructions below are for a source checkout.

Opt-in proof of the local path `game Lua -> native queue/worker -> Windows named pipe -> C# launcher -> pong -> Lua`.
Multiplayer commands, lockstep barriers, hashes and existing file IPC still use their current transport.

## Build and launch

From the repository root, build the launcher into `launcher/bin/PipeProbe` and the native DLL with `native/build.bat`.
Copy `native/out/winhttp.dll` and `native/mpfever_sigs.txt` beside that launcher's executable.
Its normal installation locates this repository's mod tree and installs it when starting the game.
Close existing games before starting this package, since installation replaces the installed mod/native files.

```powershell
$env:MPFEVER_IPC_PROBE = "1"
& .\launcher\bin\PipeProbe\MPFever.exe --dev
```

Use **Local test (2 games)** for the two independent manual game instances, or launch one normally.
Alternatively, run `start-pipe-probe.bat` from the repository root: it selects this exact build and sets the opt-in flag.
Each `GameLink.Launch` creates its own random `mpfever-probe-<32 hex digits>` endpoint and passes its name in `MPFEVER_PIPE`.
Endpoints are restricted to the current Windows user. The second local launcher inherits the opt-in flag and gets a different endpoint.
Open Multiplayer to exercise the menu probe; load a game to exercise the UI and simulation probes.

Expected observations:

- Launcher: `pipe probe: native client connected`, then `ping #<ticket> -> pong`.
- Session `native.log`: `worker started, game Lua API verified`, then `Lua module ready in state ...`.
- Session `menu.log`, `ui_mod.log` or `mod.log`: `pipe probe [menu/ui/simulation]: ping #... sent` followed by `pong #...`.

Remove the flag for ordinary runs: `Remove-Item Env:MPFEVER_IPC_PROBE`.
No Lua API address resolution, worker, endpoint or Lua probe runs with the flag unset.

## Lua and wire contract

The module is registered in each relevant VM's registry `_LOADED` table after `lua_load` successfully parses an MPFever entry file.
Registration preserves the stack and happens on the Lua-owning thread. A VM's coroutines share the registry identity used to route replies.
No Lua API is called from the native pipe worker, and no second Lua interpreter is loaded.

```lua
local ipc = package.loaded["mpfever_ipc"]
local ticket = ipc.ping() -- positive integer, or 0 when unavailable/busy/full
-- On a later update callback, on this VM (a coroutine is fine):
local reply = ipc.poll(ticket) -- nil while pending; pong/timeout/disconnected/unknown when terminal
```

Tickets are nonzero uint32 values, unique across VMs until wraparound; each reply is consumed once.
The queue holds 32 requests. Game-thread calls only attempt a lock and return immediately if busy.
Pending requests expire after five seconds. Abandoned slots expire after thirty seconds.
An idle worker sleeps on an event; only active requests poll for arriving bytes.
The synchronous Win32 pipe handle belongs to the worker alone; at most one twelve-byte request is outstanding.

Both directions use a twelve-byte frame: `uint32 LE length=8`, `uint32 LE ticket`, four ASCII bytes `ping`/`pong`.
The C# byte-stream reader handles split/coalesced frames, validates the fixed length before reading the body,
and disconnects malformed clients. A partial/idle frame has a thirty-second deadline.
Failed requests are never automatically replayed; late/mismatched replies close the connection.
Server disposal/game exit cancels pending I/O. The native DLL/worker live for the game process's lifetime.

## Supported game and evidence boundary

The initial Lua binding supports the installed GOG executable with PE stamp `0x6ad9e27e`, SHA-256
`b4cd74c80edcbed0dfaf685f6f0335305b9772beb3fda19dea72d6653155c419`.
All thirteen Lua 5.2 API entry prefixes are checked before any function pointer is used.
An unknown build or mismatched prefix disables the probe and is reported in `native.log`.
The existing multiplayer implementation's broader signature support is independent of this experiment.

Static API identification used PE unwind ranges and instruction semantics matched to Lua 5.2 `lapi.c`
(stack indices, value tags, table operations, numeric conversion and C closures).
REA admitted the binary with the SHA above, but its Ghidra analysis failed; it supplied no function-analysis evidence.
The identification was completed with targeted local PE/Capstone inspection instead.

## Game-free verification

```powershell
cmd /c tests\build_native_ipc_probe.bat
$env:MPFEVER_IPC_NATIVE_TEST = (Resolve-Path native\out\native_ipc_probe.exe).Path
dotnet launcher\bin\PipeProbe\MPFever.dll --pipe-selftest
python tests\test_pipe_probe.py
python tests\run_all.py --exe launcher\bin\PipeProbe\MPFever.exe
```

The optional native helper links the production worker. It checks actual Win32 pipes, 32 queued pongs,
queue saturation, VM ownership, consumed tickets, fragmented replies, timeout and wrong-ticket rejection.
Its fake Lua host exercises production binding stack preservation, idempotent registration, separate VMs,
coroutine identity and malformed ticket handling. The Python test runs the production probe script under real Lua 5.2.
The C# tests additionally cover split/coalesced requests, invalid frames, separate endpoints and cancellation.
These tests do not establish that the game has executed the binding; verify the three runtime log stages above.

Verification on 2026-11-09: both Release builds completed with zero warnings/errors and all pipe cases passed.
The initial development run completed 171/172 checks; the launcher's existing `FineTimer` timing assertion failed because
`Thread.Sleep(5)` averaged 15.7 ms on this Windows session instead of less than 10 ms. The pipe checks within
that self-test all passed. For release verification, the API success/disable checks remain mandatory and the
scheduler-dependent timing measurement is diagnostic, as required by Windows' timer contract.
The dedicated `--pipe-selftest` command runs the pipe checks independently.

References: [Lua 5.2 C API](https://www.lua.org/manual/5.2/manual.html#4),
[Lua 5.2 lapi.c](https://www.lua.org/source/5.2/lapi.c.html),
[NamedPipeServerStream (.NET 10)](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstream?view=net-10.0),
[PeekNamedPipe](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-peeknamedpipe).
