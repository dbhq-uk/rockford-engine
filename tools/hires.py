#!/usr/bin/env python3
"""Redraw every sprite at four times the resolution, and build a new atlas.

The game's art is 16x16. Scaled up to fill a modern screen it is honest but
chunky, so this makes a parallel set at 64x64 and an atlas laid out exactly like
the original sheet, four times bigger. Nothing else has to change: the sprite
coordinate tables in sprites.json are multiplied by four and the front end draws
64 pixel tiles instead of 16.

    python3 tools/hires.py plan          # what needs doing, and what it will cost
    python3 tools/hires.py extract       # write the 16x16 sources as references
    python3 tools/hires.py generate      # redraw the ones not done yet
    python3 tools/hires.py atlas         # assemble the 4x sheets

`generate` is resumable. It skips anything already rendered, so it can be stopped
and restarted, and a failed sprite can be deleted and redone on its own.

Every sprite is generated from the original as a reference image, with the prompt
insisting on the same silhouette and palette. That matters more than it sounds:
these are tiles that butt up against each other, so a sprite that outgrows its
cell or drifts in colour looks wrong in a way a standalone illustration would not.
"""
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from usage import help_and_exit, wants_help  # noqa: E402

# Before anything is read: without the game data (the public repository has none)
# this crashed on `--help` instead of describing itself.
if wants_help():
    help_and_exit(sys.modules[__name__])

sys.path.insert(0, str(Path(__file__).resolve().parent))
import worlds  # noqa: E402
import bonusring  # noqa: E402
import worldlist  # noqa: E402
import dissolve  # noqa: E402
from tilecmp import differing  # noqa: E402
import tapturn  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
GAME = ROOT / 'web/Rockford.Web/wwwroot/game'
WORK = ROOT / 'build/hires'

# 8 gives 128-pixel tiles. The sources are drawn at 1024 square, so anything less
# is throwing away art that has already been paid for, and 64-pixel tiles get
# upscaled on any reasonably sized window: a 20-column viewport across 1600
# pixels wants 80 a tile before a high-DPI display doubles it. At 128 there is
# headroom on both.
SCALE = 8
TILE = 16

# gpt-image-2 rather than the newer default, chosen deliberately: it is the only
# one eligible for the Batch API's half-price tier if this ever needs rerunning at
# scale, and a set has to stay on one model or the look drifts across it.
# The image model. gpt-image-2 drew the 1988 five, with a transparent background; on
# 3 October 2026 the API began refusing that request for it ("Transparent background
# is not supported for this model"), so the hidden worlds are drawn with a 2.5 model,
# which accepts it. ROCKFORD_MODEL overrides, and a world is drawn start to finish
# with one model, because two models in one animation do not agree with each other.
MODEL = os.environ.get('ROCKFORD_MODEL', 'gpt-image-2')

# Seconds between requests. The API rate limits a long run otherwise.
PACE = 4

# Failures that will still be failures in five minutes. These arrive as a 429
# exactly like a rate limit does, so they have to be recognised by message.
PERMANENT = (
    'credit_balance_exhausted',
    'insufficient_quota',
    'no credits remaining',
    'billing_hard_limit_reached',
    'invalid_api_key',
    'incorrect api key',
)

# Failures that are permanent for this one sprite but say nothing about the next.
# A moderation refusal retried is the same refusal, three times slower, so the
# sprite is given up on and the run carries on to the rest.
REFUSED = (
    'moderation',
    'safety system',
)

# The image CLI these were drawn with. IMAGER_DIR points at another install.
IMAGER_DIR = Path(os.environ.get('IMAGER_DIR', Path.home() / '.claude/skills/imager'))
IMAGER = IMAGER_DIR / 'scripts/imager.py'
IMAGER_PY = IMAGER_DIR / '.venv/bin/python'

# The worlds this run works on: those with a VIP atlas, or whatever
# ROCKFORD_WORLDS names. From worlds.json, not typed here - see worldlist.py (#107).
WORLDS = worldlist.working()

# Authored byte to runtime value, mirroring CaveLoader.
AUTHORED = {
    0x00: 0x00, 0x01: 0x01, 0x02: 0x02, 0x03: 0x03, 0x04: 0x74, 0x08: 0x15,
    0x0C: 0x04, 0x10: 0x48, 0x20: 0x10, 0x28: 0x44, 0x2C: 0x8C, 0x2D: 0x8C,
    0x2E: 0x8C, 0x30: 0x8C, 0x34: 0x90, 0x35: 0x90, 0x36: 0x90, 0x37: 0x90,
    0x38: 0x0C, 0x70: 0x78, 0x74: 0x07, 0x78: 0x6C, 0x7C: 0x70, 0x84: 0x7C,
    0x88: 0x40, 0xC4: 0x08,
}

# States the rules produce that never appear in a cave file.
#
# This list is hand-maintained and it has been wrong once already: the taps, both
# projectile chains and the collectible animations were all missing, so VIP drew
# nothing at all where the faithful build drew fire, acid and a spinning prize.
# cmd_atlas used to fall back to the original for anything not named here. It
# does not any more - that fallback WAS the leak Astra found, fourteen rectangles
# per atlas of the original enlarged - so an omission now costs a hole, or a
# SUBSTITUTE if one is declared, and a tile with neither stops the build.
PRODUCED = {
    0x41, 0x42, 0x43, 0x45, 0x46, 0x47, 0x16, 0x49, 0x4A, 0x4B,
    0x75, 0x76, 0x77, 0x30, 0x31, 0x32, 0x33, 0x8D, 0x91,
    0x11, 0x12, 0x13, 0x14, 0x1B, 0x1C, 0x1D, 0x1E,

    # The two taps, once armed, advance and wrap through their own animation.
    # Simulation.Projectiles.cs. The armed states, not the authored seeds.
    0x09, 0x0A, 0x0B,          # acid tap, seeded 0x08
    0x0D, 0x0E, 0x0F,          # fire tap, seeded 0x0C

    # Acid: emitted at 0x1F and advanced by the phase dispatch to 0x23. 0x1F is
    # deliberately absent from the redraw list - its crop is blank in the
    # original, so there is nothing to redraw and a generated sprite would be an
    # invention.
    0x20, 0x21, 0x22, 0x23,

    # Acid that could not fall: 0x24, then 0x25, then settles at 0x28 and the
    # phase dispatch carries it to 0x2B.
    0x24, 0x25, 0x28, 0x29, 0x2A, 0x2B,

    # Fire: emitted at 0x2C and advanced to 0x2F.
    0x2C, 0x2D, 0x2E, 0x2F,

    # The two collectibles, authored as 0x6C and 0x70, cycling through their own
    # animations.
    0x6D, 0x6E, 0x6F,

    # The worms, and everything they make. Added 18 September 2026, when the two
    # movement routines landed and the scan started dispatching these - before
    # that nothing reached them, so nobody drew them and the atlas simply kept
    # the original's pixels underneath. That is the leak the transparent canvas
    # closed, and this is what stops it becoming a hole instead.
    #
    #   79-7B, 7D-7F   the two eggs, frames 1 to 3 of their countdown
    #   34-37          on its way to a boulder      0x37 is the terminal
    #   38-3B          on its way to an enemy       0x3B
    #   3C-3F          on its way to a boulder      0x3F
    #   17-1A          on its way to treasure       0x1A
    #
    # 0x37, 0x3B, 0x3F and 0x1A are the terminals the dispatch replaces, so they
    # are drawn for exactly one scan each and are as reachable as the rest.
    # THE WORMS THEMSELVES, which this list named everything ABOUT and not. The
    # heading above says "the worms, and everything they make"; it held what
    # they make - the eggs, the transformation chains - and neither worm. So
    # wanted() never covered them, no art was ever commissioned, and all 40
    # positions were blank in all five atlases while the faithful build drew
    # them. #73.
    #
    # TileSet.For dispatches 0xA0-0xA7 to GroupA and 0xB0-0xB7 to GroupB, and
    # Simulation.cs writes 0xA0 and 0xB0 into the grid when the eggs hatch.
    0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
    0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7,

    0x79, 0x7A, 0x7B, 0x7D, 0x7E, 0x7F,
    0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B,
    0x3C, 0x3D, 0x3E, 0x3F, 0x17, 0x18, 0x19, 0x1A,
    0x71, 0x72, 0x73,

    # Deliberately NOT here: 0xF0, 0xF1 and 0xF2, the deferred acid and fire
    # movement seeds. They are resolved during the same scan that creates them
    # and the renderer has no sprite for them, so there is nothing to draw.
}

# What each sprite is, named by looking at the decoded sheet rather than guessed
# from the runtime value. This table is the whole ballgame: an earlier run left
# half of these unnamed, and the model filled the gaps by inventing - a bird became
# a brick wall, a blank tile became a lit bomb. A guess rendered beautifully is
# still a guess.
#
# Keyed by (runtime value, x, y) where a value has several frames, and by value
# alone where it has one. The hunter world is the reference; the same positions
# hold the equivalent art in the other four.
BOY = ('a cheerful cartoon boy with a pink face, a red peaked cap, a red and white '
       'striped shirt, white trousers and red shoes')

POSITIONS = {
    (0x10, 0, 48): f'{BOY}, walking to the left, seen from the side',
    (0x10, 16, 48): f'{BOY}, mid-stride walking to the left, seen from the side',
    (0x10, 32, 48): f'{BOY}, seen from behind, walking away from the viewer',
    (0x10, 48, 48): f'{BOY}, seen from behind, mid-stride walking away',
    (0x10, 64, 48): f'{BOY}, facing the viewer, standing',
    (0x10, 80, 48): f'{BOY}, facing the viewer, mid-stride walking towards the viewer',
    (0x10, 96, 48): f'{BOY}, walking to the right, seen from the side',
    (0x10, 112, 48): f'{BOY}, mid-stride walking to the right, seen from the side',
    (0x10, 128, 48): f'{BOY}, reaching out to the right with one arm extended',
    (0x10, 144, 48): f'{BOY}, arm extended to the right, pushing something',
    (0x10, 160, 48): f'{BOY}, reaching out to the left with one arm extended',
    (0x10, 176, 48): f'{BOY}, arm extended to the left, pushing something',
    (0x10, 192, 48): f'{BOY}, standing still facing the viewer, arms at his sides',
    (0x10, 208, 48): f'{BOY}, standing still facing the viewer, blinking',
    (0x10, 224, 48): f'{BOY}, standing still facing the viewer, shifting his weight',
    (0x10, 240, 48): f'{BOY}, standing still facing the viewer, tapping a foot',
    (0x10, 256, 48): f'{BOY}, standing and looking about impatiently',
    (0x10, 272, 48): f'{BOY}, standing with one hand raised in a small wave',
    (0x10, 288, 48): f'{BOY}, standing and stretching',
    (0x10, 304, 48): f'{BOY}, crouching slightly, facing the viewer',

    # The creature families. Each has four frames of one animal.
    (0x8C, 64, 64): 'a grey moth with wings spread wide and a single amber eye',
    (0x8C, 80, 64): 'a grey moth with its wings raised together above it',
    (0x8C, 96, 64): 'a grey moth with its wings swept to one side',
    (0x8C, 112, 64): 'a grey moth with its wings swept to the other side',
    (0x8C, 128, 64): 'a grey moth with wings spread wide, seen from below',
    (0x8C, 144, 64): 'a grey moth with wings folded downward',
    (0x8C, 160, 64): 'a grey moth in flight, wings blurred to one side',
    (0x8C, 176, 64): 'a grey moth in flight, wings blurred to the other side',

    (0x90, 192, 64): 'a small brown monkey with a pale face, facing the viewer',
    (0x90, 208, 64): 'a small brown monkey, arms raised, facing the viewer',
    (0x90, 224, 64): 'a small brown monkey, crouched, facing the viewer',
    (0x90, 240, 64): 'a small brown monkey turning to one side',

    # THE TIMED WALL, four frames of one animation across the top band. These
    # said "a perched tropical bird with a long pale beak, wings folded" and so
    # on, which is a reading of what the HUNTER world draws on this wall rather
    # than of what the tile is. It is a wall in all five worlds and in all four
    # frames - Simulation.Entities.cs TimedWall cycles it and turns it into
    # nothing else - so the subject is the world's own wall description and all
    # this adds is which beat of the animation it is on.
    #
    # Nothing here names a bird now. The world table supplies the wall; these
    # say how it is moving. 19 September 2026.
    #
    # And the beats were wrong as well: "stirring", "fully active", "settling
    # back". The four frames are ONE picture scrolled 0, 4, 8 and 12 pixels to
    # the left with wrap-round, exactly, in all five worlds - a conveyor. So the
    # last three are no longer drawn at all; see SHIFTED. 29 September 2026.
    (0x74, 132, 4): 'the wall, still',
    (0x75, 164, 4): 'the wall scrolled a quarter of a tile to the left, wrapping round',
    (0x76, 196, 4): 'the wall scrolled half a tile to the left, wrapping round',
    (0x77, 228, 4): 'the wall scrolled three quarters of a tile to the left, wrapping round',

    # The two explosion chains, four frames each.
    (0x11, 256, 144): 'a scatter of small grey rock fragments flying apart',
    (0x12, 272, 144): 'a wider scatter of grey rock fragments with gold glints',
    (0x13, 288, 144): 'grey fragments thinning out, with gold coin pieces forming',
    (0x14, 304, 144): 'a round gold treasure coin with an engraved face',
    (0x1B, 192, 144): 'a bright burst of red and violet sparks, dense at the centre',
    (0x1C, 208, 144): 'a spreading burst of red and violet sparks',
    (0x1D, 224, 144): 'a thinning scatter of red and violet sparks',
    (0x1E, 240, 144): 'a few last red and violet sparks fading out',

    # The doorway opening.
    (0x31, 208, 112): 'a wooden doorway standing slightly ajar',
    (0x32, 224, 112): 'a wooden doorway standing half open',
    (0x33, 240, 112): 'a wooden doorway standing wide open on darkness',

    # The living growth, four frames of the same thing.
    (0x48, 192, 24): 'a spreading patch of living tan and brown slime',
    (0x49, 224, 24): 'a spreading patch of living tan and brown slime, bubbling',
    (0x4A, 256, 24): 'a spreading patch of living tan and brown slime, swelling',
    (0x4B, 288, 24): 'a spreading patch of living tan and brown slime, rippling',
}

