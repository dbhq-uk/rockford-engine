#!/usr/bin/env python3
"""What every sprite actually depicts, per world.

The five worlds are not recolours of one another. The same runtime value is drawn
as a different themed object in each, and that is the whole point of them: a
boulder is a rock in the hunter world, an orange in the kitchen, a cactus in the
west, the planet Earth in space and an eyeball inside the body.

A first pass at the sprite redraw keyed its descriptions on the runtime value
alone, so every world was told the hunter world's story. The kitchen's fried egg
was drawn as a monkey, its cutlery as mossy stone, its corn cobs as toucans.
Roughly 235 of the 315 sprites were generated against a description of a
different object.

The naming here is checked against three sources rather than guessed from the
art:

  * The game's own executable, which lists ten world files - SPACE, COOK, COWBOY,
    BODY, LUCK, HUNTER, MINER, MUSIC, PLAYER, SCUBA - and ten treasure names:
    GOLD, GEMS, APPLES, CUPS, COINS, NOTES, SUNS, CLUBS, HEARTS, JEWELS. Five
    worlds shipped; the other five exist only as names and cave data.
  * The Amiga manual, which describes each world's objects by name: "normal walls
    made up of toucans and magic walls of elephants", "walls are corn cobs or
    cutlery", and so on.
  * The decoded sheets themselves, read tile by tile.

Where the manual and the art disagree, the art wins - it is what the game draws.
"""
import json
from pathlib import Path

# Position -> what that position is FOR. The role is the same in every world; only
# the object filling it changes. Holding it this way means ~20 roles times five
# worlds rather than 235 separate descriptions, and a mistake is visible because
# the five entries for a role sit next to each other.
ROLES = {
    (0, 64): 'boulder',
    (36, 4): 'dirt',
    (68, 4): 'special-wall',
    (68, 28): 'wall-b',
    (132, 4): 'wall-1', (164, 4): 'wall-2', (196, 4): 'wall-3', (228, 4): 'wall-4',
    (192, 24): 'growth-a', (256, 24): 'growth-b',
    (256, 128): 'treasure',
    (64, 64): 'enemy-a', (80, 64): 'enemy-a', (96, 64): 'enemy-a', (112, 64): 'enemy-a',
    (128, 64): 'enemy-a', (144, 64): 'enemy-a', (160, 64): 'enemy-a', (176, 64): 'enemy-a',
    (192, 64): 'enemy-b', (208, 64): 'enemy-b', (224, 64): 'enemy-b', (240, 64): 'enemy-b',
    (256, 144): 'treasure-blast', (272, 144): 'treasure-blast', (288, 144): 'treasure-blast',
    # The LAST frame of the blast, not the treasure. It was listed as 'treasure'
    # and prompted as one, but the tile is a rougher picture than (256,128) and
    # ends the chain the three frames before it start. Read off the tiles, 29
    # September 2026.
    (304, 144): 'treasure-blast',
    (192, 144): 'spark', (208, 144): 'spark', (224, 144): 'spark', (240, 144): 'spark',
    # The first frame of each tap. Both were 'misc' - "a jungle object" - so
    # the fire tap's first frame came back as a BIRD in all five worlds, its
    # upright neck read as a head, and the acid tap's as a tangle of roots.
    # They are frame 0 of the same four-frame turn as the three after them.
    (256, 176): 'tap', (256, 64): 'lamp',
    (272, 48): 'player', (288, 48): 'player', (304, 48): 'player',

    # The taps and what they emit. These were missing entirely - the atlas never
    # covered them, so VIP drew nothing where the faithful build drew fire and
    # acid falling through a cave.
    #
    # The manual names the pair: a tap that produces acid and a magic lamp that
    # produces fire, and they explode when they meet. Each world dresses them
    # differently, and the body world is the one the manual singles out - there
    # the taps drip blood.
    (272, 176): 'tap', (288, 176): 'tap', (304, 176): 'tap',
    (272, 64): 'lamp', (288, 64): 'lamp', (304, 64): 'lamp',

    # The drop falling, breaking up, and settling into a pool.
    (0, 112): 'drip', (16, 112): 'drip',
    (32, 112): 'drip-breaking', (48, 112): 'drip-breaking',
    (64, 112): 'drip-pool', (80, 112): 'drip-pool',

    # What the lamp throws upward.
    (128, 112): 'flame', (144, 112): 'flame',
    (160, 112): 'flame', (176, 112): 'flame',

    # The two bonus collectibles, each a framed badge cycling through its own
    # animation. One counts 0, 1, 2, 3 - the digit is what it pays - and the
    # other turns a pale shape inside the same frame.
    #
    # Both used to start at the second frame. (0,128) was listed above as
    # `exit`, which it is not: the exit is the door chain at (192,112), and
    # (0,128) is the first frame of the count. So the treasure bonus was drawn
    # as a doorway for one frame out of four. (64,128) was in no role at all
    # and fell through to the value table, which described it the same way in
    # all five worlds while its own next three frames were per-world.
    (0, 128): 'bonus-count', (16, 128): 'bonus-count',
    (32, 128): 'bonus-count', (48, 128): 'bonus-count',
    (64, 128): 'bonus-turn', (80, 128): 'bonus-turn',
    (96, 128): 'bonus-turn', (112, 128): 'bonus-turn',
}

