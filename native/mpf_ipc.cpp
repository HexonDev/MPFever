#include "mpf_ipc.h"

namespace {
struct Slot { void* owner; u32 ticket; int state; u64 at; };
static Slot slots[32] = {};
static SRWLOCK_ gate = {};
static volatile long running = 0;
static u32 nextTicket = 0;
static char endpoint[96];
static HANDLE wake = 0;

struct Frame { u32 length, ticket; char text[4]; };
static_assert(sizeof(Frame) == 12, "pipe wire layout");

// A synchronous pipe is used on this worker only. At most one 12-byte request is outstanding;
// PeekNamedPipe avoids an unbounded read from an idle peer. Lua never calls any Win32 pipe API.
static DWORD WINAPI Worker(void*)
{
    HANDLE pipe = INVALID_HANDLE_VALUE;
    u32 active = 0;
    u64 sentAt = 0;
    while (_InterlockedCompareExchange(&running, 1, 1)) {
        if (pipe == INVALID_HANDLE_VALUE) {
            pipe = CreateFileA(endpoint, GENERIC_READ | GENERIC_WRITE, 0, 0, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, 0);
            if (pipe == INVALID_HANDLE_VALUE) { Sleep(20); continue; }
        }
        u64 now = GetTickCount64();
        AcquireSRWLockExclusive(&gate);
        for (auto& s : slots) {
            if (s.state == -1 && now - s.at >= 5000) s.state = 2;
            if (s.state > 0 && now - s.at >= 30000) s.ticket = 0;
        }
        if (!active) {
            for (auto& s : slots) if (s.ticket && s.state == -1) { active = s.ticket; sentAt = now; break; }
        }
        ReleaseSRWLockExclusive(&gate);
        if (!active) { WaitForSingleObject(wake, 1000); continue; }

        Frame request = { 8, active, { 'p', 'i', 'n', 'g' } };
        DWORD written = 0;
        bool ok = WriteFile(pipe, &request, sizeof(request), &written, 0) && written == sizeof(request);
        int result = 3;
        Frame reply = {};
        DWORD received = 0;
        while (ok && _InterlockedCompareExchange(&running, 1, 1)) {
            if (GetTickCount64() - sentAt >= 5000) { result = 2; break; }
            DWORD available = 0;
            if (!PeekNamedPipe(pipe, 0, 0, 0, &available, 0)) break;
            if (!available) { Sleep(1); continue; }
            DWORD want = (DWORD)sizeof(reply) - received;
            if (want > available) want = available;
            DWORD count = 0;
            if (!ReadFile(pipe, (u8*)&reply + received, want, &count, 0) || !count) break;
            received += count;
            if (received < sizeof(reply)) continue;
            if (reply.length == 8 && reply.ticket == active
                && reply.text[0] == 'p' && reply.text[1] == 'o' && reply.text[2] == 'n' && reply.text[3] == 'g') result = 1;
            break;
        }
        AcquireSRWLockExclusive(&gate);
        for (auto& s : slots) if (s.ticket == active && s.state == -1) s.state = result;
        ReleaseSRWLockExclusive(&gate);
        active = 0;
        // Failed/late replies must never satisfy a subsequent request. Nothing is silently replayed.
        if (result != 1) { CloseHandle(pipe); pipe = INVALID_HANDLE_VALUE; }
    }
    if (pipe != INVALID_HANDLE_VALUE) CloseHandle(pipe);
    return 0;
}
}

bool MpfIpcStart(const char* name)
{
    // Only our generated local endpoint names are admitted, never filesystem or remote UNC paths.
    const char prefix[] = "mpfever-probe-";
    int n = 0;
    while (name[n] && n < 64) {
        if (n < (int)sizeof(prefix) - 1) { if (name[n] != prefix[n]) return false; }
        else if (!((name[n] >= '0' && name[n] <= '9') || (name[n] >= 'a' && name[n] <= 'f'))) return false;
        n++;
    }
    if (n != (int)sizeof(prefix) - 1 + 32 || name[n]) return false;
    if (_InterlockedCompareExchange(&running, 1, 0)) return true;
    wake = CreateEventA(0, 0, 0, 0);
    if (!wake) { _InterlockedExchange(&running, 0); return false; }
    const char lead[] = "\\\\.\\pipe\\";
    int k = 0;
    for (; k < (int)sizeof(lead) - 1; k++) endpoint[k] = lead[k];
    for (int j = 0; j <= n; j++) endpoint[k + j] = name[j];
    HANDLE thread = CreateThread(0, 0, Worker, 0, 0, 0);
    if (!thread) { CloseHandle(wake); wake = 0; _InterlockedExchange(&running, 0); return false; }
    CloseHandle(thread);
    return true;
}

u32 MpfIpcPing(void* owner)
{
    if (!_InterlockedCompareExchange(&running, 1, 1) || !TryAcquireSRWLockExclusive(&gate)) return 0;
    u32 ticket = 0;
    const u64 now = GetTickCount64();
    for (auto& s : slots) if (!s.ticket || now - s.at >= 30000) {
        if (++nextTicket == 0) ++nextTicket;
        ticket = nextTicket;
        s = { owner, ticket, -1, now };
        break;
    }
    ReleaseSRWLockExclusive(&gate);
    if (ticket) SetEvent(wake);
    return ticket;
}

int MpfIpcPoll(void* owner, u32 ticket)
{
    if (!ticket) return 4;
    if (!TryAcquireSRWLockExclusive(&gate)) return 0;
    int result = 4;
    for (auto& s : slots) if (s.ticket == ticket && s.owner == owner) {
        result = s.state;
        if (result == -1) result = GetTickCount64() - s.at >= 5000 ? 2 : 0;
        if (result > 0) s.ticket = 0;
        break;
    }
    ReleaseSRWLockExclusive(&gate);
    return result;
}

void MpfIpcStop() { _InterlockedExchange(&running, 0); if (wake) SetEvent(wake); }
