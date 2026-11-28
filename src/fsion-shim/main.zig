const std = @import("std");
const builtin = @import("builtin");

const is_windows = builtin.os.tag == .windows;
const is_linux = builtin.os.tag == .linux;
const is_macos = builtin.os.tag == .macos;

const PROTOCOL_VERSION: i32 = 6;
const VERSION_STR = "0.1.0";

// ---------------------------------------------------------------------------
// Minimal cross-platform byte buffer backed by OS allocator (no std.Io init)
// ---------------------------------------------------------------------------
const Buf = struct {
    data: []u8 = &.{},
    len: usize = 0,

    fn deinit(self: *Buf) void {
        if (self.data.len > 0) osFree(self.data);
        self.data = &.{};
        self.len = 0;
    }

    fn ensure(self: *Buf, extra: usize) void {
        const needed = self.len + extra;
        if (needed <= self.data.len) return;
        var cap: usize = if (self.data.len == 0) 32768 else self.data.len * 2;
        while (cap < needed) cap *= 2;
        self.data = osRealloc(self.data, cap);
    }

    fn u8Val(self: *Buf, v: u8) void {
        self.ensure(1);
        self.data[self.len] = v;
        self.len += 1;
    }

    fn i32Le(self: *Buf, v: i32) void {
        self.ensure(4);
        const u: u32 = @bitCast(v);
        self.data[self.len + 0] = @truncate(u);
        self.data[self.len + 1] = @truncate(u >> 8);
        self.data[self.len + 2] = @truncate(u >> 16);
        self.data[self.len + 3] = @truncate(u >> 24);
        self.len += 4;
    }

    fn varU32(self: *Buf, v_in: u32) void {
        self.ensure(5);
        var v = v_in;
        while (v >= 0x80) {
            self.data[self.len] = @as(u8, @truncate(v & 0x7F)) | 0x80;
            self.len += 1;
            v >>= 7;
        }
        self.data[self.len] = @truncate(v);
        self.len += 1;
    }

    fn bytes(self: *Buf, src: []const u8) void {
        if (src.len == 0) return;
        self.ensure(src.len);
        @memcpy(self.data[self.len .. self.len + src.len], src);
        self.len += src.len;
    }

    fn str(self: *Buf, s: []const u8) void {
        self.varU32(@intCast(s.len));
        self.bytes(s);
    }

    fn patchFrameLen(self: *Buf) void {
        const payload_len: u32 = @intCast(self.len - 4);
        self.data[0] = @truncate(payload_len);
        self.data[1] = @truncate(payload_len >> 8);
        self.data[2] = @truncate(payload_len >> 16);
        self.data[3] = @truncate(payload_len >> 24);
    }
};

fn rdI32(p: []const u8, off: *usize) ?i32 {
    if (off.* + 4 > p.len) return null;
    const o = off.*;
    const u: u32 = @as(u32, p[o]) |
        (@as(u32, p[o + 1]) << 8) |
        (@as(u32, p[o + 2]) << 16) |
        (@as(u32, p[o + 3]) << 24);
    off.* = o + 4;
    return @bitCast(u);
}

fn rdU8(p: []const u8, off: *usize) ?u8 {
    if (off.* + 1 > p.len) return null;
    const v = p[off.*];
    off.* += 1;
    return v;
}

fn rdVarU32(p: []const u8, off: *usize) ?u32 {
    var result: u32 = 0;
    var shift: u5 = 0;
    var i: usize = 0;
    while (i < 5) : (i += 1) {
        if (off.* >= p.len) return null;
        const b = p[off.*];
        off.* += 1;
        result |= @as(u32, b & 0x7F) << shift;
        if ((b & 0x80) == 0) return result;
        if (shift >= 28) return null;
        shift += 7;
    }
    return null;
}

fn sanitizeChar(c: u8) u8 {
    if ((c >= 'a' and c <= 'z') or (c >= 'A' and c <= 'Z') or (c >= '0' and c <= '9') or c == '-' or c == '_' or c == '.') {
        return c;
    }
    return '_';
}

fn hasPathSep(s: []const u8) bool {
    for (s) |c| {
        if (c == '\\' or c == '/') return true;
    }
    return false;
}

// ---------------------------------------------------------------------------
// Minimal OS allocator (zero CRT on Windows, page_allocator on POSIX)
// ---------------------------------------------------------------------------
const win_alloc = if (is_windows) struct {
    extern "kernel32" fn GetProcessHeap() callconv(.winapi) ?*anyopaque;
    extern "kernel32" fn HeapAlloc(hHeap: *anyopaque, dwFlags: u32, dwBytes: usize) callconv(.winapi) ?[*]u8;
    extern "kernel32" fn HeapReAlloc(hHeap: *anyopaque, dwFlags: u32, lpMem: [*]u8, dwBytes: usize) callconv(.winapi) ?[*]u8;
    extern "kernel32" fn HeapFree(hHeap: *anyopaque, dwFlags: u32, lpMem: [*]u8) callconv(.winapi) i32;
    var heap: ?*anyopaque = null;
    fn getHeap() *anyopaque {
        if (heap) |h| return h;
        const h = GetProcessHeap().?;
        heap = h;
        return h;
    }
} else struct {};

fn osAlloc(n: usize) []u8 {
    const sz = if (n == 0) 1 else n;
    if (is_windows) {
        const ptr = win_alloc.HeapAlloc(win_alloc.getHeap(), 0, sz) orelse osExit(1);
        return ptr[0..sz];
    } else {
        return std.heap.page_allocator.alloc(u8, sz) catch osExit(1);
    }
}

fn osRealloc(old: []u8, n: usize) []u8 {
    const sz = if (n == 0) 1 else n;
    if (is_windows) {
        if (old.len == 0) return osAlloc(sz);
        const ptr = win_alloc.HeapReAlloc(win_alloc.getHeap(), 0, old.ptr, sz) orelse osExit(1);
        return ptr[0..sz];
    } else {
        if (old.len == 0) return osAlloc(sz);
        return std.heap.page_allocator.realloc(old, sz) catch osExit(1);
    }
}

fn osFree(mem: []u8) void {
    if (mem.len == 0) return;
    if (is_windows) {
        _ = win_alloc.HeapFree(win_alloc.getHeap(), 0, mem.ptr);
    } else {
        std.heap.page_allocator.free(mem);
    }
}

// ---------------------------------------------------------------------------
// Platform layer: Windows (bare kernel32)
// ---------------------------------------------------------------------------
const ConnectResult = union(enum) {
    connected: if (is_windows) win.HANDLE else i32,
    not_found,
    security_rejected,
};