SUBJECTS = {
    0x01: 'dense green jungle foliage and broad leaves, a solid ground tile',
    0x02: 'a pale lilac stone block streaked with green lichen, a solid wall tile',
    0x04: 'a slab of mottled violet crystal, a solid wall tile',
    0x07: 'a closed wooden door with two recessed panels, set in a stone frame',
    0x08: 'a forked tree branch with a purple creeping vine wound around it',
    0x0C: 'a small brown monkey reaching upward with both arms',
    0x15: 'a block of pale growing crystal, a solid wall tile',
    0x16: 'a round grey and brown rock boulder',
    0x40: 'a round gold treasure coin with an engraved face',
    0x44: 'a round grey and brown rock boulder',
    # 0x6C and 0x70 are frame 0 of the two bonus chains and are described by the
    # world tables like every other frame of them. These entries described them
    # as single objects, which is what they looked like to a reader who did not
    # know they were the start of an animation.
    0x70: 'a violet crystal shard set inside a red starburst frame',
    0x74: 'a slab of mottled violet crystal, a solid wall tile',
    0x78: 'a round green striped melon',
    0x7C: 'a golden egg with a pink core',
}
GENERIC = None

# Nothing to draw. The empty tile is empty.
SKIP = {0x00}

# Which sprites are frames of one animation.
#
# This matters more than any single prompt. Frames generated independently do not
# agree, and disagreement between frames does not read as style - it reads as the
# picture flickering. So a sequence is generated in order, the first frame is kept
# as the anchor, and every later frame is given that anchor as a second reference
# with instructions to match it.
#
# The first entry of each list is the anchor.
SEQUENCES = {
    # THE PLAYER'S NAMES WERE THREE-QUARTERS WRONG. 'boy-left' was the walk
    # away from the viewer, 'boy-away' the walk to the right and 'boy-right' the
    # walk to the left. The engine settles it - PlayerSprite() indexes pose/2 +
    # step, and PoseLeft is 0 - and so does every one of the five sheets. The
    # names only label the run, but the wrong ones had been copied into the
    # poses, which is where they did the damage. 29 September 2026.
    'boy-left':    [(0x10, 96, 48), (0x10, 112, 48)],
    'boy-away':    [(0x10, 0, 48), (0x10, 16, 48)],
    'boy-right':   [(0x10, 32, 48), (0x10, 48, 48)],
    'boy-toward':  [(0x10, 64, 48), (0x10, 80, 48)],
    'boy-push-r':  [(0x10, 128, 48), (0x10, 144, 48)],
    'boy-push-l':  [(0x10, 160, 48), (0x10, 176, 48)],
    # Two idle animations, not one. The engine cycles 12-15 or 16-19 and never
    # runs one into the other, and in most worlds they are different acts - in
    # hunter a fidget, then a hopping spin.
    'boy-idle-a':  [(0x10, 192, 48), (0x10, 208, 48), (0x10, 224, 48), (0x10, 240, 48)],
    'boy-idle-b':  [(0x10, 256, 48), (0x10, 272, 48), (0x10, 288, 48), (0x10, 304, 48)],
    'enemy-cw':    [(0x8C, 64, 64), (0x8C, 80, 64), (0x8C, 96, 64), (0x8C, 112, 64),
                    (0x8C, 128, 64), (0x8C, 144, 64), (0x8C, 160, 64), (0x8C, 176, 64)],
    'enemy-acw':   [(0x90, 192, 64), (0x90, 208, 64), (0x90, 224, 64), (0x90, 240, 64)],
    # THE TIMED WALL, and it was called 'bird' because of what the hunter world
    # happens to draw on it.
    #
    # Simulation.Entities.cs TimedWall cycles one cell 0x74 -> 0x75 -> 0x76 ->
    # 0x77 -> 0x74 while the window is open, and its comment is explicit:
    # "Nothing here turns the wall into anything else... it simply stops
    # animating and stays a wall". All four frames are the SAME WALL. It tiles
    # edge to edge in every one of them, which is why all four are in
    # FULL_BLEED.
    #
    # What went wrong is a reading of the hunter world's description of it - "a
    # wall of brightly coloured toucans perched among green leaves" - as though
    # the subject were a bird. The sequence was named `bird`, 0x74 was left out
    # of it, and 0x75 to 0x77 were given free-standing prompts: "a perched
    # tropical bird with a long pale beak, wings folded". So three frames of a
    # wall were each told to be an object centred in its square with clear space
    # around it, in all five worlds.
    #
    # Astra caught the first fix for this being backwards - it dropped 0x74 from
    # the metadata, which hides the mixture without removing it from the game:
    # the engine goes on cycling a full-bleed frame into three that are not.
    # 19 September 2026.
    'timed-wall':  [(0x74, 132, 4), (0x75, 164, 4), (0x76, 196, 4), (0x77, 228, 4)],

    # THE TWO WORMS. Six sequences, because each creature is three things that
    # have to agree with themselves across four facings: a head, a repeating
    # body segment and a tail.
    #
    # Anchored on the facing that comes towards the viewer rather than on the
    # table's own first entry. The first four generated without any sequence at
    # all came back two good and two stair-stepped - the failure this whole
    # exercise exists to avoid - and the two good ones were the pair looking at
    # the viewer. A head seen head-on is a picture the model can draw; a long
    # low shape seen side-on is one it copies the pixels of. Anchor on the one
    # that works and the rest are told to match it.
    #
    # wanted() gives every position in a group the group's first value, so the
    # value below is 0xA0 or 0xB0 throughout rather than the value that draws
    # that particular segment.
    'wormA-head':  [(0xA0, 0, 96), (0xA0, 16, 96), (0xA0, 32, 96), (0xA0, 48, 96),
                    (0xA0, 64, 96), (0xA0, 80, 96), (0xA0, 96, 96), (0xA0, 112, 96)],
    'wormA-tail':  [(0xA0, 128, 96), (0xA0, 144, 96), (0xA0, 160, 96), (0xA0, 176, 96),
                    (0xA0, 192, 96), (0xA0, 208, 96), (0xA0, 224, 96), (0xA0, 240, 96)],
    'wormA-body':  [(0xA0, 256, 96), (0xA0, 272, 96), (0xA0, 288, 96), (0xA0, 304, 96)],

    'wormB-head':  [(0xB0, 0, 80), (0xB0, 16, 80), (0xB0, 32, 80), (0xB0, 48, 80),
                    (0xB0, 64, 80), (0xB0, 80, 80), (0xB0, 96, 80), (0xB0, 112, 80)],
    'wormB-tail':  [(0xB0, 128, 80), (0xB0, 144, 80), (0xB0, 160, 80), (0xB0, 176, 80),
                    (0xB0, 192, 80), (0xB0, 208, 80), (0xB0, 224, 80), (0xB0, 240, 80)],
    'wormB-body':  [(0xB0, 256, 80), (0xB0, 272, 80), (0xB0, 288, 80), (0xB0, 304, 80)],
    'blast-gold':  [(0x11, 256, 144), (0x12, 272, 144), (0x13, 288, 144), (0x14, 304, 144)],
    'blast-spark': [(0x1B, 192, 144), (0x1C, 208, 144), (0x1D, 224, 144), (0x1E, 240, 144)],
    'door':        [(0x07, 192, 112), (0x31, 208, 112), (0x32, 224, 112), (0x33, 240, 112)],
    'slime':       [(0x48, 192, 24), (0x49, 224, 24), (0x4A, 256, 24), (0x4B, 288, 24)],
    'boulder':     [(0x44, 0, 64), (0x16, 0, 64)],

    # The taps and what they emit. Each is its own animation, so each is
    # anchored: frames drawn independently do not agree, and a flame that
    # changes style every frame reads as flicker rather than fire.
    #
    # Frame 0 of each (0x08, 0x0C) was left out, so it was drawn on its own with
    # no anchor and its own crop - and in all five worlds the tap changed shape
    # once a cycle where only its handle turns. 29 September 2026.
    'tap':         [(0x08, 256, 176), (0x09, 272, 176), (0x0A, 288, 176), (0x0B, 304, 176)],
    'lamp':        [(0x0C, 256, 64), (0x0D, 272, 64), (0x0E, 288, 64), (0x0F, 304, 64)],
    'drip':        [(0x20, 0, 112), (0x22, 16, 112)],
    'drip-break':  [(0x24, 32, 112), (0x25, 48, 112)],
    'drip-pool':   [(0x28, 64, 112), (0x2A, 80, 112)],
    'flame':       [(0x2C, 128, 112), (0x2D, 144, 112),
                    (0x2E, 160, 112), (0x2F, 176, 112)],
    # Both of these used to start at frame 1, leaving frame 0 outside the
    # sequence - unanchored, and free to be described as something else
    # entirely. It was: (0,128) carried the role `exit` in the world tables, so
    # the first frame of the treasure bonus was drawn as a doorway while its
    # other three were drawn as a numbered badge. The result is visible in the
    # shipped atlas as a light badge followed by three dark ones.
    'bonus-count': [(0x6C, 0, 128), (0x6D, 16, 128), (0x6E, 32, 128), (0x6F, 48, 128)],
    'bonus-turn':  [(0x70, 64, 128), (0x71, 80, 128), (0x72, 96, 128), (0x73, 112, 128)],
}


def sequence_of(value, x, y):
    """The sequence a sprite belongs to: its name, position and length."""
    for name, frames in SEQUENCES.items():
        if (value, x, y) in frames:
            return name, frames.index((value, x, y)), len(frames), frames[0]
    return None, 0, 1, None

# Things that tile edge to edge must fill their whole square or the cave shows seams.
# 0x75, 0x76 and 0x77 are here because they are the timed wall's other three
# frames and a wall tiles edge to edge in every one of them. They were missing,
# which is what let three frames of a wall be prompted as a free-standing bird.
#
# The amoeba too. Its four frames fill the tile to every edge in all five worlds,
# so a colony tiles as one continuous mass. It was not in this set, so it was
# prompted "centred in the square with a little clear space around it" - and
# came back as a cube in hunter and a free-standing blob in the other four.
FULL_BLEED = {0x01, 0x02, 0x03, 0x04, 0x15, 0x74, 0x75, 0x76, 0x77,
              0x48, 0x49, 0x4A, 0x4B}

# The tiles that are meant to be in pieces: both blasts, the two
# transformation chains and the breaking drop. See `scattered` in prompt_for.
SCATTERED = {0x11, 0x12, 0x13, 0x14, 0x1B, 0x1C, 0x1D, 0x1E,
             0x34, 0x35, 0x36, 0x37, 0x17, 0x18, 0x19, 0x1A,
             0x24, 0x25}

# The two enemy families, which #70 gave a silhouette class each. Named here
# rather than written as bare numbers at the point of use, because 0x8C and 0x90
# are the sort of constant that ends up swapped.
RuntimeTile_EnemyClockwise = 0x8C
RuntimeTile_EnemyAnticlockwise = 0x90

# The alpha at which a pixel counts as being THERE rather than as the soft tail
# of an antialiased edge. Above zero is not the same question: a row of alpha-3
# pixels is invisible on screen and still inside a bounding box.
SOLID = 32


def sprites():
    return json.loads((GAME / 'sprites.json').read_text())


# Where a tile with no art of its own borrows it from.
#
# Every one of these is a transition: a cell on its way from one thing to
# another, or a frame of something whose first frame IS redrawn. The original
# gives each its own picture; VIP borrows the nearest state it has, which costs
# the animation and keeps the artwork honest.
#
# The four-frame rows at y=160 and y=176 are each shared by TWO chains - 0x34 to
# 0x37 becomes a boulder and 0x38 to 0x3B becomes an enemy, from the same four
# pictures - so there is no end state to borrow that is right for both. They take
# the explosion instead, which is what those tiles actually depict and what a cell
# in the middle of being transformed is doing.
#
# These became reachable when the worms landed: the chains are what a worm turns
# things into, and the eggs are what it hatches from. Before that nothing
# dispatched them, so nobody drew them.
SUBSTITUTE = {
    # EMPTY, and #32 is why. It held fourteen positions a world: the eight
    # transformation frames borrowing the explosion, and three frames of each
    # egg borrowing the first.
    #
    # That kept every object visible at the cost of the distinctions that
    # explain what the engine is DOING. A transformation showing an explosion
    # says something blew up; it did not, it turned into something else. An egg
    # whose four frames are the same picture says nothing is happening, while
    # the engine counts down to a worm.
    #
    # All fourteen are drawn now. What made that possible was #73 writing the
    # descriptions - the transformation stages by position, and a hatching
    # countdown for both eggs rather than a frame number.
    #
    # Kept as an empty dict rather than deleted: it is a real mechanism, the
    # plan and the atlas both report against it, and the next sprite that
    # genuinely has no art of its own should borrow deliberately rather than
    # have the machinery reinvented.
}


