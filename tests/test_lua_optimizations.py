"""Lua 5.2 behavioral checks for UI log reuse, pending text pacing, AI config caching and opt-in state profiling.

Runs the production functions with live file-content mocks (including partial appends and truncation), plus the real AI
module through ai_mock. Counts operations, not machine-dependent wall-clock timings. No game or external network needed.
Run: python tests/test_lua_optimizations.py
"""
from pathlib import Path
from tempfile import TemporaryDirectory

import lupa.lua52 as lua52

from ai_mock import load as load_ai

CONTENT = Path(__file__).resolve().parent.parent / "mod" / "mpfever_1" / "content"
COMMON = (CONTENT / "mpfever_common.lua").read_text(encoding="utf-8")
UI = (CONTENT / "mpfever_ui.script.lua").read_text(encoding="utf-8")
BRIDGE = (CONTENT / "mpfever_bridge.script.lua").read_text(encoding="utf-8")


def between(source, start, end):
    begin = source.index(start)
    return source[begin:source.index(end, begin)]


def ui_runtime():
    lua = lua52.LuaRuntime(unpack_returned_tuples=True)
    lua.execute(r'''
NOW, FS, OPENS, CLOSES, READS, LOG = 0, {}, {}, {}, {}, {}
os.clock = function() return NOW end
U = { tok = "test", pending = {}, stats = { answered = 0 }, resOff = 0, bindOff = 0, rev = {} }
C = { ACTIVE = true, DIR = "D", BS = "/", NL = string.char(10), TAB = string.char(9), IO = {} }
C.plain = function(x) return x end
log = function(s) LOG[#LOG + 1] = s end
function C.IO.open(path, mode)
  assert(mode == "rb")
  OPENS[path] = (OPENS[path] or 0) + 1
  if FS[path] == nil then return nil end
  local pos, closed = 0, false
  return {
    seek = function(_, kind, offset)
      assert(not closed, "read after close")
      if kind == "end" then pos = #FS[path] + (offset or 0)
      elseif kind == "set" then pos = offset
      else error("unexpected seek") end
      return pos
    end,
    read = function(_, size)
      assert(not closed, "read after close")
      READS[path] = (READS[path] or 0) + 1
      if size == "*a" then size = #FS[path] - pos end
      local data = FS[path]:sub(pos + 1, pos + size)
      pos = pos + #data
      return data
    end,
    close = function()
      assert(not closed, "double close")
      closed = true; CLOSES[path] = (CLOSES[path] or 0) + 1
    end,
  }
end
''')
    lua.execute(between(COMMON, "C.kept, C.keptMissed =", "\nfunction C.logger"))
    lua.execute(between(COMMON, "function C.deser(s)", "\nend") + "\nend")
    lua.execute(between(UI, "local function pollResults()", "\nlocal M = {}")
                + "\npollResultsTest = pollResults")
    lua.execute(between(UI, "local function readPending()", "\n-- ---------------------------------------------------------------- resynchronisation")
                + "\nreadPendingTest = readPending; pollBindingsTest = pollBindings")
    return lua