const win = if (is_windows) struct {
    const HANDLE = *anyopaque;
    const INVALID_HANDLE_VALUE: HANDLE = @ptrFromInt(@as(usize, @bitCast(@as(isize, -1))));
    const CURRENT_PROCESS: HANDLE = @ptrFromInt(@as(usize, @bitCast(@as(isize, -1))));
    const GENERIC_READ: u32 = 0x80000000;
    const GENERIC_WRITE: u32 = 0x40000000;
    const READ_CONTROL: u32 = 0x00020000;
    const OPEN_EXISTING: u32 = 3;
    const SECURITY_SQOS_PRESENT: u32 = 0x00100000;
    const SECURITY_IDENTIFICATION: u32 = 0x00010000;
    const FILE_FLAG_OVERLAPPED: u32 = 0x40000000;
    const ERROR_PIPE_BUSY: u32 = 231;
    const ERROR_IO_PENDING: u32 = 997;
    const OWNER_SECURITY_INFORMATION: u32 = 0x00000001;
    const TOKEN_QUERY: u32 = 0x0008;
    const TOKEN_INFO_OWNER: u32 = 4;
    const STATUS_BUFFER_TOO_SMALL: i32 = @bitCast(@as(u32, 0xC0000023));
    const CP_UTF8: u32 = 65001;
    const INFINITE: u32 = 0xFFFFFFFF;
    const STD_INPUT_HANDLE: u32 = @bitCast(@as(i32, -10));
    const STD_OUTPUT_HANDLE: u32 = @bitCast(@as(i32, -11));
    const STD_ERROR_HANDLE: u32 = @bitCast(@as(i32, -12));

    const OVERLAPPED = extern struct {
        Internal: usize = 0,
        InternalHigh: usize = 0,
        Offset: u32 = 0,
        OffsetHigh: u32 = 0,
        hEvent: ?HANDLE = null,
    };

    const CRITICAL_SECTION = extern struct {
        DebugInfo: ?*anyopaque = null,
        LockCount: i32 = 0,
        RecursionCount: i32 = 0,
        OwningThread: ?HANDLE = null,
        LockSemaphore: ?HANDLE = null,
        SpinCount: usize = 0,
    };

    const STARTUPINFOW = extern struct {
        cb: u32 = @sizeOf(STARTUPINFOW),
        lpReserved: ?[*]u16 = null,
        lpDesktop: ?[*]u16 = null,
        lpTitle: ?[*]u16 = null,
        dwX: u32 = 0,
        dwY: u32 = 0,
        dwXSize: u32 = 0,
        dwYSize: u32 = 0,
        dwXCountChars: u32 = 0,
        dwYCountChars: u32 = 0,
        dwFillAttribute: u32 = 0,
        dwFlags: u32 = 0,
        wShowWindow: u16 = 0,
        cbReserved2: u16 = 0,
        lpReserved2: ?[*]u8 = null,
        hStdInput: ?HANDLE = null,
        hStdOutput: ?HANDLE = null,
        hStdError: ?HANDLE = null,
    };

    const PROCESS_INFORMATION = extern struct {
        hProcess: ?HANDLE = null,
        hThread: ?HANDLE = null,
        dwProcessId: u32 = 0,
        dwThreadId: u32 = 0,
    };

    extern "kernel32" fn ExitProcess(uExitCode: u32) callconv(.winapi) noreturn;
    extern "kernel32" fn GetStdHandle(nStdHandle: u32) callconv(.winapi) ?HANDLE;
    extern "kernel32" fn GetCommandLineW() callconv(.winapi) [*:0]const u16;
    extern "kernel32" fn GetModuleFileNameW(hModule: ?HANDLE, lpFilename: [*]u16, nSize: u32) callconv(.winapi) u32;
    extern "kernel32" fn GetCurrentDirectoryW(nBufferLength: u32, lpBuffer: ?[*]u16) callconv(.winapi) u32;
    extern "kernel32" fn GetEnvironmentVariableW(lpName: [*:0]const u16, lpBuffer: ?[*]u16, nSize: u32) callconv(.winapi) u32;
    extern "kernel32" fn GetEnvironmentStringsW() callconv(.winapi) ?[*]u16;
    extern "kernel32" fn FreeEnvironmentStringsW(penv: [*]u16) callconv(.winapi) i32;
    extern "kernel32" fn WideCharToMultiByte(cp: u32, flags: u32, src: [*]const u16, src_len: i32, dst: ?[*]u8, dst_len: i32, def: ?*anyopaque, used: ?*i32) callconv(.winapi) i32;
    extern "kernel32" fn MultiByteToWideChar(cp: u32, flags: u32, src: [*]const u8, src_len: i32, dst: ?[*]u16, dst_len: i32) callconv(.winapi) i32;
    extern "kernel32" fn CreateFileW(name: [*:0]const u16, access: u32, share: u32, sa: ?*anyopaque, disp: u32, flags: u32, tmpl: ?HANDLE) callconv(.winapi) HANDLE;
    extern "kernel32" fn WaitNamedPipeW(name: [*:0]const u16, timeout: u32) callconv(.winapi) i32;
    extern "kernel32" fn ReadFile(h: HANDLE, buf: [*]u8, n: u32, got: *u32, ov: ?*OVERLAPPED) callconv(.winapi) i32;
    extern "kernel32" fn WriteFile(h: HANDLE, buf: [*]const u8, n: u32, sent: *u32, ov: ?*OVERLAPPED) callconv(.winapi) i32;
    extern "kernel32" fn GetOverlappedResult(h: HANDLE, ov: *OVERLAPPED, transferred: *u32, bWait: i32) callconv(.winapi) i32;
    extern "kernel32" fn CreateEventW(sa: ?*anyopaque, manual_reset: i32, initial_state: i32, name: ?[*:0]const u16) callconv(.winapi) ?HANDLE;
    extern "kernel32" fn SetEvent(h: HANDLE) callconv(.winapi) i32;
    extern "kernel32" fn CreateThread(sa: ?*anyopaque, stack_size: usize, start: *const fn (?*anyopaque) callconv(.winapi) u32, param: ?*anyopaque, flags: u32, tid: ?*u32) callconv(.winapi) ?HANDLE;
    extern "kernel32" fn InitializeCriticalSection(cs: *CRITICAL_SECTION) callconv(.winapi) void;
    extern "kernel32" fn EnterCriticalSection(cs: *CRITICAL_SECTION) callconv(.winapi) void;
    extern "kernel32" fn LeaveCriticalSection(cs: *CRITICAL_SECTION) callconv(.winapi) void;
    extern "kernel32" fn CloseHandle(h: HANDLE) callconv(.winapi) i32;
    extern "kernel32" fn GetLastError() callconv(.winapi) u32;
    extern "kernel32" fn CreateProcessW(app: ?[*:0]const u16, cmd: ?[*]u16, pa: ?*anyopaque, ta: ?*anyopaque, inherit: i32, flags: u32, env: ?*anyopaque, cwd: ?[*:0]const u16, si: *STARTUPINFOW, pi: *PROCESS_INFORMATION) callconv(.winapi) i32;
    extern "kernel32" fn WaitForSingleObject(h: HANDLE, ms: u32) callconv(.winapi) u32;
    extern "kernel32" fn GetExitCodeProcess(h: HANDLE, code: *u32) callconv(.winapi) i32;

    // Zero-advapi32 ntdll security verification (ntdll is always mapped in every Windows process).
    extern "ntdll" fn NtQuerySecurityObject(handle: HANDLE, sec_info: u32, sd: ?[*]u8, len: u32, needed: *u32) callconv(.winapi) i32;
    extern "ntdll" fn RtlGetOwnerSecurityDescriptor(sd: [*]const u8, owner: *?*const anyopaque, defaulted: *u8) callconv(.winapi) i32;
    extern "ntdll" fn NtOpenProcessToken(process: HANDLE, access: u32, token: *?HANDLE) callconv(.winapi) i32;
    extern "ntdll" fn NtQueryInformationToken(token: HANDLE, class: u32, info: ?[*]u8, len: u32, ret_len: *u32) callconv(.winapi) i32;
    extern "ntdll" fn RtlEqualSid(sid1: *const anyopaque, sid2: *const anyopaque) callconv(.winapi) u8;
    fn wlen(p: [*]const u16) usize {
        var n: usize = 0;
        while (p[n] != 0) n += 1;
        return n;
    }

    fn utf16ToUtf8(src: []const u16) []u8 {
        if (src.len == 0) return &.{};
        const n = WideCharToMultiByte(CP_UTF8, 0, src.ptr, @intCast(src.len), null, 0, null, null);
        if (n <= 0) return &.{};
        const out = osAlloc(@intCast(n));
        _ = WideCharToMultiByte(CP_UTF8, 0, src.ptr, @intCast(src.len), out.ptr, n, null, null);
        return out;
    }

    fn writeUtf16Str(b: *Buf, src: []const u16) void {
        if (src.len == 0) {
            b.varU32(0);
            return;
        }
        const n = WideCharToMultiByte(CP_UTF8, 0, src.ptr, @intCast(src.len), null, 0, null, null);
        if (n <= 0) {
            b.varU32(0);
            return;
        }
        const un: usize = @intCast(n);
        b.varU32(@intCast(un));
        b.ensure(un);
        _ = WideCharToMultiByte(CP_UTF8, 0, src.ptr, @intCast(src.len), b.data.ptr + b.len, n, null, null);
        b.len += un;
    }

    fn utf8ToUtf16Z(src: []const u8, dst: []u16) ?usize {
        if (src.len == 0) {
            if (dst.len == 0) return null;
            dst[0] = 0;
            return 0;
        }
        const n = MultiByteToWideChar(CP_UTF8, 0, src.ptr, @intCast(src.len), dst.ptr, @intCast(dst.len - 1));
        if (n <= 0) return null;
        dst[@intCast(n)] = 0;
        return @intCast(n);
    }

    fn hasEnv(comptime name: []const u8) bool {
        var wname: [64:0]u16 = undefined;
        inline for (name, 0..) |c, idx| wname[idx] = c;
        wname[name.len] = 0;
        var tmp: [4]u16 = undefined;
        return GetEnvironmentVariableW(&wname, &tmp, 4) > 0;
    }

    fn getEnvUtf8(comptime name: []const u8) ?[]u8 {
        var wname: [64:0]u16 = undefined;
        inline for (name, 0..) |c, idx| wname[idx] = c;
        wname[name.len] = 0;
        const needed = GetEnvironmentVariableW(&wname, null, 0);
        if (needed == 0) return null;
        const wbuf = @as([*]u16, @ptrCast(@alignCast(osAlloc((needed + 1) * 2).ptr)))[0 .. needed + 1];
        const actual = GetEnvironmentVariableW(&wname, wbuf.ptr, needed);
        if (actual == 0) return null;
        return utf16ToUtf8(wbuf[0..actual]);
    }

    fn parseArgs() [][]const u8 {
        const raw = GetCommandLineW();
        const raw_len = wlen(raw);
        var p: usize = 0;
        while (p < raw_len and (raw[p] == ' ' or raw[p] == '\t')) p += 1;
        if (p < raw_len and raw[p] == '"') {
            p += 1;
            while (p < raw_len and raw[p] != '"') p += 1;
            if (p < raw_len and raw[p] == '"') p += 1;
        } else {
            while (p < raw_len and raw[p] != ' ' and raw[p] != '\t') p += 1;
        }

        var args_cap: usize = 16;
        var args_mem = osAlloc(args_cap * @sizeOf([]const u8));
        var args: [*][]const u8 = @ptrCast(@alignCast(args_mem.ptr));
        var argc: usize = 0;

        var tmp_cap: usize = 128;
        var tmp_mem = osAlloc(tmp_cap * 2);
        var tmp: [*]u16 = @ptrCast(@alignCast(tmp_mem.ptr));

        while (p < raw_len) {
            while (p < raw_len and (raw[p] == ' ' or raw[p] == '\t')) p += 1;
            if (p >= raw_len) break;
            var alen: usize = 0;
            var in_quote = false;
            while (p < raw_len) {
                const c = raw[p];
                if (!in_quote and (c == ' ' or c == '\t')) break;
                if (c == '\\') {
                    var bs: usize = 0;
                    while (p < raw_len and raw[p] == '\\') {
                        bs += 1;
                        p += 1;
                    }
                    if (p < raw_len and raw[p] == '"') {
                        const emit_bs = bs / 2;
                        if (alen + emit_bs + 2 >= tmp_cap) {
                            while (alen + emit_bs + 2 >= tmp_cap) tmp_cap *= 2;
                            tmp_mem = osRealloc(tmp_mem, tmp_cap * 2);
                            tmp = @ptrCast(@alignCast(tmp_mem.ptr));
                        }
                        var k: usize = 0;
                        while (k < emit_bs) : (k += 1) {
                            tmp[alen] = '\\';
                            alen += 1;
                        }
                        if ((bs & 1) == 1) {
                            tmp[alen] = '"';
                            alen += 1;
                            p += 1;
                        } else {
                            in_quote = !in_quote;
                            p += 1;
                        }
                    } else {
                        if (alen + bs + 2 >= tmp_cap) {
                            while (alen + bs + 2 >= tmp_cap) tmp_cap *= 2;
                            tmp_mem = osRealloc(tmp_mem, tmp_cap * 2);
                            tmp = @ptrCast(@alignCast(tmp_mem.ptr));
                        }
                        var k: usize = 0;
                        while (k < bs) : (k += 1) {
                            tmp[alen] = '\\';
                            alen += 1;
                        }
                    }
                } else if (c == '"') {
                    if (in_quote and p + 1 < raw_len and raw[p + 1] == '"') {
                        if (alen + 2 >= tmp_cap) {
                            tmp_cap *= 2;
                            tmp_mem = osRealloc(tmp_mem, tmp_cap * 2);
                            tmp = @ptrCast(@alignCast(tmp_mem.ptr));
                        }
                        tmp[alen] = '"';
                        alen += 1;
                        p += 2;
                    } else {
                        in_quote = !in_quote;
                        p += 1;
                    }
                } else {
                    if (alen + 2 >= tmp_cap) {
                        tmp_cap *= 2;
                        tmp_mem = osRealloc(tmp_mem, tmp_cap * 2);
                        tmp = @ptrCast(@alignCast(tmp_mem.ptr));
                    }
                    tmp[alen] = c;
                    alen += 1;
                    p += 1;
                }
            }
            if (argc >= args_cap) {
                args_cap *= 2;
                args_mem = osRealloc(args_mem, args_cap * @sizeOf([]const u8));
                args = @ptrCast(@alignCast(args_mem.ptr));
            }
            args[argc] = utf16ToUtf8(tmp[0..alen]);
            argc += 1;
        }
        return args[0..argc];
    }

    fn fallbackCli() noreturn {
        const exe_mem = osAlloc(32768 * 2);
        const exe_path: [*]u16 = @ptrCast(@alignCast(exe_mem.ptr));
        const n = GetModuleFileNameW(null, exe_path, 32768);
        if (n == 0 or n >= 32760) ExitProcess(1);
        var slash: usize = n;
        while (slash > 0 and exe_path[slash - 1] != '\\' and exe_path[slash - 1] != '/') slash -= 1;

        const suffix = "fsion-cli.exe";
        const cli_mem = osAlloc((slash + suffix.len + 1) * 2);
        const cli_path: [*]u16 = @ptrCast(@alignCast(cli_mem.ptr));
        var i: usize = 0;
        while (i < slash) : (i += 1) cli_path[i] = exe_path[i];
        inline for (suffix, 0..) |c, idx| cli_path[slash + idx] = c;
        cli_path[slash + suffix.len] = 0;

        const raw = GetCommandLineW();
        const rlen = wlen(raw);
        const cmd_mem = osAlloc((rlen + 1) * 2);
        const cmd_copy: [*]u16 = @ptrCast(@alignCast(cmd_mem.ptr));
        i = 0;
        while (i <= rlen) : (i += 1) cmd_copy[i] = raw[i];

        var si = STARTUPINFOW{};
        var pi = PROCESS_INFORMATION{};
        if (CreateProcessW(@ptrCast(cli_path), cmd_copy, null, null, 1, 0, null, null, &si, &pi) == 0) {
            ExitProcess(1);
        }
        _ = WaitForSingleObject(pi.hProcess.?, INFINITE);
        var code: u32 = 1;
        _ = GetExitCodeProcess(pi.hProcess.?, &code);
        _ = CloseHandle(pi.hThread.?);
        _ = CloseHandle(pi.hProcess.?);
        ExitProcess(code);
    }

    fn verifyPipeOwner(pipe: HANDLE) bool {
        // Parity with .NET NamedPipeClientStream.ValidateRemotePipeUser():
        // compare the pipe security descriptor Owner SID against current process TokenOwner.
        var sd_stack: [256]u8 align(8) = undefined;
        var sd_needed: u32 = 0;
        var sd_slice: []u8 = &sd_stack;
        var sd_heap: []u8 = &.{};
        defer if (sd_heap.len > 0) osFree(sd_heap);

        var st = NtQuerySecurityObject(pipe, OWNER_SECURITY_INFORMATION, sd_slice.ptr, @intCast(sd_slice.len), &sd_needed);
        if (st == STATUS_BUFFER_TOO_SMALL and sd_needed > sd_slice.len and sd_needed <= 65536) {
            sd_heap = osAlloc(sd_needed);
            sd_slice = sd_heap;
            st = NtQuerySecurityObject(pipe, OWNER_SECURITY_INFORMATION, sd_slice.ptr, @intCast(sd_slice.len), &sd_needed);
        }
        if (st < 0) return false;

        var pipe_owner: ?*const anyopaque = null;
        var owner_defaulted: u8 = 0;
        if (RtlGetOwnerSecurityDescriptor(sd_slice.ptr, &pipe_owner, &owner_defaulted) < 0) return false;
        const remote_sid = pipe_owner orelse return false;

        var token_opt: ?HANDLE = null;
        if (NtOpenProcessToken(CURRENT_PROCESS, TOKEN_QUERY, &token_opt) < 0) return false;
        const token = token_opt orelse return false;
        defer _ = CloseHandle(token);

        var tok_stack: [256]u8 align(8) = undefined;
        var tok_needed: u32 = 0;
        var tok_slice: []u8 = &tok_stack;
        var tok_heap: []u8 = &.{};
        defer if (tok_heap.len > 0) osFree(tok_heap);

        var tst = NtQueryInformationToken(token, TOKEN_INFO_OWNER, tok_slice.ptr, @intCast(tok_slice.len), &tok_needed);
        if (tst == STATUS_BUFFER_TOO_SMALL and tok_needed > tok_slice.len and tok_needed <= 65536) {
            tok_heap = osAlloc(tok_needed);
            tok_slice = tok_heap;
            tst = NtQueryInformationToken(token, TOKEN_INFO_OWNER, tok_slice.ptr, @intCast(tok_slice.len), &tok_needed);
        }
        if (tst < 0 or tok_needed < @sizeOf(?*const anyopaque)) return false;

        // TOKEN_OWNER struct starts with PSID Owner at offset 0.
        const local_owner_ptr: *const ?*const anyopaque = @ptrCast(@alignCast(tok_slice.ptr));
        const local_sid = local_owner_ptr.* orelse return false;
        return RtlEqualSid(remote_sid, local_sid) != 0;
    }

    fn connectEndpoint(endpoint: []const u8) ConnectResult {
        var path_utf8: [540]u8 = undefined;
        const pfx = "\\\\.\\pipe\\";
        if (pfx.len + endpoint.len >= path_utf8.len) return .not_found;
        @memcpy(path_utf8[0..pfx.len], pfx);
        @memcpy(path_utf8[pfx.len .. pfx.len + endpoint.len], endpoint);
        var wpath: [540]u16 = undefined;
        _ = utf8ToUtf16Z(path_utf8[0 .. pfx.len + endpoint.len], &wpath) orelse return .not_found;

        var attempt: usize = 0;
        while (attempt < 3) : (attempt += 1) {
            const h = CreateFileW(
                @ptrCast(&wpath),
                GENERIC_READ | GENERIC_WRITE | READ_CONTROL,
                0,
                null,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION,
                null,
            );
            if (h != INVALID_HANDLE_VALUE) {
                if (!verifyPipeOwner(h)) {
                    _ = CloseHandle(h);
                    return .security_rejected;
                }
                return .{ .connected = h };
            }
            if (GetLastError() == ERROR_PIPE_BUSY) {
                _ = WaitNamedPipeW(@ptrCast(&wpath), 50);
                continue;
            }
            return .not_found;
        }
        return .not_found;
    }

    fn writeCwd(req: *Buf) void {
        const cwd_len = GetCurrentDirectoryW(0, null);
        if (cwd_len > 0) {
            const wbuf = @as([*]u16, @ptrCast(@alignCast(osAlloc((cwd_len + 2) * 2).ptr)));
            const actual = GetCurrentDirectoryW(cwd_len + 1, wbuf);
            writeUtf16Str(req, wbuf[0..actual]);
        } else {
            req.varU32(0);
        }
    }

    fn writeEnv(req: *Buf) void {
        const env_block = GetEnvironmentStringsW() orelse {
            req.i32Le(0);
            return;
        };
        defer _ = FreeEnvironmentStringsW(env_block);

        var count: i32 = 0;
        var p: usize = 0;
        while (env_block[p] != 0) {
            const elen = wlen(env_block + p);
            var eq: usize = 0;
            while (eq < elen and env_block[p + eq] != '=') eq += 1;
            if (eq > 0 and eq < elen) count += 1;
            p += elen + 1;
        }
        req.i32Le(count);
        p = 0;
        while (env_block[p] != 0) {
            const elen = wlen(env_block + p);
            var eq: usize = 0;
            while (eq < elen and env_block[p + eq] != '=') eq += 1;
            if (eq > 0 and eq < elen) {
                writeUtf16Str(req, env_block[p .. p + eq]);
                writeUtf16Str(req, env_block[p + eq + 1 .. p + elen]);
            }
            p += elen + 1;
        }
    }

    fn readExactPipe(h: HANDLE, ev: HANDLE, dst: []u8) bool {
        var got: usize = 0;
        while (got < dst.len) {
            var ov = OVERLAPPED{ .hEvent = ev };
            var chunk: u32 = 0;
            const rem: u32 = @intCast(@min(dst.len - got, 0x40000000));
            if (ReadFile(h, dst.ptr + got, rem, &chunk, &ov) == 0) {
                if (GetLastError() != ERROR_IO_PENDING) return false;
                if (GetOverlappedResult(h, &ov, &chunk, 1) == 0) return false;
            }
            if (chunk == 0) return false;
            got += chunk;
        }
        return true;
    }

    fn writeAllPipe(h: HANDLE, ev: HANDLE, src: []const u8) bool {
        var sent: usize = 0;
        while (sent < src.len) {
            var ov = OVERLAPPED{ .hEvent = ev };
            var chunk: u32 = 0;
            const rem: u32 = @intCast(@min(src.len - sent, 0x40000000));
            if (WriteFile(h, src.ptr + sent, rem, &chunk, &ov) == 0) {
                if (GetLastError() != ERROR_IO_PENDING) return false;
                if (GetOverlappedResult(h, &ov, &chunk, 1) == 0) return false;
            }
            if (chunk == 0) return false;
            sent += chunk;
        }
        return true;
    }

    fn writeAllStd(h: HANDLE, src: []const u8) bool {
        var sent: usize = 0;
        while (sent < src.len) {
            var chunk: u32 = 0;
            const rem: u32 = @intCast(@min(src.len - sent, 0x40000000));
            if (WriteFile(h, src.ptr + sent, rem, &chunk, null) == 0 or chunk == 0) return false;
            sent += chunk;
        }
        return true;
    }

    fn readStdinSome(dst: []u8) usize {
        const h = GetStdHandle(STD_INPUT_HANDLE) orelse return 0;
        var got: u32 = 0;
        if (ReadFile(h, dst.ptr, @intCast(dst.len), &got, null) == 0) return 0;
        return got;
    }

    // Single lazily-started background stdin reader with serialized queue and its own write event.
    const StdinWorker = struct {
        pipe: HANDLE,
        write_ev: HANDLE,
        wake_ev: HANDLE,
        cs: CRITICAL_SECTION,
        queue: [64]usize,
        head: usize,
        tail: usize,
        count: usize,
        started: bool,

        fn init(pipe: HANDLE, write_ev: HANDLE, wake_ev: HANDLE) StdinWorker {
            var w = StdinWorker{
                .pipe = pipe,
                .write_ev = write_ev,
                .wake_ev = wake_ev,
                .cs = .{},
                .queue = @splat(0),
                .head = 0,
                .tail = 0,
                .count = 0,
                .started = false,
            };
            InitializeCriticalSection(&w.cs);
            return w;
        }

        fn workerProc(param: ?*anyopaque) callconv(.winapi) u32 {
            const self: *StdinWorker = @ptrCast(@alignCast(param orelse return 0));
            while (true) {
                var max_bytes: usize = 0;
                var has_item = false;
                EnterCriticalSection(&self.cs);
                if (self.count > 0) {
                    max_bytes = self.queue[self.head];
                    self.head = (self.head + 1) % self.queue.len;
                    self.count -= 1;
                    has_item = true;
                }
                LeaveCriticalSection(&self.cs);

                if (!has_item) {
                    _ = WaitForSingleObject(self.wake_ev, INFINITE);
                    continue;
                }

                const tmp = osAlloc(max_bytes);
                const got = readStdinSome(tmp);
                var chunk = Buf{};
                chunk.i32Le(0);
                chunk.i32Le(PROTOCOL_VERSION);
                chunk.u8Val(6); // StdinChunk
                chunk.u8Val(if (got == 0) 1 else 0);
                chunk.i32Le(@intCast(got));
                if (got > 0) chunk.bytes(tmp[0..got]);
                chunk.patchFrameLen();
                const ok = writeAllPipe(self.pipe, self.write_ev, chunk.data[0..chunk.len]);
                chunk.deinit();
                osFree(tmp);
                if (!ok) return 0;
            }
        }

        fn enqueue(self: *StdinWorker, max_bytes: usize) bool {
            EnterCriticalSection(&self.cs);
            const need_start = !self.started;
            if (self.count < self.queue.len) {
                self.queue[self.tail] = max_bytes;
                self.tail = (self.tail + 1) % self.queue.len;
                self.count += 1;
            }
            if (need_start) self.started = true;
            LeaveCriticalSection(&self.cs);

            if (need_start) {
                const th = CreateThread(null, 0, workerProc, self, 0, null) orelse return false;
                _ = CloseHandle(th);
            }
            _ = SetEvent(self.wake_ev);
            return true;
        }
    };
} else struct {};

