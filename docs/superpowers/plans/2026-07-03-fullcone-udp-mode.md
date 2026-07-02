# Full Cone UDP Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Opt-in per-rule "Full Cone UDP" mode: SOCKS5-encapsulated UDP relay with one UDP ASSOCIATE per client socket, plus XUDP mux in the generated xray config — fixes P2P games (MK1) hanging on match connect.

**Architecture:** Every relayed datagram carries its destination/origin in an in-band SOCKS5 UDP header, so the data path needs no shared state. A new relay on port 34012 keeps one SOCKS5 UDP ASSOCIATE per client source port. The legacy path (34011) is untouched. Spec: `docs/superpowers/specs/2026-07-03-fullcone-udp-mode-design.md`.

**Tech Stack:** C (MSVC via `Windows/compile.ps1`, WinDivert 2.2), C# Avalonia GUI (`Windows/gui`), xray-core (VLESS+REALITY), Python for e2e tests.

## Global Constraints

- Legacy UDP path (port 34011) must behave byte-for-byte as before when the flag is off.
- New relay port: `34012` (`LOCAL_UDP_FULLCONE_PORT`), session cap 256, idle timeout 120 s.
- `ProxyBridge_AddRule` / `ProxyBridge_EditRule` signatures must NOT change (upstream sync); new API is additive: `ProxyBridge_SetRuleFullCone`.
- Build: `powershell -ExecutionPolicy Bypass -File Windows/compile.ps1` (needs WinDivert at `C:\WinDivert-2.2.2-A`). GUI: `dotnet build Windows/gui/ProxyBridge.GUI.csproj`.
- E2E tests need admin rights and require the user's running ProxyBridge GUI + xray to be stopped first (ask the user before stopping them).
- All user-visible GUI strings must be localized in en/ru/zh (`Resources.resx`, `Resources.ru.resx`, `Resources.zh.resx` + `Resources.Designer.cs` + `Loc.cs`).
- Mux block for xray (only when `XudpEnabled`): `"mux": { "enabled": true, "concurrency": -1, "xudpConcurrency": 16, "xudpProxyUDP443": "reject" }`.

---

### Task 1: E2E test harness + baseline failure

**Files:**
- Create: `Windows/tests/fullcone_udp_test.py`
- Create: `Windows/tests/xray-local-socks.json`
- Create: `Windows/tests/test-legacy.pbprofile`
- Create: `Windows/tests/test-fullcone.pbprofile`
- Modify: `Windows/cli/main.c` (parse `FullConeUdp`, optional setter)

**Interfaces:**
- Produces: test script exit code 0 = pass (3/3 distinct reply sources), 1 = fail. CLI understands `"FullConeUdp": true` in `.pbprofile` rules and calls `ProxyBridge_SetRuleFullCone(rid, TRUE)` when the export exists (optional via `GetProcAddress` — must not fail on a DLL without it).

- [ ] **Step 1: Write the e2e test script**

`Windows/tests/fullcone_udp_test.py`:

```python
"""One UDP socket -> 3 DNS servers. Full-cone routing must deliver all 3 replies
with correct source addresses. Legacy single-slot tracking delivers only 1."""
import socket, sys

SERVERS = ["8.8.8.8", "1.1.1.1", "9.9.9.9"]

def dns_query(qid):
    return bytes([qid >> 8, qid & 0xFF]) + b"\x01\x00\x00\x01\x00\x00\x00\x00\x00\x00" \
         + b"\x06google\x03com\x00\x00\x01\x00\x01"

s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.bind(("0.0.0.0", 0))
s.settimeout(6)
for i, srv in enumerate(SERVERS):
    s.sendto(dns_query(i + 1), (srv, 53))

got = set()
try:
    while len(got) < len(SERVERS):
        data, addr = s.recvfrom(4096)
        print(f"reply from {addr[0]}:{addr[1]}, {len(data)} bytes")
        got.add(addr[0])
except socket.timeout:
    pass

print(f"{len(got)}/{len(SERVERS)} distinct reply sources: {sorted(got)}")
sys.exit(0 if got == set(SERVERS) else 1)
```

- [ ] **Step 2: Write the local xray config (SOCKS5 with UDP, direct outbound — no VPS dependency)**

`Windows/tests/xray-local-socks.json`:

```json
{
  "log": { "loglevel": "warning" },
  "inbounds": [
    {
      "protocol": "socks",
      "listen": "127.0.0.1",
      "port": 11080,
      "settings": { "auth": "noauth", "udp": true }
    }
  ],
  "outbounds": [ { "protocol": "freedom" } ]
}
```

- [ ] **Step 3: Write the two test profiles**

`Windows/tests/test-legacy.pbprofile`:

```json
{
  "Version": "1.0",
  "Name": "test-legacy",
  "LocalhostViaProxy": false,
  "IsTrafficLoggingEnabled": true,
  "ProxyConfigs": [
    { "Id": 1, "Type": "SOCKS5", "Host": "127.0.0.1", "Port": "11080", "Username": "", "Password": "" }
  ],
  "ProxyRules": [
    { "ProcessName": "python.exe", "TargetHosts": "*", "TargetPorts": "*",
      "Protocol": "UDP", "Action": "PROXY", "IsEnabled": true, "ProxyConfigId": 1 }
  ]
}
```

`Windows/tests/test-fullcone.pbprofile`: identical, but `"Name": "test-fullcone"` and the rule gets `"FullConeUdp": true`.

- [ ] **Step 4: Add FullConeUdp support to the CLI**

In `Windows/cli/main.c`:

1. `PBRule` struct (line ~59): add field `int full_cone;` after `proxy_config_id`.
2. Profile parser (after line 386 `r->proxy_config_id = ...`): add

```c
            r->full_cone       = jbool(buf, "FullConeUdp", false) ? 1 : 0;
```

3. Function pointer typedefs (near line 26): add

```c
typedef BOOL     (*pfnSetRuleFullCone)(uint32_t rule_id, BOOL enable);
```

and a global near `g_AddRule`:

```c
static pfnSetRuleFullCone       g_SetRuleFullCone = NULL;
```