def test_streams():
    lua = ui_runtime()
    lua.execute(r'''
-- Missing streams must be retried next tick, without keptOpen's default half-second backoff.
pollBindingsTest(); pollResultsTest()
assert(OPENS["D/bindings.log"] == 1 and OPENS["D/results.log"] == 1)
NOW = 0.001
FS["D/bindings.log"] = "old 10\npartial "
SEEN = {}
U.pending[1] = { name = "buy", t0 = 0, auto = true, cb = function(data, ok, entities)
  assert(U.cbAuto == true and ok and data.x == 7 and entities[1] == 10)
  SEEN[#SEEN + 1] = 1
end }
FS["D/results.log"] = 'result\tHost\t{tok="test",id=1,success=true,data={x=7},entities={10}}'
pollBindingsTest(); pollResultsTest()
assert(U.rev[10] == "old" and U.byKey.partial == nil and #SEEN == 0)
assert(OPENS["D/bindings.log"] == 2 and OPENS["D/results.log"] == 2)
FS["D/bindings.log"] = FS["D/bindings.log"] .. "20\n"
FS["D/results.log"] = FS["D/results.log"] .. "\n"
pollBindingsTest(); pollResultsTest()
assert(U.rev[20] == "partial" and U.byKey.partial == 20)
assert(#SEEN == 1 and U.stats.answered == 1 and U.cbAuto == nil)

-- Repeated idle ticks keep handles and do not reread consumed bytes or redeliver callbacks.
local bindingReads, resultReads = READS["D/bindings.log"], READS["D/results.log"]
for i = 1, 600 do NOW = i / 60; pollBindingsTest(); pollResultsTest() end
assert(OPENS["D/bindings.log"] == 2 and OPENS["D/results.log"] == 2)
assert(READS["D/bindings.log"] == bindingReads and READS["D/results.log"] == resultReads)
assert(not CLOSES["D/bindings.log"] and not CLOSES["D/results.log"] and #SEEN == 1)

-- Rebinding and explicit RESET records keep their original semantics.
FS["D/bindings.log"] = FS["D/bindings.log"] .. "old 30\n"
pollBindingsTest()
assert(U.rev[10] == nil and U.rev[30] == "old")
FS["D/bindings.log"] = FS["D/bindings.log"] .. "RESET 0\nfresh 40\n"
pollBindingsTest()
assert(U.rev[30] == nil and U.byKey.old == nil and U.rev[40] == "fresh")

-- The launcher truncates these files in place between sessions; the same handles see new bytes.
FS["D/bindings.log"], FS["D/results.log"] = "", ""
pollBindingsTest(); pollResultsTest()
assert(U.bindOff == 0 and U.resOff == 0 and next(U.rev) == nil and next(U.byKey) == nil)
U.pending[2] = { name = "line", t0 = NOW, cb = function() SEEN[#SEEN + 1] = 2 end }
FS["D/bindings.log"] = "new 50\n"
FS["D/results.log"] = 'result\tHost\t{tok="old-session",id=2}\nresult\tHost\t{tok="test",id=2}\n'
pollBindingsTest(); pollResultsTest(); pollResultsTest()
assert(U.rev[50] == "new" and #SEEN == 2 and SEEN[2] == 2 and U.stats.answered == 2)

-- A failed callback must not prevent the following one or leave its automatic-command context active.
U.pending[3] = { name = "bad", t0 = NOW, auto = true, cb = function() error("test callback") end }
U.pending[4] = { name = "good", t0 = NOW, cb = function() assert(not U.cbAuto); SEEN[#SEEN + 1] = 4 end }
FS["D/results.log"] = FS["D/results.log"] .. 'result\tHost\t{tok="test",id=3}\nresult\tHost\t{tok="test",id=4}\n'
pollResultsTest()
assert(#SEEN == 3 and SEEN[3] == 4 and U.cbAuto == nil)

-- Existing keptOpen callers retain their default missing-file backoff.
NOW = 20; assert(not C.keptOpen("D/optional"))
FS["D/optional"] = "ready"; NOW = 20.1
assert(not C.keptOpen("D/optional") and OPENS["D/optional"] == 1)
NOW = 20.6; assert(C.keptOpen("D/optional") and OPENS["D/optional"] == 2)
''')
    print("PASS UI streams: partial records, callbacks, token filtering, rebind/reset/truncation, 600 idle ticks without reopening")


def test_pending():
    lua = ui_runtime()
    lua.execute(r'''
FS["D/pending_build.txt"] = "building"
assert(readPendingTest() == "building")
FS["D/pending_build.txt"] = "updated"
NOW = 0.05; assert(readPendingTest() == "building" and OPENS["D/pending_build.txt"] == 1)
NOW = 0.11; assert(readPendingTest() == "updated")
FS["D/pending_build.txt"] = ""; NOW = 0.22
assert(readPendingTest() == "")
FS["D/pending_build.txt"] = nil; NOW = 0.33
assert(readPendingTest() == "")
FS["D/pending_build.txt"] = "back"; NOW = 0.44
assert(readPendingTest() == "back")
-- A clock reset must not keep the cached text indefinitely.
FS["D/pending_build.txt"] = "reset"; NOW = 0
assert(readPendingTest() == "reset")
OPENS["D/pending_build.txt"] = 0; U.pendingReadAt = nil
for i = 0, 599 do NOW = i / 60; readPendingTest() end
PENDING_OPENS = OPENS["D/pending_build.txt"]
assert(PENDING_OPENS <= 100 and PENDING_OPENS >= 80)
-- Pending's cooldown must not delay a newly arrived command callback.
U.pending[1] = { name = "buy", t0 = NOW, cb = function() ANSWERED = true end }
FS["D/results.log"] = 'result\tHost\t{tok="test",id=1}\n'
NOW = NOW + 0.001; readPendingTest(); pollResultsTest(); assert(ANSWERED)
''')
    print(f"PASS pending text: updates, clears, missing/recreated file; {lua.globals().PENDING_OPENS} opens / 600 simulated 60 Hz ticks")