// ---------------------------------------------------------------------------
// Platform layer: POSIX (Linux direct syscalls, macOS libSystem)
// ---------------------------------------------------------------------------
const posix = if (!is_windows) struct {
    const AF_UNIX: u32 = 1;
    const SOCK_STREAM: u32 = 1;

    const SockAddrUnLinux = extern struct {
        family: u16,
        path: [108]u8,
    };

    const SockAddrUnMac = extern struct {
        len: u8,
        family: u8,
        path: [104]u8,
    };

    fn getEnvNonEmpty(env_block: [:null]const ?[*:0]const u8, name: []const u8) ?[]const u8 {
        for (env_block) |maybe_entry| {
            const entry = maybe_entry orelse break;
            const s = std.mem.span(entry);
            if (s.len > name.len and s[name.len] == '=' and std.mem.eql(u8, s[0..name.len], name)) {
                const val = s[name.len + 1 ..];
                if (val.len == 0) return null;
                return val;
            }
        }
        return null;
    }

    fn hasEnv(env_block: [:null]const ?[*:0]const u8, name: []const u8) bool {
        return getEnvNonEmpty(env_block, name) != null;
    }

    fn writeAllFd(fd: i32, src: []const u8) bool {
        var sent: usize = 0;
        while (sent < src.len) {
            const rc: isize = if (is_linux)
                @bitCast(std.os.linux.write(fd, src.ptr + sent, src.len - sent))
            else
                std.c.write(fd, src.ptr + sent, src.len - sent);
            if (rc <= 0) return false;
            sent += @intCast(rc);
        }
        return true;
    }

    fn readExactFd(fd: i32, dst: []u8) bool {
        var got: usize = 0;
        while (got < dst.len) {
            const rc: isize = if (is_linux)
                @bitCast(std.os.linux.read(fd, dst.ptr + got, dst.len - got))
            else
                std.c.read(fd, dst.ptr + got, dst.len - got);
            if (rc <= 0) return false;
            got += @intCast(rc);
        }
        return true;
    }

    fn readStdinSome(dst: []u8) usize {
        const rc: isize = if (is_linux)
            @bitCast(std.os.linux.read(0, dst.ptr, dst.len))
        else
            std.c.read(0, dst.ptr, dst.len);
        if (rc <= 0) return 0;
        return @intCast(rc);
    }

    fn closeFd(fd: i32) void {
        if (is_linux) {
            _ = std.os.linux.close(fd);
        } else {
            _ = std.c.close(fd);
        }
    }

    fn getEuid() u32 {
        if (is_linux) return std.os.linux.geteuid();
        return std.c.geteuid();
    }

    fn formatU32(val: u32, buf: []u8) []const u8 {
        var v = val;
        var idx: usize = buf.len;
        if (v == 0) {
            idx -= 1;
            buf[idx] = '0';
            return buf[idx..];
        }
        while (v > 0) {
            idx -= 1;
            buf[idx] = @intCast('0' + (v % 10));
            v /= 10;
        }
        return buf[idx..];
    }

    const DirCheck = enum { ok, missing, unsafe };

    fn verifyPrivateSocketDir(dir_z: [*:0]const u8) DirCheck {
        const my_uid = getEuid();
        if (is_linux) {
            var stx: std.os.linux.Statx = undefined;
            const rc: isize = @bitCast(std.os.linux.statx(
                std.os.linux.AT.FDCWD,
                dir_z,
                std.os.linux.AT.SYMLINK_NOFOLLOW,
                .{ .TYPE = true, .MODE = true, .UID = true },
                &stx,
            ));
            if (rc < 0) {
                // ENOENT (2) or ENOTDIR (20) -> daemon not started yet
                if (rc == -2 or rc == -20) return .missing;
                return .unsafe;
            }
            if (!stx.mask.TYPE or !stx.mask.MODE or !stx.mask.UID) return .unsafe;
            if ((stx.mode & 0o170000) != 0o040000) return .unsafe;
            if (stx.uid != my_uid) return .unsafe;
            if ((stx.mode & 0o077) != 0) return .unsafe;
            return .ok;
        } else {
            var st: std.c.Stat = undefined;
            if (std.c.fstatat(std.c.AT.FDCWD, dir_z, &st, std.c.AT.SYMLINK_NOFOLLOW) != 0) {
                const err = std.c._errno().*;
                if (err == 2 or err == 20) return .missing;
                return .unsafe;
            }
            if ((st.mode & 0o170000) != 0o040000) return .unsafe;
            if (st.uid != my_uid) return .unsafe;
            if ((st.mode & 0o077) != 0) return .unsafe;
            return .ok;
        }
    }

    const SocketPathResult = union(enum) {
        path: []const u8,
        missing_or_long,
        unsafe,
    };

    fn resolveSocketPath(env_block: [:null]const ?[*:0]const u8, endpoint: []const u8, out: []u8) SocketPathResult {
        var uid_buf: [16]u8 = undefined;
        const uid_s = formatU32(getEuid(), &uid_buf);

        const base_root = getEnvNonEmpty(env_block, "XDG_RUNTIME_DIR") orelse getEnvNonEmpty(env_block, "TMPDIR") orelse "/tmp";
        var root_len = base_root.len;
        while (root_len > 1 and base_root[root_len - 1] == '/') root_len -= 1;

        var san: [256]u8 = undefined;
        if (endpoint.len > san.len) return .missing_or_long;
        for (endpoint, 0..) |c, idx| san[idx] = sanitizeChar(c);
        const san_s = san[0..endpoint.len];

        // If candidate > 100 bytes, fall back to F# fsion-cli (handles SHA256 socket shortening).
        if (root_len + 7 + uid_s.len + 1 + san_s.len + 5 > 100) return .missing_or_long;
        if (root_len + 7 + uid_s.len + 1 + san_s.len + 6 > out.len) return .missing_or_long;

        var pos: usize = 0;
        @memcpy(out[pos .. pos + root_len], base_root[0..root_len]);
        pos += root_len;
        @memcpy(out[pos .. pos + 7], "/fsion-");
        pos += 7;
        @memcpy(out[pos .. pos + uid_s.len], uid_s);
        pos += uid_s.len;
        out[pos] = 0;

        switch (verifyPrivateSocketDir(@ptrCast(out.ptr))) {
            .ok => {},
            .missing => return .missing_or_long,
            .unsafe => return .unsafe,
        }

        out[pos] = '/';
        pos += 1;
        @memcpy(out[pos .. pos + san_s.len], san_s);
        pos += san_s.len;
        @memcpy(out[pos .. pos + 5], ".sock");
        pos += 5;
        out[pos] = 0;
        return .{ .path = out[0..pos] };
    }

    fn pollConnOrStdin(conn_fd: i32, stdin_pending: bool) u32 {
        var fds: [2]std.posix.pollfd = .{
            .{ .fd = conn_fd, .events = std.posix.POLL.IN, .revents = 0 },
            .{ .fd = 0, .events = std.posix.POLL.IN, .revents = 0 },
        };
        const nfds: usize = if (stdin_pending) 2 else 1;
        if (is_linux) {
            const rc: isize = @bitCast(std.os.linux.poll(&fds, @intCast(nfds), -1));
            if (rc <= 0) return 0;
        } else {
            const rc = std.c.poll(&fds, @intCast(nfds), -1);
            if (rc <= 0) return 0;
        }
        var mask: u32 = 0;
        if (fds[0].revents != 0) mask |= 1;
        if (stdin_pending and fds[1].revents != 0) mask |= 2;
        return mask;
    }

    fn connectEndpoint(env_block: [:null]const ?[*:0]const u8, endpoint: []const u8) ConnectResult {
        var spath_buf: [128]u8 = undefined;
        const spath = switch (resolveSocketPath(env_block, endpoint, &spath_buf)) {
            .path => |p| p,
            .missing_or_long => return .not_found,
            .unsafe => return .security_rejected,
        };
        if (spath.len >= 104) return .not_found;
        const fd_signed: isize = if (is_linux)
            @bitCast(std.os.linux.socket(AF_UNIX, SOCK_STREAM | std.os.linux.SOCK.CLOEXEC, 0))
        else
            std.c.socket(AF_UNIX, SOCK_STREAM, 0);
        if (fd_signed < 0) return .not_found;
        const fd: i32 = @intCast(fd_signed);

        if (is_macos) {
            var addr = SockAddrUnMac{
                .len = @intCast(2 + spath.len + 1),
                .family = @intCast(AF_UNIX),
                .path = @splat(0),
            };
            @memcpy(addr.path[0..spath.len], spath);
            if (std.c.connect(fd, @ptrCast(&addr), addr.len) != 0) {
                closeFd(fd);
                return .not_found;
            }
        } else {
            var addr = SockAddrUnLinux{
                .family = @intCast(AF_UNIX),
                .path = @splat(0),
            };
            @memcpy(addr.path[0..spath.len], spath);
            const rc: isize = @bitCast(std.os.linux.connect(fd, @ptrCast(&addr), @intCast(2 + spath.len + 1)));
            if (rc != 0) {
                closeFd(fd);
                return .not_found;
            }
        }
        return .{ .connected = fd };
    }

    fn writeCwd(req: *Buf) void {
        var cwd_buf: [4096]u8 = undefined;
        if (is_linux) {
            const signed: isize = @bitCast(std.os.linux.getcwd(&cwd_buf, cwd_buf.len));
            if (signed > 1) {
                req.str(cwd_buf[0..@intCast(signed - 1)]);
            } else {
                req.str("");
            }
        } else {
            if (std.c.getcwd(&cwd_buf, cwd_buf.len)) |_| {
                const clen = std.mem.indexOfScalar(u8, &cwd_buf, 0) orelse 0;
                req.str(cwd_buf[0..clen]);
            } else {
                req.str("");
            }
        }
    }

    fn writeEnv(env_block: [:null]const ?[*:0]const u8, req: *Buf) void {
        var count: i32 = 0;
        for (env_block) |maybe_entry| {
            const entry = maybe_entry orelse break;
            const s = std.mem.span(entry);
            if (std.mem.indexOfScalar(u8, s, '=')) |eq| {
                if (eq > 0) count += 1;
            }
        }
        req.i32Le(count);
        for (env_block) |maybe_entry| {
            const entry = maybe_entry orelse break;
            const s = std.mem.span(entry);
            if (std.mem.indexOfScalar(u8, s, '=')) |eq| {
                if (eq > 0) {
                    req.str(s[0..eq]);
                    req.str(s[eq + 1 ..]);
                }
            }
        }
    }

    extern "c" fn _NSGetExecutablePath(buf: [*]u8, bufsize: *u32) i32;

    fn fallbackCli(raw_argv: []const [*:0]const u8, env_block: [:null]const ?[*:0]const u8) noreturn {
        var exe_buf: [4096]u8 = undefined;
        var exe_len: usize = 0;
        if (is_linux) {
            const signed: isize = @bitCast(std.os.linux.readlink("/proc/self/exe", &exe_buf, exe_buf.len - 1));
            if (signed <= 0) osExit(1);
            exe_len = @intCast(signed);
        } else {
            var sz: u32 = @intCast(exe_buf.len - 1);
            if (_NSGetExecutablePath(&exe_buf, &sz) != 0) osExit(1);
            exe_len = std.mem.indexOfScalar(u8, &exe_buf, 0) orelse osExit(1);
        }
        var slash = exe_len;
        while (slash > 0 and exe_buf[slash - 1] != '/') slash -= 1;

        const suffix = "fsion-cli";
        if (slash + suffix.len + 1 > exe_buf.len) osExit(1);
        @memcpy(exe_buf[slash .. slash + suffix.len], suffix);
        exe_buf[slash + suffix.len] = 0;
        const cli_path: [*:0]const u8 = @ptrCast(&exe_buf);

        const argc = raw_argv.len;
        const argv_mem = osAlloc((argc + 1) * @sizeOf(?[*:0]const u8));
        const new_argv: [*]?[*:0]const u8 = @ptrCast(@alignCast(argv_mem.ptr));
        new_argv[0] = cli_path;
        var i: usize = 1;
        while (i < argc) : (i += 1) new_argv[i] = raw_argv[i];
        new_argv[argc] = null;

        const envp: [*:null]const ?[*:0]const u8 = @ptrCast(env_block.ptr);
        if (is_linux) {
            _ = std.os.linux.execve(cli_path, @ptrCast(new_argv), envp);
        } else {
            _ = std.c.execve(cli_path, @ptrCast(new_argv), envp);
        }
        osExit(1);
    }
} else struct {};

