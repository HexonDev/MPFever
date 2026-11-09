#pragma once
#include "mpf_ipc.h"

// Lua 5.2 ABI. These are the host's functions; a second Lua runtime is never loaded into the game.
struct MpfLuaApi {
    int (*checkstack)(void*, int);
    void (*createtable)(void*, int, int);
    void (*getfield)(void*, int, const char*);
    int (*gettop)(void*);
    void (*pushcclosure)(void*, int (*)(void*), int);
    void (*pushinteger)(void*, i64);
    const char* (*pushlstring)(void*, const char*, size_t);
    void (*setfield)(void*, int, const char*);
    void (*settop)(void*, int);
    i64 (*tointegerx)(void*, int, int*);
    double (*tonumberx)(void*, int, int*);
    const void* (*topointer)(void*, int);
    int (*type)(void*, int);
};
static MpfLuaApi g_ipcLua = {};

static int MpfLuaPing(void* L)
{
    // Registry identity is shared by a VM's coroutines; their lua_State pointers need not be the same each update.
    void* owner = (void*)g_ipcLua.topointer(L, -1001000);
    g_ipcLua.pushinteger(L, MpfIpcPing(owner)); // zero: not started, queue full, or briefly busy; never block the game
    return 1;
}

static int MpfLuaPoll(void* L)
{
    // Reject non-numbers, NaN, fractions and out-of-range values before converting an integer.
    // Lua's own conversion is used, so a malformed argument cannot read a native pointer.
    if (g_ipcLua.type(L, 1) != 3) { g_ipcLua.pushlstring(L, "unknown", 7); return 1; }
    const double value = g_ipcLua.tonumberx(L, 1, 0);
    int result = 4;
    if (value >= 1 && value <= 4294967295.0) {
        const i64 ticket = g_ipcLua.tointegerx(L, 1, 0);
        if ((double)ticket == value) result = MpfIpcPoll((void*)g_ipcLua.topointer(L, -1001000), (u32)ticket);
    }
    if (!result) return 0; // no value is nil in Lua
    const char* text = result == 1 ? "pong" : result == 2 ? "timeout" : result == 3 ? "disconnected" : "unknown";
    size_t n = 0; while (text[n]) n++;
    g_ipcLua.pushlstring(L, text, n);
    return 1;
}

static bool MpfLuaRegister(void* L)
{
    const int top = g_ipcLua.gettop(L);
    if (!g_ipcLua.checkstack(L, 6)) return false;
    g_ipcLua.getfield(L, -1001000, "_LOADED"); // LUA_REGISTRYINDEX, LUA_LOADED_TABLE in Lua 5.2
    if (g_ipcLua.type(L, -1) != 5) { g_ipcLua.settop(L, top); return false; }
    g_ipcLua.getfield(L, -1, "mpfever_ipc");
    if (g_ipcLua.type(L, -1) == 5) { g_ipcLua.settop(L, top); return true; }
    g_ipcLua.settop(L, top + 1); // remove the missing module, keep the loaded table
    g_ipcLua.createtable(L, 0, 2);
    g_ipcLua.pushcclosure(L, MpfLuaPing, 0);
    g_ipcLua.setfield(L, -2, "ping");
    g_ipcLua.pushcclosure(L, MpfLuaPoll, 0);
    g_ipcLua.setfield(L, -2, "poll");
    g_ipcLua.setfield(L, -2, "mpfever_ipc");
    g_ipcLua.settop(L, top); // including the compiled chunk that lua_load just left here
    return true;
}