def wanted():
    """Every (runtime value, sprite position) pair the game can actually draw."""
    s = sprites()
    maps = (GAME / 'cellmaps.bin').read_bytes()

    seen = set()
    # Every raw world, 0 to 9. This read the five 1988 worlds only (0, 2, 4, 6, 8)
    # and the hidden caves added nothing new "by luck, not design" (#107); it is
    # the same 139 today, and a hidden cave that did use a new state would now
    # be counted rather than silently missed.
    for world in range(10):
        for stage in range(4):
            r = world * 4 + stage
            seen.update(maps[r * 880:(r + 1) * 880])

    values = {AUTHORED[a] for a in seen if a in AUTHORED} | PRODUCED

    out = {}
    for v in sorted(values):
        if v == 0x10:
            table = s['player']
        elif v == 0x8C:
            table = s['enemyA']
        elif v == 0x90:
            table = s['enemyB']
        elif 0xA0 <= v <= 0xA7:
            table = s['groupA']
        elif 0xB0 <= v <= 0xB7:
            table = s['groupB']
        elif v < len(s['main']):
            table = [s['main'][v]]
        else:
            continue

        if v in SKIP:
            continue

        for x, y in table:
            if x < 0:
                continue
            out.setdefault((x, y), v)
    return out


def subject_for(value, x, y, world=None):
    """What this sprite depicts.

    The world's own table wins where it has an answer, because the five worlds draw
    the same runtime value as entirely different objects - a boulder is a rock, an
    orange, a cactus, the planet Earth or an eyeball depending on where you are.
    Everything below it is keyed on the value alone and therefore describes the
    hunter world, which is where the first pass went wrong for the other four.
    """
    if world:
        # NO SILENT FALLBACK (#107). The tables below describe the HUNTER world, so
        # a world they do not know used to be generated against hunter's words - the
        # failure that once drew 235 sprites as the wrong object. A world with no
        # OBJECTS entry, or a position with no role in it, is an error that names
        # both, never a quiet borrow.
        if world not in worlds.OBJECTS:
            raise ValueError(f'{world!r} has no entry in worlds.OBJECTS, so {value:#04x} at '
                             f'{x},{y} would be described in hunter\'s words')
        described = worlds.describe(world, x, y)
        if not described:
            raise ValueError(f'{world!r} has no description for the position {x},{y} '
                             f'(value {value:#04x}); give it a role in worlds.ROLES')
        return described

    return POSITIONS.get((value, x, y)) or SUBJECTS.get(value)


def prompt_for(value, x, y, anchored=False, world=None):
    """The prompt, and the thing it has to get right.

    A first version asked for the reference's shapes to be reproduced exactly. The
    model obliged and copied the pixel steps, so the output was the same blocky art
    at a larger size - which is the one thing this whole exercise exists to avoid.

    The brief is therefore: same character, same colours, same pose, drawn properly.
    Not a faithful enlargement of a bitmap. Smooth, illustrated, no visible pixels.
    """
    smooth = (
        'Draw it as smooth high-resolution illustration with clean curved edges. '
        'It must NOT look like pixel art: no visible pixels, no blocky or stair-'
        'stepped edges, no jagged outlines, no mosaic. The reference is a tiny '
        'bitmap and you are drawing the artwork it was trying to be. '
    )

    # THE ORIGINAL IS A MOTIF ON A SOLID BACKING, and that is the answer to #75.
    #
    # The question the issue left open was whether a magic wall tile shows one
    # elephant head drawn out to its own edges or a repeating field of them. It
    # is neither. Tiling hunter's original 16x16 three by three shows heads
    # sitting on a solid violet ground - the tile is 100% opaque because of the
    # BACKGROUND, not because the motif reaches the corners.
    #
    # So the redraws came back at 52% for a reason no rewording of "reach every
    # edge" could fix: they were drawing the motif and leaving the ground
    # transparent, which is exactly what they had been asked for everywhere
    # else. The instruction has to name the ground, and the request has to stop
    # asking for a transparent background for these nine values.
    placing = ('It is a WALL TILE and it tiles edge to edge with its '
               'neighbours. Fill the entire square with solid opaque colour: '
               'the motif sits on a solid background of its own, in a colour '
               'that suits it, covering every pixel out to all four edges and '
               'into all four corners. NOTHING may be transparent, and there '
               'must be no border, frame, margin or rounded corner. '
               if value in FULL_BLEED else
               'Centred in the square with a little clear space around it. ')

    seq, index, length, _ = sequence_of(value, x, y)

    # The two bonus tiles are a framed badge rather than a single object, and
    # three of the standing clauses below are wrong for them specifically.
    framed = seq in ('bonus-count', 'bonus-turn')

    # The silhouette clause. Without it the model answers "smooth, detailed
    # illustration" by adding organic irregularity, and the boulder came back as a
    # lumpy mass shedding fragments instead of the original's clean disc. That is
    # wrong for a tile specifically: a tile has to be recognised instantly at a
    # glance against a busy cave, and the outline is what does that. Detail that
    # breaks the outline is worse than no detail at all.
    #
    # The exception is the bonus frame. Its ring is a chain of separate jewels
    # with the background showing between them, so "one single closed shape"
    # obeyed literally fills the gaps and turns the ring into a disc. What has
    # to hold there is the diamond the chain describes, not a continuous edge.
    # AND THE OTHER EXCEPTION IS EVERYTHING THAT IS MEANT TO BE IN PIECES. The
    # clause below bans "loose fragments, crumbs, chips, sparks ... floating
    # outside the shape", and the explosions ARE sparks - 37% of the tile in
    # a dozen pieces, then 14, then one small knot, then a few specks at the
    # edges. Told to make one closed shape, the model drew a solid splat in
    # every frame of every world. The transformations dissolve through a
    # scatter the same way, and the acid drop breaks into a crown of spray.
    # 29 September 2026.
    scattered = value in SCATTERED
    silhouette = (
        'The picture is made of SEPARATE PIECES with clear space between them, '
        'as many and as spread out as the description says. Do not join them '
        'into one mass, do not fill the gaps between them, and cover no more of '
        'the square than the description says. '
        if scattered else
        'The jewels of the ring are separate from each other with clear space '
        'showing between them, arranged around a diamond-shaped opening exactly '
        'as the reference arranges them. Each jewel is a smooth rounded stone, '
        'not a square block. The ring as a whole reads as a clean diamond at a '
        'glance, and nothing floats outside it. '
        if framed else
        'The outline is one single closed shape, smooth and unbroken, and it '
        'follows the reference silhouette exactly: if the reference is a circle '
        'draw a clean circle, if it is a square draw a clean square. The edge '
        'itself is even - no lumps, bumps, spikes, bites, notches or ragged '
        'protrusions along it. All texture and detail lives INSIDE the outline '
        'and never disturbs it. No loose fragments, crumbs, chips, sparks, dust '
        'or debris floating outside the shape and nothing trailing off it. Read '
        'at the size of a thumbnail it must still be obvious what it is. ')

    # THE TWO ENEMY FAMILIES USED TO BE GIVEN A SHAPE EACH (#70): the clockwise
    # one "a WIDE SPREAD shape, broader than it is tall", the anticlockwise one
    # "a COMPACT UPRIGHT shape", in every frame. The originals break both rules
    # in about half their frames. The clockwise enemy TURNS through its eight -
    # the cowboy hammer is a full circle in 45-degree steps, the bat folds its
    # wings and points up and down - and the anticlockwise one cartwheels. So
    # the rule drew one picture eight times: a horizontal hammer, a head-up bat.
    #
    # The families still differ, because the originals make them differ. What
    # each frame looks like is in its reading now. Removed 29 September 2026.

    # "no text, no border, no frame" earns its place everywhere else - an
    # unasked-for frame around a boulder is a wasted tile - and contradicts the
    # subject outright here, where the sprite IS a numeral inside a ring.
    bans = ('no backdrop, no ground plane. ' if framed else
            'no backdrop, no ground plane, no text, no border, no frame. ')

    tail = (f'{placing}{silhouette}Isolated on a transparent background, no shadow, '
            f'no halo, {bans}'
            'Flat even game-asset lighting, in the style of a modern 2D platform '
            'game.')

    # The treasure bonus draws the number it pays. `0x11709` pays `al - 0x6C`,
    # so frame 0 pays nothing and frame 3 pays three, and the original tile
    # carries that digit as the largest thing on it: 0, 1, 2, 3 across the four.
    #
    # The prompt used to say "a numbered badge" and stop, which left the model
    # reading a digit off a 16 pixel reference. It got six of the twenty wrong -
    # hunter and space and body ran 0 1 2 1, cook ran 0 1 1 3, cowboy ran
    # 0 1 1 1 - so a player could read a tile and collect the wrong amount. The
    # digit is the one thing on this sprite that is not decoration, so it is
    # named rather than inferred.
    digit = (f'The single numeral {index} is the largest element on the tile, '
             'centred inside the frame, filling most of the opening, upright, '
             'plain and unmistakable. Draw exactly that digit and no other '
             'digit, letter or symbol. '
             if seq == 'bonus-count' else '')

    if length > 1:
        run = (f'This is frame {index + 1} of {length} in an animation of the same '
               'thing. Every frame must look like the same object drawn by the same '
               'hand: identical colours, identical proportions, identical outline '
               'weight and identical level of detail. Only the pose changes, and '
               'the pose is the one described above. ')
        # "Match its style, its palette and its size" was read as "match it", and
        # the finished frame's POSE came along too: eight frames of a turning
        # enemy drawn as one picture, both feet of a walk on the same side.
        if anchored:
            run += ('The second attached image is an already-finished frame from this '
                    'same animation, at a DIFFERENT moment. Take only its style from '
                    'it - palette, line weight, proportions and size on the square. '
                    'Do NOT copy its pose, facing, rotation or arrangement: this frame '
                    'is posed as described above and as the small pixel image shows, '
                    'and where that differs from the finished frame, the description '
                    'wins. ')
        how = worlds.pattern(world, x, y) if world else None
        if how:
            run += f'How the frames of this animation relate: {how} '
    else:
        run = ''

    subject = subject_for(value, x, y, world)

    if subject is None:
        # We cannot name it, so describe the job instead: read the reference, work
        # out what it depicts, and draw THAT properly. Still bounded - it may not
        # invent an object that is not there, which an earlier run happily did.
        return (
            'Look at the attached low-resolution game sprite, work out what it '
            'depicts, and draw that same thing properly at high resolution. Keep the '
            'same subject, the same colours, the same overall shape and the same '
            f'orientation. {smooth}'
            'Do not substitute a different object and do not add any creature, '
            'symbol or scenery that is not already in the reference. '
            f'{digit}{run}{tail}'
        )

    fix = CORRECTIONS.get((world, (x, y)))
    if fix:
        digit = f'{digit}{fix} '

    # The readings place things by row and column of the 16-pixel tile, which
    # is the only exact way to say which foot is down.
    grid = ('Rows and columns count the small reference image from 0 at its top '
            'left; screen-left means the left of the image. ' if world else '')

    return (
        f'A single game sprite of {subject}. {grid}The attached low-resolution image is the '
        'same sprite in the original game - match its colours exactly, its pose, its '
        f'orientation and its overall proportions. {smooth}'
        'Add the detail the original was too small to hold: form, texture, soft '
        'shading and a clean dark outline. Do not add anything that is not in the '
        f'reference. {digit}{run}{tail}'
    )


def paths(world, x, y):
    d = WORK / world
    return d / f'src-{x}-{y}.png', d / f'out-{x}-{y}.png'


def ground_to_transparent(tile, floor):
    """`tile` with the world's floor colour made transparent where it is connected
    to the tile's edge, so the model is shown the object and not the object on a square.

    Scuba, player and music are painted on an OPAQUE ground (the world's `floor` in
    worlds.json), where the 1988 sheets and luck and miner are transparent. Shown the
    square, the model draws the square, and the atlas then has a blue box round every
    creature. Only ground reachable from the edge goes: a floor-coloured pixel inside
    the object - an eye, a gap between the legs of a crab - is part of it and stays.
    """
    tile = tile.convert('RGBA')
    w, h = tile.size
    px = tile.load()
    stack = [(x, y) for x in range(w) for y in (0, h - 1)] + [(x, y) for y in range(h) for x in (0, w - 1)]
    seen = set()
    while stack:
        x, y = stack.pop()
        if (x, y) in seen or not (0 <= x < w and 0 <= y < h):
            continue
        seen.add((x, y))
        if px[x, y][3] and px[x, y][:3] == floor:
            px[x, y] = (0, 0, 0, 0)
            stack += [(x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)]
    return tile


def cmd_extract():
    for world in WORLDS:
        sheet = Image.open(GAME / f'{world}.png').convert('RGBA')
        (WORK / world).mkdir(parents=True, exist_ok=True)
        floor = floor_of(world)

        for (x, y), value in wanted().items():
            src, _ = paths(world, x, y)
            tile = sheet.crop((x, y, x + TILE, y + TILE))
            # A tile that fills its square IS the ground - a wall, the dirt, the
            # amoeba - so it keeps it; everything else loses the part of it that
            # is only the sheet's background.
            if value not in FULL_BLEED:
                tile = ground_to_transparent(tile, floor)
            # Nearest to 512 so the model sees pixels rather than a blur.
            tile.resize((512, 512), Image.NEAREST).save(src)
        print(f'{world}: {len(wanted())} references')


def first_line(output):
    """The most useful line of a failure, for the one-line stop message."""
    for line in output.splitlines():
        line = line.strip()
        if any(p in line for p in PERMANENT):
            return line[:160]
    return output.strip()[:160]