# THE TWO WORMS, which had no roles at all and so no art in any world - 40
# positions blank in all five atlases while the faithful build drew them (#73).
#
# They are what the two eggs hatch into: segmented creatures that crawl, with a
# head, a repeating body segment and a tail. TileSet.Grouped indexes them as
# three groups of eight, each group four facings by two frames, which is what
# the bands below are:
#
#   x=0..112    the head, four facings
#   x=128..240  the tail, four facings
#   x=256..304  the body segment, which only has two distinct pairs because
#               left reuses right and up reuses down
#
# Every one of them is drawn in the original sheets and always was. There was
# nothing to invent here - these descriptions are read off the decoded tiles the
# same way the other eighty-five were.
for _x in (0, 16, 32, 48, 64, 80, 96, 112):
    ROLES[(_x, 96)] = 'wormA-head'
    ROLES[(_x, 80)] = 'wormB-head'
for _x in (128, 144, 160, 176, 192, 208, 224, 240):
    ROLES[(_x, 96)] = 'wormA-tail'
    ROLES[(_x, 80)] = 'wormB-tail'
for _x in (256, 272, 288, 304):
    ROLES[(_x, 96)] = 'wormA-body'
    ROLES[(_x, 80)] = 'wormB-body'

# SIX MORE FAMILIES THAT HAD NO ROLE, so each fell through to the value table
# and was described the same way in all five worlds - the exact failure the
# header of this file is about, still running in twenty-one positions on 20
# September 2026. They are per-world objects: the exit is a brown door in the
# jungle and a blue one in space, the egg is a different egg in every world, and
# the two transformation chains end in that world's own boulder and treasure.
#
# Found by asking which renderer positions had no role rather than by noticing a
# bad sprite, which is the only way this kind of gap ever turns up.
ROLES[(192, 112)] = 'exit-shut'
for _x in (208, 224, 240):
    ROLES[(_x, 112)] = 'exit-open'

# The two eggs, which hatch into the two worms. 0x7C already had a role - the
# generic 'misc' - and its own three frames had none, so one egg was described
# as "a jungle object" and the rest as nothing at all.
for _x in (192, 208, 224, 240):
    ROLES[(_x, 128)] = 'eggA'
for _x in (256, 272, 288, 304):
    ROLES[(_x, 112)] = 'eggB'

# The chains, read in value order: 0x34 at x=240 down to the terminal 0x37 at
# x=192, and 0x17 to 0x1A the same way. What they end as is what that world
# already calls its boulder and its treasure, so these say so rather than
# inventing a second description of the same object.
for _x in (192, 208, 224, 240):
    ROLES[(_x, 160)] = 'to-boulder'
    ROLES[(_x, 176)] = 'to-treasure'

# Two more frames of the growth tiles, whose other two frames already had roles.
ROLES[(224, 24)] = 'growth-a'
ROLES[(288, 24)] = 'growth-b'

# Every position on the y=48 band is the player.
for _x in range(0, 320, 16):
    ROLES.setdefault((_x, 48), 'player')

# The five worlds, as the manual names them. The never-released five have no
# title: the manual describes what shipped, and inventing one for them would put
# a made-up fact next to five sourced ones.
TITLES = {
    'hunter': 'The Caverns of Graymar',
    'cook': 'The Kitchens of Kyssandra',
    'cowboy': 'The Search for El Dorado',
    'space': 'Deep Dark Depths of Outer Space',
    'body': 'Doctor Frankenstein',
}

# The five worlds the 1988 game shipped a sheet for. The order is the select
# screen's. Kept for what is true of them alone - First Star drew them; the other
# five are Sahala's - and no longer the list of worlds with readings, which is
# every world in OBJECTS.
SHIPPED = ('hunter', 'cook', 'cowboy', 'space', 'body')

