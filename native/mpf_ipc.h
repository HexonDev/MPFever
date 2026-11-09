#pragma once
#include "win.h"

// No Lua pointers are dereferenced by the worker. Its only callers are Lua's owning thread.
bool MpfIpcStart(const char* pipeName);
u32 MpfIpcPing(void* owner);
// 0 pending, 1 pong, 2 timeout, 3 disconnected, 4 unknown/consumed ticket.
int MpfIpcPoll(void* owner, u32 ticket);
void MpfIpcStop();