4. In `load_dll()` after the `LOAD_FN` block (line ~433): load it **optionally** (no `LOAD_FN`, which errors when missing):

```c
    g_SetRuleFullCone = (pfnSetRuleFullCone)GetProcAddress(g_hDll, "ProxyBridge_SetRuleFullCone");
```

5. In the rule-adding loop, after `if (!r->is_enabled) g_DisableRule(rid);` (line ~937):

```c
        if (r->full_cone && g_SetRuleFullCone)
            g_SetRuleFullCone(rid, TRUE);
```

and extend the printf marker (line ~941): append `r->full_cone ? "  [fullcone]" : ""` as an extra `%s`.

- [ ] **Step 5: Build and run the baseline (EXPECT FAILURE on legacy path)**

Ask the user to close the ProxyBridge GUI (this also stops its xray). Then, from an elevated shell:

```powershell
powershell -ExecutionPolicy Bypass -File Windows/compile.ps1        # builds DLL + CLI into Windows/output
# find xray.exe: check XRayPath in %APPDATA%\ProxyBridgeXRay\settings.json, else look next to the installed app
& <xray.exe> run -c Windows/tests/xray-local-socks.json             # background window
Windows/output/ProxyBridge_CLI.exe --profile Windows/tests/test-legacy.pbprofile --verbose 3   # background window
python Windows/tests/fullcone_udp_test.py
```

Expected: `1/3 distinct reply sources` and exit code 1 (all three queries funnel to the first destination; replies all appear to come from 8.8.8.8). Record the actual output. Stop the CLI (Ctrl+C / kill) after the run; leave xray running for later tasks.

- [ ] **Step 6: Commit**

```bash
git add Windows/tests Windows/cli/main.c
git commit -m "test: add full-cone UDP e2e harness and CLI FullConeUdp support"
```

---

### Task 2: Core — rule flag + `ProxyBridge_SetRuleFullCone`

**Files:**
- Modify: `Windows/src/ProxyBridge.h` (API declaration)
- Modify: `Windows/src/ProxyBridge.c` (PROCESS_RULE, AddRule init, setter, match plumbing)

**Interfaces:**
- Produces: `PROXYBRIDGE_API BOOL ProxyBridge_SetRuleFullCone(UINT32 rule_id, BOOL enable);`
- Produces: `check_process_rule(..., BOOL *out_full_cone)` / `check_process_rule_v6(..., BOOL *out_full_cone)` — extra trailing nullable out param (TRUE if the matched PROXY rule has full cone). `match_rule` / `match_rule_v6` get the same trailing param.

- [ ] **Step 1: Add the flag to the rule struct**

`ProxyBridge.c`, `PROCESS_RULE` (line ~39): add after `BOOL enabled;`:

```c
    BOOL full_cone;         // Full Cone UDP mode (SOCKS5 UDP ASSOCIATE per client socket)
```

In `ProxyBridge_AddRule` (line ~3975; struct is `malloc`ed, NOT calloc — this init is mandatory), after `rule->proxy_config_id = proxy_config_id;`:

```c
    rule->full_cone = FALSE;
```

- [ ] **Step 2: Declare and implement the setter**

`ProxyBridge.h`, after `ProxyBridge_EditRule` declaration:

```c
PROXYBRIDGE_API BOOL ProxyBridge_SetRuleFullCone(UINT32 rule_id, BOOL enable);  // Full Cone UDP mode for a rule (SOCKS5 rules only take effect)
```

`ProxyBridge.c`, after `ProxyBridge_DisableRule` (line ~4100), modeled on EnableRule:

```c
PROXYBRIDGE_API BOOL ProxyBridge_SetRuleFullCone(UINT32 rule_id, BOOL enable)
{
    if (rule_id == 0)
        return FALSE;

    PROCESS_RULE *rule = rules_list;
    while (rule != NULL)
    {
        if (rule->rule_id == rule_id)
        {
            rule->full_cone = enable;
            log_message("Rule ID %u: Full Cone UDP %s", rule_id, enable ? "enabled" : "disabled");
            return TRUE;
        }
        rule = rule->next;
    }
    return FALSE;
}
```

- [ ] **Step 3: Plumb the flag through rule matching**

Change signatures (forward declarations at lines ~371-374 and definitions):

```c
static RuleAction match_rule(const char *process_name, UINT32 dest_ip, UINT16 dest_port, BOOL is_udp, UINT32 *out_proxy_config_id, BOOL *out_full_cone);
static RuleAction match_rule_v6(const char *process_name, const UINT8 dest_ip6[16], UINT16 dest_port, BOOL is_udp, UINT32 *out_proxy_config_id, BOOL *out_full_cone);
static RuleAction check_process_rule(UINT32 src_ip, UINT16 src_port, UINT32 dest_ip, UINT16 dest_port, BOOL is_udp, DWORD *out_pid, UINT32 *out_proxy_config_id, BOOL *out_full_cone);
static RuleAction check_process_rule_v6(const UINT8 src_ip6[16], UINT16 src_port, const UINT8 dest_ip6[16], UINT16 dest_port, BOOL is_udp, DWORD *out_pid, UINT32 *out_proxy_config_id, BOOL *out_full_cone);
```

In `match_rule` / `match_rule_v6`, at every point where a matching rule's action is returned, set (guard `out_full_cone != NULL`):

```c
            if (out_full_cone) *out_full_cone = rule->full_cone;
```

`check_process_rule*` just forwards the pointer to `match_rule*`. Update ALL call sites: pass `NULL` everywhere except the two UDP PROXY paths used in Task 3 (IPv4 UDP outbound, line ~823; IPv6 UDP outbound, line ~500). Grep to find every caller: `grep -n "check_process_rule\|match_rule" Windows/src/ProxyBridge.c`.

- [ ] **Step 4: Build**

```powershell
powershell -ExecutionPolicy Bypass -File Windows/compile.ps1
```

Expected: clean build, no warnings about the new code.

- [ ] **Step 5: Commit**