# The treasure each world collects, from the executable's own name table at
# ds:0x1C81, indexed by raw world. All ten are there; the hidden five are added
# from hidden-objects.json below.
TREASURE = {
    'hunter': 'GOLD', 'cook': 'APPLES', 'cowboy': 'COINS',
    'space': 'SUNS', 'body': 'HEARTS',
}

OBJECTS = {
    'hunter': {
        'exit-shut': 'a closed door of dark jungle wood with two recessed panels',
        'exit-open': 'a door of dark jungle wood standing open, the way out',
        'eggA': 'a round egg shelled in green and cream with a magenta core',
        'eggB': 'a round egg shelled in gold and cream with a magenta core',
        'to-boulder': 'a small brown monkey and a round grey and brown rock, one breaking up into the other',
        'to-treasure': "a round grey and brown rock and a gold pharaoh's death mask, one breaking up into the other",
        'wormA-head': 'the head of a green crocodile, yellow scales ringing its jaw',
        'wormA-body': 'a rounded body segment of a green crocodile, smooth dark green scales',
        'wormA-tail': 'the tapering green tail of a crocodile',
        'wormB-head': 'the head of a golden yellow serpent with violet eyes and a blunt snout',
        'wormB-body': 'a rounded body segment of a pale golden serpent',
        # A stub, not a tuft: the tile is a short smooth tapering end, and the
        # word 'curved' drew eight feathery plumes (#117).
        'wormB-tail': 'a short smooth tapering stub of pale gold serpent tail, with no tuft',
        'player': 'a boy jungle explorer in a red shirt and a red pith helmet',
        'boulder': 'a round grey and brown rock boulder',
        'dirt': 'fine green jungle moss speckled with black, a solid ground tile',
        # The manual is explicit: normal walls are toucans, magic walls elephants.
        # ONE toucan, not a wall of them. The plural put two birds in a bed of
        # leaves on the served tile, where the original has one bird on a flat
        # terracotta ground. Dan, 29 September 2026.
        'wall-1': 'a single toucan perched on a thin black branch against a flat terracotta ground, a solid tile that tiles edge to edge',
        'wall-2': 'a single toucan perched on a thin black branch against a flat terracotta ground, a solid tile that tiles edge to edge',
        'wall-3': 'a single toucan perched on a thin black branch against a flat terracotta ground, a solid tile that tiles edge to edge',
        'wall-4': 'a single toucan perched on a thin black branch against a flat terracotta ground, a solid tile that tiles edge to edge',
        # NOT the magic wall, though the manual calls the elephants that. This
        # position is runtime 0x02, the steel border - sprites.json main[2] -
        # and the magic wall 0x74 shares the ordinary wall tile at (132,4) with
        # 0x03 and the expanding wall 0x15. The art at this position is
        # elephants in every hunter sheet, so the drawing is right and only the
        # manual's label was wrong. Corrected 25 September 2026, before it could
        # be copied into a world being drawn from scratch.
        'special-wall': 'a single elephant head, face-on, with tusks and a curled '
                        'trunk, green and tan on a plain lilac ground, a solid tile '
                        'that tiles edge to edge - the unbreakable border of the cave',
        'wall-b': 'a slab of mottled violet crystal, a solid wall tile',
        'growth-a': 'a living mass of mottled brown jungle fungus filling the tile '
                    'edge to edge, the amoeba, a solid tile that tiles edge to edge',
        'growth-b': 'a living mass of mottled brown jungle fungus filling the tile '
                    'edge to edge, the amoeba, a solid tile that tiles edge to edge',
        'treasure': "a gold pharaoh's death mask with a striped headdress",
        'enemy-a': 'a bat with dark wings and a small orange body, seen from above',
        'enemy-b': 'a small brown monkey',
        'treasure-blast': "the bat dissolving into a gold pharaoh's death mask",
        'spark': 'a burst of red and yellow explosion sparks',
        'exit': 'a doorway ringed with red and violet gems',
        'tap': 'a weathered brass jungle tap dripping bright violet acid',
        'lamp': 'a brass jungle tap built upside down, its spout pointing up with a '
                'small orange flame burning at the mouth',
        'drip': 'a single falling droplet of bright violet acid, teardrop shaped',
        'drip-breaking': 'a droplet of violet acid breaking apart into splashes',
        'drip-pool': 'a round settled bead of bright violet acid',
        'flame': 'a lick of orange and gold flame, burning upward',
        'bonus-count': 'a bonus token: a large numeral on a pale tablet, held in a '
                       'ring of smooth rounded red and violet jewels',
        'bonus-turn': 'a clock face whose long hand turns, inside a ring of smooth rounded '
                      'red and violet jewels',
    },
    'cook': {
        'exit-shut': 'a closed red kitchen door with two recessed panels',
        'exit-open': 'a red kitchen door standing open, the way out',
        'eggA': 'a round egg shelled in green and magenta with a scarlet core',
        'eggB': 'a round egg shelled in scarlet and blue with a pale core',
        'to-boulder': 'a fried egg and a large ripe orange, one breaking up into the other',
        'to-treasure': 'a large ripe orange and a glossy red apple cut open to show a green gemstone core, one breaking up into the other',
        'wormA-head': 'a wedge of pizza with tomato, green herbs and melted cheese, as the head of a crawling creature',
        'wormA-body': 'a whole round pizza with tomato, green herbs and melted cheese, as a body segment',
        'wormA-tail': 'a small wedge of pizza, as the tail of a crawling creature',
        'wormB-head': 'a cheeseburger with bright blue eyes and white teeth, as the head of a crawling creature',
        'wormB-body': 'a cheeseburger in a sesame bun, as a body segment',
        'wormB-tail': 'a handful of golden fried potato chips, as the tail of a crawling creature',
        'player': 'a boy chef in a red jacket, white apron and a tall white hat',
        'boulder': 'a large round ripe orange',
        'dirt': 'a scatter of green herb flecks on blue-grey ground, a solid ground tile',
        'wall-1': 'two corn cobs standing upright on black, yellow and red kernels in green husks, a solid tile that tiles edge to edge',
        'wall-2': 'two corn cobs standing upright on black, yellow and red kernels in green husks, a solid tile that tiles edge to edge',
        'wall-3': 'two corn cobs standing upright on black, yellow and red kernels in green husks, a solid tile that tiles edge to edge',
        'wall-4': 'two corn cobs standing upright on black, yellow and red kernels in green husks, a solid tile that tiles edge to edge',
        'special-wall': 'a knife, a fork and a spoon laid side by side in bright '
                        'steel and pale blue, a solid wall of cutlery',
        'wall-b': 'a single slice of pizza, point down, its toppings scattered with '
                  'the black of the cave showing between them, a solid tile that '
                  'tiles edge to edge',
        'growth-a': 'a living lattice of round pink and magenta cells filling the '
                    'tile edge to edge, the amoeba, a solid tile that tiles edge to edge',
        'growth-b': 'a living lattice of round pink and magenta cells filling the '
                    'tile edge to edge, the amoeba, a solid tile that tiles edge to edge',
        'treasure': 'a glossy red apple cut open to show a green gemstone core',
        'enemy-a': 'a butterfly made of two slices of bread for wings either side '
                   'of a green stalk, forked at the front',
        'enemy-b': 'a fried egg, white with a bright orange yolk',
        'treasure-blast': 'the bread butterfly dissolving into a red apple with a '
                          'green core',
        'spark': 'a burst of red, blue and white explosion sparks',
        'exit': 'a doorway ringed with red',
        'tap': 'a polished gold kitchen tap',
        'lamp': 'a silver kitchen gas pipe built like an upside-down tap, its '
                'spout pointing up with a green flame at the mouth',
        'drip': 'a single falling droplet of bright cyan and blue liquid',
        'drip-breaking': 'a droplet of cyan liquid breaking apart into splashes',
        'drip-pool': 'a round settled bead of bright cyan liquid',
        'flame': 'a lick of bright green gas flame, burning upward',
        'bonus-count': 'a bonus token: a large numeral on a pale tablet, held in a '
                       'ring of smooth rounded red and violet jewels',
        'bonus-turn': 'a clock face whose long hand turns, inside a ring of smooth rounded '
                      'red and violet jewels',
    },
    'cowboy': {
        'exit-shut': 'a closed door of pale timber in a green frame, two recessed panels',
        'exit-open': 'a door of pale timber in a green frame standing open, the way out',
        'eggA': 'a round egg shelled in green and deep blue with a grey core',
        'eggB': 'a round egg shelled in white and deep blue with a sand-coloured core',
        'to-boulder': 'a blued steel Colt revolver and a round clump of dark green prickly cactus, one breaking up into the other',
        'to-treasure': 'a clump of dark green prickly cactus and a gold Aztec coin stamped with a feathered head-dress, one breaking up into the other',
        'wormA-head': 'the front of a black steam locomotive with a round headlamp',
        'wormA-body': 'a wooden railway flatbed truck loaded with timber',
        'wormA-tail': 'a small wooden railway wagon on spoked wheels',
        'wormB-head': 'a brown horse in harness, pulling',
        'wormB-body': 'a covered wagon with a white canvas tilt',
        'wormB-tail': 'a covered wagon with a white canvas tilt, on spoked wheels',
        'player': 'a boy cowboy in a pale hat, white shirt and red trousers',
        'boulder': 'a round clump of dark green prickly cactus',
        'dirt': 'pale pink and tan desert sand, a solid ground tile',
        'wall-1': 'two saguaro cacti on pale desert sand, a solid tile that tiles edge to edge',
        'wall-2': 'two saguaro cacti on pale desert sand, a solid tile that tiles edge to edge',
        'wall-3': 'two saguaro cacti on pale desert sand, a solid tile that tiles edge to edge',
        'wall-4': 'two saguaro cacti on pale desert sand, a solid tile that tiles edge to edge',
        'special-wall': 'a face of dark grey canyon rock, a solid wall tile',
        'wall-b': 'pale desert rock streaked with red ore, a solid wall tile',
        'growth-a': 'a living mass of deep blue and violet mineral filling the tile '
                    'edge to edge, the amoeba, a solid tile that tiles edge to edge',
        'growth-b': 'a living mass of deep blue and violet mineral filling the tile '
                    'edge to edge, the amoeba, a solid tile that tiles edge to edge',
        'treasure': "a gold Aztec coin stamped with a chief's feathered head-dress",
        'enemy-a': "a geologist's hammer with a steel head and a wooden handle",
        'enemy-b': 'a Colt revolver in blued steel and silver',
        'treasure-blast': "the geologist's hammer dissolving into a gold Aztec coin",
        'spark': 'a burst of yellow and green explosion sparks',
        'exit': 'a doorway ringed with red and gold',
        'tap': 'a dull iron pump handle in weathered grey steel',
        'lamp': 'a grey iron pump built upside down, its spout pointing up with a '
                'yellow flame at the mouth',
        'drip': 'a single falling droplet of deep blue-violet liquid',
        'drip-breaking': 'a droplet of blue-violet liquid breaking apart',
        'drip-pool': 'a round settled bead of deep blue-violet liquid',
        'flame': 'a lick of yellow and orange campfire flame, burning upward',
        'bonus-count': 'a bonus token: a large numeral on a pale tablet, held in a '
                       'ring of smooth rounded red and gold jewels',
        'bonus-turn': 'a clock face whose long hand turns, inside a ring of smooth rounded '
                      'red and gold jewels',
    },
    'space': {
        'exit-shut': 'a closed hatch of pale panelling in a blue frame, two recessed panels',
        'exit-open': 'a hatch of pale panelling in a blue frame standing open, the way out',
        'eggA': 'a round egg shelled in blue and sea green with a pale core',
        'eggB': 'a round egg shelled in lavender and green with an orange rim',
        'to-boulder': 'a swirling blue spiral galaxy and the planet Earth as a small blue and green globe, one breaking up into the other',
        'to-treasure': 'the planet Earth and a blazing yellow sun with orange rays, one breaking up into the other',
        'wormA-head': 'the nose of a silver arrowhead spacecraft with red running lights',
        'wormA-body': 'a silver spacecraft hull section with red running lights',
        'wormA-tail': 'the swept silver tail fins of a spacecraft, tipped with red lights',
        'wormB-head': 'a green flying saucer with a red dome light on top',
        'wormB-body': 'the green hull of a flying saucer with red lights',
        'wormB-tail': 'the trailing edge of a green flying saucer, a red light at its centre',
        'player': 'a boy astronaut in a cream spacesuit with a red chest panel',
        'boulder': 'the planet Earth as a small blue and green globe',
        'dirt': 'grey and white static, a solid ground tile',
        'wall-1': 'a wall of gold metal panelling with blue indicator strips, '
                  'a solid tile that tiles edge to edge',
        'wall-2': 'a wall of gold metal panelling with blue indicator strips, '
                  'a solid tile that tiles edge to edge',
        'wall-3': 'a wall of gold metal panelling with blue indicator strips, '
                  'a solid tile that tiles edge to edge',
        'wall-4': 'a wall of gold metal panelling with blue indicator strips, '
                  'a solid tile that tiles edge to edge',
        'special-wall': 'a single square riveted hull plate in pale lilac with red '
                        'rivets, a solid tile that tiles edge to edge',
        'wall-b': 'a green lattice of circuitry with holes the black of the cave '
                  'shows through, a solid tile that tiles edge to edge',
        'growth-a': 'a living lattice of round green plasma cells filling the tile '
                    'edge to edge, the amoeba, a solid tile that tiles edge to edge',
        'growth-b': 'a living lattice of round green plasma cells filling the tile '
                    'edge to edge, the amoeba, a solid tile that tiles edge to edge',
        'treasure': 'a blazing yellow sun with orange rays',
        'enemy-a': 'a bird of orange flame with spread wings, a space-dove',
        'enemy-b': 'a swirling blue spiral galaxy',
        'treasure-blast': 'the flame bird dissolving into a blazing sun',
        'spark': 'a burst of pale green explosion sparks',
        'exit': 'a doorway ringed with orange and yellow',
        'tap': 'a gold alloy valve',
        'lamp': 'a silver pipe built like an upside-down valve, its nozzle pointing '
                'up with a red and orange flame at the mouth',
        'drip': 'a single falling droplet of bright blue coolant, teardrop shaped',
        'drip-breaking': 'a droplet of blue coolant breaking apart into beads',
        'drip-pool': 'a round settled bead of bright blue coolant',
        'flame': 'a lick of red and orange rocket flame, burning upward',
        'bonus-count': 'a bonus token: a large numeral on a pale cream tablet, held '
                       'in a ring of smooth rounded orange and yellow jewels',
        'bonus-turn': 'a clock face whose long hand turns, inside a ring of smooth rounded '
                      'orange and yellow jewels',
    },
    'body': {
        'exit-shut': 'a closed door of orange-red tissue with two recessed panels',
        'exit-open': 'a door of orange-red tissue standing open, the way out',
        'eggA': 'a round egg shelled in green and blue with a black core',
        'eggB': 'a round egg shelled in orange and flesh pink with a black core',
        'to-boulder': 'a grey human skull and a large human eyeball, white with a blue iris, one breaking up into the other',
        'to-treasure': 'a human eyeball and a red heart symbol with a square green gem in the middle, one breaking up into the other',
        'wormA-head': 'a white skeletal hand, fingers spread',
        'wormA-body': 'a white ribcage',
        'wormA-tail': 'the bones of a white skeletal foot',
        'wormB-head': 'a living hand with red painted nails, fingers spread',
        'wormB-body': 'a rounded mass of pink flesh',
        'wormB-tail': 'a bare foot with red painted toenails',
        'player': 'a boy surgeon in white scrubs with an orange belt',
        'boulder': 'a large human eyeball, white with a blue iris',
        'dirt': 'pale pink flesh flecked with brown, a solid ground tile',
        'wall-1': 'a wall of grinning mouths with red lips and white teeth, '
                  'a solid tile that tiles edge to edge',
        'wall-2': 'a wall of grinning mouths with red lips and white teeth, '
                  'a solid tile that tiles edge to edge',
        'wall-3': 'a wall of grinning mouths with red lips and white teeth, '
                  'a solid tile that tiles edge to edge',
        'wall-4': 'a wall of grinning mouths with red lips and white teeth, '
                  'a solid tile that tiles edge to edge',
        # The manual calls these graves, and the tile really does read RIP.
        'special-wall': 'a single grey headstone carved with the letters R I P, '
                        'standing in green grass, a solid tile that tiles edge to edge',
        'wall-b': 'meandering dark red blood vessels with the black of the cave '
                  'showing between them, a solid tile that tiles edge to edge',
        'growth-a': 'a living lattice of round blue cells filling the tile edge to '
                    'edge, the amoeba, a solid tile that tiles edge to edge',
        'growth-b': 'a living lattice of round blue cells filling the tile edge to '
                    'edge, the amoeba, a solid tile that tiles edge to edge',
        # A heart SYMBOL, two lobes and a point, not an anatomical heart - the
        # tile and the end of its chain both draw the symbol.
        'treasure': 'a red heart symbol, point down, with a square green gem set '
                    'in the middle',
        'enemy-a': 'a butterfly made of two pink human ears for wings either side '
                   'of a blue surgical probe, forked at the front',
        'enemy-b': 'a grey human skull',
        'treasure-blast': 'the ear butterfly dissolving into a red heart symbol',
        'spark': 'a burst of pink and brown explosion sparks',
        'exit': 'a doorway ringed with blue and green',
        'tap': 'a surgical steel tap in blue and cyan',
        'lamp': 'a blue and cyan surgical pipe built like an upside-down tap, its '
                'spout pointing up with a green flame at the mouth',
        'drip': 'a single falling droplet of bright red blood, teardrop shaped',
        'drip-breaking': 'a droplet of bright red blood breaking apart into splashes',
        'drip-pool': 'a round settled bead of bright red blood',
        'flame': 'a lick of bright green flame, burning upward',
        'bonus-count': 'a bonus token: a large numeral on a pale tan tablet, held in '
                       'a ring of smooth rounded blue and green jewels',
        'bonus-turn': 'a clock face whose long hand turns, inside a ring of smooth rounded '
                      'blue and green jewels',
    },

    # ---- the five hidden worlds ----------------------------------------------
    #
    # Nothing is described here. The faithful build draws scuba, player, music, luck
    # and miner with Aleksi Sahala's sheets (third_party/rockford-patcher), and their
    # descriptions are read off those sheets and live in hidden-objects.json, loaded
    # below. This project no longer carries a hand-drawn luck of its own.
}

