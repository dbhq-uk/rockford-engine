#!/usr/bin/env python3
"""Build the browser front end's assets from the original game files.

Writes into web/Rockford.Web/wwwroot/game/. Those files are committed as of 18
September 2026 - they were git-ignored before, and the reasoning for the change
is in .github/workflows/guard.yml. Run this whenever the extracted data or the
tables this reads change; the build does not run it.

    python3 tools/webassets.py

Three kinds of asset come out:

  <world>.png   the world's 16 by 16 sprite sheet, 20 tiles across, with the
                palette that ships inside the .SCM applied and colour index 0
                made transparent
  cellmaps.bin  the 40 cave records, copied as-is
  caves.json    the four per-cave tables and the per-world amoeba cap, read out
                of the unpacked executable

The .SCM palette is not where a first reading puts it. The RLE stream ends after
exactly 64,000 pixels and the palette follows it in the RAW file, not in the
decoded output - decoding past the pixels turns the palette into nonsense. It is
32 entries of three little-endian words with values 0 to 15, but it does not start
at a fixed offset: some files have three bytes of slack before it and some have
one. So it is found by looking for the first window where all 96 words are in
range rather than assumed, which is what lets COOK, MENU_I and TITLEANI decode at
all - a fixed offset silently rejected them.
"""
import json
import os
import struct
import sys

from PIL import Image

from scmformat import ScmError, decode
from cartoons import strip as cartoon_strip_pixels

WIDTH, HEIGHT = 320, 200
PALETTE_ENTRIES = 32

REQUIRED_TREASURE = 0x245E
SCORE_BEFORE_QUOTA = 0x24AE
SCORE_AFTER_QUOTA = 0x24FE
TIME_LIMIT = 0x26A2
AMOEBA_CAP = 0x2274

# The music is a tracker, not a melody. The note table is found by shape so the EGA
# build works too, and everything else is a fixed distance from it.
NOTE_COUNT = 36
TEMPO_FROM_TABLE = -8         # the tempo word sits just before the note table
PATTERNS_FROM_TABLE = 0x90    # 22 patterns of 8 two-byte events
ORDER_FROM_TABLE = 0x2B4      # the PC speaker order list
PATTERN_STEPS = 8
PATTERN_COUNT = 22
ORDER_LENGTH = 48

# The player multiplies every divisor by four for the PC speaker, at 0x14932. Miss
# that and the tune comes out between 1695 Hz and 12693 Hz, which is a whistle
# rather than music.
SPEAKER_SCALE = 4

# One sub-step is tempo + 1 ticks of a 60 Hz timer.
TICK_HZ = 1193180 / 0x4DAE

# THE WORLD LIST COMES OUT OF THE EXECUTABLE, all ten of it. It used to be five
# names typed here, which was the 1988 menu's page and nothing else.
#
# The menu map at ds:0x26F2 is ten words, 0, 2, 4, 6, 8, 1, 3, 5, 7, 9: menu
# position to RAW world number. The 1988 select screen shows the first five; the
# second five are the worlds its menu cannot reach, and they are the select
# screen's second page here (#44). A cave record is raw_world * 4 + stage, so the
# first page plays records 0-3, 8-11, 16-19, 24-27 and 32-35, not the first twenty.
#
# The names are the tileset filename table at ds:0x267A - ten pointers to
# HUNTER.SCM, SCUBA.SCM and the rest - indexed by the raw world. The executable
# names the sheet for every world, including the five it never loads.
MENU_WORLDS = 0x26F2
WORLD_FILES = 0x267A
WORLD_COUNT = 10

# Scuba and player shipped in the November 1987 release and were dropped from the
# 1988 menu, so that release's EGA .FIL files are their original sheets. Music,
# luck and miner were never drawn in any release. The build draws all five with
# Aleksi Sahala's art (see SAHALA below); the 1987 sheets are what scuba and
# player fall back to without it.
#
# NO COORDINATE REMAP IS NEEDED, and that was measured rather than assumed. The
# VGA executable nudges some bands four pixels right (see sprite_xy), which looks
# as if EGA art should be moved to match. It should not: the EGA art already sits
# at the nudged positions. Taking a 1988 EGA .FIL exactly as it is, cowboy's
# silhouettes match its VGA sheet at all 141 positions sprites.json names, and
# hunter's and cook's match at every nudged one. The positions that differ are
# tiles the VGA artists redrew, not tiles that moved. 25-27 September 2026.
RELEASE_1987 = 'extracted/ega1987-2'
EGA_EXE = 'unpacked/ega.bin'

