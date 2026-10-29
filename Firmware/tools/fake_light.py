#!/usr/bin/env python3
"""Fake the bat tag's light sensor so real-light features can be tested without hardware.

Sends "LUX <lux>" at 10 Hz to Unity's UdpLink (default 127.0.0.1:1234), like the tag's BH1750.
Unity learns the room's normal light from it, treats a sudden jump as a phone flash and light
that stays up as a held torch (EdgeLightEffects).

Interactive (default): type a letter and press Enter
    f   phone flash: 0.3 s burst
    t   torch on / off (held light)
    q   quit
Scripted:
    python3 fake_light.py --scenario demo    # flash, then a 6 s torch, repeating

Only LUX is sent, so the bat can still be flown with the keyboard. Add --ori to also send a
level "ORI" stream, as a connected tag would (the keyboard then no longer steers).
Standard library only.
"""
import argparse
import socket
import sys
import threading
import time


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=1234)
    ap.add_argument("--room", type=float, default=120.0, help="normal room light (lux)")
    ap.add_argument("--flash", type=float, default=3000.0, help="phone flash peak (lux)")
    ap.add_argument("--torch", type=float, default=1500.0, help="held torch (lux)")
    ap.add_argument("--scenario", choices=["interactive", "demo"], default="interactive")
    ap.add_argument("--ori", action="store_true", help="also send a level ORI stream at 50 Hz")
    args = ap.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    target = (args.host, args.port)
    state = {"flash_until": 0.0, "torch": False, "quit": False}

    def send(msg):
        sock.sendto(msg.encode(), target)

    def demo():
        while not state["quit"]:
            time.sleep(4)
            print("demo: flash")
            state["flash_until"] = time.time() + 0.3
            time.sleep(5)
            print("demo: torch on (6 s)")
            state["torch"] = True
            time.sleep(6)
            print("demo: torch off")
            state["torch"] = False

    def keys():
        print("f = flash, t = torch on/off, q = quit (then Enter)")
        for line in sys.stdin:
            c = line.strip().lower()[:1]
            if c == "f":
                state["flash_until"] = time.time() + 0.3
                print("flash")
            elif c == "t":
                state["torch"] = not state["torch"]
                print("torch", "on" if state["torch"] else "off")
            elif c == "q":
                break
        state["quit"] = True

    worker = demo if args.scenario == "demo" else keys
    threading.Thread(target=worker, daemon=True).start()

    print(f"sending LUX to {target[0]}:{target[1]} (room {args.room:.0f} lx){' + ORI' if args.ori else ''}; Ctrl+C stops")
    seq, next_lux = 0, 0.0
    try:
        while not state["quit"]:
            now = time.time()
            if args.ori:
                send(f"ORI 0.00 0.00 0.00 0 0 0 0 {seq}")
                seq += 1
            if now >= next_lux:
                lux = args.room
                if state["torch"]:
                    lux = args.torch
                if now < state["flash_until"]:
                    lux = max(lux, args.flash)
                send(f"LUX {lux:.1f}")
                next_lux = now + 0.1
            time.sleep(0.02)
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