fn osExit(code: u32) noreturn {
    if (is_windows) {
        win.ExitProcess(code);
    } else if (is_linux) {
        std.os.linux.exit_group(@intCast(code));
    } else {
        std.c.exit(@intCast(code));
    }
}

fn writeStdout(data: []const u8) void {
    if (data.len == 0) return;
    if (is_windows) {
        if (win.GetStdHandle(win.STD_OUTPUT_HANDLE)) |h| _ = win.writeAllStd(h, data);
    } else {
        _ = posix.writeAllFd(1, data);
    }
}

fn writeStderr(data: []const u8) void {
    if (data.len == 0) return;
    if (is_windows) {
        if (win.GetStdHandle(win.STD_ERROR_HANDLE)) |h| _ = win.writeAllStd(h, data);
    } else {
        _ = posix.writeAllFd(2, data);
    }
}

fn fallbackCli(init: std.process.Init.Minimal) noreturn {
    if (is_windows) win.fallbackCli() else posix.fallbackCli(init.args.vector, init.environ.block.slice);
}

// ---------------------------------------------------------------------------
// Shared hot-path entry point (Windows + Linux + macOS)
// ---------------------------------------------------------------------------
pub fn main(init: std.process.Init.Minimal) void {
    const args: [][]const u8 = if (is_windows)
        win.parseArgs()
    else blk: {
        const raw = init.args.vector;
        if (raw.len <= 1) break :blk &.{};
        const mem = osAlloc((raw.len - 1) * @sizeOf([]const u8));
        const slice: [*][]const u8 = @ptrCast(@alignCast(mem.ptr));
        var idx: usize = 1;
        while (idx < raw.len) : (idx += 1) {
            slice[idx - 1] = std.mem.span(raw[idx]);
        }
        break :blk slice[0 .. raw.len - 1];
    };

    if (args.len == 0) fallbackCli(init);

    // Fast --version
    if (args.len == 1 and std.mem.eql(u8, args[0], "--version")) {
        writeStdout(if (is_windows) "fsion " ++ VERSION_STR ++ "\r\n" else "fsion " ++ VERSION_STR ++ "\n");
        osExit(0);
    }

    // Custom prelude / config dir env -> defer to F# fsion-cli for exact prelude resolution
    const has_prelude_env = if (is_windows)
        (win.hasEnv("FSION_PRELUDE") or win.hasEnv("FSION_CONFIG_DIR"))
    else
        (posix.hasEnv(init.environ.block.slice, "FSION_PRELUDE") or posix.hasEnv(init.environ.block.slice, "FSION_CONFIG_DIR"));
    if (has_prelude_env) fallbackCli(init);

    var pipe_opt: ?[]const u8 = null;
    var session_id: ?[]const u8 = null;
    var reset_flag = false;
    var no_prelude = false;
    var is_eval = false;
    var source: ?[]const u8 = null;
    var script_args_start: usize = args.len;

    var i: usize = 0;
    while (i < args.len) {
        const a = args[i];
        if (std.mem.eql(u8, a, "--pipe") and i + 1 < args.len) {
            pipe_opt = args[i + 1];
            i += 2;
        } else if (std.mem.eql(u8, a, "--session") and i + 1 < args.len) {
            session_id = args[i + 1];
            i += 2;
        } else if (std.mem.eql(u8, a, "--reset")) {
            reset_flag = true;
            i += 1;
        } else if (std.mem.eql(u8, a, "--no-prelude")) {
            no_prelude = true;
            i += 1;
        } else if (std.mem.eql(u8, a, "-e") and i + 1 < args.len) {
            is_eval = true;
            source = args[i + 1];
            script_args_start = i + 2;
            break;
        } else if (std.mem.eql(u8, a, "--")) {
            if (i + 1 >= args.len) fallbackCli(init);
            source = args[i + 1];
            script_args_start = i + 2;
            break;
        } else if (a.len > 0 and a[0] == '-') {
            // --json, --no-daemon, --timeout, --prelude, -h, -, etc. -> F# fsion-cli
            fallbackCli(init);
        } else {
            if (std.mem.eql(u8, a, "warmup") or std.mem.eql(u8, a, "daemon")) fallbackCli(init);
            source = a;
            script_args_start = i + 1;
            break;
        }
    }

    const src_val = source orelse fallbackCli(init);
    if (reset_flag and session_id == null) fallbackCli(init);
    if (session_id) |sid| {
        var all_ws = true;
        for (sid) |c| {
            if (c != ' ' and c != '\t' and c != '\r' and c != '\n') {
                all_ws = false;
                break;
            }
        }
        if (all_ws) fallbackCli(init);
    }

    const endpoint_name: []const u8 = blk: {
        if (pipe_opt) |p| {
            if (p.len == 0 or hasPathSep(p)) fallbackCli(init);
            break :blk p;
        }
        const env_pipe = if (is_windows) win.getEnvUtf8("FSION_PIPE") else posix.getEnvNonEmpty(init.environ.block.slice, "FSION_PIPE");
        if (env_pipe) |ep| {
            if (ep.len == 0 or hasPathSep(ep)) fallbackCli(init);
            break :blk ep;
        }
        fallbackCli(init);
    };

    const conn = switch (if (is_windows)
        win.connectEndpoint(endpoint_name)
    else
        posix.connectEndpoint(init.environ.block.slice, endpoint_name)) {
        .connected => |c| c,
        .not_found => fallbackCli(init),
        .security_rejected => {
            writeStderr(if (is_windows)
                "fsion: refused to connect to endpoint with untrusted owner\r\n"
            else
                "fsion: refused to connect to unsafe POSIX socket directory\n");
            osExit(1);
        },
    };
    // Build v6 ExecuteRequest frame
    var req = Buf{};
    req.i32Le(0); // payload length placeholder
    req.i32Le(PROTOCOL_VERSION);
    req.u8Val(1); // Request.Execute
    req.str(if (is_eval) "eval" else "file");
    req.str(src_val);
    if (is_windows) win.writeCwd(&req) else posix.writeCwd(&req);
    const script_args = args[script_args_start..];
    const arg0 = if (is_eval) "eval.fsx" else src_val;
    req.i32Le(@intCast(script_args.len + 1));
    req.str(arg0);
    for (script_args) |sa| req.str(sa);
    if (is_windows) win.writeEnv(&req) else posix.writeEnv(init.environ.block.slice, &req);
    req.u8Val(0); // IncludeDiagnostics = false
    if (session_id) |sid| {
        req.u8Val(1);
        req.str(sid);
    } else {
        req.u8Val(0);
    }
    req.u8Val(if (reset_flag) 1 else 0);
    req.i32Le(0); // TimeoutMs = 0
    req.u8Val(if (no_prelude) 1 else 0);
    req.u8Val(0); // PreludePath = None
    req.patchFrameLen();

    const win_read_ev = if (is_windows) (win.CreateEventW(null, 1, 0, null) orelse fallbackCli(init)) else {};
    const win_write_ev = if (is_windows) (win.CreateEventW(null, 1, 0, null) orelse fallbackCli(init)) else {};
    var win_stdin_worker = if (is_windows) blk: {
        const stdin_write_ev = win.CreateEventW(null, 1, 0, null) orelse fallbackCli(init);
        const stdin_wake_ev = win.CreateEventW(null, 0, 0, null) orelse fallbackCli(init);
        break :blk win.StdinWorker.init(conn, stdin_write_ev, stdin_wake_ev);
    } else {};

    const send_ok = if (is_windows)
        win.writeAllPipe(conn, win_write_ev, req.data[0..req.len])
    else
        posix.writeAllFd(conn, req.data[0..req.len]);
    if (!send_ok) {
        if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
        fallbackCli(init);
    }

    // Response loop: handles on-demand StdinReadRequest(17) and final Executed(11) / Failure(15)
    var resp_buf: []u8 = &.{};
    var posix_stdin_pending: usize = 0;
    while (true) {
        if (!is_windows and posix_stdin_pending > 0) {
            const ready = posix.pollConnOrStdin(conn, true);
            if ((ready & 2) != 0 and (ready & 1) == 0) {
                const max_b = posix_stdin_pending;
                posix_stdin_pending = 0;
                const tmp = osAlloc(max_b);
                defer osFree(tmp);
                const got = posix.readStdinSome(tmp);
                var chunk = Buf{};
                defer chunk.deinit();
                chunk.i32Le(0);
                chunk.i32Le(PROTOCOL_VERSION);
                chunk.u8Val(6); // StdinChunk
                chunk.u8Val(if (got == 0) 1 else 0);
                chunk.i32Le(@intCast(got));
                if (got > 0) chunk.bytes(tmp[0..got]);
                chunk.patchFrameLen();
                if (!posix.writeAllFd(conn, chunk.data[0..chunk.len])) {
                    posix.closeFd(conn);
                    osExit(1);
                }
                continue;
            }
        }

        var hdr: [4]u8 = undefined;
        const hdr_ok = if (is_windows) win.readExactPipe(conn, win_read_ev, &hdr) else posix.readExactFd(conn, &hdr);
        if (!hdr_ok) {
            if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
            osExit(1);
        }
        const rlen: usize = @as(usize, hdr[0]) |
            (@as(usize, hdr[1]) << 8) |
            (@as(usize, hdr[2]) << 16) |
            (@as(usize, hdr[3]) << 24);
        if (rlen == 0 or rlen > 64 * 1024 * 1024) {
            if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
            osExit(1);
        }
        if (rlen > resp_buf.len) {
            resp_buf = osRealloc(resp_buf, rlen);
        }
        const body = resp_buf[0..rlen];
        const body_ok = if (is_windows) win.readExactPipe(conn, win_read_ev, body) else posix.readExactFd(conn, body);
        if (!body_ok) {
            if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
            osExit(1);
        }

        var off: usize = 0;
        const ver = rdI32(body, &off) orelse {
            if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
            osExit(1);
        };
        const kind = rdU8(body, &off) orelse {
            if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
            osExit(1);
        };
        if (ver != PROTOCOL_VERSION) {
            if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
            osExit(1);
        }

        if (kind == 17) {
            // StdinReadRequest(maxBytes) — service asynchronously so response loop never stalls
            // if a background thread reads stdin while foreground evaluation completes.
            const raw_max = rdI32(body, &off) orelse 4096;
            const clamped: usize = if (raw_max <= 0) 4096 else @min(@as(usize, @intCast(raw_max)), 65536);
            if (is_windows) {
                if (!win_stdin_worker.enqueue(clamped)) {
                    _ = win.CloseHandle(conn);
                    osExit(1);
                }
            } else {
                posix_stdin_pending = clamped;
            }
            continue;
        }

        if (kind == 11) {
            // Executed
            const exit_code = rdI32(body, &off) orelse 1;
            const out_len = rdVarU32(body, &off) orelse {
                if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
                osExit(1);
            };
            if (off + out_len > body.len) {
                if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
                osExit(1);
            }
            const out_slice = body[off .. off + out_len];
            off += out_len;
            const err_len = rdVarU32(body, &off) orelse {
                if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
                osExit(1);
            };
            if (off + err_len > body.len) {
                if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
                osExit(1);
            }
            const err_slice = body[off .. off + err_len];
            writeStdout(out_slice);
            writeStderr(err_slice);
            if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
            osExit(@bitCast(exit_code));
        }

        if (kind == 15) {
            // Failure(message)
            if (rdVarU32(body, &off)) |mlen| {
                if (off + mlen <= body.len) {
                    writeStderr("fsion: ");
                    writeStderr(body[off .. off + mlen]);
                    writeStderr(if (is_windows) "\r\n" else "\n");
                }
            }
            if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
            osExit(1);
        }

        if (is_windows) _ = win.CloseHandle(conn) else posix.closeFd(conn);
        osExit(1);
    }
}