# ALL FIVE HIDDEN WORLDS ARE ALEKSI SAHALA'S DRAWINGS, from asahala/Rockford-Patcher
# at dca39a2 (28 April 2026), used with credit. His repository carries no licence,
# so this is not a licence either; Dan decided on 29 September 2026 to use them
# and to tell him at the same time, offering to take them down. The terms and the
# takedown are in third_party/rockford-patcher/README.md.
#
# Scuba and player included, over the 1987 originals (Dan, 29 September 2026), so
# the second page is one artist's set: his tiles in play and his five portraits,
# drawn together for exactly this select screen. The 1987 sheets were First
# Star's and are the real thing; his are reconstructions. The site says so.
#
# His patcher plays the hidden caves in the shipped worlds' slots, so each of his
# sheets is named after the shipped world it stands in for, and he overwrites
# that world's palette block with his own. The palettes below are his, copied
# from PALETTES in rockford_patcher.py - not the executable's blocks for music,
# luck and miner, because his indices were drawn against his palettes and the
# executable's would recolour them. His menu.ega is the version 2.0 select screen
# (April 2026) with the gambler; MENU.ADD beside it is the 2023 one with a
# leprechaun, and is not used. Portraits come from menu.ega's boxes, which sit at
# x = 8 + 64 * box, y = 48, 48 pixels with the white ring MENU_I has.
#
# Remove the folder and all five fall back: scuba and player to the 1987 sheets,
# and music, luck and miner stop being offered, their sheets deleted.
SAHALA = 'third_party/rockford-patcher'
SAHALA_MENU = 'menu.ega'
SAHALA_WORLDS = {
    #          his sheet     his palette                         box on his menu
    'scuba':  ('HUNTER.EGA', '01010203040506071011001314151617', 0),
    'player': ('COOK.EGA',   '02010203040506071011121314001617', 1),
    'music':  ('COWBOY.EGA', '01010203040506071011001314151617', 2),
    'luck':   ('SPACE.EGA',  '00010203040506071000121314151617', 3),
    'miner':  ('BODY.EGA',   '00010203040506071011121314151617', 4),
}
MENU_BOX_X, MENU_BOX_STEP, MENU_BOX_Y = 8, 64, 48

# A portrait for the select screen's second page, when the 1987 sheets are in use
# (that is, without Sahala's art). 1987 had no select screen - it
# typed the world number - so no portrait was ever drawn for scuba or player. But
# its between-level cartoons sit on an exact 48 pixel grid, six frames across and
# four rows down (new, win, lose, time), which is the size of a portrait box on
# MENU_I. So the portrait is the first "new" frame of the world's own .CAR: the
# 1987 artists' drawing of that character, unaltered, at the size the box wants.
PORTRAIT = 48

# Sprite lookup tables. An entry is an EGA planar byte offset that the startup code
# at 0x11395 rewrites for the current video mode; we do the VGA conversion here.
SPRITE_MAIN = 0x22E4        # 128 words, values 0x00-0x7F
SPRITE_PLAYER = 0x2288      # 20 words
SPRITE_ENEMY_A = 0x23E4     # 8 words, runtime 0x8C
SPRITE_ENEMY_B = 0x23F4     # 4 words, runtime 0x90
SPRITE_GROUP_A = 0x23FC     # 24 words, runtime 0xA0-0xA7
SPRITE_GROUP_B = 0x242C     # 24 words, runtime 0xB0-0xB7

# The palette words are indices into this 16-entry ramp, and the result is a 6-bit
# DAC value. n * 17 is close but not the hardware number.
DAC_RAMP = 0x2176


