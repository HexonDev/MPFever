"""Production probe under Lua 5.2: disabled mode, asynchronous reply, busy queue and terminal failures."""
from pathlib import Path
from lupa.lua52 import LuaRuntime

SOURCE = (Path(__file__).resolve().parents[1] / "mod/mpfever_1/content/mpfever_ipc_probe.lua").read_text(encoding="utf-8")


def runtime(enabled=True, module=True):
    lua = LuaRuntime(unpack_returned_tuples=True)
    lua.execute('''
        logs, sends, polls = {}, 0, 0
        enabled, busy, reply = true, false, nil
        os.getenv = function() return enabled and "1" or nil end
        logger = function(s) logs[#logs + 1] = s end
        package.loaded.mpfever_ipc = {
            ping = function() sends = sends + 1 return busy and 0 or 17 end,
            poll = function(id) assert(id == 17) polls = polls + 1 return reply end,
        }
    ''')
    lua.globals().enabled = enabled
    if not module:
        lua.execute("package.loaded.mpfever_ipc = nil")
    lua.globals().probe = lua.execute(SOURCE)
    return lua


def ticks(lua, count=1):
    lua.execute(f'for i = 1, {count} do probe.tick(logger, "test") end')


def main():
    lua = runtime(False)
    ticks(lua, 500)
    assert lua.eval("sends == 0 and polls == 0 and #logs == 0")
    lua = runtime()
    ticks(lua, 10)
    assert lua.eval("sends == 1 and polls == 9 and #logs == 1")
    lua.globals().reply = "pong"
    ticks(lua, 500)
    assert lua.eval('sends == 1 and polls == 10 and #logs == 2 and logs[2]:find("pong #17", 1, true) ~= nil')
    lua = runtime()
    lua.globals().busy = True
    ticks(lua, 2)
    lua.globals().busy = False
    ticks(lua)
    assert lua.eval("sends == 3 and polls == 0")
    for error in ("timeout", "disconnected", "unknown"):
        lua = runtime()
        lua.globals().reply = error
        ticks(lua, 500)
        assert lua.eval("sends == 1 and polls == 1 and #logs == 2")
    lua = runtime(module=False)
    ticks(lua, 500)
    assert lua.eval("sends == 0 and #logs == 1")
    lua = runtime()
    lua.execute('package.loaded.mpfever_ipc.poll = function() error("broken") end')
    ticks(lua, 500)
    assert lua.eval('sends == 1 and #logs == 2 and logs[2]:find("broken", 1, true) ~= nil')
    lua = runtime()
    lua.globals().busy = True
    ticks(lua, 500)
    assert lua.eval("sends == 300 and polls == 0 and #logs == 1")
    print("OK: Lua 5.2 pipe probe: disabled, pending, pong, queue retry/bound, failure, missing module, callback error")


if __name__ == "__main__":
    main()