```bash
git add Windows/src/ProxyBridge.h Windows/src/ProxyBridge.c
git commit -m "feat(core): add per-rule Full Cone UDP flag and ProxyBridge_SetRuleFullCone API"
```

---

### Task 3: Core — IPv4 encapsulation/decapsulation data path

**Files:**
- Modify: `Windows/src/ProxyBridge.c` (constants, connection-table flag, packet_processor, WinDivert filter)

**Interfaces:**
- Consumes: `check_process_rule(..., &rule_full_cone)` from Task 2.
- Produces: `#define LOCAL_UDP_FULLCONE_PORT 34012`; helper `static BOOL fullcone_encapsulate_v4(unsigned char *packet, UINT *packet_len, PWINDIVERT_IPHDR ip_header, PWINDIVERT_UDPHDR udp_header);` (prepends SOCKS5 header from the packet's own DstAddr/DstPort, retargets to 34012); `add_connection` gains trailing `BOOL full_cone` param; new accessors `static BOOL connection_is_fullcone(UINT16 src_port);` and `static BOOL get_connection_client(UINT16 src_port, UINT32 *out_src_ip, UINT32 *out_proxy_config_id);` (Task 4 consumes the latter).

- [ ] **Step 1: Constant + connection-table flag**

After `#define LOCAL_UDP_RELAY_PORT 34011` (line 18):

```c
#define LOCAL_UDP_FULLCONE_PORT 34012  // Full Cone UDP relay: SOCKS5-encapsulated datagrams, one ASSOCIATE per client socket
```

`CONNECTION_INFO` struct: add `BOOL full_cone;`. `add_connection` (line ~3651) and `add_connection_v6` (~3694): add trailing param `BOOL full_cone`, assign in both the update and the create branches. Update all existing callers with `FALSE` except the UDP PROXY paths below.

New accessors next to `get_connection_proxy_id`:

```c
static BOOL connection_is_fullcone(UINT16 src_port)
{
    BOOL fc = FALSE;
    AcquireSRWLockShared(&lock);
    CONNECTION_INFO *conn = connection_hash_table[src_port % CONNECTION_HASH_SIZE];
    while (conn != NULL) {
        if (conn->src_port == src_port) { fc = conn->full_cone; break; }
        conn = conn->next;
    }
    ReleaseSRWLockShared(&lock);
    return fc;
}

static BOOL get_connection_client(UINT16 src_port, UINT32 *out_src_ip, UINT32 *out_proxy_config_id)
{
    BOOL found = FALSE;
    AcquireSRWLockShared(&lock);
    CONNECTION_INFO *conn = connection_hash_table[src_port % CONNECTION_HASH_SIZE];
    while (conn != NULL) {
        if (conn->src_port == src_port && !conn->is_ipv6) {
            *out_src_ip = conn->src_ip;
            *out_proxy_config_id = conn->proxy_config_id;
            InterlockedExchange64((LONGLONG volatile*)&conn->last_activity, (LONGLONG)GetTickCount64());
            found = TRUE;
            break;
        }
        conn = conn->next;
    }
    ReleaseSRWLockShared(&lock);
    return found;
}
```

- [ ] **Step 2: Encapsulation helper**

Place above `packet_processor`:

```c
// Insert a SOCKS5 UDP request header (RFC 1928) between the UDP header and payload,
// using the packet's own destination, and retarget the packet at the full-cone relay.
// Returns FALSE (drop) if the packet would overflow MAXBUF.
static BOOL fullcone_encapsulate_v4(unsigned char *packet, UINT *packet_len,
                                    PWINDIVERT_IPHDR ip_header, PWINDIVERT_UDPHDR udp_header)
{
    if (*packet_len + 10 > MAXBUF)
    {
        log_message("[FULLCONE] UDP packet too large to encapsulate (%u bytes) - dropped", *packet_len);
        return FALSE;
    }
    UINT8 *payload = (UINT8 *)udp_header + sizeof(WINDIVERT_UDPHDR);
    UINT   headers = (UINT)(payload - packet);
    UINT32 dest_ip = ip_header->DstAddr;               // network order
    UINT16 dest_port = ntohs(udp_header->DstPort);     // host order

    memmove(payload + 10, payload, *packet_len - headers);
    payload[0] = 0x00; payload[1] = 0x00;              // RSV
    payload[2] = 0x00;                                 // FRAG
    payload[3] = SOCKS5_ATYP_IPV4;
    memcpy(&payload[4], &dest_ip, 4);
    payload[8] = (UINT8)((dest_port >> 8) & 0xFF);
    payload[9] = (UINT8)(dest_port & 0xFF);

    *packet_len += 10;
    ip_header->Length  = htons((UINT16)(ntohs(ip_header->Length) + 10));
    udp_header->Length = htons((UINT16)(ntohs(udp_header->Length) + 10));
    udp_header->DstPort = htons(LOCAL_UDP_FULLCONE_PORT);
    return TRUE;
}
```

- [ ] **Step 3: Outbound IPv4 UDP — wire into packet_processor**

In the IPv4 UDP outbound section (line ~750):

a) **Decapsulation branch** — insert BEFORE the existing `if (udp_header->SrcPort == htons(LOCAL_UDP_RELAY_PORT))` (line 754):

```c
                if (udp_header->SrcPort == htons(LOCAL_UDP_FULLCONE_PORT))
                {
                    // Full-cone relay reply: origin address rides in the SOCKS5 header.
                    UINT8 *payload = (UINT8 *)udp_header + sizeof(WINDIVERT_UDPHDR);
                    UINT   headers = (UINT)(payload - packet);
                    UINT   data_len = packet_len - headers;
                    if (data_len < 10 || payload[2] != 0x00 || payload[3] != SOCKS5_ATYP_IPV4)
                        continue;  // malformed or fragmented - drop

                    UINT32 origin_ip;
                    memcpy(&origin_ip, &payload[4], 4);
                    UINT16 origin_port = (UINT16)((payload[8] << 8) | payload[9]);

                    memmove(payload, payload + 10, data_len - 10);
                    packet_len -= 10;
                    ip_header->Length  = htons((UINT16)(ntohs(ip_header->Length) - 10));
                    udp_header->Length = htons((UINT16)(ntohs(udp_header->Length) - 10));
                    ip_header->SrcAddr = origin_ip;
                    udp_header->SrcPort = htons(origin_port);

                    BYTE dst_first_octet = (ntohl(ip_header->DstAddr) >> 24) & 0xFF;
                    if (dst_first_octet != 127)
                        addr.Outbound = FALSE;
                    // else: stay OUTBOUND - loopback echo delivers the packet (same as 34011 path)
                }
                else if (udp_header->SrcPort == htons(LOCAL_UDP_RELAY_PORT))
```