# ---- the title animation, from 0xF29C ------------------------------------
#
# The title screen is not a still. Rockford blinks, taps a foot and tosses a
# diamond up and catches it, and the frames live in TITLEANI.SCM, which is loaded
# to the page after the title picture and blitted from there.
#
# Four layers, each its own routine and each with a table of source offsets. The
# offsets are plain planar addresses into the 320 by 200 page - 40 bytes to a row,
# eight pixels to a byte - so unlike the sprite sheets there is no VGA nudge to
# undo. Compositing frame zero over the title picture lands every rectangle
# exactly, which is the check that says so.
#
#   layer     routine   counter    table   destination  size
#   arm       0xEF4F    2E22 (<=4) 2E6C    0x0F62       8 by 39 bytes
#   diamond   0xF014    2E22       2E3C    0x08D2       3 by 42
#   face      0xF0C9    2E22       2E50    0x0A66       6 by 23
#   foot      0xF1BE    2E20       2E64    0x1874       4 by 24
#
# The face has a second table at 2E32 - a different column of expressions - which
# 0xF0F0 picks when ds:0x2E28 is set, and 0xF2FF sets that one toss in four.
TITLE_LAYERS = [
    ('arm',     0x2E6C, 5, 0x0F62,  8, 39),
    ('diamond', 0x2E3C, 9, 0x08D2,  3, 42),
    ('face',    0x2E50, 9, 0x0A66,  6, 23),
    ('faceAlt', 0x2E32, 5, 0x0A66,  6, 23),
    ('foot',    0x2E64, 4, 0x1874,  4, 24),
]

PLANE_STRIDE = 40
PIXELS_PER_BYTE = 8


def planar_xy(offset):
    """A planar byte offset in the 320 by 200 page to a pixel position."""
    y, b = divmod(offset, PLANE_STRIDE)
    return [b * PIXELS_PER_BYTE, y]


def title_animation(image):
    """The four layers' frame rectangles and where each one lands."""
    layers = {}
    for name, table, count, dest, wide, high in TITLE_LAYERS:
        frames = [planar_xy(struct.unpack_from('<H', image, table + 2 * i)[0])
                  for i in range(count)]
        layers[name] = {
            'at': planar_xy(dest),
            'w': wide * PIXELS_PER_BYTE,
            'h': high,
            'frames': frames,
        }
    return layers


def palette(tail, ramp):
    """Find the 32 three-word entries. Returns None when there is no such window.

    Each word is an index into the game's own 16-entry ramp, and the result is a
    6-bit DAC value, so the conversion is ramp[n] * 255 / 63 rather than n * 17.
    """
    words = PALETTE_ENTRIES * 3
    for off in range(0, len(tail) - words * 2 + 1):
        w = struct.unpack_from(f'<{words}H', tail, off)
        if all(v <= 15 for v in w):
            def c(n):
                return round(ramp[n] * 255 / 63)
            return [(c(w[i]), c(w[i + 1]), c(w[i + 2])) for i in range(0, words, 3)]
    return None