def cmd_plan():
    # `order()`, not `wanted()` - what generate will actually draw. Those two
    # disagreed by the 70 substituted tiles, so plan quoted a run 64% larger
    # than the one generate performs and named a price for work it never does.
    # The mirrored positions are not generated and never will be, so counting
    # them as "sprites a world" overstates the run by four and disagrees with
    # what the atlas step reports. See mirrors().
    drawn = {w: [(x, y, v) for (x, y, v) in order() if not built_here(w, (x, y))]
             for w in WORLDS}
    todo = [(w, x, y) for w in WORLDS for (x, y, _) in drawn[w]
            if not paths(w, x, y)[1].exists()]
    total = sum(len(d) for d in drawn.values())
    flipped = sum(len(mirrors(w)) for w in WORLDS)
    borrowed = len(SUBSTITUTE)

    print(f'{total} sprites to draw across {len(WORLDS)} worlds')
    print(f'{flipped} more are flips of their opposite facing, '
          f'{sum(len(shifted(w)) for w in WORLDS)} are the timed wall moved along, and '
          f'{sum(dissolve.applies(w, p) for w in WORLDS for p in dissolve.POSITIONS)} are dissolved and '
          f'{sum(tapturn.applies(w, p) for w in WORLDS for p in tapturn.POSITIONS)} are taps turned, '
          f'from finished tiles')
    if borrowed:
        print(f'{borrowed} more a world are substituted from a finished tile '
              'and are never generated')
    print(f'{total - len(todo)} done, {len(todo)} to go')
    # What a sprite actually bills, not what a bare draft costs. The imager's
    # history has 1,434 of these at $0.024 on average - the 1988 tile, and on a
    # later frame its anchor too, go in as input tokens on every call. This said
    # $0.016 and quoted a 644-sprite run at $10 when it is about $16.
    print(f'roughly ${len(todo) * 0.024:.2f} at draft quality')


def order():
    """Generation order: every sequence's anchor first, then its later frames.

    A later frame is given its own anchor as a second reference, so the run has to
    produce the anchor before anything that depends on it.
    """
    items = wanted()
    first, rest = [], []

    for (x, y), v in items.items():
        # A substituted tile borrows a finished one and is never drawn. It was
        # still counted here, so `plan` reported 110 outstanding when 70 of them
        # were never going to be generated - and an unfiltered `generate` would
        # have paid for all 70 and thrown every one away.
        if (x, y) in SUBSTITUTE:
            continue

        name, index, length, _ = sequence_of(v, x, y)
        (first if length == 1 or index == 0 else rest).append((x, y, v))

    rest.sort(key=lambda t: sequence_of(t[2], t[0], t[1])[1])
    return first + rest


def cmd_generate(limit=None):
    todo = []
    for world in WORLDS:
        for x, y, v in order():
            # Taken from its opposite number at assembly time. Generating it
            # would produce a second, independent drawing - which is the defect
            # mirrors() exists to remove.
            if built_here(world, (x, y)):
                continue
            if not paths(world, x, y)[1].exists():
                todo.append((world, x, y, v))

    if limit:
        todo = todo[:int(limit)]

    for i, (world, x, y, v) in enumerate(todo, 1):
        src, out = paths(world, x, y)
        if not src.exists():
            print(f'  no reference for {world} {x},{y} - run extract first')
            continue

        # A later frame of a sequence is anchored to the first, so the frames agree
        # with each other rather than each being a fresh interpretation.
        name, index, length, anchor = sequence_of(v, x, y)
        anchor_file = None
        if anchor and index > 0:
            # An anchor that is itself a flip has no file of its own; the frame
            # it is flipped from carries the same style, which is all an anchor
            # is for now.
            at = mirrors(world).get((anchor[1], anchor[2]), (anchor[1], anchor[2]))
            candidate = paths(world, *at)[1]
            if candidate.exists():
                anchor_file = candidate

        # A full-bleed tile asks for an OPAQUE background. Requesting a
        # transparent one and then telling the model in words to fill the
        # square is a contradiction, and the request wins: every one of the 27
        # seaming tiles was generated this way.
        args = [
            str(IMAGER_PY), str(IMAGER), '--draft', '--model', MODEL,
            '--background', 'opaque' if v in FULL_BLEED else 'transparent',
            '--output-format', 'png', '--size', '1024x1024',
            '--reference', str(src),
        ]
        if anchor_file:
            args += ['--reference', str(anchor_file)]
        args += [prompt_for(v, x, y, anchored=bool(anchor_file), world=world),
                 str(out)]

        # The image API rate limits hard on a long run. A first attempt at this
        # burned through all 315 and landed only a third of them, because a
        # rate-limited call fails rather than waiting. So: pace the requests, and
        # back off and retry rather than losing the sprite.
        #
        # But not everything that arrives as a 429 is a rate limit. An exhausted
        # credit balance comes back with the same status and never clears, so
        # retrying it walks the whole queue at four waits a sprite and renders
        # nothing. That happened: a run sat for an hour reporting rate limits
        # when the account had simply run out of money. A permanent failure ends
        # the run rather than being retried.
        tag = f'{name} {index + 1}/{length}' if length > 1 else 'single'

        for attempt in range(1, 5):
            result = subprocess.run(args, capture_output=True, text=True)
            if out.exists():
                break

            output = (result.stdout + result.stderr).lower()
            if any(p in output for p in PERMANENT):
                print(f'\nStopping: {first_line(output)}', flush=True)
                print(f'{i - 1} of {len(todo)} done. Re-run generate to continue.',
                      flush=True)
                return

            if any(p in output for p in REFUSED):
                break

            if attempt < 4:
                wait = PACE * (4 ** attempt) if 'rate' in output else 5
                print(f'[{i}/{len(todo)}] {world} {x},{y} {tag} retry {attempt} in {wait}s')
                time.sleep(wait)

        state = 'ok' if out.exists() else 'FAILED'
        print(f'[{i}/{len(todo)}] {world} {x},{y} {tag} {state}', flush=True)
        if state == 'FAILED':
            print('   ', (result.stderr or result.stdout).strip()[:200], flush=True)

        time.sleep(PACE)


# Positions taken from their opposite number, flipped, rather than generated.
#
# THE ORIGINAL DOES THIS AND SO SHOULD WE. The two horizontal facings of each
# worm are mirror images in the 1988 art - cowboy's is an exact 1.000 match
# against the flip of its partner, and nine of the ten pairs are closer to the
# mirror than to each other. Generating both independently is what produced
# creatures whose head swapped ends between facings and between frames: the
# model has no way to know the other tile exists.
#
# Mirroring makes the two facings opposites by construction, which is the
# property that was wanted and could not be asked for in a prompt. It also
# halves the generation for these forty positions.
#
# Facing 2 (RIGHT, x=32 and 48) is generated and facing 0 (LEFT, x=96 and 112)
# is its flip, matching the engine's own numbering in Simulation.Entities.cs.
#
# WHICH RIGHT-FACING FRAME IS NOT FIXED, and a fixed pairing swapped them. This
# was 96 from 32 and 112 from 48. The hunter crocodile's 96 is the exact flip
# of 48 and its 112 of 32, so its jaws came out shut where they open; cowboy's
# is the other way round. The partner is now whichever right-facing frame the
# original is closest to the flip of, per world. 29 September 2026.
#
# AND THE SAME IS TRUE WELL BEYOND THE WORMS. In hunter the left walk is the
# exact flip of the right walk, the two walk-away frames are exact flips of
# each other and the enemy's left facing is the exact flip of its right; cowboy
# is 98 to 100% the same. Where the original is a flip - EXACT or better of its
# pixels - the redraw is a flip too, and "facing the wrong way" stops being
# possible. Where it is not, as in cook and space, the frame is drawn from its
# reading like any other.
EXACT = 0.97

# target -> the frames it may be the flip of. The worm heads are handled as a
# pair in mirrors() and are always flipped.
_FLIP_IF_EXACT = {
    (96, 48): ((32, 48), (48, 48)), (112, 48): ((32, 48), (48, 48)),
    (160, 48): ((128, 48), (144, 48)), (176, 48): ((128, 48), (144, 48)),
    (16, 48): ((0, 48),), (80, 48): ((64, 48),),
    (160, 64): ((96, 64), (112, 64)), (176, 64): ((96, 64), (112, 64)),
}
for _y in (96, 80):
    _FLIP_IF_EXACT[(224, _y)] = ((160, _y), (176, _y))
    _FLIP_IF_EXACT[(240, _y)] = ((160, _y), (176, _y))

_mirrors = {}


def mirrors(world):
    """{target: source} for this world - the positions drawn as a flip."""
    if world in _mirrors:
        return _mirrors[world]

    # PIL only. `plan` calls this, `--help` lands in `plan`, and the CI job that
    # checks every tool starts cleanly has no numpy.
    from PIL import ImageChops, ImageOps

    sheet = Image.open(GAME / f'{world}.png').convert('RGBA')

    def tile(pos):
        return sheet.crop((pos[0], pos[1], pos[0] + TILE, pos[1] + TILE))

    def match(target, source):
        """The share of pixels identical in all four channels."""
        diff = ImageChops.difference(tile(target), ImageOps.mirror(tile(source)))
        worst = diff.getchannel(0)
        for band in range(1, 4):
            worst = ImageChops.lighter(worst, diff.getchannel(band))
        return worst.histogram()[0] / (TILE * TILE)

    found = {}

    # The heads are always flipped, and as a PAIR: 96 and 112 take the two
    # right-facing frames between them, whichever way round matches better.
    # Picked one at a time, a worm whose heads are not mirror images at all -
    # cook, space, body - sent both to the same frame and lost its second one:
    # the body hand that opens and then clenches would only ever open.
    for y in (96, 80):
        a, b = (96, y), (112, y)
        straight = match(a, (32, y)) + match(b, (48, y))
        crossed = match(a, (48, y)) + match(b, (32, y))
        found[a], found[b] = (((32, y), (48, y)) if straight >= crossed
                              else ((48, y), (32, y)))

    for target, sources in _FLIP_IF_EXACT.items():
        score, source = max((match(target, s), s) for s in sources)
        if score >= EXACT:
            found[target] = source
    _mirrors[world] = found
    return found


# TILES THE IMAGE API WILL NOT DRAW, and what each is built from instead.
#
# OpenAI's moderation refuses these at the output stage on every attempt, with
# any wording - the cook's right-facing walk and push have been refused since the
# first run, and the body enemy's upward frame joined them on 29 September 2026.
# Each is built from a finished tile that the original says it matches:
#
#   cook, right-facing: the left-facing frame of the same stride, flipped. The
#   1988 frames are 72 to 87 per cent flips of each other and run in the same
#   order - stride, then legs together - so the flip is the same moment.
#   body (80,64): the downward frame (144,64) turned half a turn. The reading
#   says exactly that; the pixels match 70 per cent only because the 1988 drop
#   shadow stays at the lower left, and the redraws have no shadow.
#
# world -> {target: (source, 'mirror' | 'rotate')}
REFUSED_TILES = {
    'cook': {(32, 48): ((96, 48), 'mirror'), (48, 48): ((112, 48), 'mirror'),
             (128, 48): ((160, 48), 'mirror'), (144, 48): ((176, 48), 'mirror')},
    'body': {(80, 64): ((144, 64), 'rotate')},
}


# FRAMES THAT ARE THE OTHER FRAME OF THEIR PAIR, in 1988 and so here.
#
# Each of these is 85 to 100 per cent the same tile as its partner in the
# original, and its redraw was wrong where the partner's was right - a worm's
# second frame turning its head away, a tail hanging from the wrong edge, a body
# segment switching from side view to top view every other frame. A redraw with
# the same prompt drew the same mistake (#117). A copy of the partner cannot:
# what the pair differs by in 1988 - a blink, a sway - is small, and losing it
# is far less wrong than the head turning round.
#
# world -> {target: the position it is a copy of}
SAME_AS = {
    # Four more below 85 per cent, where the redraw flipped a tail end to end or
    # turned the body 90 degrees every other frame and a copy of the right-drawn
    # partner is plainly less wrong: hunter's two down tails and ball body,
    # cowboy's right-facing locomotive. 30 September 2026.
    'hunter': {(208, 96): (192, 96), (208, 80): (192, 80), (304, 80): (288, 80),
               (144, 80): (128, 80)},
    'body': {(176, 96): (160, 96), (208, 96): (192, 96), (240, 96): (224, 96), (304, 96): (288, 96),
             (176, 80): (160, 80), (208, 80): (192, 80), (240, 80): (224, 80),
             (304, 80): (288, 80)},
    'cook': {(192, 96): (208, 96), (224, 96): (240, 96),
             (48, 80): (32, 80), (80, 80): (64, 80)},
    'cowboy': {(304, 80): (288, 80), (48, 96): (32, 96)},
    'space': {(48, 80): (32, 80), (80, 80): (64, 80)},
}