# ---- the five hidden worlds, from Aleksi Sahala's sheets -----------------------
#
# scuba, player, music, luck and miner are drawn in the faithful build in Sahala's
# reconstructions (asahala/Rockford-Patcher, copied to third_party/rockford-patcher/),
# so these descriptions are read off HIS tiles, tile by tile, on 3 October 2026. They
# say what his drawing shows and nothing about the 1988 game, which never drew these
# worlds - the point of marking every entry with its source, so nobody later takes a
# description of a reconstruction for a fact about the original.
#
# They live in hidden-objects.json rather than here, as 180 long strings that a
# reader of this file would have to scroll past, and are loaded below. Hunter's
# and the other four 1988 worlds' are above, read off First Star's sheets.
#
# Five things the readings found that the prompts must not assume, from the notes in
# sprite-readings.json: his tiles are often on an opaque ground, not transparent
# (scuba's blue, player's green); several roles are drawn as something other than
# the role's name (scuba's and luck's fire taps are birds, luck's amoeba is
# marbling); and his timed walls, taps, bonus rings and transformation chains do
# not all move the way the 1988 ones do, which hires.py measures per world instead
# of assuming.
_HIDDEN = json.loads((Path(__file__).resolve().parent / 'hidden-objects.json').read_text())
OBJECTS.update(_HIDDEN['objects'])
TREASURE.update(_HIDDEN['treasure'])
PROVENANCE = _HIDDEN['provenance']

