import os
import socket
import struct
import sys
import threading
import time

UPSTREAM_HOST = sys.argv[1] if len(sys.argv) > 1 else "108.175.2.199"
UPSTREAM_LOBBY = int(sys.argv[2]) if len(sys.argv) > 2 else 25000
UPSTREAM_CHAT = int(sys.argv[3]) if len(sys.argv) > 3 else 25900
LOCAL_LOBBY_PORTS = (25100, 25110)
LOCAL_CHAT_PORT = 25900

CATEGORIES = (
    "Start sLoginFailed sCreateIDFailed sSessionInfo sStartSession sServer sUserStart sUserRestart "
    "sIntro sUserInfo sGlobalSync sInventory sStore sRoomList sMode sLobby sRoom sLadder sSinglePlay "
    "sUserRecords sHost sGame sObserver sUserMsg sTransServer sReward sNotice sError sRank sGMStart "
    "sCapsuleMachine sCommunity sChat sChatRoom sSecurity hsHostServerInfo hsHostServer lsHostServer "
    "cClientInfo cClientTransferInfo cClientInfo_Chatter cClientInfo_Host cSessionReady cServer "
    "cUserInfo cIntro cTutorial cInventory cStore cMode cLobby cRoom cLadder cSinglePlay cHost cGame "
    "cItem cUserMsg cUDP cCommand cDevCommand cRank cRecord cCapsuleMachine cCommunity cChat cChatRoom "
    "cSecurity gmClientInfo gmMsg gmCommand gmServerInfo gmGMStart gmGMEnd End"
).split()

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CAPTURE_PATH = os.path.join(ROOT, "logs", "capture-%s.log" % time.strftime("%Y%m%d-%H%M%S"))
_lock = threading.Lock()


def log(text):
    line = "%s %s" % (time.strftime("%H:%M:%S"), text)
    with _lock:
        print(line, flush=True)
        with open(CAPTURE_PATH, "a", encoding="utf-8") as handle:
            handle.write(line + "\n")


def lzf_decompress(data):
    out, i = bytearray(), 0
    while i < len(data):
        ctrl = data[i]
        i += 1
        if ctrl < 32:
            out += data[i:i + ctrl + 1]
            i += ctrl + 1
        else:
            length = ctrl >> 5
            if length == 7:
                length += data[i]
                i += 1
            ref = len(out) - ((ctrl & 0x1F) << 8) - data[i] - 1
            i += 1
            for k in range(length + 2):
                out.append(out[ref + k])
    return bytes(out)


def read_varlen(buf):
    if not buf:
        return None
    kind = buf[0] & 0xC0
    if kind == 0x00:
        return buf[0], 1
    if kind == 0x40:
        return (((buf[0] & 0x3F) << 8) | buf[1], 2) if len(buf) >= 2 else None
    if kind == 0x80:
        if len(buf) < 4:
            return None
        return ((buf[0] & 0x3F) << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3], 4
    return (struct.unpack_from("<I", buf, 1)[0], 5) if len(buf) >= 5 else None


def describe(message):
    category = message[0]
    name = CATEGORIES[category] if category < len(CATEGORIES) else "0x%02X" % category
    return "%-16s %s" % (name, message[1:].hex())


class Direction:
    def __init__(self, label, tag):
        self.label = label
        self.tag = tag
        self.session = False
        self.buffer = b""

    def feed(self, data):
        if not self.session:
            log("%s %s HANDSHAKE %s" % (self.tag, self.label, data.hex()))
            return
        self.buffer += data
        while True:
            header = read_varlen(self.buffer)
            if header is None:
                return
            size, head = header
            if len(self.buffer) < head + size:
                return
            body, self.buffer = self.buffer[head:head + size], self.buffer[head + size:]
            try:
                plain = lzf_decompress(body)
                if plain[0] == 0x0B:
                    log("%s %s seq %3d %s" % (self.tag, self.label, plain[1], describe(plain[2:])))
                else:
                    log("%s %s seq %3d BATCH/type %02x %s" % (self.tag, self.label, plain[1], plain[0], plain[2:].hex()))
            except Exception as error:
                log("%s %s UNDECODED (%s) %s" % (self.tag, self.label, error, body.hex()))