# WHAT THE LAST TWO DRAWS OF A TILE GOT WRONG, said in its prompt.
#
# These tiles were drawn from their reading twice and made the same mistake
# both times (#117). The reading says what the tile is; this says what the model
# keeps doing instead, because it had not been told. From the check of 30
# September 2026.
CORRECTIONS = {
    **{(w, (48, 112)): 'The splash must be BIG: a wide crown of spray reaching both side edges '
       'and covering about two thirds of the square, far bigger than the small first splash, '
       'with the puddle under it narrower. Earlier attempts drew thin streaks covering under '
       'half the square, which read as the same splash as the frame before.'
       for w in ('hunter', 'cowboy', 'space', 'body')},
    ('cowboy', (272, 144)): 'There is NO whole hammer: only coloured specks - grey, yellow, red, '
       'green, peach and black - scattered evenly over the whole square, with one small grey '
       'clump at the top left. Earlier attempts kept the hammer head intact and swirled the '
       'specks round one corner.',
    ('cook', (96, 64)): 'Exactly ONE slice of bread, a single diamond on one side of the green '
       'stalk, the mirror image of the first frame. Earlier attempts added a second, narrow '
       'slice on the other side of the stalk. There is no second slice.',
    ('cook', (68, 4)): 'The ground is flat mid grey reaching every edge and every corner of the '
       'square, with a black shadow along the bottom. There is NO white card, NO margin and NO '
       'border: the spoon bowl touches the left edge.',
    ('body', (68, 4)): 'The headstone is a flat-topped slab that fills the WHOLE square to all '
       'four corners - no rounded top, no sky or ground showing in the top corners, no margin '
       'down the sides. Earlier attempts drew a rounded headstone standing in a scene.',
    ('hunter', (192, 48)): 'His MOUTH IS CLOSED - no open mouth, no dark gap between the lips. '
       'Earlier attempts drew a small open mouth.',
    ('hunter', (160, 80)): 'A SHORT stub of tail, half a tile long, with its thick joined end '
       'against the RIGHT edge of the square at mid-height and its point towards the centre. '
       'Earlier attempts hung a long tail from the TOP edge across the whole square.',
    ('cowboy', (208, 96)): 'The tow bar comes out of the BOTTOM edge only, where it joins the '
       'train. Nothing comes out of the top edge.',
    **{('cowboy', p): 'This tile FILLS THE SQUARE edge to edge: a locomotive seen from directly '
       'above or below, with a strip of rail and wheels running down BOTH side edges of the '
       'square. Earlier attempts drew a narrow free-standing engine in the middle with empty '
       'space either side.' for p in ((0, 96), (16, 96), (64, 96), (80, 96))},
    ('cowboy', (80, 80)): 'The horse FACES THE VIEWER, head towards us, seen from the front. '
       'Earlier attempts drew it from behind with its tail hanging down the middle.',
    ('body', (272, 112)): 'The egg is WHOLE and UNBROKEN: one smooth oval shell with an eye-shaped '
       'slit opening in its middle. Earlier attempts split it into two ragged halves, which '
       'reads as cracking - it does not crack.',
    ('cook', (240, 128)): 'The same ROUND egg as the previous frame with the same oval core, only '
       'the small plus mark moved. Earlier attempts drew a taller, lumpier egg with the core '
       'pushed to one side.',

    # From the final check of 30 September 2026.
    **{('cowboy', (x, 64)): 'The hammer head is a PLAIN SOLID BLOCK of grey steel with smooth '
       'straight edges. No teeth, no notches, no comb, no claw, no serrations - earlier attempts '
       'drew it as a notched comb that read as a rake or a key.'
       for x in (64, 80, 96, 112, 128, 144, 160, 176)},
    **{(w, (32, 112)): 'A SMALL splash: the puddle, with only TWO pieces above it - one arm of '
       'spray on the left and one droplet on the right. Not a fan of droplets; the big crown '
       'comes in the next frame. Earlier attempts threw four or five droplets up.'
       for w in ('hunter', 'cook', 'cowboy', 'space', 'body')},
    ('body', (208, 144)): 'The burst has SHRUNK to one small dense knot of sparks at the centre '
       'right, with a few loose sparks. Not a ring with an empty middle.',
    ('cowboy', (192, 144)): 'An EVEN spread of sparks over the whole square, weighted a little '
       'to the left, already dimming - not a centred starburst with a bright solid core.',
    **{('cowboy', (x, 112)): 'The egg is WHOLE: one smooth unbroken shell, the white band round '
       'it smooth-edged. Nothing is cracked, split, ragged or broken - it does not crack.'
       for x in (272, 304)},
    ('space', (112, 128)): 'The 12 o\'clock hour mark is a SMALL SQUARE, the same size as the '
       'marks at 3, 6 and 9 - not a long bar. The only long hand points to 9.',
    ('cook', (68, 28)): 'The crust is a band running EDGE TO EDGE across the very top of the '
       'square, touching the top edge and both side edges, so tiles join crust to crust.',
    **{('space', (x, 112)): 'A plain flame with NO FACE: the dark flecks are scattered at random '
       'through it, not arranged as eyes and a mouth.' for x in (128, 144)},
}


_copies = {}


def copies(world):
    """{target: source} for every frame built as a copy of its pair partner.

    SAME_AS, the pairs copied by judgement, and every worm pair whose two 1988
    frames are IDENTICAL - 97 per cent or more of their pixels. The pizza worm,
    both space worms and most body segments never move between their two frames
    in 1988; drawn separately, their second frames shimmered where the original
    stands still (#117). Measured here rather than listed, so a sheet that
    changes cannot leave a stale list behind.
    """
    if world in _copies:
        return _copies[world]
    from PIL import ImageChops

    sheet = Image.open(GAME / f'{world}.png').convert('RGBA')

    def tile(pos):
        return sheet.crop((pos[0], pos[1], pos[0] + TILE, pos[1] + TILE))

    def identical(a, b):
        diff = ImageChops.difference(tile(a), tile(b))
        worst = diff.getchannel(0)
        for band in range(1, 4):
            worst = ImageChops.lighter(worst, diff.getchannel(band))
        drawn = ImageChops.lighter(tile(a).getchannel('A'), tile(b).getchannel('A'))
        drawn = drawn.point(lambda v: 255 if v else 0)
        differ = ImageChops.multiply(worst.point(lambda v: 255 if v else 0), drawn)
        return 1 - differ.histogram()[255] / max(1, drawn.histogram()[255])

    found = dict(SAME_AS.get(world, {}))
    flips = mirrors(world)
    for y in (80, 96):
        for x in range(0, 320, 32):
            a, b = (x, y), (x + 16, y)
            if a in flips or b in flips or a in found or b in found:
                continue
            if identical(a, b) >= .97:
                found[b] = a
    # THE HIDDEN WORLDS' OTHER ANIMATIONS (3 October 2026). Their readings found
    # frames the original draws IDENTICALLY - the player's idle B repeating a frame,
    # scuba's enemy holding one frame twice - and a redraw made them differ: the
    # football swapped hands, a light went out. Any frame identical to an earlier
    # frame of its own sequence is a copy of it. Not applied to the 1988 five, whose
    # sheets are shipped and were checked without it.
    if world not in worldlist.first_page():
        for frames in SEQUENCES.values():
            spots = [(x, y) for _, x, y in frames]
            for i, b in enumerate(spots):
                if b in found or b in flips:
                    continue
                for a in spots[:i]:
                    if a == b or a in found or a in flips:
                        continue
                    if identical(a, b) >= .97:
                        found[b] = a
                        break
    # The player's idle frames, across the idle A and idle B animations: in several
    # hidden worlds one repeats an earlier frame of the other exactly (miner's 16 is
    # its 14, scuba's 17 its 12, luck's 19 its 13), and a redraw of the repeat
    # drifted in style from the frame it repeats.
    if world not in worldlist.first_page():
        idle = [tuple(pos) for pos in sprites()['player'][12:]]
        for i, b in enumerate(idle):
            if b in found or b in flips:
                continue
            for a in idle[:i]:
                if a == b or a in found or a in flips:
                    continue
                if identical(a, b) >= .97:
                    found[b] = a
                    break

    # A frame that tools/hidden-edits.json makes as an imager EDIT of its pair partner
    # is drawn, not copied: the edit is how it gets the small movement 1988 gives it
    # (a blink, a wheel turning) that a copy would throw away.
    import edits
    for key, spec in edits.entries().get(world, {}).items():
        if spec.get('op') == 'edit':
            found.pop(tuple(int(v) for v in key.split(',')), None)

    # One hop only: a copy's source must be a tile that is drawn, because that is the
    # one with a file. A position that is both a source and a target of two sequences
    # is followed to the end.
    for target in list(found):
        seen = {target}
        while found[target] in found and found[target] not in seen:
            seen.add(found[target])
            found[target] = found[found[target]]
    _copies[world] = found
    return found


# THE HIDDEN WORLDS' CORRECTIONS (3 October 2026), from the first check of every new
# tile against its reading: what the model got wrong, said in the tile's prompt so the
# second drawing does not repeat it. A data file, because it is machine-written from
# the check and runs to sixty tiles. The 1988 five's are written by hand above.
_FILE = Path(__file__).resolve().parent / 'hidden-corrections.json'
if _FILE.exists():
    for _world, _tiles in json.loads(_FILE.read_text()).items():
        for _pos, _text in _tiles.items():
            _x, _y = (int(v) for v in _pos.split(','))
            CORRECTIONS[(_world, (_x, _y))] = ('An earlier drawing of this tile was wrong: '
                                               f'{_text} Draw it as described above.')


def drawn_from(world, pos):
    """The position whose redraw this position's art is: itself, or what it is a
    copy of."""
    return copies(world).get(pos, pos)


# THE TIMED WALL IS ONE PICTURE SCROLLING. In 1988 its later frames are frame 0
# moved 4, 8 and 12 pixels to the left with wrap-round - an exact pixel match in
# all five worlds - so a redraw of those three can only be worse than the same move
# made on the finished frame 0. Built in the atlas, never generated.
#
# But the HIDDEN worlds scroll differently, and each is measured (3 October 2026):
# music moves 2 pixels a frame, miner moves 4 rows down, scuba moves diagonally, and
# player and luck are not a shift of frame 0 at all. So the direction is FOUND per
# world by shifted(), not typed, and a world whose three frames are not all a shift
# is drawn like any other tile.
#
# target -> the frame it is moved from
SHIFTED = {(164, 4): (132, 4), (196, 4): (132, 4), (228, 4): (132, 4)}

# How many of a tile's 256 pixels may differ and it still be called a shift. The
# 1988 five differ by none, scuba by three or four of its glints; player's are 91
# or more out and luck's 56.
SHIFT_TOLERANCE = 6

_shifted = {}


def shift(tile, dx, dy):
    """The tile moved dx to the left and dy up, wrapping round - so result[y][x] is
    tile[(y + dy) % h][(x + dx) % w]. dy of 12 on a 16-pixel tile is four rows down."""
    w, h = tile.size
    dx, dy = dx % w, dy % h
    out = Image.new(tile.mode, tile.size)
    for ox, ow, sx in ((0, w - dx, dx), (w - dx, dx, 0)):
        for oy, oh, sy in ((0, h - dy, dy), (h - dy, dy, 0)):
            if ow and oh:
                out.paste(tile.crop((sx, sy, sx + ow, sy + oh)), (ox, oy))
    return out


def shifted(world):
    """{target: (source, dx, dy)} for the timed-wall frames this world builds by
    moving frame 0, or {} unless ALL THREE are a shift of it.

    A scrolling wall is ONE step applied again and again: frame k is frame 0 moved
    k steps. So the search is for a single step (sx, sy), and each frame has to be
    close to exact at k times it - not each frame's best shift on its own, which
    body's mouths tile, repeating every eight rows, answers wrongly because several
    shifts tie. Among steps that fit, the best total match wins, then the shortest.
    """
    if world in _shifted:
        return _shifted[world]

    sheet = Image.open(GAME / f'{world}.png').convert('RGBA')
    targets = sorted(SHIFTED)
    (fx, fy) = SHIFTED[targets[0]]
    first = sheet.crop((fx, fy, fx + TILE, fy + TILE))
    frames = [sheet.crop((t[0], t[1], t[0] + TILE, t[1] + TILE)) for t in targets]

    best = None
    for sx in range(TILE):
        for sy in range(TILE):
            if (sx, sy) == (0, 0):
                continue
            misses = [differing(shift(first, k * sx, k * sy), frame)
                      for k, frame in enumerate(frames, 1)]
            if max(misses) > SHIFT_TOLERANCE:
                continue
            length = min(sx, TILE - sx) + min(sy, TILE - sy)
            if best is None or (sum(misses), length) < best[0]:
                best = ((sum(misses), length), sx, sy)

    found = {}
    if best:
        _, sx, sy = best
        found = {t: ((fx, fy), k * sx % TILE, k * sy % TILE) for k, t in enumerate(targets, 1)}
    _shifted[world] = found
    return found


def floor_of(world):
    """The ground colour a world's sheet is painted on, as (r, g, b), from worlds.json."""
    for entry in worldlist.entries():
        if entry['sheet'] == world:
            h = entry['floor'].lstrip('#')
            return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))
    raise KeyError(world)


def has_art(world, tile):
    """Whether a 16x16 tile of this world's sheet draws anything.

    Not `getbbox()` on the alpha: Sahala's scuba, player and music sheets are painted
    on an OPAQUE ground, so every blank tile has pixels - the whole sheet does - and a
    check for a blank position would find every position drawn. A pixel counts when it
    is visible and is not the world's own floor colour. The 1988 sheets and luck and
    miner are transparent, where this is the same answer as before.
    """
    floor = floor_of(world)
    px = tile.convert('RGBA').load()
    return any(px[x, y][3] and px[x, y][:3] != floor
               for y in range(tile.height) for x in range(tile.width))


def built_here(world, pos):
    """Whether this world's tile at pos is made from other tiles in code - flipped,
    copied, shifted, turned or blended - rather than drawn by the model.

    One answer for plan, generate and atlas, so a tile cannot be skipped by one and
    built by another. Each method is measured per world; none is assumed (#107).
    """
    return (pos in mirrors(world) or pos in REFUSED_TILES.get(world, {})
            or pos in copies(world) or pos in shifted(world)
            or tapturn.applies(world, pos) or dissolve.applies(world, pos))


def mirror_of(sheet, x, y):
    """A position whose original tile is this one flipped, if there is one.

    The left- and right-facing poses are drawn as mirror images of each other -
    in the cook world the two push frames differ by 13 percent of their pixels,
    which is the outline moving a little, not a different drawing. So a sprite
    that cannot be generated can be taken from its opposite number and flipped.

    This exists because two cook push poses are blocked by the image API's
    moderation at the OUTPUT stage - not for anything in the prompt, but for
    whatever the model draws from them. Rewording did not help, and the
    alternative was leaving two tiles as upscaled 1988 pixels in an otherwise
    redrawn set.
    """
    from PIL import ImageOps

    mine = sheet.crop((x, y, x + TILE, y + TILE))
    best, score = None, 0.18 * TILE * TILE   # must match to better than 18%

    for (ox, oy) in wanted():
        if (ox, oy) == (x, y) or oy != y:
            continue
        flipped = ImageOps.mirror(sheet.crop((ox, oy, ox + TILE, oy + TILE)))
        differ = sum(1 for a, b in zip(mine.getdata(), flipped.getdata()) if a != b)
        if differ < score:
            best, score = (ox, oy), differ

    return best


