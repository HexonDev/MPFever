// Stack/registry contract test for the production binding. This is a fake Lua host, not a game-runtime test.
#include "../native/mpf_lua_ipc.h"
extern "C" int _fltused = 0;
extern "C" void* memset(void*, int, size_t);
#pragma function(memset)
extern "C" void* memset(void* dst, int value, size_t n) { auto p = (u8*)dst; while (n--) *p++ = (u8)value; return dst; }
struct Value { int type; double number; int (*function)(void*); const char* text; };
struct Vm { bool loaded = true, registered = false; int tables = 0; Value ping = {}, poll = {}; };
struct State { Vm* vm; int top = 0; Value stack[32] = {}; bool room = true; };
static Value& At(State* L, int i) { return L->stack[i > 0 ? i - 1 : L->top + i]; }
static bool Equal(const char* a, const char* b) { while (*a && *a == *b) { a++; b++; } return *a == *b; }
static int Top(void* state) { return ((State*)state)->top; }
static int Room(void* state, int) { return ((State*)state)->room; }
static void SetTop(void* state, int n) { auto L = (State*)state; L->top = n < 0 ? L->top + n + 1 : n; }
static void GetField(void* state, int i, const char* key)
{
    auto L = (State*)state;
    bool table = i == -1001000 ? L->vm->loaded : L->vm->registered;
    if (!Equal(key, "_LOADED") && !Equal(key, "mpfever_ipc")) table = false;
    L->stack[L->top++] = { table ? 5 : 0, 0, 0, 0 };
}
static int Type(void* state, int i) { return At((State*)state, i).type; }
static void Create(void* state, int, int) { auto L = (State*)state; L->vm->tables++; L->stack[L->top++] = { 5, 0, 0, 0 }; }
static void Closure(void* state, int (*f)(void*), int) { auto L = (State*)state; L->stack[L->top++] = { 6, 0, f, 0 }; }
static void SetField(void* state, int i, const char* key)
{
    auto L = (State*)state;
    if (At(L, i).type != 5) { L->top = -100; return; }
    Value value = L->stack[--L->top];
    if (Equal(key, "ping")) L->vm->ping = value;
    if (Equal(key, "poll")) L->vm->poll = value;
    if (Equal(key, "mpfever_ipc")) L->vm->registered = value.type == 5;
}
static void Integer(void* state, i64 value) { auto L = (State*)state; L->stack[L->top++] = { 3, (double)value, 0, 0 }; }
static const char* String(void* state, const char* text, size_t) { auto L = (State*)state; L->stack[L->top++] = { 4, 0, 0, text }; return text; }
static i64 ToInteger(void* state, int i, int*) { return (i64)At((State*)state, i).number; }
static double ToNumber(void* state, int i, int*) { return At((State*)state, i).number; }
static const void* Pointer(void* state, int) { return ((State*)state)->vm; }

bool TestLuaBinding()
{
    g_ipcLua = { Room, Create, GetField, Top, Closure, Integer, String, SetField, SetTop, ToInteger, ToNumber, Pointer, Type };
    Vm vm, other;
    State L = { &vm }, second = { &other };
    L.stack[L.top++] = { 6, 0, 0, "compiled chunk" };
    if (!MpfLuaRegister(&L) || L.top != 1 || !vm.ping.function || !vm.poll.function || vm.tables != 1) return false;
    if (!MpfLuaRegister(&L) || L.top != 1 || vm.tables != 1 || !MpfLuaRegister(&second) || other.tables != 1) return false;
    Vm missing; missing.loaded = false; State noLoaded = { &missing };
    if (MpfLuaRegister(&noLoaded) || noLoaded.top != 0) return false;
    State full = { &vm }; full.room = false;
    if (MpfLuaRegister(&full) || full.top != 0) return false;

    // Calls made by two different coroutines still use the same VM registry as their owner.
    State coroutine = { &vm };
    L.top = 0;
    if (vm.ping.function(&L) != 1 || L.top != 1 || L.stack[0].number < 1) return false;
    const double ticket = L.stack[0].number;
    coroutine.stack[0] = { 3, ticket + 0.5, 0, 0 }; coroutine.top = 1;
    if (vm.poll.function(&coroutine) != 1 || !Equal(coroutine.stack[1].text, "unknown")) return false;
    const u64 until = GetTickCount64() + 6000;
    while (GetTickCount64() < until) {
        coroutine.top = 1; coroutine.stack[0] = { 3, ticket, 0, 0 };
        if (vm.poll.function(&coroutine) == 1) return Equal(coroutine.stack[1].text, "pong");
        Sleep(1);
    }
    return false;
}