def test_real_files():
    lua = ui_runtime()
    with TemporaryDirectory() as directory:
        globals_ = lua.globals()
        globals_.C.DIR = Path(directory).as_posix()
        globals_.C.IO = globals_.io
        bindings = Path(directory) / "bindings.log"
        results = Path(directory) / "results.log"
        bindings.write_bytes(b"")
        results.write_bytes(b"")
        try:
            lua.execute("pollBindingsTest(); pollResultsTest()")
            bindings.write_bytes("régi 10\n".encode("utf-8"))
            results.write_bytes(b'result\tHost\t{tok="test",id=1,success=true}\n')
            lua.execute('''
ANSWERED = 0
U.pending[1] = { name = "real file", t0 = 0, cb = function() ANSWERED = ANSWERED + 1 end }
pollBindingsTest(); pollResultsTest()
assert(U.rev[10] == "régi" and ANSWERED == 1)
''')
            bindings.write_bytes(b"")
            results.write_bytes(b"")
            lua.execute("pollBindingsTest(); pollResultsTest(); assert(U.bindOff == 0 and U.resOff == 0 and not U.rev[10])")
            with bindings.open("ab") as file:
                file.write(b"new 20\n")
            lua.execute('pollBindingsTest(); assert(U.rev[20] == "new")')
        finally:
            lua.execute("for _, f in pairs(C.kept) do f:close() end; C.kept = {}")
    print("PASS real files: kept Lua handles see EOF, appends and in-place truncation while the writer opens separately")


def test_ai_cache():
    lua, _ = load_ai()
    lua.execute(r'''
E.C.ROLE = "host"; G.cmode = "separate"; G.connected = true
G.session = { started = true }; G.myCo = 1; G.frames = 1
G.AIT = { nextAt = 1000000 }; ENV.MPFEVER_AI = "0"
G.coAi = "1:7:2:2000000"
PARSES = 0
local original = M.listOf
M.listOf = function(text) PARSES = PARSES + 1; return original(text) end
for i = 1, 600 do G.frames = i; M.tick(E, {}) end
assert(PARSES == 1 and G.AIT.configList[1].money == 2000000)
local cached = G.AIT.configList
G.coAi = "1:4:3:3000000,2:6:1:0"; M.tick(E, {})
assert(PARSES == 2 and G.AIT.configList ~= cached and #G.AIT.configList == 2)
assert(G.AIT.configList[1].color == 4 and G.AIT.configList[1].level == 3 and G.AIT.configList[2].money == 0)
G.coAi = ""; M.tick(E, {}); M.tick(E, {})
assert(PARSES == 3 and #G.AIT.configList == 0)
ENV.MPFEVER_AI = "1"; M.tick(E, {})
assert(PARSES == 4 and #G.AIT.configList == 1 and G.AIT.configList[1].n == 1)
ENV.MPFEVER_AI = "0"; M.tick(E, {})
assert(PARSES == 5 and #G.AIT.configList == 0)
G.AIT = nil; M.tick(E, {}); assert(PARSES == 6)
-- Early returns must still protect clients, paused sessions and resyncs.
G.coAi = "1:1:1"; E.C.ROLE = "client"; M.tick(E, {}); assert(PARSES == 6)
E.C.ROLE = "host"; G.session.pauseAt = 0; M.tick(E, {}); assert(PARSES == 6)
G.session.pauseAt = nil; G.session.resync = true; M.tick(E, {}); assert(PARSES == 6)
G.session.resync = nil; M.tick(E, {}); assert(PARSES == 7)
''')
    print("PASS AI: 1 parse / 600 ticks; live colors/levels/money/list, force switch and session reset invalidate the cache")


def test_state_profile():
    lua = lua52.LuaRuntime(unpack_returned_tuples=True)
    block = between(BRIDGE, "\tif SIM.evTick % 3000 == 0 then", "\tif not SIM.subscribed then")
    lua.execute('''
ENV, LOG, GETS = {}, {}, 0
os.getenv = function(k) return ENV[k] end
log = function(s) LOG[#LOG + 1] = s end
ST = { builds = { { n = 1 }, { n = 2 } } }; ST.self = ST
SIM = { evTick = 3000, evN = 6000 }
state = { get = function() GETS = GETS + 1; return ST end }
''')
    lua.execute("function profileTest()\n" + block + "\nend")
    lua.execute('''
profileTest()
assert(GETS == 0 and SIM.evN == 0 and LOG[1]:find("2.0 per update", 1, true))
assert(LOG[1]:find("not profiled", 1, true))
ENV.MPFEVER_STATEPROFILE = "1"; SIM.evN = 3000; profileTest()
assert(GETS == 1 and SIM.evN == 0 and LOG[2]:find("entries", 1, true))
assert(ST.self == ST and #ST.builds == 2 and ST.builds[2].n == 2)
SIM.evTick = 2999; SIM.evN = 42; profileTest()
assert(GETS == 1 and SIM.evN == 42 and #LOG == 2)
SIM.evTick = 3000; state.get = function() error("unavailable state") end
profileTest(); assert(SIM.evN == 0 and #LOG == 3)
''')
    print("PASS diagnostics: no state reads by default, opt-in traversal handles cycles/errors and preserves event logging")


if __name__ == "__main__":
    test_streams()
    test_pending()
    test_real_files()
    test_ai_cache()
    test_state_profile()
    print("PASS Lua optimizations")
