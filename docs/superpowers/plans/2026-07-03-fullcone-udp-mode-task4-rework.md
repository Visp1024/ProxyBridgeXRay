# Full Cone UDP — Task 4 rework (patch to the plan)

> **This document REPLACES `### Task 4` in `2026-07-03-fullcone-udp-mode.md` in full.**
> Tasks 1-3 and 5-8 are unchanged. Task 5 (IPv6) still slots in on top of this — the
> public names it depends on are preserved: `udp_fullcone_relay_server`, `fullcone_socket`,
> `fullcone_socket6`, `FULLCONE_SESSION.is_ipv6`, `FULLCONE_SESSION.client_ip6`,
> `get_connection_client` (+ future `get_connection_client_v6`).

## Why this rework

The original Task 4 ran the blocking SOCKS5 handshake inline in the single relay thread and
freed sessions unconditionally after a 2 s join. Three concrete defects, fixed here:

1. **Head-of-line blocking + lost first pings (architecture).** `fullcone_create_session()`
   did `connect()` + UDP `ASSOCIATE` (up to ~3 s) inside the `recvfrom` loop. While it ran,
   the relay drained no other client socket, so datagrams for *other* game sockets piled up
   in `SO_RCVBUF` and were dropped once it filled — and the spec's own "first pings to each
   region are not lost" guarantee was gone (the plan had swapped the per-session 32-packet
   ring for a shared `SO_RCVBUF`, which only buffers the *same* socket). **Fix:** the
   handshake moves to a per-session **worker thread**; datagrams that arrive before the
   associate is ready buffer in a **per-session 32-packet ring** and flush in order on
   success. The relay loop never blocks.

