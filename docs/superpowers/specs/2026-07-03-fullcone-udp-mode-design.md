# Full Cone UDP mode — design

Date: 2026-07-03
Status: approved

## Problem

P2P games (verified with Mortal Kombat 1) hang forever on match connect when their UDP
is routed through ProxyBridge, while TCP (lobby/API) works. Root cause, confirmed by
live logs: the UDP relay tracks **one destination per client source port**
(`add_connection` overwrites `orig_dest_ip/port` on every packet to a new destination).
MK1 pings ~18 matchmaking relay endpoints from a single socket within one second, so:

- outbound: the relay resolves each datagram's destination from the same single slot,
  which may have already flipped to another endpoint — packets can go to the wrong host;
- inbound: replies are matched by `reply source == tracked destination`, so replies from
  all but the most recently contacted endpoint are dropped, and the WinDivert
  re-injection rewrites the packet source from the same stale slot.

Additionally, without XUDP each UDP flow leaves the xray server from a different source
port (symmetric-NAT behaviour), which breaks NAT hole punching with peers even if the
relay is fixed.

## Solution overview

A new opt-in **Full Cone UDP** mode implemented alongside the existing path:

- carried **in-band**: every relayed datagram carries its own SOCKS5 UDP header, so the
  data path needs no shared state at all;
- one dedicated SOCKS5 UDP ASSOCIATE **per client socket** (per src_port) — replies from
  any origin reach the right client socket (true full cone at the proxy layer);
- XUDP mux in the generated xray config so all UDP flows share one public port on the VPS.

The existing UDP path (port 34011, shared funnel, single-slot tracking) is untouched and
remains the default.

## Scope of the flag

Per-rule flag `Full Cone UDP`. Effective only when the rule's protocol includes UDP,
action is PROXY and the target proxy config is SOCKS5. In all other cases behaviour is
exactly as today.

## Core data path (`Windows/src/ProxyBridge.c`)

New constant `LOCAL_UDP_FULLCONE_PORT 34012`.

**Outbound (app → network), full-cone rule matched:**

- The WinDivert packet thread inserts a standard SOCKS5 UDP request header before the
  payload: 10 bytes for IPv4 (`RSV=0, FRAG=0, ATYP=1, DST.ADDR, DST.PORT` — the real
  destination), 22 bytes for IPv6 (`ATYP=4`). Adjusts `IP.Length`/`UDP.Length`,
  redirects to port 34012 (same loopback/NIC logic as the 34011 branch), recalculates
  checksums.
- If `packet_len + header > MAXBUF` the packet is dropped with a log message
  (unreachable for game-sized packets).
- The existing connection table is updated only to provide `src_port → proxy_config_id`
  for session creation; it no longer participates in data routing for this mode.

**Full-cone relay (new thread `udp_fullcone_relay_server`):**

- Listens on 34012 (IPv4 and IPv6 sockets).
- Per client socket — a session, keyed by `(client_port, is_ipv6)`: own TCP control +
  own UDP ASSOCIATE to the SOCKS5 proxy. Sessions stored in a hash table under a dedicated
  `g_fullcone_lock`.
- Client datagrams are already SOCKS5-formatted → forwarded to the proxy **verbatim**.
- The associate handshake (up to ~3 s) runs on a per-session **worker thread**, so it never
  blocks the relay's accept loop or other sessions. While it is in flight, incoming
  datagrams are buffered in a **per-session 32-packet ring** and flushed in order once the
  ASSOCIATE completes — the first pings to each region are not lost.
- Per session, a reader thread: datagrams from the proxy (SOCKS5 format, origin in the
  header) are sent to the client **verbatim** from port 34012. Its `sendto` on the shared
  34012 socket uses `SO_SNDTIMEO` so it can never wedge and block teardown.
- Lifecycle: idle timeout 120 s → close sockets, delete session; TCP control / proxy send
  failure → tear down the session, the next client packet recreates it (per-session 1 s
  retry guard via a lingering `FC_DEAD` entry). Teardown is relay-thread-only and joins the
  worker/reader before freeing (no use-after-free).
- Session cap 256; on overflow — log + drop.

**Inbound (relay 34012 → app):**

- The WinDivert thread sees `SrcPort == 34012`, reads the origin from the SOCKS5 header,
  strips the header, sets the origin as the packet's source ip/port, injects (same
  loopback/inbound logic as the 34011 branch).
- Key property: **no table lookups on the data path** — a reply from any address
  (including a peer the client never contacted first, i.e. hole punching) reaches the
  right client socket with the right source address.

## API and GUI

- `ProxyBridge.h`: additive `PROXYBRIDGE_API BOOL ProxyBridge_SetRuleFullCone(UINT32
  rule_id, BOOL enable);` — `AddRule`/`EditRule` signatures unchanged (less upstream
  friction). `PROCESS_RULE` gains `BOOL full_cone`; `check_process_rule` /
  `check_process_rule_v6` gain an out flag.
- GUI (`Windows/gui`): checkbox "Full Cone UDP" in the rule editor, enabled only for
  UDP+PROXY+SOCKS5; `FullConeUdp` field in profile JSON (absent = false, old profiles
  stay compatible); localization en/ru/zh; `ProxyBridgeService` calls the setter after
  `AddRule`.

## XRay (XUDP)

- `XRayConfig.XudpEnabled` (default **true**), checkbox "XUDP (Full Cone)" in XRay
  settings.
- When enabled, `vless-out` gains:
  `"mux": { "enabled": true, "concurrency": -1, "xudpConcurrency": 16,
  "xudpProxyUDP443": "<mode>" }` — TCP is not muxed (vision stays effective), UDP goes
  through XUDP with a single public source port on the VPS. Requires xray-core ≥ 1.8 on
  both ends; the checkbox is the escape hatch for old servers.
- `XRayConfig.XudpProxyUDP443` controls how UDP:443 (QUIC/HTTP3) is handled, exposed as a
  dropdown: **`allow`** (default — route QUIC through XUDP), `skip` (send QUIC direct,
  bypassing the proxy), or `reject` (block QUIC so apps fall back to TCP). The old
  hard-coded `reject` is no longer the default, so HTTP/3 traffic is not silently
  blackholed.

## Errors and edge cases

- Associate fails → drop packets with log, retry behind the existing guard interval.
- Full-cone rule + HTTP proxy → UDP dropped with log (as today).
- `FRAG != 0` in proxy replies → drop (SOCKS5 UDP fragmentation unsupported, consistent
  with the rest of the code).

## Testing

- **Failing test first**: a python script (full-cone rule for `python.exe` via local
  xray) opens **one** UDP socket and sends DNS queries to 8.8.8.8, 1.1.1.1 and 9.9.9.9
  concurrently. Today at most one reply arrives; after the fix — all three, each with
  the correct source address.
- **Regression**: same test without the flag — the legacy path behaves as before.
- Build with `Windows/compile.ps1`; tests need admin rights (WinDivert) and the running
  ProxyBridge instance stopped for the duration.
- Final acceptance — live MK1 match connect with UDP→PROXY and the flag enabled.
