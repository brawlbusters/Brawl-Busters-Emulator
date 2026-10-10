"""Gives ranked matches their own loading screen: the team deathmatch backdrop with a "RANKED MATCH" title.

The loading screen (Data/ui_gfx_pbloading.bus, pbloading.gfx) has one background clip per game mode and a table
aBGName[mode] -> 'mcBG_TDM', 'mcBG_SUV', ...; SetLevelInfo(name, mode, image) shows this[aBGName[mode]]. Ranked play
is mode 10 (TYPE_GAME_MODE_LADDER_TDM) and has no entry, so its loading screen was black.

A background clip is: a backdrop shape, three description lines, and a "PbLocalizeMovieClip" whose instance name is
the linkage name of the title picture it attaches ('Mode_Teamdeathmatch' -> the picture PbLoading_I3B.dds).

What this tool adds to pbloading.gfx (new tags in front of the first ShowFrame, nothing existing is moved or changed):

    image 210   PbLoading_ID2.dds, 393 x 39 like the team deathmatch title        (DefineExternalImage, tag 1001)
    shape 211   the title shape of team deathmatch, filled with image 210         (copy of shape 60)
    sprite 212  shows shape 211, exported as 'Mode_Laddermatch'                   (copy of sprite 61)
    sprite 213  the team deathmatch background with the title clip named 'Mode_Laddermatch'   (copy of sprite 153)
    placement   sprite 213 on the stage as 'mcBG_LAD', at depth 60, where mcBG_TDM is
    script      aBGName[10] = 'mcBG_LAD'; this.mcBG_LAD._visible = false;

and to the archive: the picture data/ui/gfx/pbloading/pbloading_id2.dds (512 x 64, DXT5), drawn here.
The description lines are the team deathmatch ones - ranked play is team deathmatch.

The archive is rewritten in place; the first time it is kept as ui_gfx_pbloading.bus.before-ranked-title.
The client must not be running, and tools/patch_client_ranked_loading.py must not be applied (it makes the client
ask for the team deathmatch background instead; `python tools/patch_client_ranked_loading.py undo` removes it).

    python tools/patch_loading_ranked_title.py            # patch
    python tools/patch_loading_ranked_title.py show       # say whether the archive has the patch
    python tools/patch_loading_ranked_title.py undo       # put the kept original back
"""
import os
import shutil
import struct
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCHIVE = os.path.join(ROOT, "..", "Data", "ui_gfx_pbloading.bus")
BACKUP = ARCHIVE + ".before-ranked-title"

FOLDER = "data/ui/gfx/pbloading/"
GFX_NAME = FOLDER + "pbloading.gfx"
TDM_TITLE_DDS = FOLDER + "pbloading_i3b.dds"
NEW_DDS = FOLDER + "pbloading_id2.dds"
NEW_DDS_TAG_NAME = b"PbLoading_ID2.dds"

IMAGE_ID, SHAPE_ID, TITLE_SPRITE_ID, BACKGROUND_SPRITE_ID = 210, 211, 212, 213
TDM_IMAGE_ID, TDM_SHAPE_ID, TDM_TITLE_SPRITE_ID, TDM_BACKGROUND_SPRITE_ID = 59, 60, 61, 153
TITLE_LINKAGE, TDM_TITLE_LINKAGE = b"Mode_Laddermatch", b"Mode_Teamdeathmatch"
INSTANCE, TDM_INSTANCE = b"mcBG_LAD", b"mcBG_TDM"
STAGE_DEPTH = 60
LADDER_MODE = 10
TITLE_TEXT, TDM_TITLE_TEXT = "RANKED MATCH", "TEAM DEATHMATCH"
TEXTURE = (512, 64)                              # the 393 x 39 title stretched to powers of two, as the originals are


