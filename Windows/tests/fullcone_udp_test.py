"""Hermetic full-cone UDP test using local echo servers.

One UDP socket -> 3 echo servers on the machine's real LAN IP (not 127.0.0.1),
ports 15301/15302/15303.  Full-cone routing must deliver all 3 replies with
correct source (ip, port) pairs.  Legacy single-slot tracking delivers only 1.

ProxyBridge is configured to proxy python.exe UDP to ports 15301-15303 only,
so the echo servers' replies to xray's random port are never intercepted.
"""
import socket, sys, threading, time

# ── Discover the machine's real LAN IP (first non-loopback routable addr) ─────
def get_lan_ip():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        try:
            s.connect(("8.8.8.8", 80))  # does not send any packet
            return s.getsockname()[0]
        except Exception:
            return "127.0.0.1"

LAN_IP = get_lan_ip()
PORTS  = [15301, 15302, 15303]
SERVERS = [(LAN_IP, p) for p in PORTS]

print(f"LAN IP: {LAN_IP}")
print(f"Echo servers: {SERVERS}")

# ── UDP echo server (runs in a background thread) ──────────────────────────────
def echo_server(host, port, stop_event):
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        s.bind((host, port))
    except OSError as e:
        print(f"[ECHO] bind({host}:{port}) failed: {e}", file=sys.stderr)
        return
    s.settimeout(0.5)
    while not stop_event.is_set():
        try:
            data, addr = s.recvfrom(4096)
            s.sendto(data, addr)   # echo back to sender (xray relay)
        except socket.timeout:
            pass
        except Exception as e:
            if not stop_event.is_set():
                print(f"[ECHO] {host}:{port} error: {e}", file=sys.stderr)
    s.close()

stop = threading.Event()
for p in PORTS:
    t = threading.Thread(target=echo_server, args=(LAN_IP, p, stop), daemon=True)
    t.start()

time.sleep(0.3)  # give servers time to bind

# ── Client: one UDP socket, send to all 3 echo servers ────────────────────────
client = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
client.bind(("0.0.0.0", 0))
client.settimeout(6)

for i, (ip, port) in enumerate(SERVERS):
    payload = f"ping-{i}".encode()
    client.sendto(payload, (ip, port))
    print(f"sent to {ip}:{port}")

# ── Collect replies ────────────────────────────────────────────────────────────
got = set()
try:
    while len(got) < len(SERVERS):
        data, addr = client.recvfrom(4096)
        print(f"reply from {addr[0]}:{addr[1]}, {len(data)} bytes")
        got.add(addr)
except socket.timeout:
    pass

stop.set()

print(f"{len(got)}/{len(SERVERS)} distinct reply sources: {sorted(got)}")
sys.exit(0 if len(got) == len(SERVERS) else 1)