2. **Use-after-free on teardown (memory safety).** `fullcone_close_session()` did
   `WaitForSingleObject(reader, 2000)` then `free(s)` unconditionally; a reader momentarily
   stuck in `sendto()` on the shared client socket would not be joined in time and `s` would
   be freed under it. **Fix:** teardown is **relay-thread-only**, joins with `INFINITE`
   after closing sockets (bounded because the worker's `connect` has a 3 s timeout and the
   reader's `sendto` uses `SO_SNDTIMEO`, so no thread can wedge), and **only the relay thread
   ever frees** — workers and readers never free.

3. **Session key collisions (correctness).** Sessions were matched by `client_port` alone;
   a v4 and v6 client on the same ephemeral port would cross-route. **Fix:** the lookup key
   is `(client_port, is_ipv6)`.

Because the handshake is now concurrent with the relay loop, the session table is
multi-writer and gets a dedicated `SRWLOCK g_fullcone_lock`. **Creation/insertion is still
single-threaded (relay only)**; workers only transition state + publish sockets, readers only
touch `last_activity` (Interlocked). Lock ordering: `g_fullcone_lock` is never held while
acquiring the connection `lock` (the `get_connection_client` call is made with no
`g_fullcone_lock` held), so the two locks never nest.

---

### Task 4: Core — full-cone relay server (async handshake, safe teardown)

**Files:**
- Modify: `Windows/src/ProxyBridge.c` (session table + lock, ring, worker/reader threads, relay thread, start/stop wiring)

**Interfaces:**
- Consumes: `get_connection_client(src_port, &client_ip, &cfg_id)` (Task 3), `find_proxy_config`, `socks5_udp_associate_with_config`, `configure_tcp_socket`, `configure_udp_socket`, `resolve_hostname` (all existing).
- Produces: thread `static DWORD WINAPI udp_fullcone_relay_server(LPVOID arg);`, globals `static SOCKET fullcone_socket / fullcone_socket6;`, `static HANDLE fullcone_relay_thread;`, session table `g_fullcone_sessions` guarded by `g_fullcone_lock`.

---

- [ ] **Step 1: Session structures + lock**

```c
#define FULLCONE_MAX_SESSIONS    256
#define FULLCONE_SESSION_HASH    64
#define FULLCONE_IDLE_TIMEOUT_MS 120000
#define FULLCONE_RING_MAX        32       // datagrams buffered per session during handshake
#define FULLCONE_FAIL_GUARD_MS   1000     // per-session retry guard after a failed associate

typedef enum { FC_PENDING = 0, FC_ACTIVE, FC_DEAD } FC_STATE;

typedef struct { unsigned char *data; int len; } FC_RING_ENTRY;

typedef struct FULLCONE_SESSION {
    UINT16   client_port;
    BOOL     is_ipv6;                     // part of the lookup key
    UINT32   client_ip;                   // v4 reply target (game's local address)
    UINT8    client_ip6[16];              // v6 reply target (Task 5)
    UINT32   proxy_config_id;

    volatile FC_STATE state;              // PENDING -> ACTIVE | DEAD
    SOCKET   tcp_ctrl;                    // keeps the UDP ASSOCIATE alive (set by worker)
    SOCKET   proxy_sock;                  // datagrams to/from the SOCKS5 relay (set by worker)
    struct sockaddr_in proxy_relay_addr;

    FC_RING_ENTRY ring[FULLCONE_RING_MAX];// buffered while PENDING; flushed on ACTIVE
    int      ring_count;

    volatile LONGLONG last_activity;      // Interlocked (relay + reader)
    volatile LONGLONG fail_tick;          // set on failure; drives FAIL_GUARD

    HANDLE   worker_thread;               // handshake; lives only during PENDING
    HANDLE   reader_thread;               // proxy->client pump; lives during ACTIVE
    struct FULLCONE_SESSION *next;
} FULLCONE_SESSION;

static FULLCONE_SESSION *g_fullcone_sessions[FULLCONE_SESSION_HASH];
static int      g_fullcone_session_count = 0;
static SRWLOCK  g_fullcone_lock = SRWLOCK_INIT;
static SOCKET   fullcone_socket  = INVALID_SOCKET;
static SOCKET   fullcone_socket6 = INVALID_SOCKET;
static HANDLE   fullcone_relay_thread = NULL;
```

**Ownership contract (keep as a comment above the table):**
- Structure mutation (insert / unlink) — **relay thread only**.
- State transition `PENDING -> ACTIVE|DEAD` and socket publication — worker, under `g_fullcone_lock`.
- `last_activity` — relay + reader, via `InterlockedExchange64`.
- `free(s)` — **relay thread only**, in `fullcone_destroy_session`, after unlink + socket close + thread joins.

Forward declarations needed (near the other `static` prototypes):

```c
static DWORD WINAPI fullcone_session_reader(LPVOID arg);
static DWORD WINAPI fullcone_connect_worker(LPVOID arg);
static void  fullcone_destroy_session(FULLCONE_SESSION *s);
```

- [ ] **Step 2: Ring helpers**

```c
// caller holds g_fullcone_lock
static void fullcone_ring_push(FULLCONE_SESSION *s, const unsigned char *data, int len)
{
    if (s->ring_count >= FULLCONE_RING_MAX) return;   // ring full during handshake: drop
    unsigned char *copy = (unsigned char *)malloc((size_t)len);
    if (copy == NULL) return;
    memcpy(copy, data, (size_t)len);
    s->ring[s->ring_count].data = copy;
    s->ring[s->ring_count].len  = len;
    s->ring_count++;
}

// caller holds g_fullcone_lock, OR the session is already unlinked (teardown)
static void fullcone_ring_clear(FULLCONE_SESSION *s)
{
    for (int i = 0; i < s->ring_count; i++) { free(s->ring[i].data); s->ring[i].data = NULL; }
    s->ring_count = 0;
}
```

- [ ] **Step 3: Handshake worker (off the relay thread)**

Mirrors the legacy `establish_udp_associate_for_config()` but per-session and non-blocking to
the relay. On success it publishes the sockets, flushes the ring in order, goes `ACTIVE` and
starts the reader — all under one lock hold so the relay never observes a half-built session.

```c
static DWORD WINAPI fullcone_connect_worker(LPVOID arg)
{
    FULLCONE_SESSION *s = (FULLCONE_SESSION *)arg;
    PROXY_CONFIG *cfg = find_proxy_config(s->proxy_config_id);
    SOCKET tcp_sock = INVALID_SOCKET, proxy_sock = INVALID_SOCKET;
    struct sockaddr_in relay_addr = {0};
    BOOL ok = FALSE;

    if (cfg != NULL && cfg->type == PROXY_TYPE_SOCKS5)
    {
        tcp_sock = socket(AF_INET, SOCK_STREAM, 0);
        if (tcp_sock != INVALID_SOCKET)
        {
            configure_tcp_socket(tcp_sock, 262144, 3000);   // 3 s connect/recv cap
            UINT32 socks5_ip = resolve_hostname(cfg->host);
            if (socks5_ip != 0)
            {
                struct sockaddr_in sa = {0};
                sa.sin_family = AF_INET;
                sa.sin_addr.s_addr = socks5_ip;
                sa.sin_port = htons(cfg->port);
                if (connect(tcp_sock, (struct sockaddr *)&sa, sizeof(sa)) != SOCKET_ERROR &&
                    socks5_udp_associate_with_config(tcp_sock, &relay_addr, cfg) == 0)
                {
                    if (relay_addr.sin_addr.s_addr == INADDR_ANY)
                        relay_addr.sin_addr.s_addr = socks5_ip;

                    // Same keepalive treatment as establish_udp_associate_for_config()
                    DWORD zero_timeout = 0;
                    setsockopt(tcp_sock, SOL_SOCKET, SO_RCVTIMEO, (const char*)&zero_timeout, sizeof(zero_timeout));
                    setsockopt(tcp_sock, SOL_SOCKET, SO_SNDTIMEO, (const char*)&zero_timeout, sizeof(zero_timeout));
                    BOOL ka_on = TRUE;
                    setsockopt(tcp_sock, SOL_SOCKET, SO_KEEPALIVE, (const char*)&ka_on, sizeof(ka_on));
                    struct tcp_keepalive ka = { 1, 10000, 2000 };
                    DWORD ka_bytes;
                    WSAIoctl(tcp_sock, SIO_KEEPALIVE_VALS, &ka, sizeof(ka), NULL, 0, &ka_bytes, NULL, NULL);

                    proxy_sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
                    if (proxy_sock != INVALID_SOCKET)
                    {
                        configure_udp_socket(proxy_sock, 262144, 0);   // blocking recv; closed socket ends the reader
                        ok = TRUE;
                    }
                }
            }
        }
    }

    AcquireSRWLockExclusive(&g_fullcone_lock);

    if (s->state != FC_PENDING)
    {
        // Torn down mid-handshake (shutdown / cap). Relay owns free; just drop our sockets.
        ReleaseSRWLockExclusive(&g_fullcone_lock);
        if (tcp_sock   != INVALID_SOCKET) closesocket(tcp_sock);
        if (proxy_sock != INVALID_SOCKET) closesocket(proxy_sock);
        return 0;
    }

    if (!ok)
    {
        if (tcp_sock   != INVALID_SOCKET) closesocket(tcp_sock);
        if (proxy_sock != INVALID_SOCKET) closesocket(proxy_sock);
        fullcone_ring_clear(s);
        s->fail_tick = (LONGLONG)GetTickCount64();
        s->state = FC_DEAD;                 // relay drops for FAIL_GUARD, then reaps + recreates
        ReleaseSRWLockExclusive(&g_fullcone_lock);
        log_message("[FULLCONE] Associate failed for client port %u", s->client_port);
        return 0;
    }

    // Success: publish sockets, flush buffered datagrams IN ORDER, go ACTIVE, start reader.
    s->tcp_ctrl         = tcp_sock;
    s->proxy_sock       = proxy_sock;
    s->proxy_relay_addr = relay_addr;
    for (int i = 0; i < s->ring_count; i++)
    {
        sendto(proxy_sock, (char*)s->ring[i].data, s->ring[i].len, 0,
               (struct sockaddr*)&relay_addr, sizeof(relay_addr));
        free(s->ring[i].data);
        s->ring[i].data = NULL;
    }
    s->ring_count    = 0;
    s->last_activity = (LONGLONG)GetTickCount64();
    s->state         = FC_ACTIVE;
    // Start the reader under the lock so teardown always sees a valid reader_thread handle.
    s->reader_thread = CreateThread(NULL, 0, fullcone_session_reader, s, 0, NULL);
    BOOL reader_ok = (s->reader_thread != NULL);
    if (!reader_ok) { s->fail_tick = (LONGLONG)GetTickCount64(); s->state = FC_DEAD; }
    ReleaseSRWLockExclusive(&g_fullcone_lock);

    if (reader_ok)
        log_message("[FULLCONE] Session ready for client port %u -> %s:%d (relay %s:%d)",
            s->client_port, cfg->host, cfg->port,
            inet_ntoa(relay_addr.sin_addr), ntohs(relay_addr.sin_port));
    return 0;
}
```

- [ ] **Step 4: Per-session reader (proxy -> client)**

Unchanged in intent from the original plan; the only hardening is that its `sendto` runs on
`fullcone_socket`, which carries `SO_SNDTIMEO` (Step 6) so it can never wedge and block a join.
Reads `s->client_ip/client_port`, which are valid for the reader's whole lifetime because
teardown joins the reader **before** `free(s)`.

```c
// Datagrams from the proxy are already SOCKS5-formatted; forward verbatim from port 34012
// so the WinDivert layer decapsulates and restores the origin.
static DWORD WINAPI fullcone_session_reader(LPVOID arg)
{
    FULLCONE_SESSION *s = (FULLCONE_SESSION *)arg;
    unsigned char buf[MAXBUF];
    struct sockaddr_in from; int from_len;

    for (;;)
    {
        from_len = sizeof(from);
        int len = recvfrom(s->proxy_sock, (char*)buf, sizeof(buf), 0, (struct sockaddr*)&from, &from_len);
        if (len == SOCKET_ERROR || len == 0)
            break;                          // socket closed by teardown, or proxy gone
        if (len < 10 || buf[2] != 0x00)     // malformed / fragmented
            continue;

        InterlockedExchange64((LONGLONG volatile*)&s->last_activity, (LONGLONG)GetTickCount64());

        struct sockaddr_in target = {0};
        target.sin_family = AF_INET;
        target.sin_addr.s_addr = s->client_ip;
        target.sin_port = htons(s->client_port);
        sendto(fullcone_socket, (char*)buf, len, 0, (struct sockaddr*)&target, sizeof(target));
        // send failure (incl. SO_SNDTIMEO WSAEWOULDBLOCK) -> drop this datagram, keep pumping
    }
    return 0;
}
```

- [ ] **Step 5: Teardown (relay-thread only, join-before-free)**

The one place a session is freed. Unlinks and marks `DEAD` under the lock (so a still-running
worker sees `state != FC_PENDING` and bails without starting a reader), then closes sockets
(unblocks the reader's `recvfrom`), joins both threads with `INFINITE` (bounded: worker ≤ 3 s
via connect timeout, reader unblocks on socket close and cannot wedge in `sendto`), then frees.

```c
// Relay thread only. Safe to call on PENDING / ACTIVE / DEAD sessions.
static void fullcone_destroy_session(FULLCONE_SESSION *s)
{
    int hash = s->client_port % FULLCONE_SESSION_HASH;

    AcquireSRWLockExclusive(&g_fullcone_lock);
    FULLCONE_SESSION **pp = &g_fullcone_sessions[hash];
    while (*pp != NULL && *pp != s) pp = &(*pp)->next;
    if (*pp == s) { *pp = s->next; g_fullcone_session_count--; }
    s->state = FC_DEAD;                     // stop the worker from transitioning to ACTIVE
    HANDLE w = s->worker_thread, r = s->reader_thread;
    SOCKET t = s->tcp_ctrl, p = s->proxy_sock;
    ReleaseSRWLockExclusive(&g_fullcone_lock);

    if (t != INVALID_SOCKET) closesocket(t);   // unblock reader recvfrom + drop the associate
    if (p != INVALID_SOCKET) closesocket(p);
    if (w != NULL) { WaitForSingleObject(w, INFINITE); CloseHandle(w); }
    if (r != NULL) { WaitForSingleObject(r, INFINITE); CloseHandle(r); }
    fullcone_ring_clear(s);
    free(s);
}
```

- [ ] **Step 6: Idle/dead sweep**

```c
// Relay thread only.
static void fullcone_sweep(ULONGLONG now)
{
    for (int h = 0; h < FULLCONE_SESSION_HASH; h++)
    {
        // Find one victim under a shared lock, then destroy it lock-free (destroy re-locks).
        FULLCONE_SESSION *victim = NULL;
        AcquireSRWLockShared(&g_fullcone_lock);
        for (FULLCONE_SESSION *s = g_fullcone_sessions[h]; s != NULL; s = s->next)
        {
            ULONGLONG la = (ULONGLONG)s->last_activity;
            if (s->state == FC_ACTIVE && now - la > FULLCONE_IDLE_TIMEOUT_MS) { victim = s; break; }
            if (s->state == FC_DEAD && now - (ULONGLONG)s->fail_tick > FULLCONE_FAIL_GUARD_MS) { victim = s; break; }
            // FC_PENDING is never swept: the worker resolves it within its 3 s connect timeout.
        }
        ReleaseSRWLockShared(&g_fullcone_lock);
        if (victim != NULL)
        {
            if (victim->state == FC_ACTIVE)
                log_message("[FULLCONE] Session for client port %u idle - closed", victim->client_port);
            fullcone_destroy_session(victim);
            h--;                            // rescan this bucket for more victims
        }
    }
}
```

- [ ] **Step 7: Dispatch (relay -> session), IPv4**

The hot path. Creation stays single-threaded (relay only), so the "release lock, look up the
client, re-acquire, create" sequence is race-free — no other thread ever inserts. `ACTIVE`
sends copy the socket handle out and `sendto` **without** holding the lock.

```c
// Relay thread only. Routes one client datagram (already SOCKS5-encapsulated).
static void fullcone_dispatch_v4(unsigned char *buf, int len, UINT16 client_port)
{
    ULONGLONG now = GetTickCount64();
    int hash = client_port % FULLCONE_SESSION_HASH;

    AcquireSRWLockExclusive(&g_fullcone_lock);
    FULLCONE_SESSION *s = g_fullcone_sessions[hash];
    while (s != NULL && !(s->client_port == client_port && !s->is_ipv6)) s = s->next;

    if (s != NULL)
    {
        if (s->state == FC_ACTIVE)
        {
            SOCKET psock = s->proxy_sock;
            struct sockaddr_in raddr = s->proxy_relay_addr;
            InterlockedExchange64((LONGLONG volatile*)&s->last_activity, (LONGLONG)now);
            ReleaseSRWLockExclusive(&g_fullcone_lock);
            if (sendto(psock, (char*)buf, len, 0, (struct sockaddr*)&raddr, sizeof(raddr)) == SOCKET_ERROR)
            {
                // Proxy path broke (e.g. xray restarted). Tear down; next packet recreates.
                log_message("[FULLCONE] sendto proxy failed (%d) for client port %u - session reset",
                            WSAGetLastError(), client_port);
                fullcone_destroy_session(s);   // s is still valid: only the relay thread frees
            }
            return;
        }
        if (s->state == FC_PENDING)
        {
            fullcone_ring_push(s, buf, len);   // buffer until the associate is ready
            s->last_activity = (LONGLONG)now;
            ReleaseSRWLockExclusive(&g_fullcone_lock);
            return;
        }
        // FC_DEAD
        if (now - (ULONGLONG)s->fail_tick < FULLCONE_FAIL_GUARD_MS)
        {
            ReleaseSRWLockExclusive(&g_fullcone_lock);
            return;                            // recently failed - don't hammer the proxy
        }
        ReleaseSRWLockExclusive(&g_fullcone_lock);
        fullcone_destroy_session(s);           // guard elapsed: reap, then fall through to create
        s = NULL;
        AcquireSRWLockExclusive(&g_fullcone_lock);
    }

    // Not found (or just reaped): create a PENDING session and kick off the worker.
    if (g_fullcone_session_count >= FULLCONE_MAX_SESSIONS)
    {
        ReleaseSRWLockExclusive(&g_fullcone_lock);
        log_message("[FULLCONE] Session cap (%d) reached - dropping client port %u",
                    FULLCONE_MAX_SESSIONS, client_port);
        return;
    }
    ReleaseSRWLockExclusive(&g_fullcone_lock);

    // Resolve the client + config WITHOUT g_fullcone_lock held (avoids nesting with `lock`).
    UINT32 client_ip = 0, cfg_id = 0;
    if (!get_connection_client(client_port, &client_ip, &cfg_id))
    {
        log_message("[FULLCONE] No tracked client for port %u - dropped", client_port);
        return;
    }

    FULLCONE_SESSION *ns = (FULLCONE_SESSION *)calloc(1, sizeof(FULLCONE_SESSION));
    if (ns == NULL) return;
    ns->client_port     = client_port;
    ns->is_ipv6         = FALSE;
    ns->client_ip       = client_ip;
    ns->proxy_config_id = cfg_id;
    ns->state           = FC_PENDING;
    ns->tcp_ctrl        = INVALID_SOCKET;
    ns->proxy_sock      = INVALID_SOCKET;
    ns->last_activity   = (LONGLONG)now;

    AcquireSRWLockExclusive(&g_fullcone_lock);
    fullcone_ring_push(ns, buf, len);          // first datagram, before the worker exists
    ns->worker_thread = CreateThread(NULL, 0, fullcone_connect_worker, ns, 0, NULL);
    if (ns->worker_thread == NULL)
    {
        ReleaseSRWLockExclusive(&g_fullcone_lock);
        fullcone_ring_clear(ns);
        free(ns);
        return;
    }
    ns->next = g_fullcone_sessions[hash];
    g_fullcone_sessions[hash] = ns;
    g_fullcone_session_count++;
    ReleaseSRWLockExclusive(&g_fullcone_lock);
    log_message("[FULLCONE] New session pending for client port %u", client_port);
}
```

- [ ] **Step 8: Relay main thread**

```c
static DWORD WINAPI udp_fullcone_relay_server(LPVOID arg)
{
    unsigned char buf[MAXBUF];
    struct sockaddr_in from; int from_len;
    ULONGLONG last_sweep = GetTickCount64();

    fullcone_socket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (fullcone_socket == INVALID_SOCKET) return 1;
    int on = 1;
    setsockopt(fullcone_socket, SOL_SOCKET, SO_REUSEADDR, (const char*)&on, sizeof(on));
    configure_udp_socket(fullcone_socket, 262144, 30000);
    // Readers sendto() this shared socket; a send timeout guarantees a reader can never
    // wedge in sendto and block a teardown join (fix #2).
    DWORD snd_to = 100;   // ms
    setsockopt(fullcone_socket, SOL_SOCKET, SO_SNDTIMEO, (const char*)&snd_to, sizeof(snd_to));

    struct sockaddr_in local = {0};
    local.sin_family = AF_INET;
    local.sin_addr.s_addr = htonl(INADDR_ANY);   // WinDivert swaps addrs; packets arrive at the real IP
    local.sin_port = htons(LOCAL_UDP_FULLCONE_PORT);
    if (bind(fullcone_socket, (struct sockaddr*)&local, sizeof(local)) == SOCKET_ERROR)
    { closesocket(fullcone_socket); fullcone_socket = INVALID_SOCKET; return 1; }

    log_message("Full Cone UDP relay listening on port %d", LOCAL_UDP_FULLCONE_PORT);

    while (running)
    {
        fd_set fds; FD_ZERO(&fds); FD_SET(fullcone_socket, &fds);
        struct timeval tv = {1, 0};
        int sel = select(0, &fds, NULL, NULL, &tv);

        ULONGLONG now = GetTickCount64();
        if (now - last_sweep >= 5000) { last_sweep = now; fullcone_sweep(now); }

        if (sel <= 0 || !FD_ISSET(fullcone_socket, &fds)) continue;

        from_len = sizeof(from);
        int len = recvfrom(fullcone_socket, (char*)buf, sizeof(buf), 0, (struct sockaddr*)&from, &from_len);
        if (len == SOCKET_ERROR || len < 10) continue;   // header always present (built by encapsulation)

        fullcone_dispatch_v4(buf, len, ntohs(from.sin_port));
    }

    // Shutdown: destroy every session (joins workers/readers, frees).
    for (int h = 0; h < FULLCONE_SESSION_HASH; h++)
        while (g_fullcone_sessions[h] != NULL)
            fullcone_destroy_session(g_fullcone_sessions[h]);
    g_fullcone_session_count = 0;
    if (fullcone_socket != INVALID_SOCKET) { closesocket(fullcone_socket); fullcone_socket = INVALID_SOCKET; }
    return 0;
}
```

- [ ] **Step 9: Start/stop wiring**

In `ProxyBridge_Start`, next to the `udp_relay_thread = CreateThread(...)` call, add
`fullcone_relay_thread = CreateThread(NULL, 0, udp_fullcone_relay_server, NULL, 0, NULL);`
with the same error-handling pattern. In `ProxyBridge_Stop`, mirror the `udp_relay_thread`
shutdown: `running` is already cleared, so closing `fullcone_socket` (and `fullcone_socket6`
in Task 5) unblocks the `recvfrom`; then `WaitForSingleObject(fullcone_relay_thread, ...)`,
`CloseHandle`, NULL. The relay thread's own shutdown loop destroys all sessions, so `Stop`
must **not** touch the session table directly.

- [ ] **Step 10: Build and run the e2e test (EXPECT PASS)**

```powershell
powershell -ExecutionPolicy Bypass -File Windows/compile.ps1
# xray with Windows/tests/xray-local-socks.json still running (Task 1)
Windows/output/ProxyBridge_CLI.exe --profile Windows/tests/test-fullcone.pbprofile --verbose 3   # background
python Windows/tests/fullcone_udp_test.py
```

Expected: `3/3 distinct reply sources: ['1.1.1.1', '8.8.8.8', '9.9.9.9']`, exit 0.
Then regression: rerun with `test-legacy.pbprofile` → still `1/3`, exit 1 (legacy path untouched).

The first-ping buffering can be exercised directly: the three DNS queries are sent back-to-back
before any reply, so at least two of them land while the session is still `FC_PENDING` — they
must be flushed from the ring, not dropped (all three replies still arrive).

- [ ] **Step 11: Amend the spec buffering note**

In `docs/superpowers/specs/2026-07-03-fullcone-udp-mode-design.md`, keep the "32-packet ring"
wording but make it per-session and tie it to the async handshake:

> "While the associate is being established (~RTT), incoming datagrams are buffered in a
> per-session 32-packet ring and flushed in order once the ASSOCIATE completes — the first
> pings to each region are not lost. The handshake runs on a per-session worker thread, so it
> never blocks the relay's accept loop or other sessions."

- [ ] **Step 12: Commit**

```bash
git add Windows/src/ProxyBridge.c docs/superpowers/specs/2026-07-03-fullcone-udp-mode-design.md
git commit -m "feat(core): Full Cone UDP relay - async associate, per-session ring, safe teardown"
```

---

## Notes for Task 5 (IPv6) on top of this rework

- Add `fullcone_dispatch_v6` mirroring Step 7: key `(client_port, is_ipv6=TRUE)`, resolve the
  client via `get_connection_client_v6` (fills `client_ip6`), buffer/relay identically. The
  worker stays IPv4 to the proxy (the SOCKS5 relay address from UDP ASSOCIATE is IPv4; v6
  destinations ride inside `ATYP=4` headers), so `fullcone_connect_worker` is reused as-is.
- The reader needs an `is_ipv6` branch: send v6 replies via `fullcone_socket6` to
  `[client_ip6]:client_port`. Factor the target-build + `sendto` into a small helper or branch
  on `s->is_ipv6`.
- Bind `fullcone_socket6` to `[::]:34012` with `IPV6_V6ONLY` and its own `SO_SNDTIMEO`; add it
  to the `select` set; datagrams from it call `fullcone_dispatch_v6`. `fullcone_sweep` /
  `fullcone_destroy_session` are address-family agnostic and need no change.