(after this new branch, execution falls through to the shared checksum+send at the bottom, same as the 34011 branch).

b) **Tracked fast path** (line ~785 `else if (is_connection_tracked(...))`): the existing body sets `udp_header->DstPort = htons(LOCAL_UDP_RELAY_PORT);`. Replace that single line with:

```c
                    if (connection_is_fullcone(src_port))
                    {
                        if (!fullcone_encapsulate_v4(packet, &packet_len, ip_header, udp_header))
                            continue;
                    }
                    else
                    {
                        udp_header->DstPort = htons(LOCAL_UDP_RELAY_PORT);
                    }
```

(the loopback/address-swap code below it stays unchanged).

c) **Rule-matched path** (line ~823): call with the new out param:

```c
                    BOOL rule_full_cone = FALSE;
                    action = check_process_rule(src_ip, src_port, dest_ip, dest_port, TRUE, &pid, &proxy_config_id, &rule_full_cone);
```

and in the `if (action == RULE_ACTION_PROXY)` block (line ~888) replace

```c
                        add_connection(src_port, src_ip, dest_ip, dest_port, proxy_config_id);

                        // redirect to UDP relay server at 127.0.0.1:34011
                        udp_header->DstPort = htons(LOCAL_UDP_RELAY_PORT);
```

with

```c
                        PROXY_CONFIG *fc_cfg = rule_full_cone ? find_proxy_config(proxy_config_id) : NULL;
                        BOOL use_fullcone = (fc_cfg != NULL && fc_cfg->type == PROXY_TYPE_SOCKS5);

                        add_connection(src_port, src_ip, dest_ip, dest_port, proxy_config_id, use_fullcone);

                        if (use_fullcone)
                        {
                            if (!fullcone_encapsulate_v4(packet, &packet_len, ip_header, udp_header))
                                continue;
                        }
                        else
                        {
                            // redirect to UDP relay server at 127.0.0.1:34011
                            udp_header->DstPort = htons(LOCAL_UDP_RELAY_PORT);
                        }
```

(the loopback/address-swap code below stays unchanged; note the extra `add_connection` arg — update the remaining `add_connection`/`add_connection_v6` callers with `FALSE`).

- [ ] **Step 4: WinDivert filter**

In `ProxyBridge_Start` (line ~4713) extend the two IPv4/IPv6 UDP clauses to also match 34012, e.g.:

```c
        "(udp and (outbound or loopback or (udp.DstPort == %d or udp.SrcPort == %d or udp.DstPort == %d or udp.SrcPort == %d))) or "
```

and pass `LOCAL_UDP_FULLCONE_PORT` twice more per clause in the snprintf argument list (same for the ipv6 udp clause).

- [ ] **Step 5: Build + legacy regression**

```powershell
powershell -ExecutionPolicy Bypass -File Windows/compile.ps1
```