# ---------------------------------------------------------------------------------------------- the archive (.bus)
def mersenne(seed):
    n = 624
    s = [0] * n
    s[0] = seed
    for i in range(1, n):
        s[i] = (1812433253 * (s[i - 1] ^ (s[i - 1] >> 30)) + i) & 0xFFFFFFFF
    index = n
    while True:
        if index >= n:
            for k in range(n):
                y = (s[k] & 0x80000000) | (s[(k + 1) % n] & 0x7FFFFFFF)
                s[k] = s[(k + 397) % n] ^ (y >> 1) ^ (0x9908B0DF if y & 1 else 0)
            index = 0
        y = s[index]
        index += 1
        y ^= y >> 11
        y ^= (y << 7) & 0x9D2C5680
        y ^= (y << 15) & 0xEFC60000
        y ^= y >> 18
        yield y


def archive_key():
    generator = mersenne(0x6D2C1B76)
    return bytes(((next(generator) // 16384) % 0x69) & 0xFF for _ in range(0x1000))


KEY = archive_key()
PREFIX, HEADER, ENTRY, NAME = 0x21, 0x110, 0x114, 0x108


def crypt(block):
    return bytes(x ^ KEY[i % 0x1000] for i, x in enumerate(block))


def read_archive(data):
    header = crypt(data[PREFIX:PREFIX + HEADER])
    count = struct.unpack_from("<I", header, NAME + 4)[0]
    base = PREFIX + HEADER + count * ENTRY
    entries = []
    for index in range(count):
        at = PREFIX + HEADER + index * ENTRY
        raw = crypt(data[at:at + ENTRY])
        name = raw[:NAME].split(b"\0")[0].decode("cp949")
        offset, size = struct.unpack_from("<II", raw, NAME)
        entries.append([name, raw, bytes(data[base + offset:base + offset + size])])
    return header, entries


def write_archive(prefix, header, entries):
    header = bytearray(header)
    struct.pack_into("<I", header, NAME + 4, len(entries))
    table, blob, offset = bytearray(), bytearray(), 0
    for name, raw, content in entries:
        raw = bytearray(raw)
        struct.pack_into("<II", raw, NAME, offset, len(content))
        table += crypt(raw)
        blob += content
        offset += len(content)
    return bytes(prefix) + crypt(header) + bytes(table) + bytes(blob)


def new_entry(name):
    raw = bytearray(ENTRY)
    encoded = name.encode("cp949")
    raw[:len(encoded)] = encoded
    return raw


# ------------------------------------------------------------------------------------------------- the title picture
def title_font(size):
    for name, variation in (("bahnschrift.ttf", b"Bold Condensed"), ("arialbd.ttf", None), ("DejaVuSans-Bold.ttf", None)):
        try:
            font = ImageFont.truetype(name, size)
        except OSError:
            continue
        if variation:
            try:
                font.set_variation_by_name(variation)
            except (OSError, AttributeError, ValueError):
                pass
        return font
    return ImageFont.load_default()


def gradient(width, height):
    """Red to blue from left to right, as on the team deathmatch title."""
    stops = [(0.0, (255, 84, 84)), (0.45, (255, 62, 150)), (0.62, (236, 70, 236)), (0.8, (150, 110, 255)), (1.0, (70, 140, 255))]
    image = Image.new("RGB", (width, height))
    pixels = image.load()
    for x in range(width):
        t = x / max(1, width - 1)
        colour = stops[-1][1]
        for (a, first), (b, second) in zip(stops, stops[1:]):
            if a <= t <= b:
                u = (t - a) / (b - a)
                colour = tuple(int(first[i] + (second[i] - first[i]) * u) for i in range(3))
                break
        for y in range(height):
            pixels[x, y] = colour
    return image


def title_picture():
    """
    "RANKED MATCH" in the proportions of the original title: there "TEAM DEATHMATCH" fills the whole picture, so
    these twelve letters, as tall as those, take the share of the width their natural length has - centred.
    """
    width, height = TEXTURE
    scale = 4
    font = title_font(height * scale)
    probe = ImageDraw.Draw(Image.new("L", (8, 8)))

    def glyphs(text):
        box = probe.textbbox((0, 0), text, font=font)
        layer = Image.new("L", (box[2] - box[0], box[3] - box[1]), 0)
        ImageDraw.Draw(layer).text((-box[0], -box[1]), text, font=font, fill=255)
        return layer

    reference, own = glyphs(TDM_TITLE_TEXT), glyphs(TITLE_TEXT)
    own_width = min(width, round(width * own.width / reference.width))
    mask = Image.new("L", (width, height), 0)
    mask.paste(own.resize((own_width, height), Image.LANCZOS), ((width - own_width) // 2, 0))
    picture = gradient(width, height).convert("RGBA")
    picture.putalpha(mask)
    return picture


def to_565(colour):
    return ((colour[0] >> 3) << 11) | ((colour[1] >> 2) << 5) | (colour[2] >> 3)


def from_565(value):
    r, g, b = (value >> 11) & 31, (value >> 5) & 63, value & 31
    return (r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2)


def dxt5(image):
    width, height = image.size
    pixels = image.load()
    out = bytearray()
    for by in range(0, height, 4):
        for bx in range(0, width, 4):
            block = [pixels[bx + x, by + y] for y in range(4) for x in range(4)]

            alphas = [p[3] for p in block]
            a0, a1 = max(alphas), min(alphas)
            if a0 == a1:
                palette = [a0] * 8
            else:
                palette = [a0, a1] + [((7 - i) * a0 + i * a1) // 7 for i in range(1, 7)]
            bits = 0
            for i, alpha in enumerate(alphas):
                index = min(range(8), key=lambda k: abs(palette[k] - alpha))
                bits |= index << (3 * i)
            out += bytes([a0, a1]) + bits.to_bytes(6, "little")

            colours = [p[:3] for p in block]
            c0 = to_565(max(colours, key=sum))
            c1 = to_565(min(colours, key=sum))
            if c0 < c1:
                c0, c1 = c1, c0
            p0, p1 = from_565(c0), from_565(c1)
            table = [p0, p1,
                     tuple((2 * p0[i] + p1[i]) // 3 for i in range(3)),
                     tuple((p0[i] + 2 * p1[i]) // 3 for i in range(3))]
            bits = 0
            for i, colour in enumerate(colours):
                index = min(range(4), key=lambda k: sum((table[k][j] - colour[j]) ** 2 for j in range(3)))
                bits |= index << (2 * i)
            out += struct.pack("<HHI", c0, c1, bits)
    return bytes(out)


def title_dds(original_title):
    """The new picture with the file header of the original title (same size, same format)."""
    header = original_title[:128]
    height, width = struct.unpack_from("<II", header, 12)
    if header[:4] != b"DDS " or header[84:88] != b"DXT5" or (width, height) != TEXTURE or len(original_title) != 128 + width * height:
        raise ValueError("the team deathmatch title is not the 512 x 64 DXT5 picture this tool expects")
    return header + dxt5(title_picture())


# --------------------------------------------------------------------------------------------------- the UI file (.gfx)
def tag(code, body, short=False):
    """A tag with the long header the file's own sprites, shapes, placements and scripts all have."""
    if short and len(body) < 0x3F:
        return struct.pack("<H", (code << 6) | len(body)) + body
    return struct.pack("<HI", (code << 6) | 0x3F, len(body)) + body


def read_tags(data, position, end):
    tags = []
    while position < end:
        head = struct.unpack_from("<H", data, position)[0]
        code, length, head_length = head >> 6, head & 0x3F, 2
        if length == 0x3F:
            length = struct.unpack_from("<I", data, position + 2)[0]
            head_length = 6
        tags.append((position, code, head_length, length))
        position += head_length + length
        if code == 0:
            break
    return tags


def first_tags(data):
    rect_bits = data[8] >> 3
    return 8 + (5 + 4 * rect_bits + 7) // 8 + 4


def find(tags, data, code, starts_with):
    matches = [t for t in tags if t[1] == code and data[t[0] + t[2]:t[0] + t[2] + len(starts_with)] == starts_with]
    if len(matches) != 1:
        raise ValueError("expected exactly one tag %d starting with %s, found %d" % (code, starts_with.hex(), len(matches)))
    position, _, head_length, length = matches[0]
    return data[position + head_length:position + head_length + length]


def u16(value):
    return struct.pack("<H", value)


def replace_once(body, old, new, what):
    if body.count(old) != 1:
        raise ValueError("%s: expected one %s, found %d" % (what, old.hex(), body.count(old)))
    return body.replace(old, new)


def push(*items):
    body = bytearray()
    for item in items:
        if isinstance(item, bool):
            body += bytes([5, 1 if item else 0])
        elif isinstance(item, int):
            body += bytes([7]) + struct.pack("<i", item)
        else:
            body += bytes([0]) + item + b"\0"
    return bytes([0x96]) + u16(len(body)) + bytes(body)


GET_VARIABLE, GET_MEMBER, SET_MEMBER, END = b"\x1c", b"\x4e", b"\x4f", b"\x00"


def patch_gfx(gfx):
    if gfx[:3] != b"GFX" or struct.unpack_from("<I", gfx, 4)[0] != len(gfx):
        raise ValueError("pbloading.gfx is not an uncompressed GFX file of the stated length")
    tags = read_tags(gfx, first_tags(gfx), len(gfx))
    if tags[-1][1] != 0 or tags[-1][0] + tags[-1][2] != len(gfx):
        raise ValueError("pbloading.gfx does not end with an End tag")
    used = set()
    for position, code, head_length, length in tags:
        if code in (2, 22, 32, 37, 39, 1001, 1003) and length >= 2:
            used.add(struct.unpack_from("<H", gfx, position + head_length)[0])
    if used & {IMAGE_ID, SHAPE_ID, TITLE_SPRITE_ID, BACKGROUND_SPRITE_ID}:
        raise ValueError("the character ids 210-213 are already in use")

    # 1. the picture: the team deathmatch one with another id and file name (same 393 x 39)
    image = find(tags, gfx, 1001, u16(TDM_IMAGE_ID))
    size = image[4:8]
    new_image = u16(IMAGE_ID) + image[2:4] + size + b"\0" + bytes([len(NEW_DDS_TAG_NAME)]) + NEW_DDS_TAG_NAME

    # 2. the shape that shows it: id, 8 bytes of bounds, one fill style of type 0x41 with the picture's id
    shape = find(tags, gfx, 2, u16(TDM_SHAPE_ID))
    if shape[10:12] != b"\x01\x41" or shape[12:14] != u16(TDM_IMAGE_ID):
        raise ValueError("the team deathmatch title shape is not the one this tool expects")
    new_shape = u16(SHAPE_ID) + shape[2:12] + u16(IMAGE_ID) + shape[14:]

    # 3. the sprite that is attached by name
    sprite = find(tags, gfx, 39, u16(TDM_TITLE_SPRITE_ID))
    new_sprite = u16(TITLE_SPRITE_ID) + replace_once(sprite[2:], u16(TDM_SHAPE_ID) + b"\0", u16(SHAPE_ID) + b"\0", "title sprite")
    export = u16(1) + u16(TITLE_SPRITE_ID) + TITLE_LINKAGE + b"\0"

    # 4. the background: the team deathmatch one, its title clip under the new name
    background = find(tags, gfx, 39, u16(TDM_BACKGROUND_SPRITE_ID))
    inner = read_tags(background, 4, len(background))
    rebuilt = bytearray(u16(BACKGROUND_SPRITE_ID) + background[2:4])
    renamed = 0
    for position, code, head_length, length in inner:
        body = background[position + head_length:position + head_length + length]
        if code == 26 and body.endswith(TDM_TITLE_LINKAGE + b"\0"):
            body = body[:-len(TDM_TITLE_LINKAGE) - 1] + TITLE_LINKAGE + b"\0"
            renamed += 1
            rebuilt += tag(code, body)
        else:
            rebuilt += background[position:position + head_length + length]
    if renamed != 1 or inner[-1][1] != 0:
        raise ValueError("the team deathmatch background is not the one this tool expects")

    # 5. on the stage where mcBG_TDM is, under its own name and depth
    placement = None
    for position, code, head_length, length in tags:
        body = gfx[position + head_length:position + head_length + length]
        if code == 26 and body.endswith(TDM_INSTANCE + b"\0"):
            placement = body
    if placement is None or placement[3:5] != u16(TDM_BACKGROUND_SPRITE_ID):
        raise ValueError("mcBG_TDM is not placed the way this tool expects")
    depths = {struct.unpack_from("<H", gfx, p + h + 1)[0] for p, c, h, n in tags if c == 26 and n >= 3}
    if STAGE_DEPTH in depths:
        raise ValueError("stage depth %d is taken" % STAGE_DEPTH)
    new_placement = placement[:1] + u16(STAGE_DEPTH) + u16(BACKGROUND_SPRITE_ID) + placement[5:-len(TDM_INSTANCE) - 1] + INSTANCE + b"\0"

    # 6. the script: the table entry of mode 10, and hidden until SetLevelInfo shows it - like the others
    script = (push(b"aBGName") + GET_VARIABLE + push(LADDER_MODE, INSTANCE) + SET_MEMBER
              + push(b"this") + GET_VARIABLE + push(INSTANCE) + GET_MEMBER + push(b"_visible", False) + SET_MEMBER + END)

    added = (tag(1001, new_image, short=True) + tag(2, new_shape) + tag(39, new_sprite) + tag(56, export)
             + tag(39, bytes(rebuilt)) + tag(26, new_placement) + tag(12, script))

    show_frame = next(t for t in tags if t[1] == 1)[0]
    actions = [t[0] for t in tags if t[1] == 12]
    if not actions or actions[0] > show_frame:
        raise ValueError("the first frame has no script in front of its ShowFrame")
    patched = bytearray(gfx[:show_frame] + added + gfx[show_frame:])
    struct.pack_into("<I", patched, 4, len(patched))

    # the result must still read as a chain of tags that ends exactly at the end of the file
    check = read_tags(patched, first_tags(patched), len(patched))
    if check[-1][1] != 0 or check[-1][0] + check[-1][2] != len(patched) or len(check) != len(tags) + 7:
        raise ValueError("the patched file does not parse")
    return bytes(patched)


# ----------------------------------------------------------------------------------------------------------- main
def is_patched(entries):
    names = {entry[0] for entry in entries}
    gfx = next(entry[2] for entry in entries if entry[0] == GFX_NAME)
    return NEW_DDS in names and INSTANCE in gfx


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "patch"
    if mode == "undo":
        if not os.path.exists(BACKUP):
            print("No kept original (%s) - nothing to put back." % os.path.basename(BACKUP))
            return 1
        shutil.copyfile(BACKUP, ARCHIVE)
        print("Put the original %s back." % os.path.basename(ARCHIVE))
        return 0

    with open(ARCHIVE, "rb") as handle:
        data = handle.read()
    header, entries = read_archive(data)
    names = [entry[0] for entry in entries]
    if GFX_NAME not in names or TDM_TITLE_DDS not in names:
        print("This archive does not hold the loading screen - nothing written.")
        return 1
    if is_patched(entries):
        print("%s: already has the ranked loading screen." % os.path.basename(ARCHIVE))
        return 0
    if mode == "show":
        print("%s: original (no ranked loading screen)." % os.path.basename(ARCHIVE))
        return 0
    if write_archive(data[:PREFIX], header, entries) != data:
        print("This tool does not reproduce the archive byte for byte - nothing written.")
        return 1

    try:
        for entry in entries:
            if entry[0] == GFX_NAME:
                entry[2] = patch_gfx(entry[2])
        dds = title_dds(next(entry[2] for entry in entries if entry[0] == TDM_TITLE_DDS))
    except ValueError as problem:
        print("Not the loading screen this tool was made for (%s) - nothing written." % problem)
        return 1
    entries.append([NEW_DDS, new_entry(NEW_DDS), dds])
    entries.sort(key=lambda entry: entry[0])
    if [entry[0] for entry in entries if entry[0] != NEW_DDS] != names:
        print("The archive's entries are not in name order - nothing written.")
        return 1

    if not os.path.exists(BACKUP):
        shutil.copyfile(ARCHIVE, BACKUP)
    try:
        with open(ARCHIVE, "wb") as handle:
            handle.write(write_archive(data[:PREFIX], header, entries))
    except PermissionError:
        print("Could not write %s - is the game running? Close it and run the tool again." % os.path.basename(ARCHIVE))
        return 1
    print("%s: ranked loading screen added (%d entries; the original is kept as %s)."
          % (os.path.basename(ARCHIVE), len(entries), os.path.basename(BACKUP)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