def mixed_sequences():
    """Sequences that mix a full-bleed tile with free-standing objects.

    Those two get opposite instructions - "reach every edge of the square with
    no border" against "centred with a little clear space around it" - and a
    sequence anchors every later frame to its first. So one full-bleed member
    does not merely sit oddly in the row: it drags every frame after it into
    being drawn as a wall.

    That is what happened to the bird, and to bonus-count before it. Both were
    found by eye, months apart, and the second was fixed while the first sat
    three lines above it in the same table.
    """
    bad = {}
    for name, frames in SEQUENCES.items():
        kinds = {v in FULL_BLEED for v, _, _ in frames}
        if len(kinds) > 1:
            bad[name] = [(v, x, y) for v, x, y in frames]
    return bad


TILESET = ROOT / 'web/Rockford.Web/TileSet.cs'

# Every sprite table the renderer draws from. Not a list anyone should edit by
# hand - `tables_match_the_renderer` reads TileSet.cs and fails if this is not
# exactly what it declares.
RENDERER_TABLES = ('main', 'player', 'enemyA', 'enemyB', 'groupA', 'groupB')


def tables_match_the_renderer():
    """The tables TileSet.cs declares that RENDERER_TABLES does not cover.

    The coverage check used to read `main` alone and nothing noticed for a day,
    because nothing tied the list to the renderer. Naming all six fixed that
    instance and not the class of it: NOT_DRAWN_YET happens to hold only worm
    positions, so dropping `player` from the list would take player coverage
    away and report nothing at all - every exception would still be accounted
    for. Once the worms are drawn and their exceptions go, the same hole opens
    on groupA and groupB. Astra, 19 September 2026.

    So this reads the declarations rather than trusting the list. `Never` is
    excluded deliberately: it is the values the renderer refuses to draw, which
    is the opposite of a sprite table.
    """
    declared = set(re.findall(r'public required int\[\]\[\] (\w+)',
                              TILESET.read_text()))
    covered = {t.lower() for t in RENDERER_TABLES}
    return sorted(t for t in declared if t.lower() not in covered)


def unbled_descriptions():
    """Full-bleed tiles whose world description makes them an object.

    FULL_BLEED puts "the artwork must reach every edge of the square with no
    border and no rounded corners" into the prompt. A world description saying
    "an elephant's head with tusks and a curled trunk" contradicts that, and
    subject_for lets the world win - so the model was asked for an object and
    for a tile in the same breath, and drew the object.

    Four of them: hunter and space and body's magic wall, and cook's second
    wall, which were a head, a hatch, a headstone and a slice of pizza. Each
    keeps its subject and is a wall of them now.
    """
    bad = []
    for world in WORLDS:
        if world not in worlds.OBJECTS:
            continue    # said once, by name, where verify checks the descriptions
        for (x, y), value in sorted(wanted().items()):
            if value not in FULL_BLEED:
                continue
            said = subject_for(value, x, y, world=world) or ''
            if 'tiles edge to edge' not in said and 'solid' not in said:
                bad.append((world, value, x, y, said))
    return bad


def renderer_positions():
    """Every sprite position the renderer can reach, from ALL of its tables.

    This used to be `sprites()['main']` alone, which is five of the six tables
    short. TileSet.cs draws the player from `player`, the two enemy families
    from `enemyA` and `enemyB`, and the two WORMS from `groupA` and `groupB` -
    values 0xA0 to 0xA7 and 0xB0 to 0xB7, which Simulation.cs writes into the
    grid at lines 321 and 322.

    Checking `main` alone is why nobody noticed the worms were never drawn. See
    NOT_DRAWN_YET.
    """
    s = sprites()
    seen = {}
    for table in RENDERER_TABLES:
        for p in s[table]:
            if p[0] >= 0:
                seen.setdefault((p[0], p[1]), table)
    return list(seen)


# Full-bleed tiles whose redraw does not reach its own edges, per world.
#
# All four of the originals are 100% opaque across their 16 by 16 square. The
# redraws are 52% to 98%, so the cave shows through where 1988 had solid tile -
# hunter's magic wall is a floating elephant head with the cave visible around
# it, and the timed wall's three later frames are the leafy scenes they were
# drawn as while they were called birds.
#
# Bounded exactly like NOT_DRAWN_YET: a seaming tile that is not listed fails
# the build, and a listed tile that turns out to be clean fails it too.
#
# CLOSED, 21 September 2026. It is empty and it stays bounded, so a tile that
# starts seaming again fails the build.
#
# The answer to the art-direction question was neither of the two the issue
# offered. It asked whether a magic wall tile shows one head drawn out to its
# own edges or a repeating field of them. Tiling hunter's original 16x16 three
# by three shows the actual construction: a head sitting on a SOLID VIOLET
# GROUND. The tile is 100% opaque because of the background, not because the
# motif reaches the corners.
#
# So no rewording of "reach every edge" was ever going to work. The redraws were
# drawing the motif and leaving the ground transparent - which is exactly what
# they had been asked for everywhere else - while the generator requested a
# TRANSPARENT background for them in the same breath as the words asked for a
# full square. The request wins over the prose every time.
#
# Two changes, both small: the placing clause names the ground, and a full-bleed
# tile asks the image API for an opaque background. All 35 came back at 100%
# with every edge solid, first time.
WILL_SEAM = {
    'hunter': set(),
    'cook':   set(),
    'cowboy': set(),
    'space':  set(),
    'body':   set(),
}

# Positions the renderer can draw that have no VIP art, named one by one.
#
# EMPTY, and it emptied itself. It held the two worms - 40 positions, blank in
# all five atlases from the day the worms landed on 18 September 2026 until 20
# September, while the faithful build drew them. The check fails on a blank tile
# that is not listed AND on a listed position that turns out to be drawn, so
# finishing the art is what took them out: verify reported "40 entries in
# NOT_DRAWN_YET are drawn now, take them out" in every world, which is the
# bounded-both-ways property doing its job rather than somebody remembering.
#
# Keep it empty. A new entry here is a promise to come back.
NOT_DRAWN_YET: set[tuple[int, int]] = set()



def cmd_drawable():
    """Check no sprite the renderer can draw is blank in an atlas.

    This is the guard for the defect the safety net in cmd_atlas fixes. The list
    of reachable states is hand-maintained, so it will go stale again; what must
    not happen again is a stale list turning into an invisible object.

    A position that is blank in the ORIGINAL sheet is fine - the game draws
    nothing there either. Only a position with pixels in 1988 and none in the
    atlas is a fault.
    """
    positions = renderer_positions()
    faults = 0

    # Before any atlas, because these are faults in the TABLES - true of every
    # world at once, and said once rather than five times.
    # Every drawable position must have a per-world role, or it falls through to
    # the value table and gets told the hunter world's story. Three families
    # were doing exactly that until 20 September 2026.
    # Drawn in ANY world being worked on, each measured against its own sheet.
    # This took hunter's sheet for all of them, so a position only a hidden world
    # draws would have been invisible to the table checks.
    sheets = [Image.open(GAME / f'{w}.png').convert('RGBA') for w in WORLDS]
    drawn = [p for p in renderer_positions()
             if any(has_art(w, s.crop((p[0], p[1], p[0] + TILE, p[1] + TILE)))
                    for w, s in zip(WORLDS, sheets))]
    for problem in worlds.check(drawn):
        faults += 1
        print(problem)

    # A READING FOR EVERY POSITION OF EVERY WORLD BEING DRAWN - rule 1 of "Read
    # the tile before you prompt it" in docs/ROCKFORD-VIP.md. worlds.check covers
    # the five 1988 worlds; this covers whatever WORLDS is, so a hidden world
    # added to the redraw (#110) cannot be prompted from its subject alone,
    # which is how the first set came out facing the wrong way.
    # The bonus ring is drawn on one outline for all forty tiles; if a sheet ever
    # disagrees with it, the ring would sit off the 1988 border.
    if bonusring.check() > .5:
        faults += 1
        print('bonus ring: a border pixel is more than half a pixel off the outline')

    for world in WORLDS:
        if world not in worlds.OBJECTS:
            faults += 1
            print(f'{world}: no entry in worlds.OBJECTS, so it cannot be drawn without '
                  f'borrowing hunter\'s words - describe it first (#108)')
            continue
        unread = sorted(p for p in wanted() if worlds.reading(world, *p) is None)
        if unread:
            faults += 1
            print(f'{world}: {len(unread)} positions to draw with no reading in '
                  f'sprite-readings.json -> {unread[:6]}')

    missed = tables_match_the_renderer()
    if missed:
        faults += len(missed)
        print(f'TileSet.cs draws from {", ".join(missed)} and the coverage check '
              f'does not look at {"it" if len(missed) == 1 else "them"}. '
              f'Add to RENDERER_TABLES.')

    for name, frames in mixed_sequences().items():
        faults += 1
        shown = ' '.join(f'0x{v:02X}' for v, _, _ in frames)
        print(f'{name}: mixes a full-bleed tile with free-standing frames -> {shown}. '
              f'Every later frame is anchored to the first and will be drawn as a wall.')

    for world, value, x, y, said in unbled_descriptions():
        faults += 1
        print(f'{world}: 0x{value:02X} at {x},{y} tiles edge to edge, and is '
              f'described as an object -> "{said[:60]}"')

    for world in WORLDS:
        if no_atlas_yet(world):
            continue
        atlas = GAME / f'{world}-x{SCALE}.png'
        if not atlas.exists():
            print(f'{world}: no atlas, run `atlas` first')
            return 1

        orig = Image.open(GAME / f'{world}.png').convert('RGBA')
        big = Image.open(atlas).convert('RGBA')
        blank = []

        for (x, y) in positions:
            if not has_art(world, orig.crop((x, y, x + TILE, y + TILE))):
                continue        # blank in the original too, which is correct
            side = TILE * SCALE
            if not big.crop((x * SCALE, y * SCALE, x * SCALE + side, y * SCALE + side)).getbbox():
                blank.append((x, y))

        unexpected = [p for p in blank if p not in NOT_DRAWN_YET]
        if unexpected:
            faults += len(unexpected)
            shown = ' '.join(f'{x},{y}' for x, y in unexpected[:8])
            print(f'{world}: {len(unexpected)} drawable sprites are blank -> {shown}')
        else:
            note = f' ({len(blank)} known gaps)' if blank else ''
            print(f'{world}: every drawable sprite has pixels{note}')

        # A full-bleed tile with a transparent edge is a seam in the cave.
        #
        # Measured along the four edges themselves, not from the bounding box.
        # A bbox that touches all four sides says only that SOMETHING is on each
        # edge: hunter's wall at 68,4 passed that test with 122 of its 128
        # bottom-edge pixels fully transparent and the other six at an alpha of
        # 3. Astra, 19 September 2026.
        side = TILE * SCALE
        seams = []
        for (x, y), value in wanted().items():
            if value not in FULL_BLEED:
                continue
            tile = big.crop((x * SCALE, y * SCALE, x * SCALE + side, y * SCALE + side))
            a = tile.getchannel('A')
            edges = (
                [a.getpixel((i, 0)) for i in range(side)],
                [a.getpixel((i, side - 1)) for i in range(side)],
                [a.getpixel((0, i)) for i in range(side)],
                [a.getpixel((side - 1, i)) for i in range(side)],
            )
            thin = sum(1 for e in edges for v in e if v < SOLID)
            if thin:
                seams.append((x, y, thin))

        known = WILL_SEAM.get(world, set())
        unexpected = [s for s in seams if (s[0], s[1]) not in known]
        if unexpected:
            faults += len(unexpected)
            shown = ' '.join(f'{x},{y} ({n} px)' for x, y, n in unexpected)
            print(f'{world}: {len(unexpected)} full-bleed tiles have transparent '
                  f'edge pixels and will seam -> {shown}')

        mended = known - {(x, y) for x, y, _ in seams}
        if mended:
            faults += len(mended)
            shown = ' '.join(f'{x},{y}' for x, y in sorted(mended))
            print(f'{world}: {len(mended)} entries in WILL_SEAM reach their edges '
                  f'now, take them out -> {shown}')

        # The exception has to shrink to nothing, so a position that stops being
        # blank must come off the list rather than sit there for ever excusing a
        # tile that is fine.
        stale = [p for p in NOT_DRAWN_YET if p not in blank]
        if stale:
            faults += len(stale)
            shown = ' '.join(f'{x},{y}' for x, y in stale[:8])
            print(f'{world}: {len(stale)} entries in NOT_DRAWN_YET are drawn now, '
                  f'take them out -> {shown}')

    if faults:
        print(f'\n{faults} blank sprites. VIP would draw nothing where the '
              f'faithful build draws something.')
    return 1 if faults else 0


