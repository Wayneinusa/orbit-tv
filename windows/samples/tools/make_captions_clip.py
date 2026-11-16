#!/usr/bin/env python3
"""Builds samples/captions.ts: a short clip that carries both kinds of US closed captions.

  * CEA-608 ("classic") captions, the kind VLC always draws at one fixed size
  * CEA-708 ("digital", DTVCC) captions, whose size follows the subtitle size setting

The app's self-test plays this clip to check that the subtitle size setting works for captions.
Needs ffmpeg with libx264.  Usage:  python3 make_captions_clip.py OUTPUT.ts
"""
import subprocess
import sys
import tempfile
import os

FPS = 30
SECONDS = 60
TEXT_608 = "CLASSIC CAPTIONS 608"
TEXT_708 = "DIGITAL CAPTIONS 708"


def parity(byte):
    """CEA-608 bytes carry odd parity in their top bit."""
    return byte | (0x80 if bin(byte & 0x7F).count("1") % 2 == 0 else 0)


def pairs_608():
    """One roll-up caption line, sent over and over. Returns a list of (byte1, byte2)."""
    out = []
    control = [(0x14, 0x25),      # RU2: roll-up captions, two rows
               (0x14, 0x2D),      # carriage return
               (0x14, 0x70)]      # move to row 15, first column, white
    for first, second in control:
        out += [(parity(first), parity(second))] * 2          # control codes are sent twice
    text = TEXT_608 + (" " if len(TEXT_608) % 2 else "")
    for i in range(0, len(text), 2):
        out.append((parity(ord(text[i])), parity(ord(text[i + 1]))))
    out += [(0x80, 0x80)] * 20                                 # a pause before the line repeats
    return out


def packet_708(sequence):
    """One DTVCC packet for caption service 1: define a window, write the text, show it."""
    commands = bytes([
        0x98,                       # DefineWindow 0
        0x20 | 0x18 | 0x00,         # visible, row and column lock, priority 0
        50,                         # anchor: 50 of 75 rows down (not relative)
        20,                         # anchor: 20 of 210 columns across
        (0 << 4) | (2 - 1),         # anchor point top-left, two rows
        32 - 1,                     # 32 columns
        (1 << 3) | 1,               # window style 1, pen style 1 (white on black)
        0x92, 0x00, 0x00,           # SetPenLocation row 0, column 0
    ]) + TEXT_708.encode("ascii") + bytes([0x03])               # ETX: show what was written
    assert len(commands) <= 31, len(commands)
    block = bytes([(1 << 5) | len(commands)]) + commands        # service 1, block size
    size_code = (len(block) + 1 + 1) // 2                       # packet length in byte pairs
    data = block + bytes(size_code * 2 - 1 - len(block))        # zero padding ends the packet
    return bytes([(sequence & 3) << 6 | size_code]) + data


def cc_triplets(frame, line_608):
    """The caption bytes that travel with one video frame."""
    first, second = line_608[frame % len(line_608)]
    triplets = bytes([0xFC, first, second])                     # valid, field 1 (CEA-608)
    if frame % 15 == 0:                                         # twice a second: a full DTVCC packet
        packet = packet_708(frame // 15)
        triplets += bytes([0xFF, packet[0], packet[1]])         # valid, DTVCC packet start
        for i in range(2, len(packet), 2):
            triplets += bytes([0xFE, packet[i], packet[i + 1]]) # valid, DTVCC packet data
    return triplets


def sei_nal(triplets):
    count = len(triplets) // 3
    assert count <= 31
    payload = bytes([0xB5, 0x00, 0x31]) + b"GA94" + bytes([0x03, 0xC0 | count, 0xFF]) + triplets + bytes([0xFF])
    assert len(payload) < 255
    rbsp = bytes([4, len(payload)]) + payload + bytes([0x80])   # user_data_registered_itu_t_t35
    escaped = bytearray()
    zeros = 0
    for byte in rbsp:                                           # emulation prevention
        if zeros >= 2 and byte <= 3:
            escaped.append(3)
            zeros = 0
        escaped.append(byte)
        zeros = zeros + 1 if byte == 0 else 0
    return b"\x00\x00\x00\x01\x06" + bytes(escaped)


def split_nals(data):
    """Yields the NAL units of an Annex-B stream, each with its start code."""
    starts = []
    i = 0
    while True:
        i = data.find(b"\x00\x00\x01", i)
        if i < 0:
            break
        starts.append(i - 1 if i > 0 and data[i - 1] == 0 else i)
        i += 3
    for index, start in enumerate(starts):
        end = starts[index + 1] if index + 1 < len(starts) else len(data)
        yield data[start:end]


def main():
    output = sys.argv[1]
    work = tempfile.mkdtemp()
    plain = os.path.join(work, "plain.h264")
    captioned = os.path.join(work, "captioned.h264")
    # A dark, almost still picture: no B-frames, one slice per frame, so frames come in display order.
    subprocess.run([
        "ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi",
        "-i", f"color=c=0x1c2a4a:s=640x360:r={FPS},drawbox=x='mod(t*40,600)':y=40:w=40:h=40:color=0x3a6ea5:t=fill",
        "-t", str(SECONDS), "-c:v", "libx264", "-preset", "medium", "-profile:v", "main", "-pix_fmt", "yuv420p",
        "-bf", "0", "-g", str(FPS), "-x264-params", "aud=1:slices=1:sliced-threads=0:repeat-headers=1",
        "-bsf:v", "filter_units=remove_types=6", "-f", "h264", plain], check=True)

    line = pairs_608()
    frame = 0
    with open(captioned, "wb") as out:
        for nal in split_nals(open(plain, "rb").read()):
            body = nal[4:] if nal.startswith(b"\x00\x00\x00\x01") else nal[3:]
            nal_type = body[0] & 0x1F
            first_slice_of_picture = nal_type in (1, 5) and (body[1] & 0x80) != 0
            if first_slice_of_picture:
                out.write(sei_nal(cc_triplets(frame, line)))
                frame += 1
            out.write(nal)
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-framerate", str(FPS), "-i", captioned,
                    "-c", "copy", "-f", "mpegts", output], check=True)
    print(f"{output}: {frame} frames, {os.path.getsize(output)} bytes")


if __name__ == "__main__":
    main()