# TITLES for the five stays as it was: none of the sources names them. The executable
# gives a filename and a treasure for each, and Sahala's patcher a "tileset" label.
# Inventing a title here would put a made-up fact beside five sourced ones.


# WHAT EACH FRAME SHOWS, READ OFF THE TILE.
#
# This replaces a table of poses that was written without looking. It told the
# model the player walked RIGHT in the two frames where he faces left, called
# the right-facing pair "seen from behind" and the walk away from the viewer a
# walk to the left - all three wrong - and gave him idle poses, a wave and a
# stretch, that no world draws. The enemies had no pose at all, so eight
# frames of a turning creature were eight prompts for the same picture. Dan
# read the served hunter set on 29 September 2026 and found it frame by frame:
# the wrong way round, the wrong foot, both feet together where they are apart,
# a door opening the wrong way, two toucans where the tile has one.
#
# So every drawable position in every shipped world was read off the 1988 tile
# on 29 September 2026, as a character grid and at 6x beside the served VIP
# tile, and what it shows is written down in sprite-readings.json: which way it
# faces, which foot is down, where the arm is, how many of a motif, how far a
# door is open, how the frames of its animation relate. Each entry also carries
# a verdict on the tile VIP served that day, which is the review record.
#
# The readings are the prompt's POSE. The subject in OBJECTS says WHO it is.
# Where the two disagreed the subject was corrected, because a pose that
# contradicts its own subject is a coin toss for the model.
READINGS_FILE = Path(__file__).resolve().parent / 'sprite-readings.json'