def pump(source, target, on_data):
    try:
        while True:
            data = source.recv(65536)
            if not data:
                break
            on_data(data)
            target.sendall(data)
    except OSError:
        pass
    for sock in (source, target):
        try:
            sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass


_connection_ids = iter(range(1, 1 << 30))


def handle_lobby(client, address):
    tag = "[L%d]" % next(_connection_ids)
    log("%s client %s:%d connected -> %s:%d" % (tag, address[0], address[1], UPSTREAM_HOST, UPSTREAM_LOBBY))
    try:
        server = socket.create_connection((UPSTREAM_HOST, UPSTREAM_LOBBY), timeout=10)
        server.settimeout(None)
    except OSError as error:
        log("%s upstream connect failed: %s" % (tag, error))
        client.close()
        return

    to_server = Direction("C->S", tag)
    to_client = Direction("S->C", tag)

    def from_client(data):
        to_server.feed(data)
        if not to_server.session and data[:1] == b"\x2a":
            to_server.session = True

    def from_server(data):
        if not to_client.session and to_server.session and data[:1] == b"\x04":
            log("%s S->C HANDSHAKE 04" % tag)
            to_client.session = True
            if len(data) > 1:
                to_client.feed(data[1:])
            return
        to_client.feed(data)

    threading.Thread(target=pump, args=(server, client, from_server), daemon=True).start()
    pump(client, server, from_client)
    log("%s closed" % tag)


def handle_chat(client, address):
    tag = "[C%d]" % next(_connection_ids)
    log("%s chat client connected" % tag)
    try:
        server = socket.create_connection((UPSTREAM_HOST, UPSTREAM_CHAT), timeout=10)
        server.settimeout(None)
    except OSError as error:
        log("%s upstream connect failed: %s" % (tag, error))
        client.close()
        return
    threading.Thread(target=pump, args=(server, client, lambda d: log("%s S->C RAW %s" % (tag, d.hex()))), daemon=True).start()
    pump(client, server, lambda d: log("%s C->S RAW %s" % (tag, d.hex())))
    log("%s closed" % tag)


def serve_tcp(port, handler):
    listener = socket.socket()
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("0.0.0.0", port))
    listener.listen(8)
    log("TCP listening on %d" % port)
    while True:
        client, address = listener.accept()
        threading.Thread(target=handler, args=(client, address), daemon=True).start()


def serve_udp(port):
    local = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    local.bind(("0.0.0.0", port))
    upstream = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    upstream.bind(("0.0.0.0", 0))
    state = {"client": None}
    log("UDP listening on %d" % port)

    def back():
        while True:
            try:
                data, _ = upstream.recvfrom(65536)
            except OSError:
                continue
            log("[U%d] S->C %s" % (port, data.hex()))
            if state["client"]:
                local.sendto(data, state["client"])

    threading.Thread(target=back, daemon=True).start()
    seen = set()
    while True:
        try:
            data, address = local.recvfrom(65536)
        except OSError:
            continue
        state["client"] = address
        if data not in seen:
            seen.add(data)
            log("[U%d] C->S %s" % (port, data.hex()))
        upstream.sendto(data, (UPSTREAM_HOST, UPSTREAM_LOBBY))


def main():
    os.makedirs(os.path.dirname(CAPTURE_PATH), exist_ok=True)
    log("Capture file: %s" % CAPTURE_PATH)
    log("Upstream: %s lobby %d chat %d" % (UPSTREAM_HOST, UPSTREAM_LOBBY, UPSTREAM_CHAT))
    for port in LOCAL_LOBBY_PORTS:
        threading.Thread(target=serve_tcp, args=(port, handle_lobby), daemon=True).start()
        threading.Thread(target=serve_udp, args=(port,), daemon=True).start()
    threading.Thread(target=serve_tcp, args=(LOCAL_CHAT_PORT, handle_chat), daemon=True).start()
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