Then rerun the Task 1 baseline (legacy profile): behaviour must be unchanged (`1/3`, exit 1). The full-cone profile will still fail (relay doesn't exist yet) — that's expected.

- [ ] **Step 6: Commit**

```bash
git add Windows/src/ProxyBridge.c
git commit -m "feat(core): SOCKS5 in-band encapsulation data path for Full Cone UDP (IPv4)"
```

---

### Task 4: Core — full-cone relay server

**Files:**
- Modify: `Windows/src/ProxyBridge.c` (session table, relay thread, start/stop wiring)

**Interfaces:**
- Consumes: `get_connection_client(src_port, &client_ip, &cfg_id)` (Task 3), `find_proxy_config`, `socks5_udp_associate_with_config`, `configure_tcp_socket`, `configure_udp_socket`, `resolve_hostname` (all existing).
- Produces: thread `static DWORD WINAPI udp_fullcone_relay_server(LPVOID arg);`, globals `static SOCKET fullcone_socket / fullcone_socket6;`, `static HANDLE fullcone_relay_thread;`.

- [ ] **Step 1: Session structures**

```c
#define FULLCONE_MAX_SESSIONS   256
#define FULLCONE_SESSION_HASH   64
#define FULLCONE_IDLE_TIMEOUT_MS 120000

typedef struct FULLCONE_SESSION {
    UINT16  client_port;
    BOOL    is_ipv6;
    UINT32  client_ip;              // reply target (game's local address)
    UINT8   client_ip6[16];
    UINT32  proxy_config_id;
    SOCKET  tcp_ctrl;               // keeps the UDP ASSOCIATE alive
    SOCKET  proxy_sock;             // datagrams to/from the SOCKS5 relay
    struct sockaddr_in proxy_relay_addr;
    volatile LONGLONG last_activity;
    HANDLE  reader_thread;
    struct FULLCONE_SESSION *next;
} FULLCONE_SESSION;

static FULLCONE_SESSION *g_fullcone_sessions[FULLCONE_SESSION_HASH];
static int    g_fullcone_session_count = 0;
static SOCKET fullcone_socket  = INVALID_SOCKET;
static SOCKET fullcone_socket6 = INVALID_SOCKET;
static HANDLE fullcone_relay_thread = NULL;
```

The table is owned exclusively by the relay thread (create/lookup/sweep) — no extra lock. Reader threads only touch their own session's `proxy_sock`/`last_activity` (Interlocked) and `sendto` on the shared `fullcone_socket`, which is thread-safe.

- [ ] **Step 2: Session establishment**

```c
// Establishes a dedicated TCP control + UDP ASSOCIATE for one client socket.
// Mirrors establish_udp_associate_for_config() but with per-session sockets.
static FULLCONE_SESSION *fullcone_create_session(UINT16 client_port, UINT32 client_ip, UINT32 cfg_id)
{
    PROXY_CONFIG *cfg = find_proxy_config(cfg_id);
    if (cfg == NULL || cfg->type != PROXY_TYPE_SOCKS5)
        return NULL;
    if (g_fullcone_session_count >= FULLCONE_MAX_SESSIONS)
    {
        log_message("[FULLCONE] Session cap (%d) reached - dropping traffic for client port %u", FULLCONE_MAX_SESSIONS, client_port);
        return NULL;
    }

    SOCKET tcp_sock = socket(AF_INET, SOCK_STREAM, 0);
    if (tcp_sock == INVALID_SOCKET) return NULL;
    configure_tcp_socket(tcp_sock, 262144, 3000);

    UINT32 socks5_ip = resolve_hostname(cfg->host);
    if (socks5_ip == 0) { closesocket(tcp_sock); return NULL; }

    struct sockaddr_in socks_addr = {0};
    socks_addr.sin_family = AF_INET;
    socks_addr.sin_addr.s_addr = socks5_ip;
    socks_addr.sin_port = htons(cfg->port);
    if (connect(tcp_sock, (struct sockaddr *)&socks_addr, sizeof(socks_addr)) == SOCKET_ERROR)
    { closesocket(tcp_sock); return NULL; }

    struct sockaddr_in relay_addr = {0};
    if (socks5_udp_associate_with_config(tcp_sock, &relay_addr, cfg) != 0)
    { closesocket(tcp_sock); return NULL; }
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

    SOCKET proxy_sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (proxy_sock == INVALID_SOCKET) { closesocket(tcp_sock); return NULL; }
    configure_udp_socket(proxy_sock, 262144, 0);   // blocking recv; closed socket ends the reader

    FULLCONE_SESSION *s = (FULLCONE_SESSION *)calloc(1, sizeof(FULLCONE_SESSION));
    if (s == NULL) { closesocket(tcp_sock); closesocket(proxy_sock); return NULL; }
    s->client_port = client_port;
    s->client_ip = client_ip;
    s->proxy_config_id = cfg_id;
    s->tcp_ctrl = tcp_sock;
    s->proxy_sock = proxy_sock;
    s->proxy_relay_addr = relay_addr;
    s->last_activity = (LONGLONG)GetTickCount64();
    s->reader_thread = CreateThread(NULL, 0, fullcone_session_reader, s, 0, NULL);
    if (s->reader_thread == NULL)
    { closesocket(tcp_sock); closesocket(proxy_sock); free(s); return NULL; }

    int hash = client_port % FULLCONE_SESSION_HASH;
    s->next = g_fullcone_sessions[hash];
    g_fullcone_sessions[hash] = s;
    g_fullcone_session_count++;
    log_message("[FULLCONE] Session for client port %u -> %s:%d (relay %s:%d)",
        client_port, cfg->host, cfg->port,
        inet_ntoa(relay_addr.sin_addr), ntohs(relay_addr.sin_port));
    return s;
}
```

Note: the handshake runs synchronously in the relay thread (up to ~3 s worst case). Datagrams arriving meanwhile queue in the 34012 socket's 256 KB SO_RCVBUF — that replaces the spec's "32-packet ring" (amend the spec, Step 7).

Retry guard (spec: "retry behind the existing guard interval"): add a global

```c
static ULONGLONG g_fullcone_last_fail_tick = 0;
```

At the top of `fullcone_create_session`, before any work:

```c
    ULONGLONG now = GetTickCount64();
    if (now - g_fullcone_last_fail_tick < 1000)
        return NULL;   // creation failed <1s ago - don't hammer the proxy with handshakes
```

and set `g_fullcone_last_fail_tick = GetTickCount64();` on every failure return path after the cap check (socket/resolve/connect/associate failures).

- [ ] **Step 3: Per-session reader thread**

```c
// Pumps proxy -> client. Datagrams are already SOCKS5-formatted; forward verbatim
// from port 34012 so the WinDivert layer decapsulates and restores the origin.
static DWORD WINAPI fullcone_session_reader(LPVOID arg)
{
    FULLCONE_SESSION *s = (FULLCONE_SESSION *)arg;
    unsigned char buf[MAXBUF];
    struct sockaddr_in from; int from_len;

    while (running)
    {
        from_len = sizeof(from);
        int len = recvfrom(s->proxy_sock, (char*)buf, sizeof(buf), 0, (struct sockaddr*)&from, &from_len);
        if (len == SOCKET_ERROR || len == 0)
            break;                      // socket closed by sweep/stop, or proxy gone
        if (len < 10 || buf[2] != 0x00) // malformed / fragmented
            continue;

        InterlockedExchange64((LONGLONG volatile*)&s->last_activity, (LONGLONG)GetTickCount64());

        struct sockaddr_in target = {0};
        target.sin_family = AF_INET;
        target.sin_addr.s_addr = s->client_ip;
        target.sin_port = htons(s->client_port);
        sendto(fullcone_socket, (char*)buf, len, 0, (struct sockaddr*)&target, sizeof(target));
    }
    return 0;
}
```

(forward declaration needed above `fullcone_create_session`).

- [ ] **Step 4: Relay main thread**

```c
static void fullcone_close_session(FULLCONE_SESSION *s)
{
    if (s->tcp_ctrl  != INVALID_SOCKET) closesocket(s->tcp_ctrl);
    if (s->proxy_sock != INVALID_SOCKET) closesocket(s->proxy_sock);
    if (s->reader_thread != NULL)
    {
        WaitForSingleObject(s->reader_thread, 2000);
        CloseHandle(s->reader_thread);
    }
    free(s);
}

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

    struct sockaddr_in local = {0};
    local.sin_family = AF_INET;
    local.sin_addr.s_addr = htonl(INADDR_ANY);   // packets arrive at the machine's real IP (WinDivert swaps addrs)
    local.sin_port = htons(LOCAL_UDP_FULLCONE_PORT);
    if (bind(fullcone_socket, (struct sockaddr*)&local, sizeof(local)) == SOCKET_ERROR)
    { closesocket(fullcone_socket); fullcone_socket = INVALID_SOCKET; return 1; }

    log_message("Full Cone UDP relay listening on port %d", LOCAL_UDP_FULLCONE_PORT);

    while (running)
    {
        fd_set fds; FD_ZERO(&fds); FD_SET(fullcone_socket, &fds);
        struct timeval tv = {1, 0};
        int sel = select(0, &fds, NULL, NULL, &tv);

        // Idle sweep every 5 s
        ULONGLONG now = GetTickCount64();
        if (now - last_sweep >= 5000)
        {
            last_sweep = now;
            for (int h = 0; h < FULLCONE_SESSION_HASH; h++)
            {
                FULLCONE_SESSION **pp = &g_fullcone_sessions[h];
                while (*pp != NULL)
                {
                    FULLCONE_SESSION *s = *pp;
                    if (now - (ULONGLONG)s->last_activity > FULLCONE_IDLE_TIMEOUT_MS)
                    {
                        *pp = s->next;
                        g_fullcone_session_count--;
                        log_message("[FULLCONE] Session for client port %u idle - closed", s->client_port);
                        fullcone_close_session(s);
                    }
                    else pp = &s->next;
                }
            }
        }

        if (sel <= 0 || !FD_ISSET(fullcone_socket, &fds)) continue;

        from_len = sizeof(from);
        int len = recvfrom(fullcone_socket, (char*)buf, sizeof(buf), 0, (struct sockaddr*)&from, &from_len);
        if (len == SOCKET_ERROR || len < 10) continue;   // header always present (built by encapsulation)

        UINT16 client_port = ntohs(from.sin_port);
        int hash = client_port % FULLCONE_SESSION_HASH;
        FULLCONE_SESSION *s = g_fullcone_sessions[hash];
        while (s != NULL && s->client_port != client_port) s = s->next;

        if (s == NULL)
        {
            UINT32 client_ip = 0, cfg_id = 0;
            if (!get_connection_client(client_port, &client_ip, &cfg_id))
            {
                log_message("[FULLCONE] No tracked client for port %u - dropped", client_port);
                continue;
            }
            s = fullcone_create_session(client_port, client_ip, cfg_id);
            if (s == NULL) continue;
        }

        InterlockedExchange64((LONGLONG volatile*)&s->last_activity, (LONGLONG)GetTickCount64());
        if (sendto(s->proxy_sock, (char*)buf, len, 0,
                   (struct sockaddr*)&s->proxy_relay_addr, sizeof(s->proxy_relay_addr)) == SOCKET_ERROR)
        {
            // Proxy path broke (e.g. xray restarted): tear down; next packet recreates.
            log_message("[FULLCONE] sendto proxy failed (%d) for client port %u - session reset", WSAGetLastError(), client_port);
            int h2 = client_port % FULLCONE_SESSION_HASH;
            FULLCONE_SESSION **pp = &g_fullcone_sessions[h2];
            while (*pp != NULL && *pp != s) pp = &(*pp)->next;
            if (*pp == s) { *pp = s->next; g_fullcone_session_count--; }
            fullcone_close_session(s);
        }
    }

    // Shutdown: close everything
    for (int h = 0; h < FULLCONE_SESSION_HASH; h++)
    {
        while (g_fullcone_sessions[h] != NULL)
        {
            FULLCONE_SESSION *s = g_fullcone_sessions[h];
            g_fullcone_sessions[h] = s->next;
            fullcone_close_session(s);
        }
    }
    g_fullcone_session_count = 0;
    if (fullcone_socket != INVALID_SOCKET) { closesocket(fullcone_socket); fullcone_socket = INVALID_SOCKET; }
    return 0;
}
```

- [ ] **Step 5: Start/stop wiring**

In `ProxyBridge_Start`, next to the `udp_relay_thread = CreateThread(...)` call, add the same for `fullcone_relay_thread = CreateThread(NULL, 0, udp_fullcone_relay_server, NULL, 0, NULL);` with the same error handling pattern. In `ProxyBridge_Stop`, mirror the existing `udp_relay_thread` shutdown (close `fullcone_socket`/`fullcone_socket6` to unblock, wait, CloseHandle, NULL).

- [ ] **Step 6: Build and run the e2e test (EXPECT PASS)**

```powershell
powershell -ExecutionPolicy Bypass -File Windows/compile.ps1
# xray with Windows/tests/xray-local-socks.json still running (Task 1)
Windows/output/ProxyBridge_CLI.exe --profile Windows/tests/test-fullcone.pbprofile --verbose 3   # background
python Windows/tests/fullcone_udp_test.py
```

Expected: `3/3 distinct reply sources: ['1.1.1.1', '8.8.8.8', '9.9.9.9']`, exit 0.
Then regression: rerun with `test-legacy.pbprofile` → still `1/3`, exit 1 (legacy path untouched).

- [ ] **Step 7: Amend the spec buffering note**

In `docs/superpowers/specs/2026-07-03-fullcone-udp-mode-design.md`, replace the "32-packet ring" sentence with: "While the associate is being established (~RTT), datagrams queue in the 34012 socket's 256 KB receive buffer — nothing is dropped."

- [ ] **Step 8: Commit**

```bash
git add Windows/src/ProxyBridge.c docs/superpowers/specs/2026-07-03-fullcone-udp-mode-design.md
git commit -m "feat(core): Full Cone UDP relay - one SOCKS5 UDP ASSOCIATE per client socket"
```

---

### Task 5: Core — IPv6 support

**Files:**
- Modify: `Windows/src/ProxyBridge.c`

**Interfaces:**
- Consumes: Task 3/4 helpers and session table (`FULLCONE_SESSION.is_ipv6`, `client_ip6`).
- Produces: `static BOOL fullcone_encapsulate_v6(unsigned char *packet, UINT *packet_len, PWINDIVERT_IPV6HDR ipv6_header, PWINDIVERT_UDPHDR udp_header);` (22-byte header, `ATYP=4`), `fullcone_socket6` bound to `[::]:34012`.

- [ ] **Step 1: v6 encapsulation helper** — mirror `fullcone_encapsulate_v4`: 22-byte header (`payload[3] = SOCKS5_ATYP_IPV6`, 16 address bytes from `ipv6_header->DstAddr`, 2 port bytes), `ipv6_header->Length += 22` (payload length field), `udp_header->Length += 22`.

- [ ] **Step 2: v6 outbound wiring** — in the IPv6 UDP outbound section (line ~424): decapsulation branch for `SrcPort == 34012` (parse `ATYP=4`, strip 22 bytes, set `ipv6_header->SrcAddr` + `SrcPort`); tracked fast path and rule-matched PROXY path mirror Task 3 Step 3 using `check_process_rule_v6(..., &rule_full_cone)` and `add_connection_v6(..., use_fullcone)`.

- [ ] **Step 3: v6 relay socket** — in `udp_fullcone_relay_server`, bind `fullcone_socket6` to `[::]:34012` with `IPV6_V6ONLY` (mirror lines 2891-2909), add it to the select set; datagrams from it create/lookup sessions with `is_ipv6 = TRUE`, `client_ip6` from `get_connection_client_v6` (add this accessor mirroring `get_connection_client` for `is_ipv6` entries). The session's reader sends replies via `fullcone_socket6` to `[client_ip6]:client_port`. The proxy side stays IPv4 (the SOCKS5 relay address from UDP ASSOCIATE is IPv4; v6 destinations ride inside ATYP=4 headers).

- [ ] **Step 4: Build + rerun both e2e runs** (v4 behaviour must be unchanged: fullcone 3/3, legacy 1/3).

- [ ] **Step 5: Commit**

```bash
git add Windows/src/ProxyBridge.c
git commit -m "feat(core): IPv6 support for Full Cone UDP mode"
```

---

### Task 6: GUI — flag end-to-end

**Files:**
- Modify: `Windows/gui/Interop/ProxyBridgeNative.cs`
- Modify: `Windows/gui/Services/ProxyBridgeService.cs`
- Modify: `Windows/gui/Services/ProfileManager.cs` (ProxyRuleConfig)
- Modify: `Windows/gui/ViewModels/MainWindowViewModel.cs` (ProxyRule model + 3 apply sites + BuildCurrentProfile + profile load)
- Modify: `Windows/gui/ViewModels/ProxyRulesViewModel.cs`
- Modify: `Windows/gui/Views/ProxyRulesWindow.axaml`
- Modify: `Windows/gui/Services/Loc.cs`, `Windows/gui/Resources/Resources.resx`, `Resources.ru.resx`, `Resources.zh.resx`, `Resources.Designer.cs`

**Interfaces:**
- Consumes: `ProxyBridge_SetRuleFullCone` (Task 2).
- Produces: `ProxyBridgeService.SetRuleFullCone(uint ruleId, bool enable)`, `ProxyRule.FullConeUdp` (bool), `ProxyRuleConfig.FullConeUdp` (bool, default false), `ProxyRulesViewModel.NewFullConeUdp` (bool), Loc keys `LabelFullConeUdp`, `TooltipFullConeUdp`.

- [ ] **Step 1: Interop + service**

`ProxyBridgeNative.cs` (after `ProxyBridge_EditRule`):

```csharp
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_SetRuleFullCone(uint ruleId, [MarshalAs(UnmanagedType.Bool)] bool enable);
```

`ProxyBridgeService.cs` (after `EditRule`):

```csharp
    public bool SetRuleFullCone(uint ruleId, bool enable)
    {
        return ProxyBridgeNative.ProxyBridge_SetRuleFullCone(ruleId, enable);
    }
```

- [ ] **Step 2: Models + profile**

`ProfileManager.cs`, `ProxyRuleConfig`: add `public bool FullConeUdp { get; set; } = false;`

`MainWindowViewModel.cs`, `ProxyRule` class (line ~1525): add backing field `private bool _fullConeUdp;` and property (same `SetProperty` pattern as `IsEnabled`):

```csharp
    public bool FullConeUdp
    {
        get => _fullConeUdp;
        set => SetProperty(ref _fullConeUdp, value);
    }
```

`BuildCurrentProfile()` (line ~1491): add `FullConeUdp = r.FullConeUdp` to the `ProxyRuleConfig` initializer.
Profile load (line ~1408): add `FullConeUdp = rc.FullConeUdp` to the `ProxyRule` initializer.

- [ ] **Step 3: Apply the flag at every AddRule site**

At the three sites where a rule is registered with the core and `ruleId > 0` (startup line ~166, rules-window onAddRule line ~489, profile load line ~1425), add right after `rule.RuleId = ruleId;`:

```csharp
                        if (rule.FullConeUdp)
                            _proxyService.SetRuleFullCone(ruleId, true);
```

(the quick-add site at line ~622 creates TCP-only rules — no change).

- [ ] **Step 4: Rule editor**

`ProxyRulesViewModel.cs`: field `private bool _newFullConeUdp;`, property:

```csharp
    public bool NewFullConeUdp
    {
        get => _newFullConeUdp;
        set => SetProperty(ref _newFullConeUdp, value);
    }
```

`ResetRuleForm()`: add `NewFullConeUdp = false;`
`EditRuleCommand` (line ~294): add `NewFullConeUdp = rule.FullConeUdp;`
`SaveNewRuleCommand`, edit branch (line ~182, inside the `if (_proxyService.EditRule(...))` success block): add

```csharp
                    _proxyService.SetRuleFullCone(_currentEditingRuleId, NewFullConeUdp);
                    if (existRule != null)
                        existRule.FullConeUdp = NewFullConeUdp;
```

`SaveNewRuleCommand`, add branch (line ~204): add `FullConeUdp = NewFullConeUdp,` to the `new ProxyRule` initializer.

`ProxyRulesWindow.axaml`: in the add/edit rule form, insert below the Protocol selector:

```xml
                    <CheckBox Content="{Binding Loc.LabelFullConeUdp}"
                              IsChecked="{Binding NewFullConeUdp}"
                              ToolTip.Tip="{Binding Loc.TooltipFullConeUdp}"
                              Margin="0,8,0,0"/>
```

- [ ] **Step 5: Localization**

Add to all three resx files (same `<data>` format as `LabelFlow`):
- `LabelFullConeUdp`: en/zh-safe `Full Cone UDP (game mode)`, ru `Full Cone UDP (игровой режим)`, zh `Full Cone UDP（游戏模式）`
- `TooltipFullConeUdp`: en `Dedicated UDP tunnel per game socket. Fixes matchmaking/P2P in online games. SOCKS5 proxies only.`, ru `Отдельный UDP-туннель на каждый сокет игры. Чинит матчмейкинг/P2P в онлайн-играх. Только для SOCKS5-прокси.`, zh `为每个游戏套接字建立独立的 UDP 隧道。修复在线游戏的匹配/P2P。仅适用于 SOCKS5 代理。`

`Resources.Designer.cs`: add properties next to `LabelFlow` (line ~469):

```csharp
        internal static string LabelFullConeUdp => ResourceManager.GetString("LabelFullConeUdp", resourceCulture);
        internal static string TooltipFullConeUdp => ResourceManager.GetString("TooltipFullConeUdp", resourceCulture);
```

`Loc.cs` (next to `LabelFlow`, line ~158):

```csharp
    public string LabelFullConeUdp   => Resources.Resources.LabelFullConeUdp;
    public string TooltipFullConeUdp => Resources.Resources.TooltipFullConeUdp;
```

- [ ] **Step 6: Build + manual smoke**

```powershell
dotnet build Windows/gui/ProxyBridge.GUI.csproj
```

Expected: clean build. Manual check (can be deferred to final verification): rule editor shows the checkbox; saving writes `"FullConeUdp": true` into the profile; reloading keeps it.

- [ ] **Step 7: Commit**

```bash
git add Windows/gui
git commit -m "feat(gui): Full Cone UDP checkbox on proxy rules"
```

---

### Task 7: XRay — XUDP mux

**Files:**
- Modify: `Windows/gui/Services/XRayConfig.cs`
- Modify: `Windows/gui/Services/XRayService.cs` (GenerateXRayConfig)
- Modify: `Windows/gui/ViewModels/XRaySettingsViewModel.cs`
- Modify: `Windows/gui/Views/XRaySettingsWindow.axaml`
- Modify: `Loc.cs` + three resx + `Resources.Designer.cs`

**Interfaces:**
- Produces: `XRayConfig.XudpEnabled` (bool, default true), Loc key `LabelXudp`.

- [ ] **Step 1: Config model** — `XRayConfig.cs`: add `public bool XudpEnabled { get; set; } = true;` (old settings.json without the field deserializes to true).

- [ ] **Step 2: Config generation** — in `GenerateXRayConfig` (XRayService.cs line ~315), the vless-out object currently ends with the `streamSettings` block. Change the closing of that block from:

```csharp
      ""streamSettings"": {{
        ...
      }}
    }}
```

to:

```csharp
      ""streamSettings"": {{
        ...
      }}{(cfg.XudpEnabled ? @",
      ""mux"": { ""enabled"": true, ""concurrency"": -1, ""xudpConcurrency"": 16, ""xudpProxyUDP443"": ""reject"" }" : "")}
    }}
```

(the injected literal is a nested verbatim string inside the interpolation hole — single braces there are literal; only the doubled quotes matter).

- [ ] **Step 3: Settings VM + window** — `XRaySettingsViewModel.cs`: add property (same pattern as `Flow`, line ~60):

```csharp
    private bool _xudpEnabled = true;
    public bool XudpEnabled
    {
        get => _xudpEnabled;
        set => SetProperty(ref _xudpEnabled, value);
    }
```

Load (line ~197): `XudpEnabled = initial.XudpEnabled;`. Save (the object built at line ~255): add `XudpEnabled = XudpEnabled,`. The vless:// import (line ~349) does not carry xudp — leave the property untouched there (keeps current value).

`XRaySettingsWindow.axaml`: below the Flow/Fingerprint section add:

```xml
                    <CheckBox Content="{Binding Loc.LabelXudp}"
                              IsChecked="{Binding XudpEnabled}"
                              Margin="0,8,0,0"/>
```

- [ ] **Step 4: Localization** — `LabelXudp`: en `XUDP (Full Cone UDP) — requires xray-core 1.8+ on the server`, ru `XUDP (Full Cone UDP) — нужен xray-core 1.8+ на сервере`, zh `XUDP (Full Cone UDP) — 服务器需要 xray-core 1.8+`. Add resx entries + Designer + Loc properties as in Task 6 Step 5.

- [ ] **Step 5: Build + config check**

```powershell
dotnet build Windows/gui/ProxyBridge.GUI.csproj
```

Then verify generated JSON is valid: run the GUI once, start XRay, and check the temp config (path is logged / `_configFilePath`) contains the mux block; or add a quick manual `xray run -test -c <generated>` check.

- [ ] **Step 6: Commit**

```bash
git add Windows/gui
git commit -m "feat(xray): XUDP mux for full-cone UDP through VLESS (default on)"
```

---

### Task 8: Final verification

- [ ] **Step 1: Full rebuild** — `compile.ps1` + `dotnet build`, both clean.
- [ ] **Step 2: E2E matrix** (elevated, GUI stopped, local xray from Task 1):
  - fullcone profile → `3/3`, exit 0
  - legacy profile → `1/3`, exit 1 (legacy path unchanged)
- [ ] **Step 3: GUI smoke** — start GUI, enable Full Cone UDP on the MK12.exe UDP rule, confirm profile JSON gets `"FullConeUdp": true` and the core log shows `Rule ID N: Full Cone UDP enabled`.
- [ ] **Step 4: Live acceptance (user)** — MK1 with UDP rule → PROXY (SOCKS5/xray) + Full Cone UDP + XUDP on: match connect must succeed. GUI log should show `[FULLCONE] Session for client port ...` lines.
- [ ] **Step 5:** Use superpowers:verification-before-completion, then superpowers:finishing-a-development-branch.