def crop_boxes(art_for, value_at=None):
    """One crop rectangle per sprite, SHARED by every frame of an animation.

    Each frame used to be cropped to its own non-transparent bounds and then
    stretched to fill the square cell. Two things follow from that, and neither
    is anything the drawings did.

    THE SEQUENCE JITTERS. The boy's bounds are 1125 pixels wide in one walk
    frame and 1080 in the next, so the first arrives scaled by 0.114 and the
    second by 0.119 - he is 4% wider in one frame than the next and shifts
    sideways between them. The idle cycle is worse: frame 4 of 8 is 1008 wide
    against frame 1's 1095, an 8% jump, eight times a second.

    AND EVERY FRAME IS DISTORTED. The axes are scaled independently, so bounds
    of 1125 by 1254 become 0.114 across and 0.102 down - the boy drawn 11%
    fatter than he was drawn. That happens to every sprite in the atlas, not
    only the animated ones.

    One box per animation fixes both: the union of every frame's bounds, made
    square about its centre so one scale serves both axes. A sprite in no
    sequence is an animation of one and takes the same path, which is what
    removes the distortion from the rest of the sheet.

    The box can fall outside the image, and that is correct - PIL pads the crop
    with transparency, which is what a smaller frame's margin should be.

    FULL_BLEED is the exception and it has to be. Those tiles butt edge to edge
    and the cave shows seams if they do not fill their square, so they take the
    whole image and no crop at all. Today's drawings happen to bleed anyway - a
    1254 by 1254 bbox on all four - so this changes nothing now, and that is the
    point of writing it down: the next one that comes back 54 pixels short would
    otherwise get a transparent margin and a seam, quietly.
    """
    value_at = value_at or wanted()
    # Keyed by POSITION, not by runtime value. Two values can share a position -
    # 'boulder' is 0x44 and 0x16 at the same (0, 64) - and the atlas paints
    # positions, so one tile is one entry.
    sequence_at = {}
    for name, frames in SEQUENCES.items():
        for (_, x, y) in frames:
            sequence_at[(x, y)] = name

    unions = {}
    for (x, y), art in art_for.items():
        if value_at.get((x, y)) in FULL_BLEED:
            continue

        box = art.getbbox()
        if not box:
            continue

        key = sequence_at.get((x, y), (x, y))
        if key not in unions:
            unions[key] = box
        else:
            l, t, r, b = unions[key]
            unions[key] = (min(l, box[0]), min(t, box[1]),
                           max(r, box[2]), max(b, box[3]))

    out = {}
    for (x, y), art in art_for.items():
        if value_at.get((x, y)) in FULL_BLEED:
            out[(x, y)] = (0, 0, art.width, art.height)
            continue

        key = sequence_at.get((x, y), (x, y))
        box = unions.get(key)

        # A frame that is entirely transparent and belongs to no sequence has no
        # bounds of its own to take. The whole image is the honest answer: there
        # is nothing to crop to.
        if box is None:
            out[(x, y)] = (0, 0, art.width, art.height)
            continue

        l, t, r, b = box
        side = max(r - l, b - t)
        left = round((l + r) / 2 - side / 2)
        top = round((t + b) / 2 - side / 2)
        out[(x, y)] = (left, top, left + side, top + side)

    return out


def solid_box(tile, floor=SOLID):
    """The bounds of the part of a tile that is actually THERE.

    `getbbox` answers on any alpha above zero, which is not the same question.
    Hunter's wall at 68,4 has 122 of its 128 bottom-edge pixels completely
    transparent and the other six at an alpha of 3 out of 255 - so its bbox
    reaches the bottom edge, and the tile still shows the cave behind it along
    that whole row. Astra, 19 September 2026.
    """
    mask = tile.getchannel('A').point(lambda v: 255 if v >= floor else 0)
    return mask.getbbox()


# TILES DRAWN ON THE WRONG GROUND, put right in the atlas rather than drawn again.
#
# Cook's steel wall is cutlery on a flat grey ground in 1988. Drawn three times it
# came back on a white card, however the prompt said grey (#117), so the white
# that is connected to the tile's edge is filled with the 1988 ground colour. The
# cutlery is outlined in dark, so the fill stops at it.
GROUND_FILL = {('cook', (68, 4)): (117, 117, 138)}


def fill_ground(art, colour, floor=205):
    """Near-white pixels connected to the tile's edge become `colour`."""
    art = art.convert('RGBA')
    w, h = art.size
    px = art.load()

    def white(x, y):
        r, g, b, _ = px[x, y]
        return min(r, g, b) >= floor and max(r, g, b) - min(r, g, b) < 30

    seen, stack = set(), [(x, y) for x in range(w) for y in (0, h - 1)] \
        + [(x, y) for y in range(h) for x in (0, w - 1)]
    while stack:
        x, y = stack.pop()
        if (x, y) in seen or not (0 <= x < w and 0 <= y < h) or not white(x, y):
            continue
        seen.add((x, y))
        px[x, y] = colour + (255,)
        stack += [(x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)]
    return art


# TILES DRAWN THE WRONG SIZE, fitted to where the 1988 tile puts them.
#
# Hunter's serpent tails are a short smooth stub in 1988 - half a tile, thick end
# against one edge - and came back as a full-tile plume every time, however the
# prompt said stub (#117). The drawing is kept and scaled to the bounding box of
# its own 1988 tile, so its size and its joined edge are the original's. It is
# stretched to that box, not fitted inside it, which left the tail short of the edge.
FIT_TO_ORIGINAL = {('hunter', (x, 80)) for x in range(128, 256, 16)}


def fit_to_original(art, original):
    """`art` scaled, aspect kept, to sit inside the bounding box of `original`'s
    drawn pixels, centred in it, on a square the size of `art`."""
    box = original.getchannel('A').getbbox()
    if not box:
        return art
    scale = art.width / original.width
    bx0, by0, bx1, by1 = (round(v * scale) for v in box)
    own = art.getchannel('A').getbbox()
    if not own:
        return art
    piece = art.crop(own)
    # Stretched to the box on both axes, not scaled to fit inside it: a stub
    # that stops two pixels short of its joining edge leaves a gap in the worm.
    piece = piece.resize((max(1, bx1 - bx0), max(1, by1 - by0)), Image.LANCZOS)
    out = Image.new('RGBA', art.size, (0, 0, 0, 0))
    out.alpha_composite(piece, (bx0, by0))
    return out


def bleed(tile):
    """Push a full-bleed tile out to its own edges, WITHOUT scaling it.

    A tile that butts against its neighbours has to fill its square or the cave
    shows a hairline of background between them. The drawings very nearly do,
    and nearly is not enough.

    The solid part is cropped out and its edge rows and columns are REPLICATED
    into the margin. The first version cropped and resized back to the square,
    which fills the tile and is also the exact unequal-axis stretch the rest of
    this change exists to remove - 128 by 127 back to 128 by 128 is 0.8% of it.
    Replication is what bleed means and it scales nothing.

    Done at tile size rather than at source size, deliberately. At source size
    the missing rows carry an alpha of 1 or 2, so there is something there to
    find; it is only after the reduction averages them to nothing that the gap
    exists to close.
    """
    w, h = tile.size
    box = solid_box(tile)
    if not box or box == (0, 0, w, h):
        return tile

    l, t, r, b = box
    core = tile.crop(box)

    out = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    out.paste(core, (l, t))

    # Sideways first, then down the full width, so the corners are covered by
    # the second pass rather than needing a case of their own. A one-pixel strip
    # resized with NEAREST is replication exactly.
    if l > 0:
        edge = core.crop((0, 0, 1, core.height))
        out.paste(edge.resize((l, core.height), Image.NEAREST), (0, t))
    if r < w:
        edge = core.crop((core.width - 1, 0, core.width, core.height))
        out.paste(edge.resize((w - r, core.height), Image.NEAREST), (r, t))
    if t > 0:
        edge = out.crop((0, t, w, t + 1))
        out.paste(edge.resize((w, t), Image.NEAREST), (0, 0))
    if b < h:
        edge = out.crop((0, b - 1, w, b))
        out.paste(edge.resize((w, h - b), Image.NEAREST), (0, b))

    return out


def cmd_atlas():
    """Assemble the sheets, keeping the original layout so the coordinate tables
    still work once multiplied by SCALE."""
    from PIL import ImageOps

    for world in WORLDS:
        sheet = Image.open(GAME / f'{world}.png').convert('RGBA')

        # Start from the whole original sheet, scaled up, rather than from nothing.
        #
        # The atlas used to begin transparent and paint only the positions in
        # wanted(), which is built from the authored cave bytes plus a hand-kept
        # list of produced states. Anything the rules can reach but that list does
        # not name was therefore left as a hole - not upscaled pixels, nothing at
        # all. 22 sprites were blank in all five worlds on that count, the acid and
        # fire chains among them, so VIP simply did not draw objects the faithful
        # build drew.
        #
        # A safety net is the right shape for this. The list of reachable states is
        # hand-maintained and will be wrong again; a missing entry should cost a
        # tile that looks like 1988 rather than one that is not there.
        #
        # Nearest-neighbour keeps the original pixels and their transparency, and
        # the renderer only ever samples rectangles inside the sheet, so nothing
        # that should stay hidden becomes visible by doing this.
        # A TRANSPARENT canvas, not the original enlarged.
        #
        # This used to start from the whole sheet upscaled with nearest
        # neighbour, as a safety net for tiles nobody had redrawn. Astra found
        # what that actually meant on 18 September 2026: fourteen complete sprite
        # rectangles in every one of the five atlases were pixel-identical to the
        # original enlarged, and they shipped inside a build whose entire claim is
        # that its artwork is its own. A filename check cannot see that, which is
        # why the assembly's guard never caught it.
        #
        # The net is still there and it is still the right shape. It is just made
        # of THIS project's art now - see borrow() - and a tile with no art and no
        # substitute stops the build rather than quietly becoming 1988 pixels.
        big = Image.new('RGBA', (sheet.width * SCALE, sheet.height * SCALE), (0, 0, 0, 0))

        # EVERY tile resolved first, because the crop is decided per ANIMATION
        # and an animation's frames cannot be cropped until all of them are in
        # hand. See crop_boxes.
        art_for = {}
        done = mirrored = fallback = 0
        for (x, y) in wanted():
            _, out = paths(world, x, y)
            art = None

            if ((x, y) in shifted(world) or dissolve.applies(world, (x, y))
                    or tapturn.applies(world, (x, y))):
                continue   # built from finished tiles, after the paste below

            if (x, y) in mirrors(world):
                _, source = paths(world, *drawn_from(world, mirrors(world)[(x, y)]))
                if source.exists():
                    art = ImageOps.mirror(Image.open(source).convert('RGBA'))
                    mirrored += 1

            stand_in = REFUSED_TILES.get(world, {}).get((x, y))
            if art is None and stand_in:
                (sx, sy), how = stand_in
                _, source = paths(world, *drawn_from(world, (sx, sy)))
                if source.exists():
                    art = Image.open(source).convert('RGBA')
                    art = ImageOps.mirror(art) if how == 'mirror' else art.rotate(180)
                    mirrored += 1

            if art is None and (x, y) in copies(world):
                _, source = paths(world, *drawn_from(world, (x, y)))
                if source.exists():
                    art = Image.open(source).convert('RGBA')
                    mirrored += 1

            if art is not None:
                pass
            elif out.exists():
                art = Image.open(out).convert('RGBA')
                done += 1
            else:
                twin = mirror_of(sheet, x, y)
                if twin:
                    _, twin_out = paths(world, *twin)
                    if twin_out.exists():
                        art = ImageOps.mirror(Image.open(twin_out).convert('RGBA'))
                        mirrored += 1

            if art is None:
                # Not redrawn and no mirror twin, so borrow from THIS project's
                # art for the state this one is on its way to being.
                stand_in = SUBSTITUTE.get((x, y))
                if stand_in:
                    _, lend = paths(world, *stand_in)
                    if lend.exists():
                        art = Image.open(lend).convert('RGBA')
                        fallback += 1

            if art is None:
                raise SystemExit(
                    f'{world}: no art and no substitute for the tile at {x},{y}. '
                    'Redraw it, or give it a SUBSTITUTE - it must not fall back '
                    'to the original.')

            art_for[(x, y)] = art

        side = TILE * SCALE
        values = wanted()
        boxes = crop_boxes(art_for, values)
        for (x, y), art in art_for.items():
            art = art.crop(boxes[(x, y)]).resize((side, side), Image.LANCZOS)
            if values.get((x, y)) in FULL_BLEED:
                art = bleed(art)
            if (world, (x, y)) in FIT_TO_ORIGINAL:
                art = fit_to_original(art, sheet.crop((x, y, x + TILE, y + TILE)))
            if (world, (x, y)) in GROUND_FILL:
                art = fill_ground(art, GROUND_FILL[(world, (x, y))])
            big.paste(art, (x * SCALE, y * SCALE))

        # The timed wall's later frames: its finished frame 0, moved left.
        for (x, y), ((fx, fy), dx, dy) in shifted(world).items():
            frame0 = big.crop((fx * SCALE, fy * SCALE, fx * SCALE + side, fy * SCALE + side))
            big.paste(shift(frame0, dx * SCALE, dy * SCALE), (x * SCALE, y * SCALE))

        # The taps' later frames: frame 0 with the handle turned. See tapturn.py.
        for (x, y), (fx, fy) in sorted(tapturn.POSITIONS.items()):
            if not tapturn.applies(world, (x, y)):
                continue
            first = big.crop((fx * SCALE, fy * SCALE, fx * SCALE + side, fy * SCALE + side))
            where = tapturn.EDGE[y]
            ratio = (tapturn.handle_width(sheet.crop((x, y, x + TILE, y + TILE)), where)
                     / tapturn.handle_width(sheet.crop((fx, fy, fx + TILE, fy + TILE)), where))
            big.paste(tapturn.frame(first, where, ratio), (x * SCALE, y * SCALE))

        # The transformations, dissolved from their finished end tiles. See
        # dissolve.py.
        for (x, y) in sorted(p for p in dissolve.POSITIONS if dissolve.applies(world, p)):
            first, second = (big.crop((px * SCALE, py * SCALE, px * SCALE + side, py * SCALE + side))
                             for px, py in dissolve.ENDS[y])
            big.paste(dissolve.frame(first, second, x, y, world), (x * SCALE, y * SCALE))

        # The bonus borders, drawn in code over the redrawn centres - the one part
        # of those tiles a model could not hold still. See bonusring.py.
        for (x, y) in sorted(bonusring.POSITIONS if bonusring.fits(world) else []):
            vip = big.crop((x * SCALE, y * SCALE, x * SCALE + side, y * SCALE + side))
            original = sheet.crop((x, y, x + TILE, y + TILE))
            big.paste(bonusring.frame(original, vip, x), (x * SCALE, y * SCALE))

        target = GAME / f'{world}-x{SCALE}.png'
        big.save(target, optimize=True)
        note = f'{done}/{len(wanted())} redrawn'
        if mirrored:
            note += f', {mirrored} mirrored'
        if fallback:
            note += f', {fallback} borrowed'
        print(f'{world}: {note} -> {target.name}')