def _readings():
    if not READINGS_FILE.exists():
        return {}, {}
    data = json.loads(READINGS_FILE.read_text())
    frames, patterns = {}, {}
    for world, entries in data['frames'].items():
        frames[world] = {tuple(map(int, k.split(','))): v for k, v in entries.items()}
    for anim in data['animations']:
        for k in anim['positions']:
            pos = tuple(map(int, k.split(',')))
            for world, text in anim['pattern'].items():
                # The first animation a position appears in wins. (132,4) is the
                # wall, the expanding wall and frame 0 of the timed wall, and it
                # is drawn once, as the wall.
                patterns.setdefault(world, {}).setdefault(pos, text)
    return frames, patterns


READINGS, PATTERNS = _readings()


def reading(world, x, y):
    """What the original tile at this position shows, or None."""
    entry = READINGS.get(world, {}).get((x, y))
    return entry['pose'] if entry else None


def pattern(world, x, y):
    """How the frames of the animation this position belongs to relate, or None."""
    return PATTERNS.get(world, {}).get((x, y))


def describe(world, x, y):
    """What this sprite depicts in this world, or None if the role is unknown."""
    role = ROLES.get((x, y))
    if role is None:
        return None

    subject = OBJECTS.get(world, {}).get(role)
    if not subject:
        return subject

    # Every position, not only the player's. One subject covers every frame of
    # an animation, and a sprite generated without being told which way it is
    # pointing is a sprite that points whichever way the model felt like.
    pose = reading(world, x, y)
    return f"{subject}. What this frame shows: {pose.rstrip('.')}" if pose else subject