def sprite_xy(entry):
    """An EGA planar byte offset to a pixel position on the VGA sheet.

    The startup converter at 0x131B3 multiplies by eight and then nudges some
    bands four pixels right. The bands are not on a 16-pixel grid either - they are
    at y = 4, 24, 28, 48 and then every 16 - so this returns a position, not a tile
    number.

    This used to say the nudge exists "because the VGA art in those bands does not
    sit where the EGA art does". Measured, it does: an EGA .FIL taken as it is
    matches the VGA sheet at the nudged positions, so both files carry the art at
    b * 8 + 4 there and it is the planar offset that is short of it, not the EGA
    art. How the EGA build blits a tile that starts half a byte in is not settled
    here. What is settled is that the same coordinates serve both kinds of sheet.
    """
    y, b = divmod(entry, 40)
    add_four = y <= 23 or (y == 28 and b >= 1)
    return [(b // 2) * 16 + (4 if add_four else 0), y]


def sprites(image):
    def table(off, count):
        return [sprite_xy(struct.unpack_from('<H', image, off + 2 * i)[0])
                for i in range(count)]

    return {
        'main': table(SPRITE_MAIN, 128),
        'player': table(SPRITE_PLAYER, 20),
        'enemyA': table(SPRITE_ENEMY_A, 8),
        'enemyB': table(SPRITE_ENEMY_B, 4),
        'groupA': table(SPRITE_GROUP_A, 24),
        'groupB': table(SPRITE_GROUP_B, 24),
        # 0x11 is a real value but the renderer skips it at 0x10E91, so the player
        # death blast is invisible for exactly one frame.
        'never': [0x11],
    }


def floor_colour(path, ramp):
    """Palette index 0, which is the cave floor.

    This is the colour the game shows where a cave has been dug out. The sheets
    make index 0 transparent so sprites composite cleanly, which means the front
    end has to paint it back or every empty cell comes out black - which is
    exactly what happened.
    """
    data = open(path, 'rb').read()
    try:
        _, pos = decode(data)
    except ScmError:
        return None
    pal = palette(data[pos:], ramp)
    if pal is None:
        return None
    r, g, b = pal[0]
    return f'#{r:02x}{g:02x}{b:02x}'


def picture(path, out_path, ramp, transparent_index=None):
    """Decode one .SCM or .CCM to a PNG.

    A sprite sheet wants colour index 0 punched out, because that is the sheet's
    own backdrop rather than part of any sprite. A full-screen picture - the title
    and the world menu - wants every pixel kept.
    """
    data = open(path, 'rb').read()

    # The decoder is strict now, so a file that is not a whole screen says so
    # rather than handing back a short buffer. Both callers here answer "no"
    # and let main() report the skip, which is what they did before.
    try:
        px, pos = decode(data)
    except ScmError:
        return False

    pal = palette(data[pos:], ramp)
    if pal is None:
        return False

    img = Image.new('RGBA', (WIDTH, HEIGHT))
    p = img.load()
    for y in range(HEIGHT):
        for x in range(WIDTH):
            v = px[y * WIDTH + x]
            r, g, b = pal[v]
            p[x, y] = (r, g, b, 0 if v == transparent_index else 255)
    img.save(out_path)
    return True


def world_names(image):
    """The ten tileset stems, by raw world, from the pointer table at ds:0x267A."""
    names = []
    for i in range(WORLD_COUNT):
        at = struct.unpack_from('<H', image, WORLD_FILES + 2 * i)[0]
        stem = image[at:image.index(b'\0', at)].decode('ascii')
        names.append(os.path.splitext(stem)[0].lower())
    return names


def ega_sheet(fil, world, target, colours=None):
    """A 1987 .FIL as a sheet the front end can load beside the VGA ones.

    The same as the VGA sheets in every way the renderer can see: 320 by 200, the
    art at the positions sprites.json names, and colour index 0 punched out so the
    floor shows through. Sixteen colours rather than thirty-two, because that is
    what this art was drawn in and there is no VGA copy of it to prefer.

    `colours` overrides the world's own palette, for a sheet drawn against
    another one - Sahala's are.
    """
    from ega import planar
    import egapal

    px = planar(open(fil, 'rb').read())
    colours = colours or egapal.rgb(world, EGA_EXE)
    img = Image.new('RGBA', (WIDTH, HEIGHT))
    p = img.load()
    for y in range(HEIGHT):
        for x in range(WIDTH):
            v = px[y * WIDTH + x]
            r, g, b = colours[v]
            p[x, y] = (r, g, b, 0 if v == 0 else 255)
    img.save(target)


def cartoon_portrait(car, world, target):
    """The first 'new' frame of a world's cartoon, as its select-screen portrait."""
    from ega import planar
    import egapal

    px = planar(open(car, 'rb').read())
    colours = egapal.rgb(world, EGA_EXE)
    img = Image.new('RGB', (PORTRAIT, PORTRAIT))
    for y in range(PORTRAIT):
        for x in range(PORTRAIT):
            img.putpixel((x, y), colours[px[y * WIDTH + x]])
    img.save(target)


def sahala_colours(world):
    """His palette for one of his worlds, as RGB."""
    import egapal
    return [egapal.irgb(b) for b in bytes.fromhex(SAHALA_WORLDS[world][1])]


def menu_portrait(menu, box, target):
    """One 48 pixel box off a select screen, white ring and all.

    The front end crops the outermost pixel when it paints a portrait into
    MENU_I's frame, so the ring here is what gets cropped and his drawing is what
    is left.
    """
    from ega import planar
    import egapal

    px = planar(open(menu, 'rb').read())
    colours = [egapal.irgb(b) for b in egapal.palettes(EGA_EXE)['default']]
    x0 = MENU_BOX_X + MENU_BOX_STEP * box
    img = Image.new('RGB', (PORTRAIT, PORTRAIT))
    for y in range(PORTRAIT):
        for x in range(PORTRAIT):
            img.putpixel((x, y), colours[px[(MENU_BOX_Y + y) * WIDTH + x0 + x]])
    img.save(target)


def cartoon_sheet(ccm, scm, out_path, ramp):
    """A shipped world's WIN, LOSE and TIME cartoons, from its .CCM and its .SCM.

    A .CCM carries no palette and uses its world's .SCM. See tools/cartoons.py for
    what is cut from the screen and why.
    """
    scm_data = open(scm, 'rb').read()
    try:
        _, pos = decode(scm_data)
        pixels, _ = decode(open(ccm, 'rb').read())
    except ScmError:
        return False
    pal = palette(scm_data[pos:], ramp)
    if pal is None:
        return False
    cartoon_strip_pixels(pixels, pal).save(out_path)
    return True


def worlds(image, src, out, ramp):
    """worlds.json: all ten, in menu order, each saying which build can draw it.

    Both builds read this one file, and they cannot draw the same worlds - VIP's
    atlases are generated separately and exist only for the worlds that have
    them. So an entry says so, per build, and each build offers what it can draw
    rather than finding out by failing to load a sheet.
    """
    names = world_names(image)
    menu = [struct.unpack_from('<H', image, MENU_WORLDS + 2 * i)[0]
            for i in range(WORLD_COUNT)]

    os.makedirs(os.path.join(out, 'portraits'), exist_ok=True)
    entries = []
    for raw in menu:
        name = names[raw]
        scm = os.path.join(src, f'{name.upper()}.SCM')
        fil = os.path.join(RELEASE_1987, f'{name.upper()}.FIL')
        car = os.path.join(RELEASE_1987, f'{name.upper()}.CAR')
        portrait = None
        cartoon = None

        if os.path.exists(scm):
            # A world this release shipped: its sheet was written with the other
            # pictures above, and its portrait is already drawn into MENU_I.
            floor = floor_colour(scm, ramp)
            original = os.path.exists(os.path.join(out, f'{name}.png'))
            ccm = os.path.join(src, f'{name.upper()}.CCM')
            if os.path.exists(ccm) and cartoon_sheet(
                    ccm, scm, os.path.join(out, f'{name}-cartoon.png'), ramp):
                cartoon = f'{name}-cartoon.png'
        elif name in SAHALA_WORLDS and os.path.isdir(SAHALA):
            sheet, _, box = SAHALA_WORLDS[name]
            colours = sahala_colours(name)
            ega_sheet(os.path.join(SAHALA, sheet), name, os.path.join(out, f'{name}.png'), colours)
            portrait = f'portraits/{name}.png'
            menu_portrait(os.path.join(SAHALA, SAHALA_MENU), box, os.path.join(out, portrait))
            original = True
            # Ahead of the 1987 sheets on purpose: scuba and player are his too.
            # His floor, not the executable's: it is the colour his index 0 was
            # drawn to sit on.
            floor = '#%02x%02x%02x' % colours[0]
        elif os.path.exists(fil):
            ega_sheet(fil, name, os.path.join(out, f'{name}.png'))
            if os.path.exists(car):
                portrait = f'portraits/{name}.png'
                cartoon_portrait(car, name, os.path.join(out, portrait))
            original = True
            floor = None
        else:
            # Nothing draws this world: no release, and Sahala's art is not
            # here. So it is not offered, and a sheet or portrait left from a
            # run when his art WAS here is deleted - otherwise taking his art
            # down would leave it in the game directory, and in the next deploy.
            for stale in (f'{name}.png', os.path.join('portraits', f'{name}.png')):
                if os.path.exists(os.path.join(out, stale)):
                    os.remove(os.path.join(out, stale))
            original = False
            floor = None

        if floor is None:
            # Every world has a palette in the executable, drawn or not, and
            # index 0 of it is the floor that world would have shown.
            import egapal
            r, g, b = egapal.rgb(name, EGA_EXE)[0]
            floor = f'#{r:02x}{g:02x}{b:02x}'

        entries.append({
            'sheet': name,
            'world': raw,
            'floor': floor,
            'original': original,
            'vip': os.path.exists(os.path.join(out, f'{name}-x8.png')),
            'portrait': portrait,
            'cartoon': cartoon,
        })
    return entries


def main():
    from usage import wants_help, help_and_exit
    if wants_help():
        help_and_exit()

    src = sys.argv[1] if len(sys.argv) > 1 else 'extracted/vga'
    exe = sys.argv[2] if len(sys.argv) > 2 else 'unpacked/vga.bin'
    out = sys.argv[3] if len(sys.argv) > 3 else 'web/Rockford.Web/wwwroot/game'
    os.makedirs(out, exist_ok=True)

    ramp = list(open(exe, 'rb').read()[DAC_RAMP:DAC_RAMP + 16])

    # Full-screen pictures keep every pixel; everything else is a sprite sheet and
    # loses colour index 0.
    pictures = {'TITLE', 'TITLEANI', 'MENU_I'}

    written = []
    for name in sorted(os.listdir(src)):
        if not name.upper().endswith('.SCM'):
            continue
        stem = os.path.splitext(name)[0]
        clear = None if stem.upper() in pictures else 0
        target = os.path.join(out, f'{stem.lower()}.png')
        if picture(os.path.join(src, name), target, ramp, clear):
            written.append(stem.lower())
        else:
            print(f'  skipped {name}, could not find a palette')
    print(f'images: {", ".join(written)}')

    image = open(exe, 'rb').read()
    entries = worlds(image, src, out, ramp)
    json.dump(entries, open(os.path.join(out, 'worlds.json'), 'w'))
    for e in entries:
        builds = [b for b in ('original', 'vip') if e[b]] or ['no art']
        print(f"world {e['world']} {e['sheet']:7} {e['floor']}  {', '.join(builds)}")

    json.dump(sprites(image), open(os.path.join(out, 'sprites.json'), 'w'))
    print('sprites.json written')

    json.dump(title_animation(image), open(os.path.join(out, 'titleani.json'), 'w'))
    print('titleani.json written')

    caves = os.path.join(src, 'CELLMAPS.BIN')
    open(os.path.join(out, 'cellmaps.bin'), 'wb').write(open(caves, 'rb').read())

    def table(off, n=40):
        return [struct.unpack_from('<H', image, off + 2 * i)[0] for i in range(n)]

    meta = {
        'required': table(REQUIRED_TREASURE),
        'time': table(TIME_LIMIT),
        'scoreBefore': table(SCORE_BEFORE_QUOTA),
        'scoreAfter': table(SCORE_AFTER_QUOTA),
        'amoebaCap': table(AMOEBA_CAP, 10),
    }
    json.dump(meta, open(os.path.join(out, 'caves.json'), 'w'))
    print(f"cave 1 needs {meta['required'][0]} treasure in {meta['time'][0]} seconds")

    json.dump(music(image), open(os.path.join(out, 'music.json'), 'w'))
    print('music.json written')


def find_note_table(image):
    """36 divisors falling a semitone at a time, found by shape not by offset."""
    for off in range(0, len(image) - 2 * NOTE_COUNT, 2):
        w = struct.unpack_from(f'<{NOTE_COUNT}H', image, off)
        if any(x < 64 for x in w):
            continue
        if not all(w[i] > w[i + 1] for i in range(NOTE_COUNT - 1)):
            continue
        ratios = [w[i] / w[i + 1] for i in range(NOTE_COUNT - 1)]
        if not all(abs(r - 2 ** (1 / 12)) < 0.02 for r in ratios):
            continue
        span = (w[0] / w[-1]) / 2 ** ((NOTE_COUNT - 1) / 12)
        if abs(span - 1) < 0.02:
            return off, list(w)
    raise LookupError('no note table in this image')


def music(image):
    """The title theme, as the player actually reads it.

    It is a tracker. A bank of 22 patterns of eight events, and an order list that
    says which pattern to play next. Read the bank straight through and you get
    gibberish; read it through the order list and you get a 64-second theme.

    The decisive instruction is at 0x149B5: the value fetched from the order list
    is shifted left three times and used to index the pattern bank, so it is a
    pattern number, not a note.
    """
    tbl_off, table = find_note_table(image)
    tempo = struct.unpack_from('<H', image, tbl_off + TEMPO_FROM_TABLE)[0]

    bank = tbl_off + PATTERNS_FROM_TABLE
    patterns = []
    for pat in range(PATTERN_COUNT):
        base = bank + pat * PATTERN_STEPS * 2
        patterns.append([[image[base + 2 * i], image[base + 2 * i + 1]]
                         for i in range(PATTERN_STEPS)])

    order_off = tbl_off + ORDER_FROM_TABLE
    order = [struct.unpack_from('<H', image, order_off + 2 * i)[0]
             for i in range(ORDER_LENGTH)]

    if any(v >= PATTERN_COUNT for v in order):
        raise ValueError('order list holds something that is not a pattern number')

    return {
        # Already scaled, so the consumer does not have to know about 0x14932.
        'noteTable': [v * SPEAKER_SCALE for v in table],
        'patterns': patterns,
        'order': order,
        'substepSeconds': (tempo + 1) / TICK_HZ,
    }


if __name__ == '__main__':
    main()
