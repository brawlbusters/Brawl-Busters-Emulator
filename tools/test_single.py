import struct
import sys

from test_client import check
from test_shop import expect, new_player_at_home


def main():
    ok = True
    c = new_player_at_home()

    c.send(bytes.fromhex("311c"))
    ok &= expect(c, "single lobby: sMode 08", "0e08")
    ok &= expect(c, "single lobby: progress 01 00 00", "1200010000")

    c.send(bytes.fromhex("35030200"))
    ok &= expect(c, "start stage 2: progress", "1200010000")
    ok &= expect(c, "start stage 2: sMode 09", "0e09")
    ok &= expect(c, "start stage 2: sSinglePlay 01 stage 01", "1201020001")

    c.send(bytes.fromhex("3507"))
    c.send(bytes.fromhex("350501"))
    ok &= expect(c, "win: exp 10 and gold 70000", "0901" + "00" * 8 + "0300" + "0a000000" + "70110100")

    c.send(bytes.fromhex("3506"))
    ok &= expect(c, "exit: empty record update", "0901" + "00" * 10)
    ok &= expect(c, "exit: progress 03 00 04 as recorded", "1200030004")
    ok &= expect(c, "exit: sMode 08", "0e08")
    ok &= expect(c, "exit: progress again", "1200030004")

    c.send(bytes.fromhex("35030400"))
    ok &= expect(c, "start stage 4: progress", "1200030004")
    ok &= expect(c, "start stage 4: sMode 09", "0e09")
    ok &= expect(c, "start stage 4: sSinglePlay 01", "1201040001")
    c.send(bytes.fromhex("350500"))
    c.send(bytes.fromhex("3506"))
    ok &= expect(c, "exit after a loss: empty record update", "0901" + "00" * 10)
    ok &= expect(c, "exit after a loss: progress unchanged", "1200030004")

    for _ in range(2):
        c.recv()

    def balance(message):
        assert message[:12] == bytes.fromhex("0901") + bytes(8) + bytes.fromhex("0300"), message.hex()
        return struct.unpack_from("<II", message, 12)

    c.send(bytes.fromhex("35030200"))
    for _ in range(3):
        c.recv()
    c.send(bytes.fromhex("350500"))
    c.send(bytes.fromhex("3504"))
    c.send(bytes.fromhex("350501"))
    exp, gold = balance(c.recv())
    ok &= check("retry, then a repeat clear of stage 2 pays REWARD_02 (+1 exp, +1 BP)", (exp, gold) == (11, 70001), str((exp, gold)))
    c.send(bytes.fromhex("350501"))
    c.send(bytes.fromhex("3506"))
    got = c.recv()
    ok &= check("a duplicate result is not paid twice", got == bytes.fromhex("0901") + bytes(10), got.hex())
    for _ in range(3):
        c.recv()

    c.send(bytes.fromhex("35030600"))
    for _ in range(3):
        c.recv()
    c.send(bytes.fromhex("350501"))
    exp, gold = balance(c.recv())
    got = c.recv()
    ok &= check("stage 6 first clear: no exp / BP", (exp, gold) == (11, 70001), str((exp, gold)))
    ok &= check("stage 6 first clear: reward item 1005 (glasses, 14 days) added",
                got[:3] == bytes.fromhex("0b0101") and got[8] == 8 and struct.unpack_from("<I", got, 9)[0] == 1005, got.hex())
    c.send(bytes.fromhex("3506"))
    c.recv()
    got = c.recv()
    ok &= check("progress bitset now has stages 2 and 6 (7 bits, 0x44)", got == bytes.fromhex("1200070044"), got.hex())

    print("\nALL PASSED" if ok else "\nSOME CHECKS FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