def check(renderer_positions=None):
    """Every world answers for every role, and every drawable position HAS one.

    The second half is new on 20 September 2026 and is the one that matters.
    Three separate gaps - both worms, the exit chain, the two transformation
    chains - were all the same shape: a position the renderer can reach with no
    role here, falling through to a description keyed on the runtime value and
    therefore telling every world the hunter world's story. Nothing noticed,
    because nothing was asking.

    Pass the renderer's own position list to check that half; hires.py does.
    """
    roles = set(ROLES.values())
    problems = []

    # A KEY IS NOT A DESCRIPTION. This tested `roles - set(OBJECTS[world])`,
    # which an empty string satisfies - and an empty subject falls straight
    # through subject_for to the value table, which is the wrong-world
    # description this exists to prevent. Astra set OBJECTS['cook']['exit-shut']
    # to '' and watched the whole check pass while the cook world was handed
    # "a closed wooden door set in a stone frame". 20 September 2026.
    for world, entries in OBJECTS.items():
        empty = sorted(r for r in roles if not (entries.get(r) or '').strip())
        if empty:
            problems.append(f'{world}: no description for {empty}')

    if renderer_positions is not None:
        # Blank in the original is fine - the game draws nothing there either.
        # Anything else without a role is the gap this exists to catch.
        loose = sorted(p for p in renderer_positions if p not in ROLES)
        if loose:
            problems.append(
                f'{len(loose)} drawable positions have no role: {loose[:8]}')

    for world in TREASURE:
        if world not in OBJECTS:
            problems.append(f'{world}: no objects')

    # A READING FOR EVERY ROLE IN EVERY SHIPPED WORLD. Without one the prompt
    # falls back to the subject alone, which is how eight frames of a turning
    # enemy became eight prompts for one picture.
    #
    # Every world in OBJECTS, the hidden five included since 3 October 2026, when
    # they were read off Sahala's sheets. A world with descriptions and no readings
    # would be prompted from its subject alone.
    for world in OBJECTS:
        unread = sorted(p for p in ROLES if reading(world, *p) is None)
        if unread:
            problems.append(f'{world}: {len(unread)} positions with no reading: {unread[:6]}')

    # NO HIDDEN WORLD'S DESCRIPTION IS WORD FOR WORD A 1988 WORLD'S. A hidden world
    # given hunter's "a round grey and brown rock boulder" is the copy-paste this
    # file exists to catch - the same failure as the one that drew 235 sprites in the
    # wrong world's words - and a reading does not rescue it, because the subject
    # says who the object is.
    shipped = {text for w in SHIPPED for text in OBJECTS[w].values()}
    for world in OBJECTS:
        if world in SHIPPED:
            continue
        copied = sorted(role for role, text in OBJECTS[world].items() if text in shipped)
        if copied:
            problems.append(f'{world}: {len(copied)} descriptions are word for word a '
                            f'1988 world\'s: {copied[:6]}')
    return problems


if __name__ == '__main__':
    bad = check()
    print('\n'.join(bad) if bad else
          f'{len(OBJECTS)} worlds x {len(set(ROLES.values()))} roles, all present')
