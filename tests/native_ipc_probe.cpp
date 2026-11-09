// Exercise the same freestanding worker linked into winhttp.dll against the launcher's real server.
#include "../native/mpf_ipc.h"
extern "C" __declspec(dllimport) void WINAPI ExitProcess(DWORD);
bool TestLuaBinding();

extern "C" void mainCRTStartup()
{
    char name[80] = {};
    GetEnvironmentVariableA("MPFEVER_PIPE", name, sizeof(name));
    if (MpfIpcStart("invalid") || !MpfIpcStart(name)) ExitProcess(1);
    void* owner = (void*)1;
    char expected[4] = {};
    GetEnvironmentVariableA("MPFEVER_IPC_EXPECT", expected, sizeof(expected));
    if (expected[0] >= '1' && expected[0] <= '3') {
        u32 ticket = MpfIpcPing(owner);
        int result = 0;
        const u64 until = GetTickCount64() + 7000;
        while (!(result = MpfIpcPoll(owner, ticket)) && GetTickCount64() < until) Sleep(1);
        MpfIpcStop();
        ExitProcess(result == expected[0] - '0' ? 0 : 6);
    }
    u32 tickets[32] = {};
    for (int i = 0; i < 32; i++) {
        u64 until = GetTickCount64() + 1000;
        while (!(tickets[i] = MpfIpcPing(owner)) && GetTickCount64() < until) Sleep(1);
        if (!tickets[i]) ExitProcess(2);
    }
    if (MpfIpcPing(owner) || MpfIpcPoll((void*)2, tickets[0]) != 4) ExitProcess(3);
    u64 until = GetTickCount64() + 7000;
    for (int i = 0; i < 32; i++) {
        int result = 0;
        while (!(result = MpfIpcPoll(owner, tickets[i])) && GetTickCount64() < until) Sleep(1);
        if (result != 1 || MpfIpcPoll(owner, tickets[i]) != 4) ExitProcess(4);
    }
    if (!TestLuaBinding()) ExitProcess(5);
    MpfIpcStop();
    ExitProcess(0);
}