def cmd_contact():
    """A contact sheet per world: every animation, frames left to right.

    The thing nobody could do before was LOOK at a sequence. The frames are 128
    pixels square, scattered across a 2560-wide atlas by coordinates that come
    from a 1988 layout, so checking whether a walk cycle agrees with itself
    meant opening the atlas and hunting. Disagreement between frames does not
    read as style, it reads as the picture flickering - and it was invisible to
    every check here, all of which test that a tile has pixels rather than that
    it matches the tile beside it.

    Written to build/contact/. Not published and not committed: it is a thing to
    look at while deciding what to redraw.
    """
    side = TILE * SCALE
    pad = 8
    label = 110

    out_dir = ROOT / 'build/contact'
    out_dir.mkdir(parents=True, exist_ok=True)

    for world in WORLDS:
        if no_atlas_yet(world):
            continue
        atlas = GAME / f'{world}-x{SCALE}.png'
        if not atlas.exists():
            print(f'{world}: no atlas, run `atlas` first')
            return 1
        big = Image.open(atlas).convert('RGBA')

        rows = list(SEQUENCES.items())
        widest = max(len(frames) for _, frames in rows)
        sheet = Image.new('RGBA',
                          (label + widest * (side + pad) + pad,
                           len(rows) * (side + pad) + pad),
                          (24, 24, 28, 255))

        from PIL import ImageDraw
        draw = ImageDraw.Draw(sheet)

        for row, (name, frames) in enumerate(rows):
            top = pad + row * (side + pad)
            draw.text((pad, top + side // 2 - 6), name, fill=(220, 220, 220, 255))

            for col, (_, x, y) in enumerate(frames):
                tile = big.crop((x * SCALE, y * SCALE,
                                 x * SCALE + side, y * SCALE + side))
                sheet.alpha_composite(tile, (label + pad + col * (side + pad), top))

        target = out_dir / f'{world}-sequences.png'
        sheet.save(target, optimize=True)
        print(f'{world}: {len(rows)} sequences -> {target}')

    return 0


def cmd_selftest():
    """The arithmetic in crop_boxes and bleed, which no check reached.

    Both were reviewed on the source and both had a fault in them that reading
    did not catch - bleed scaled by 128/127 while claiming not to scale, and the
    edge test answered on a bounding box. These are cheap and they run in CI
    without any game files.
    """
    def art(w, h, box, alpha=255):
        im = Image.new('RGBA', (w, h), (0, 0, 0, 0))
        im.paste((10, 20, 30, alpha), box)
        return im

    fails = []

    def ok(what, got, want):
        if got != want:
            fails.append(f'{what}: got {got}, wanted {want}')

    # The timed wall moves LEFT and wraps. The 1988 frames are frame 0 with each
    # column taken from 4, 8 and 12 to its right, so column 0 of the result is
    # column px of the source and the source's first columns land at the end.
    strip = Image.new('RGBA', (16, 1), (0, 0, 0, 0))
    strip.putpixel((0, 0), (255, 0, 0, 255))
    strip.putpixel((4, 0), (0, 255, 0, 255))
    moved = shift(strip, 4, 0)
    ok('shift: column 4 arrives at 0', moved.getpixel((0, 0)), (0, 255, 0, 255))
    ok('shift: column 0 wraps to 12', moved.getpixel((12, 0)), (255, 0, 0, 255))
    column = Image.new('RGBA', (1, 16), (0, 0, 0, 0))
    column.putpixel((0, 0), (255, 0, 0, 255))
    ok('shift: dy 12 is four rows down', shift(column, 0, 12).getpixel((0, 4)), (255, 0, 0, 255))

    # OPAQUE TILES ARE NOT ALL ALIKE. differing() once asked getbbox() of the
    # difference, which for RGBA looks only at alpha, so two different opaque tiles
    # compared equal and every world "shifted" its timed wall.
    red = Image.new('RGBA', (16, 16), (255, 0, 0, 255))
    blue = Image.new('RGBA', (16, 16), (0, 0, 255, 255))
    ok('differing sees two opaque tiles differ', differing(red, blue), 256)
    ok('differing sees a tile equal to itself', differing(red, red), 0)

    # Every frame of a sequence gets ONE box, and it is square.
    frames = {(0, 48): art(100, 100, (10, 0, 90, 100)),
              (16, 48): art(100, 100, (30, 5, 70, 95))}
    boxes = crop_boxes(frames, {(0, 48): 0x10, (16, 48): 0x10})
    ok('both walk frames share a box', boxes[(0, 48)], boxes[(16, 48)])
    l, t, r, b = boxes[(0, 48)]
    ok('the shared box is square', r - l, b - t)

    # It covers the UNION - 10..90 with 30..70 - rather than either frame's own
    # bounds, and is centred on it. Squaring can then widen it past the union,
    # which is the point: the union here is 80 by 100 and the box is 100 by 100,
    # so the frames are scaled equally on both axes instead of being squashed
    # 20% sideways to fit a square cell.
    ok('the box covers the union', l <= 10 and r >= 90, True)
    ok('the box is centred on the union', (l + r) / 2, 50)

    # A sprite in no sequence is an animation of one, and still square.
    lone = {(0, 0): art(100, 100, (20, 40, 80, 60))}
    l, t, r, b = crop_boxes(lone, {(0, 0): 0x44})[(0, 0)]
    ok('a lone sprite is squared too', r - l, b - t)
    ok('and centred on its own bounds', (l + r) / 2, 50)

    # A full-bleed tile takes the whole image, crop or no crop.
    bled = {(36, 4): art(100, 100, (10, 10, 90, 90))}
    ok('full bleed takes the whole image',
       crop_boxes(bled, {(36, 4): 0x01})[(36, 4)], (0, 0, 100, 100))

    # An entirely empty frame does not crash and does not invent bounds.
    empty = {(0, 0): Image.new('RGBA', (60, 40), (0, 0, 0, 0))}
    ok('an empty sprite takes its whole image',
       crop_boxes(empty, {(0, 0): 0x44})[(0, 0)], (0, 0, 60, 40))

    # bleed fills the edges and SCALES NOTHING. A 40x39 solid block in a 40x40
    # tile must come back 40x40 with its content the same size, not stretched.
    short = art(40, 40, (0, 0, 40, 39))
    out = bleed(short)
    ok('bleed keeps the tile size', out.size, (40, 40))
    ok('bleed reaches the bottom edge', out.getpixel((20, 39))[3], 255)
    ok('bleed did not move the content', out.getpixel((20, 0))[:3], (10, 20, 30))

    # A row of near-transparent pixels is not an edge. This is the case that
    # passed the first version of the check.
    faint = art(40, 40, (0, 0, 40, 39))
    faint.paste((10, 20, 30, 3), (0, 39, 40, 40))
    ok('a faint row does not count as solid', solid_box(faint), (0, 0, 40, 39))
    ok('and bleed closes it', bleed(faint).getpixel((20, 39))[3], 255)

    # THE GROUND (#110). Only the floor colour connected to the edge goes, and a
    # floor-coloured pixel enclosed by the object stays.
    ring = Image.new('RGBA', (16, 16), (0, 0, 170, 255))
    for x in range(5, 11):
        for y in range(5, 11):
            ring.putpixel((x, y), (255, 0, 0, 255))
    ring.putpixel((7, 7), (0, 0, 170, 255))
    cleared = ground_to_transparent(ring, (0, 0, 170))
    ok('the ground round an object goes', cleared.getpixel((0, 0))[3], 0)
    ok('the object stays', cleared.getpixel((5, 5)), (255, 0, 0, 255))
    ok('a floor-coloured pixel inside the object stays', cleared.getpixel((7, 7)), (0, 0, 170, 255))

    # A tile that already fills its square is returned untouched.
    full = art(40, 40, (0, 0, 40, 40))
    ok('a filled tile is left alone', bleed(full).tobytes(), full.tobytes())

    # And the tables agree with the renderer and with themselves.
    ok('every renderer table is covered', tables_match_the_renderer(), [])
    ok('no sequence mixes bleed with objects', list(mixed_sequences()), [])

    # THE WORLD LIST (#107). One list, from worlds.json: the ten, in the menu's
    # order, and every tool's working set a subset of it.
    menu = ['hunter', 'cook', 'cowboy', 'space', 'body',
            'scuba', 'player', 'music', 'luck', 'miner']
    ok('the world list is the ten, in menu order', worldlist.all_worlds(), menu)
    ok('the working worlds are drawn from the ten', [w for w in WORLDS if w not in menu], [])
    ok('the first page is the five the 1988 menu shows', worldlist.first_page(), menu[:5])

    # THE CODE-BUILT METHODS ARE MEASURED PER WORLD (#110). All three timed-wall
    # shifts hold in each of the 1988 five, scuba's re-rolled colours are not a shift,
    # and the taps, dissolves and bonus rings apply to the five and not to scuba.
    ok('every 1988 world shifts its timed wall left', [sorted({(d[1], d[2]) for d in shifted(w).values()}) for w in menu[:5]], [[(4, 0), (8, 0), (12, 0)]] * 5)
    ok('player and luck do not shift their timed walls', [len(shifted(w)) for w in ('player', 'luck')], [0, 0])
    ok('miner\'s timed wall moves down', sorted(d[2] for d in shifted('miner').values()), [4, 8, 12])
    ok('music\'s timed wall moves two pixels a frame', sorted(d[1] for d in shifted('music').values()), [2, 4, 6])
    ok('the taps turn in the 1988 five', [tapturn.applies(w, (272, 176)) for w in menu[:5]], [True] * 5)
    ok('scuba is not squeezed into a tap turn', tapturn.applies('scuba', (272, 64)), False)
    ok('a hidden world is not dissolved until measured', dissolve.applies('music', (240, 160)), False)
    ok('a world\'s own ground colour is not art',
       has_art('scuba', Image.new('RGBA', (16, 16), floor_of('scuba') + (255,))), False)

    # The edit operations (#124).
    import edits
    ok('the edit operations behave', edits.selftest(), 0)

    # A world the prompt tables do not know is an error, not hunter's words.
    try:
        subject_for(0x44, 0, 64, world='no-such-world')
        refused = False
    except ValueError as err:
        refused = 'no-such-world' in str(err)
    ok('an undescribed world is refused, naming the world', refused, True)

    # And a name that is not one of the ten cannot be asked for.
    try:
        os.environ['ROCKFORD_WORLDS'] = 'hunter,narnia'
        worldlist.working()
        named = False
    except SystemExit as err:
        named = 'narnia' in str(err)
    finally:
        del os.environ['ROCKFORD_WORLDS']
    ok('a world that is not one of the ten is refused', named, True)

    for line in fails:
        print(f'  FAIL  {line}')
    print(f'hires: {"all good" if not fails else f"{len(fails)} failed"}')
    return 1 if fails else 0


def no_atlas_yet(world):
    """True when `world` is being worked on but has no atlas to check yet.

    A world the manifest says HAS an atlas and whose file is missing is a fault,
    and falls through to the check that reports it. A world only asked for by name
    (ROCKFORD_WORLDS) is not: it is being drawn, so verify says it is skipped and
    carries on, which is what lets it pass while the hidden five are undrawn (#107).
    """
    if (GAME / f'{world}-x{SCALE}.png').exists() or world in worldlist.with_atlas():
        return False
    print(f'{world}: no atlas yet, skipped')
    return True


def cmd_unleaked():
    """Fails if any atlas tile is the original's pixels enlarged.

    A filename check cannot see this and the site assembly's guard is a filename
    check, which is how fourteen rectangles per atlas shipped unnoticed. This
    compares pixels, so it sees what a lawyer would.
    """
    import numpy as np

    total = 0
    for world in WORLDS:
        if no_atlas_yet(world):
            continue
        original = Image.open(GAME / f'{world}.png').convert('RGBA')
        atlas = Image.open(GAME / f'{world}-x{SCALE}.png').convert('RGBA')

        enlarged = np.asarray(original.resize(
            (original.width * SCALE, original.height * SCALE), Image.NEAREST))
        mine = np.asarray(atlas)

        copied = []
        for y in range(0, original.height - TILE + 1, TILE):
            for x in range(0, original.width - TILE + 1, TILE):
                a = enlarged[y * SCALE:(y + TILE) * SCALE, x * SCALE:(x + TILE) * SCALE]
                b = mine[y * SCALE:(y + TILE) * SCALE, x * SCALE:(x + TILE) * SCALE]

                # A tile that is blank in the original is blank in both and says
                # nothing about whose artwork this is.
                if a[..., 3].max() == 0:
                    continue
                if np.array_equal(a, b):
                    copied.append((x, y))

        if copied:
            print(f'{world}: {len(copied)} tiles are the original enlarged: {copied}')
        total += len(copied)

    if total:
        raise SystemExit(f'{total} tiles across the atlases are the original\'s pixels')

    print(f'atlases: no original pixels in any of the {len(WORLDS)}')


if __name__ == '__main__':
    action = sys.argv[1] if len(sys.argv) > 1 else 'plan'
    WORK.mkdir(parents=True, exist_ok=True)

    if action == 'verify':
        # Both, and they check different things. They were both called
        # cmd_verify, so the second shadowed the first: `verify` ran the leak
        # check alone and the blank-sprite guard had never run once. There was a
        # second `elif action == 'verify'` further down for the same reason,
        # unreachable behind this branch. Neither is an error in Python, so
        # nothing said a word.
        cmd_unleaked()                  # raises if any tile is the original
        sys.exit(cmd_drawable())
    elif action == 'extract':
        cmd_extract()
    elif action == 'generate':
        cmd_generate(sys.argv[2] if len(sys.argv) > 2 else None)
    elif action == 'atlas':
        cmd_atlas()
    elif action == 'contact':
        sys.exit(cmd_contact())
    elif action in ('selftest', '--selftest'):
        sys.exit(cmd_selftest())
    else:
        cmd_plan()
