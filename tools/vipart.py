#!/usr/bin/env python3
"""Photoreal title and world art for Rockford VIP.

    python3 tools/vipart.py refs      # cut the originals out as references
    python3 tools/vipart.py draft     # cheap pass, to settle the direction
    python3 tools/vipart.py final     # the keepers, at high quality

Six pictures: the title screen and the five world characters. These are not
sprites - they are the screens the player looks at while nothing is moving, so
they are the one place in the game where a still image has to carry it alone.
That is why they are generated at a higher tier than the sprites and why the
draft pass exists: composition moves between draft and final, so the draft is
for judging the idea, not the picture.

Each prompt is anchored to the original art as a reference image. The reference
is doing the work that a description cannot: it is what keeps the hunter's cap
the right green, the cook's apple the right apple, and the astronaut's suit
badge where it belongs. A prompt alone reliably invents a different character.
"""
import json
import os
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageEnhance, ImageFilter, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parent))
import worldlist  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
GAME = ROOT / 'web/Rockford.Web/wwwroot/game'
WORK = ROOT / 'build/vip-art'

# The image CLI these were drawn with. IMAGER_DIR points at another install.
IMAGER_DIR = Path(os.environ.get('IMAGER_DIR', Path.home() / '.claude/skills/imager'))
IMAGER = IMAGER_DIR / 'scripts/imager.py'
IMAGER_PY = IMAGER_DIR / '.venv/bin/python'

# The original screens are 320x200, which is 8:5. 1536x960 is the same ratio
# exactly and both edges are multiples of 16, which the API requires.
TITLE_SIZE = '1536x960'
# The menu portraits are 48x62. 1024x1344 is within a percent of that.
PORTRAIT_SIZE = '1024x1344'

# Where the five portrait boxes sit on MENU_I, found by scanning for the lit
# frames rather than assuming an even grid.
PORTRAIT_BOXES = [(8, 56), (72, 120), (136, 184), (200, 248), (264, 312)]
PORTRAIT_BAND = (38, 100)

# The five the 1988 menu picture has portraits for: the first page of the select
# screen, from worlds.json (#107). The hidden worlds' portraits are not on MENU_I
# and are #111's.
WORLDS = worldlist.first_page()

# The second page: the five worlds after them, whose portraits are Sahala's.
HIDDEN = worldlist.all_worlds()[worldlist.PAGE_SIZE:]

# What each character actually is, read off the decoded art rather than guessed
# from the world's name. The space world's character is holding a planet, not a
# helmet, and the body world's is a surgeon rather than a patient - both of which
# a name-only prompt gets wrong.
SUBJECTS = {
    'hunter': (
        'A cheerful cartoon explorer character with a big round head, wide blue '
        'eyes and a broad smile, wearing a white pith helmet banded in red and '
        'green, a red shirt with orange trim, and heavy brown boots. Standing '
        'facing us. A brilliantly coloured macaw with red, blue and green wings '
        'perches on their lowered forearm. Hanging vines and broad green jungle '
        'leaves behind.'
    ),
    'cook': (
        'A cheerful cartoon chef character with a big round head, blond hair, '
        'wide blue eyes and a broad smile, wearing a tall white pleated toque, a '
        "red and orange chef's jacket and a clean white apron. Standing facing "
        'us, one hand on their hip. Beside them, an enormous glossy red apple '
        'cut open to show a brilliant green gemstone where the core should be. '
        'A warm professional kitchen behind.'
    ),
    'cowboy': (
        'A cheerful cartoon cowboy character with a big round head, blond hair '
        'and a broad grin, wearing a wide pale cream hat with a green band, a red '
        'neckerchief, a grey-white shirt and bright red trousers, with a tooled '
        'leather belt and a holster. Standing facing us, hands at the belt. A '
        'green cactus beside them and a dusty western canyon at golden hour '
        'behind.'
    ),
    'space': (
        'A cartoon astronaut character with a big round helmet, the wide visor '
        'dark and mirror-reflective, in a white spacesuit with a large magenta '
        'letter R on the chest. Standing facing us, holding a small green and '
        'blue planet in one gloved hand. The Earth hangs large behind them '
        'against deep starfield black.'
    ),
    # Worded as a museum prop rather than an organ on purpose. Asked for an
    # "anatomical human heart, deep red", the output was blocked for violence -
    # correctly, since that describes gore. A polished teaching model is the same
    # picture without the blood, and it is also what the 1988 sprite actually
    # draws: a clean heart shape with a gemstone in it.
    'body': (
        'A cheerful cartoon doctor character with a big round head, round '
        'glasses, ginger hair and a broad smile, in white scrubs and a white '
        'surgical cap with a round head mirror. Standing facing us, holding up a '
        'large polished museum teaching model of a heart in both hands - smooth '
        'glossy red, clean and bloodless, with a brilliant green gemstone set '
        'into the front. A plastic educational skeleton lit in green stands '
        'behind them in a bright tidy laboratory.'
    ),
}

# THE FIVE HIDDEN WORLDS' CHARACTERS, read off Aleksi Sahala's 48 pixel portraits
# (game/portraits/<world>.png) on 3 October 2026, the way the five above were read
# off MENU_I. Each is what his picture shows: costume, prop and setting, with the
# same treatment - a cheerful cartoon character with a big round head - the 1988
# five get, so the second page is a set with the first.
SUBJECTS.update({
    'scuba': (
        'A cheerful cartoon deep-sea diver character in a round polished brass '
        'diving helmet with a big round porthole window, a white and grey suit '
        'with brass fittings and a small air hose, standing facing us. Streams of '
        'bubbles rise past, and bright green sea plants wave at the bottom edge. '
        'Deep ocean blue water all behind.'
    ),
    'player': (
        'A cheerful cartoon American football player character with a big round '
        'head, in a red helmet with a white stripe and a grey face guard, a red '
        'number 3 on the shoulder, a blue jersey and grey trousers with blue '
        'boots, running forward and holding a brown football tucked under one '
        'arm. A green football pitch with pale chalk lines behind.'
    ),
    'music': (
        'A cheerful cartoon classical composer character with a big round head '
        'and a big open smile, a grey powdered wig, a black tailcoat, a white '
        'shirt front and a bright blue bow tie, brown shoes, holding a conductor\'s '
        'baton in one hand. A brown violin stands beside them. Black musical '
        'notes and a treble clef float on a plain grey wall behind.'
    ),
    'luck': (
        'A cheerful cartoon gambler character with a big round head, blond hair '
        'and a broad grin, in a wide-brimmed red hat, a brown jacket over a white '
        'shirt, grey trousers and brown boots, one arm held out and the other '
        'holding up a large playing card showing a black spade. Plain black '
        'behind.'
    ),
    'miner': (
        'A cheerful cartoon miner character with a big round head and a huge '
        'smile, in a grey hard hat with a bright glowing yellow lamp, blue '
        'overalls and brown boots, holding up a large faceted pink and blue '
        'diamond in both hands, a pickaxe leaning beside. Golden sparkles in the '
        'air against a plain black mine behind.'
    ),
})

TITLE_SUBJECT = (
    'Rockford, a cheerful cartoon character with a big round head, messy blond '
    'hair, wide eyes and a huge grin, in a red and white horizontally striped '
    'long-sleeved shirt, white trousers and bright red boots, standing with his '
    'arms flung out on a rocky ledge. '
    'Behind and above him, carved deep into a vast grey rock face in tall bold '
    'capital letters, is one single word spelled exactly R-O-C-K-F-O-R-D: '
    '"ROCKFORD". Eight letters, no more and no fewer. Do not add, repeat, '
    'reorder or invent any letter. Brilliant blue diamonds are embedded in the '
    'boulders around his feet. A magenta and purple sunset sky, with a distant '
    'glowing volcano on the horizon to the left.'
)

# The same instruction on every prompt. The failure this exists to prevent is the
# one the sprite run hit twice: asked to reproduce a reference faithfully, the
# model copies the pixel steps and returns the same blocky art at a larger size.
STYLE = (
    # The material is pinned rather than left to the model. Without this the six
    # came back in three different languages - felt and stitching, moulded vinyl,
    # glossy CG - each fine alone and obviously not a set when placed side by
    # side on the select screen. One material, one finish, one light.
    'Render this exact character as a top-tier feature-animation character, at '
    'the quality of a modern Pixar or DreamWorks hero render. Smooth polished '
    'surfaces with a soft sheen, warm skin with gentle subsurface light through '
    'it, large expressive eyes with bright glossy catchlights, rich saturated '
    'colour. Sculpted stylised hair with a soft sheen rather than individual '
    'strands. Clothing that reads as real cloth with soft folds, but clean and '
    'idealised rather than worn or gritty. Warm cinematic key light with a soft '
    'rim, shallow depth of field, the character tack sharp against a softly '
    'blurred, richly detailed environment.\n\n'
    'Not a toy, not vinyl, not plastic, not felt, not stitched fabric, not '
    'claymation and not a puppet. A polished animated film character.\n\n'
    # The cowboy came back with square eyes and pixel teeth, because that is
    # literally what the 16-colour reference draws. Every other character got
    # rounded features and he did not, so he was the one face in the lineup that
    # looked unfinished. The reference is authority on costume and colour, never
    # on the shape of a feature.
    'The face is fully sculpted and rounded: eyes are round or almond with soft '
    'glossy highlights, the mouth and teeth are curved and organic, the cheeks '
    'and nose are smoothly modelled. Never square, rectangular, stepped or '
    'blocky features, and no pixel-shaped eyes, teeth or facial markings, even '
    'though the low-resolution reference draws them that way.\n\n'
    'Keep the character exactly as the reference draws it. The same silhouette, '
    'the same proportions - including the large rounded head against a small '
    'body - the same face and expression, the same pose, the same costume, the '
    'same colours, the same props in the same hands. This is a faithful '
    'translation of that design into real materials, not a reinterpretation of '
    'it.\n\n'
    'Do NOT turn this into a realistic ordinary human being, and do NOT make it a '
    'person wearing a costume. It is a stylised character given real substance, '
    'and it keeps its own cartoon proportions. Because it is a character and not '
    'a person, it is not a photograph of a child and must not read as one.\n\n'
    'Not pixel art, not 8-bit, not a video game screenshot, not a flat '
    'illustration, and no visible pixels or blocky edges anywhere.'
)


def cmd_refs():
    """Cut the originals out at a size the model can actually see.

    Nearest-neighbour on purpose. Smoothing a 48-pixel-wide portrait before
    showing it to the model throws away the only edges it has to read.
    """
    WORK.mkdir(parents=True, exist_ok=True)

    menu = Image.open(GAME / 'menu_i.png').convert('RGB')
    for name, (x0, x1) in zip(WORLDS, PORTRAIT_BOXES):
        crop = menu.crop((x0, PORTRAIT_BAND[0], x1, PORTRAIT_BAND[1]))
        big = crop.resize((crop.width * 8, crop.height * 8), Image.NEAREST)
        big.save(WORK / f'ref-{name}.png')

    # The hidden worlds have no box on MENU_I. Their reference is Sahala's own
    # 48 pixel portrait, minus its one-pixel white ring, enlarged the same way.
    for name in HIDDEN:
        face = Image.open(GAME / 'portraits' / f'{name}.png').convert('RGB').crop((1, 1, 47, 47))
        face.resize((face.width * 8, face.height * 8), Image.NEAREST).save(WORK / f'ref-{name}.png')

    title = Image.open(GAME / 'title.png').convert('RGB')
    title.resize((title.width * 3, title.height * 3), Image.NEAREST).save(
        WORK / 'ref-title.png')

    print(f'{len(WORLDS) + len(HIDDEN) + 1} references in {WORK}')


# The select screen's wording, exactly as MENU_I.SCM has it, at the same heights
# on the picture. The layout is the original's; only the rendering is new.
#
# The second number is how wide the line should be as a fraction of the picture,
# not a point size. Sizing by point meant picking a number that only worked at one
# resolution, and the first attempt put 300-pixel letters across a 960-pixel
# screen. Fitting to a width is measured against the thing that actually matters.
MENU_TEXT = [
    (0.660, 0.40, 'Use arrow keys or joystick'),
    (0.745, 0.23, 'to select world'),
    (0.875, 0.56, 'Press ENTER or joystick button to play'),
]

# The wordmark's place on the select screen, as fractions of the picture.
LOGO_TOP = 0.045
LOGO_WIDTH = 0.46

# VIP's five portrait boxes, as fractions of the picture.
#
# These MUST match setMenuBox in Home.razor, or the selection frame lands
# somewhere other than the portrait it is meant to be framing.
#
# Not MENU_I's geometry. That screen puts its boxes in the top quarter and leaves
# the bottom fifth of the picture empty, which is what 200 lines and sixteen
# colours bought you. At 1536 by 960 the same fractions left a band of dead space
# across the bottom, so the boxes here are larger and sit on the middle of the
# screen with the wordmark above and the instructions below.
MENU_BOX = dict(x=0.028, y=0.275, w=0.152, h=0.305, step=0.198)

FONTS = [
    '/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf',
    '/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf',
]


def font_at(size):
    for path in FONTS:
        if Path(path).exists():
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def fitted_font(draw, text, target_width):
    """The largest size at which `text` still fits `target_width`."""
    size = 8
    while size < 400:
        font = font_at(size + 2)
        left, _, right, _ = draw.textbbox((0, 0), text, font=font)
        if right - left > target_width:
            break
        size += 2
    return font_at(size)


def draw_logo(menu, W, H):
    """The stone wordmark across the top, in place of a typeface.

    Falls back to nothing if the wordmark has not been generated - the
    instructions below still render, so the screen is usable either way.
    """
    logo_path = WORK / 'draft-logo.png'
    if not logo_path.exists():
        return False

    logo = Image.open(logo_path).convert('RGBA')

    # Trim to the lettering. The model leaves a wide transparent margin and
    # sometimes a faint halo, so the visible bounds are what to measure against
    # rather than the file's own size.
    box = logo.getbbox()
    if box:
        logo = logo.crop(box)

    width = round(LOGO_WIDTH * W)
    height = round(logo.height * width / logo.width)
    logo = logo.resize((width, height), Image.LANCZOS)

    menu.paste(logo, ((W - width) // 2, round(LOGO_TOP * H)), logo)
    return True


def draw_menu_text(menu, W, H):
    """The original's own words, centred, with a shadow so they survive the art."""
    draw = ImageDraw.Draw(menu)
    for y_fraction, width_fraction, text in MENU_TEXT:
        font = fitted_font(draw, text, width_fraction * W)
        left, top, right, _ = draw.textbbox((0, 0), text, font=font)
        x = (W - (right - left)) // 2 - left
        y = round(y_fraction * H) - top

        offset = max(1, H // 300)
        draw.text((x + offset, y + offset), text, font=font, fill=(0, 0, 0))
        draw.text((x, y), text, font=font, fill=(255, 255, 255))


def pages():
    """The select screen's pages, each the five world names on it.

    From worlds.json, so the second page is the five hidden worlds in menu order
    and a sixth page would appear on its own. Only the first has portraits today.
    """
    names = worldlist.all_worlds()
    return [names[i:i + worldlist.PAGE_SIZE] for i in range(0, len(names), worldlist.PAGE_SIZE)]


def page_picture(page):
    """The file a page's picture is written to: vip/menu.png, then vip/menu-2.png.

    The same names WorldList.MenuPicture gives the front end - the two have to
    agree, and WorldListTests holds the C# side.
    """
    return 'menu.png' if page == 0 else f'menu-{page + 1}.png'


def cmd_install():
    """Compose the menu screen and put both screens where the VIP build reads them.

    The select screen is assembled here rather than generated, because the five
    portraits have to sit in the boxes the front end frames the selection in, and
    those are fixed proportions of the picture. Generating a whole menu screen
    would put the characters wherever the model felt like.

    Every page is baked, the first from `draft-<world>.png` for the 1988 five and
    each later page from the same files for its own five, when ALL FIVE exist. A
    later page with no portraits yet is not an error and is not written, and the
    build offers no world on it until the manifest says it can draw one (#111).
    """
    art = ROOT / 'web/Rockford.Web/wwwroot/vip'
    art.mkdir(parents=True, exist_ok=True)

    title = WORK / 'draft-title.png'
    if not title.exists():
        print('no title art yet - run draft first')
        return 1

    screen = Image.open(title).convert('RGB')
    W, H = screen.size

    # The select screen's backdrop is its OWN picture, not the title.
    #
    # It used to be the title blurred, and for a day it was the same file. Then
    # c3e61e0 put the VIP badge on the title, took the new title.png and left
    # menu.png alone - so the two screens have come from different pictures ever
    # since, and running this would have quietly put the badge on the select
    # screen's backdrop as well. Nobody would have chosen that; it would just
    # have happened on the next install.
    #
    # So the backdrop is a tracked file of its own. It is the pre-badge title,
    # which is what the shipped menu is actually made of, and naming it for what
    # it does rather than for what it used to be is the difference between a
    # record and a trap. Astra, 19 September 2026.
    backdrop_path = WORK / 'draft-menu-backdrop.png'
    backdrop = Image.open(backdrop_path).convert('RGB') if backdrop_path.exists() else screen

    written = []
    for page, names in enumerate(pages()):
        missing = [n for n in names if not (WORK / f'draft-{n}.png').exists()]
        if missing and page == 0:
            print(f'{missing[0]}: no portrait, run draft first')
            return 1
        if missing:
            print(f'page {page + 1}: no portraits yet for {", ".join(missing)}, not baked')
            continue
        written.append(compose_page(backdrop, names, W, H, page_picture(page), art))

    screen.save(art / 'title.png')
    return finish_install(art, written)


def compose_page(backdrop, names, W, H, filename, art):
    """One select screen: the backdrop, a framed portrait per world, the wordmark
    and the instructions, saved as `art/filename`."""
    # The same fractions game.js frames the selection with. Keep the two in step.
    bx, by = MENU_BOX['x'], MENU_BOX['y']
    bw, bh, step = MENU_BOX['w'], MENU_BOX['h'], MENU_BOX['step']

    # The title screen's own rock, blurred and darkened, rather than flat black.
    # The original select screen is plain because it had sixteen colours and no
    # room; there is no reason to inherit the limitation along with the layout.
    menu = backdrop.filter(ImageFilter.GaussianBlur(W // 60))
    menu = ImageEnhance.Brightness(menu).enhance(0.35)

    for i, name in enumerate(names):
        portrait = WORK / f'draft-{name}.png'

        box = (round((bx + step * i) * W), round(by * H),
               round(bw * W), round(bh * H))

        # Cover rather than fit: the portraits are taller than the boxes and a
        # letterboxed face in a frame looks like a mistake. Crop to the top of
        # the subject, where the face is.
        src = Image.open(portrait).convert('RGB')
        scale = max(box[2] / src.width, box[3] / src.height)
        resized = src.resize((round(src.width * scale), round(src.height * scale)),
                             Image.LANCZOS)
        left = (resized.width - box[2]) // 2
        menu.paste(resized.crop((left, 0, left + box[2], box[3])), (box[0], box[1]))

        # A frame, so an unselected portrait still reads as a card rather than a
        # hole cut in the backdrop.
        ImageDraw.Draw(menu).rectangle(
            [box[0], box[1], box[0] + box[2] - 1, box[1] + box[3] - 1],
            outline=(232, 226, 214), width=max(1, W // 512))

    draw_logo(menu, W, H)
    draw_menu_text(menu, W, H)
    menu.save(art / filename)
    return filename


def finish_install(art, written):
    # THE GEOMETRY GOES WITH THE PICTURE.
    #
    # These five fractions decide where the portraits are drawn, and the page
    # needs the same five to put anything over them. They used to be typed in
    # both places - here and in Home.razor - with a comment in each asking the
    # next person to keep them in step. Two copies of a number with a note
    # attached is not one source of truth, it is a race.
    #
    # Written beside the art it describes, so a change to the layout ships with
    # the layout.
    #
    # One geometry for every page: the boxes sit in the same place on all of them,
    # so the selection ring and the buttons need no per-page numbers.
    json.dump(MENU_BOX, open(art / 'menu.json', 'w'))
    print(f'title.png, {", ".join(written)} and menu.json in {art}')
    return 0


# The select screen's wordmark. Generated rather than cut out of the title screen:
# there the word is carved into a curved rock face and photographed at an angle, so
# any crop of it arrives bent, lit from one side and carrying its own backdrop.
LOGO_SUBJECT = (
    'The single word "ROCKFORD" spelled exactly R-O-C-K-F-O-R-D, eight letters, '
    'no more and no fewer, as a wordmark. Tall bold capital letters carved from '
    'rough grey granite, each letter a solid three-dimensional block of stone '
    'with chiselled facets, pitted weathered surfaces and deep shadow in the '
    'cuts. Warm low sunlight from the upper left. The letters sit level in one '
    'straight horizontal line, evenly spaced, seen straight on with no '
    'perspective and no tilt. Isolated on a fully transparent background with no '
    'backdrop, no ground, no rock face behind, no shadow cast on anything.'
)
LOGO_SIZE = '1536x512'


def jobs():
    yield 'title', TITLE_SUBJECT, TITLE_SIZE
    yield 'logo', LOGO_SUBJECT, LOGO_SIZE
    for name in WORLDS + HIDDEN:
        yield name, SUBJECTS[name], PORTRAIT_SIZE


def generate(quality):
    WORK.mkdir(parents=True, exist_ok=True)
    made = []

    for name, subject, size in jobs():
        # The wordmark has no reference of its own - it is anchored to the title
        # screen, whose stone it has to match.
        ref = WORK / ('ref-title.png' if name == 'logo' else f'ref-{name}.png')
        if not ref.exists():
            print(f'{name}: no reference, run refs first')
            return 1

        out = WORK / f'{quality}-{name}.png'
        if out.exists():
            print(f'{name}: already done')
            made.append(out)
            continue

        args = [
            str(IMAGER_PY), str(IMAGER),
            '--model', 'gpt-image-2',
            '--quality', 'low' if quality == 'draft' else 'high',
            '--size', size,
            '--reference', str(ref),
            '-y',
        ]

        if name == 'logo':
            # The character style block is about faces and costume and says
            # nothing useful about lettering, so it is left off here.
            prompt = subject
            args += ['--background', 'transparent', '--output-format', 'png']
        else:
            prompt = f'{subject}\n\n{STYLE}'

        args += [prompt, str(out)]

        print(f'{name}: generating ({quality}, {size})', flush=True)
        result = subprocess.run(args, capture_output=True, text=True)
        if out.exists():
            made.append(out)
        else:
            print('   ', (result.stderr or result.stdout).strip()[:200])

    print(f'{len(made)} of {len(list(jobs()))} in {WORK}')
    return 0



# ---- the title animation -------------------------------------------------
#
# The original's title screen is not a still: Rockford tosses a diamond up and
# catches it, his eyes follow it, and he taps a foot. TitleAnimation.cs has the
# counters and tools/webassets.py cuts the faithful build's frames out of
# TITLEANI.SCM.
#
# VIP cannot reuse those frames - they are 1988 pixel art and the render is
# photoreal at 1536 by 960 - so the frames are its own. THE MOVEMENT IS NOT.
# The gem's path is measured out of the original's own sheet and scaled, so the
# two builds throw the same throw at the same speed: held in the hand for four
# frames, a jump of fifteen screen pixels as it leaves, then a linear climb of
# seven a frame. Inventing a decelerating arc was the first attempt and it read
# as a different animation played at the same tempo.

# Where the gem rests in VIP's open palm, read off the render.
PALM = (759, 558)

# The original's screen is 320 by 200 and VIP's title is 1536 by 960.
ORIGINAL_WIDTH = 320
TOSS_FRAMES = 9
STRIP_MARGIN = 20

# The gem's width on the 1536-wide render, which matches the ones in the scene.
GEM_WIDTH = 96


def original_toss():
    """The gem's centre in each frame, measured out of the original's own sheet.

    For the first four frames the gem is part of the ARM frames - it is still in
    his hand - and only from the fifth is it in the gem layer of its own. Both
    are read, so the path is the whole throw rather than the airborne half.
    """
    import numpy as np

    sheet = Image.open(GAME / 'titleani.png').convert('RGB')
    layout = json.loads((GAME / 'titleani.json').read_text())

    def centre(layer, index):
        at = layout[layer]['at']
        w, h = layout[layer]['w'], layout[layer]['h']
        sx, sy = layout[layer]['frames'][index]
        a = np.asarray(sheet.crop((sx, sy, sx + w, sy + h))).astype(int)
        blue = (a[..., 2] - a[..., 0]) > 50
        if blue.sum() < 20:
            return None
        ys, xs = np.nonzero(blue)
        return (xs.mean() + at[0], ys.mean() + at[1])

    out = []
    for i in range(TOSS_FRAMES):
        held = centre('arm', min(i, len(layout['arm']['frames']) - 1))
        out.append(held or centre('diamond', i))
    return out


def toss_arc():
    """The original's path, scaled to the render and anchored to VIP's palm."""
    original = original_toss()
    scale = Image.open(GAME.parent / 'vip/title.png').width / ORIGINAL_WIDTH

    ox, oy = original[0]
    dx = PALM[0] - ox * scale
    dy = PALM[1] - oy * scale

    return [(round(x * scale + dx), round(y * scale + dy)) for x, y in original]


# ---- the eyes ------------------------------------------------------------
#
# The original's five face frames are his pupils climbing to follow the gem, not
# a blink. VIP's are the SAME EYES moved, not five generated faces.
#
# Generating them was tried first and does not work. The edit endpoint re-renders
# the whole image: the mouth still correlates at 0.99 but the nose correlates at
# 0.47, the irises grow, and about a quarter of the raised iris pixels land where
# the base's eyebrows are - so there is no box that keeps the brows and the
# raised eyes both. Astra measured that and proposed this instead.
#
# Here the sclera is reconstructed once, the iris is cut out by colour, and it is
# slid up inside the eye's own aperture. The eyebrows, the eyelids, the nose and
# the size of the eye cannot change, because none of them is touched.

# One entry per eye: the patch to work in, and where its iris sits inside it.
EYES = [(895, 345, 960, 413), (969, 345, 1034, 414)]

# How far the pupils climb is EYE_RISE, below, with the smooth title.

# The face layer's own box, big enough for the eyes and nothing else.
FACE_BOX = (880, 335, 1046, 424)


def eye_parts(patch):
    """The eye's aperture, its iris, and the sclera with the iris painted out."""
    import numpy as np

    a = np.asarray(patch).astype(float)
    h, w = a.shape[:2]
    r, g, b = a.transpose(2, 0, 1)

    # The aperture is everything that is not skin - sclera and iris together -
    # filled row by row so the lashes do not punch holes in it.
    lit = ((b > r * 1.12) & (b > g * 1.05)) | ((g > r * 0.78) & (b > r * 0.82) & (g > 80))
    aperture = np.zeros((h, w), 'uint8')
    iris = np.zeros((h, w), 'uint8')

    for row in range(h):
        xs = np.flatnonzero(lit[row])
        if len(xs):
            aperture[row, xs.min():xs.max() + 1] = 255
        xs = np.flatnonzero((b[row] > r[row] * 1.25) & (b[row] > g[row] * 1.1) & (b[row] > 65))
        if len(xs):
            iris[row, xs.min():xs.max() + 1] = 255

    aperture = Image.fromarray(aperture).filter(ImageFilter.GaussianBlur(0.4))
    iris = Image.fromarray(iris).filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.GaussianBlur(0.4))

    # The sclera behind the iris, as a quadratic surface fitted to the white that
    # IS visible. A flat fill would read as a hole, because the eyeball is a
    # sphere with a shadow down one side.
    yy, xx = np.indices((h, w))
    x, y = (xx - w / 2) / w, (yy - h / 2) / h
    terms = np.stack([np.ones_like(x), x, y, x * x, x * y, y * y], axis=-1)

    white = (np.asarray(aperture) > 250) & (np.asarray(iris.filter(ImageFilter.MaxFilter(7))) < 1)
    coefficients = np.linalg.lstsq(terms[white], a[white], rcond=None)[0]
    fitted = Image.fromarray(np.clip(terms @ coefficients, 0, 255).astype('uint8'))

    sclera = Image.composite(fitted, patch, iris.filter(ImageFilter.MaxFilter(3)))
    return aperture, iris, sclera


# ---- VIP's title, drawn at the display's own rate --------------------------
#
# REBUILT on 25 September 2026, after Dan called the title "really shit". It
# was, and for four separate reasons, all visible in one recording:
#
#   * The gem climbed a straight line at a constant seven pixels a frame and
#     reversed instantly at the top. That was the original's path measured and
#     scaled, which at 320 wide and 8.6 frames a second reads as a throw; at
#     1536 wide it reads as a gem teleporting up a ladder.
#   * The eyes had five positions, so they jumped too, and the slide left a
#     pink smear under each iris and cut the top frame's irises off flat.
#   * The "blink" was the alternate expression slaved to the gem's height -
#     the lids came down over a second as the gem rose - and its frames were a
#     hard horizontal wipe into a separately generated face whose nose and
#     cheeks did not match. It read as a squint through a broken picture.
#   * The foot had four angles and ghosted between them.
#
# So VIP no longer steps through frames at the original's rate. game.js draws
# the title every display frame and INTERPOLATES the engine's counters: the
# rules are untouched - the same toss, the same foot, the same draw from the
# generator each toss, the same twenty seconds - and only the picture between
# two counter values is new. What this writes is the material for that:
#
#   gem    one sprite, and the nine points of the original's path, which the
#          renderer keeps for the hand and replaces with a gravity arc once the
#          gem is in the air
#   eyes   one frame per pixel of iris travel, so following the gem is smooth
#   blink  a real upper lid, drawn from the skin above each eye and brought
#          down to a curved edge with a lash line, at several iris positions
#   foot   the shoe cut out and turned about its heel (see the rig below)

# How far the irises climb. Was 26, which pushed the top of the iris into the
# lid and clipped it flat; 18 keeps the whole iris inside the eye at the top.
EYE_RISE = 18

# The shoe. The original's four foot frames are a toe-tap: the shoe's top edge
# rises 7, 6, 5, 3 while its x range never moves, which is a rotation about the
# heel of about eight degrees. VIP's is the same rotation - of the SHOE, cut out
# and turned about its heel, since 25 September 2026. Before that a rounded
# rectangle of the picture was turned, floor and all, so a patch of ground
# tilted with the toe (Dan: "you need to do the same with the foot").
SHOE_POLY = [
    (755, 826), (762, 808), (778, 792), (800, 786), (830, 784), (862, 782),
    (880, 790), (900, 798), (925, 800), (937, 812), (938, 838), (926, 851),
    (880, 857), (820, 858), (780, 852), (760, 841),
]
FOOT_PIVOT = (928, 850)
FOOT_ANGLES = [0.0, 2.0, 4.0, 8.0]
# The trouser hem sits on the back of the shoe and is white like the sole, so
# colour alone would take it in: nothing of the shoe is above this line there.
SHOE_HEM = (850, 794)
# Behind the shoe. A lifted toe only ever uncovers floor under the sole, so
# the floor just below the shoe fills the lower part of the hole; above that
# line the smooth fill is used, because the wall behind the upper is never
# uncovered and copied texture there left a lump of rock at the shoe's edge.
SHOE_FLOOR_LINE = 832
SHOE_FLOOR_DROP = 58
SHOE_SHADOW = 0.58

# The lid positions a blink passes through, as a share of the eye's height,
# and the iris positions a blink is drawn at. A blink lasts a fifth of a
# second, so the iris is taken from the nearest of these rather than exactly.
BLINK_COVERS = [0.22, 0.45, 0.68, 0.86, 1.0]
BLINK_RISES = [0, 5, 9, 14, 18]

def eye_model(base, box):
    """One eye, taken apart: its outline as two smooth curves, its iris as a
    disc, and the white behind the iris painted back in.

    A MODEL rather than masks, because both the first attempts at moving the
    eyes worked on thresholded pixels and both showed it. Cutting the iris out
    by colour and fitting a surface to the white gave a pink smear under a
    raised iris; sampling the lid column by column gave stripes and a ragged
    edge. Curves and a disc are smooth by construction.
    """
    import numpy as np

    patch = base.crop(box).convert('RGB')
    aperture, iris, _ = eye_parts(patch)
    a = np.asarray(patch).astype(float)
    open_ = np.asarray(aperture) > 128
    iris_m = np.asarray(iris) > 128
    h, w = open_.shape

    # The outline: the top and bottom of the opening in each column, smoothed
    # with a quartic so the lid's edge and lash line are curves, not steps.
    xs, tops, bots = [], [], []
    for x in range(w):
        ys = np.flatnonzero(open_[:, x])
        if len(ys) > 2:
            xs.append(x); tops.append(ys.min()); bots.append(ys.max())
    xs = np.array(xs, float)
    top = np.poly1d(np.polyfit(xs, tops, 4))
    bot = np.poly1d(np.polyfit(xs, bots, 4))
    span = (xs.min(), xs.max())

    # The iris as a disc. Its top is under the lid, so the width and the lowest
    # visible row say where it is, not the area.
    iy, ix = np.nonzero(iris_m)
    r = (ix.max() - ix.min()) / 2
    cx = (ix.max() + ix.min()) / 2
    cy = iy.max() - r

    # The white behind the iris, diffused in from all the white around it - a
    # harmonic fill, each hidden pixel settling to the average of its
    # neighbours - so the shading carries smoothly across the gap with no
    # rows, streaks or flat patches. Two row-by-row blends were tried first
    # and showed both.
    hole = open_ & (np.asarray(iris.filter(ImageFilter.MaxFilter(7))) > 0)
    inside = open_.astype(float)
    white = a.copy()
    white[hole] = a[open_ & ~hole].mean(axis=0)
    for _ in range(600):
        acc = np.zeros_like(white)
        cnt = np.zeros((h, w))
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            shifted = np.roll(np.roll(white, dy, 0), dx, 1)
            valid = np.roll(np.roll(inside, dy, 0), dx, 1)
            acc += shifted * valid[..., None]
            cnt += valid
        avg = acc / np.maximum(cnt, 1)[..., None]
        white[hole] = avg[hole]

    return {
        'patch': a, 'aperture': aperture, 'open': open_,
        'top': top, 'bot': bot, 'span': span,
        'iris': (cx, cy, r), 'white': white, 'size': (w, h),
    }


def disc_alpha(w, h, cx, cy, r, feather=1.2):
    import numpy as np
    yy, xx = np.indices((h, w))
    d = np.hypot(xx - cx, yy - cy)
    return np.clip((r + feather / 2 - d) / feather, 0, 1)


def track_frame(base, rise, models):
    """The base with both irises moved up by `rise` pixels, as a full picture."""
    import numpy as np

    frame = base.copy()
    if rise == 0:
        return frame

    for box, m in zip(EYES, models):
        w, h = m['size']
        cx, cy, r = m['iris']
        eye = m['white'].copy()

        # The iris disc out of the base, moved up. Where its top was under the
        # lid in the base it is still under the lid here, because the opening
        # clips it below.
        src = disc_alpha(w, h, cx, cy, r)
        moved_alpha = np.zeros((h, w))
        moved = np.zeros_like(eye)
        dy = rise
        moved_alpha[:h - dy] = src[dy:]
        moved[:h - dy] = m['patch'][dy:]
        eye = eye * (1 - moved_alpha[..., None]) + moved * moved_alpha[..., None]

        out = Image.fromarray(np.clip(eye, 0, 255).astype('uint8'))
        frame.paste(out, box[:2], m['aperture'])

    return frame


def lid_frame(base, open_frame, cover, models):
    """`open_frame` with both upper lids brought down to `cover` of the eye.

    One skin colour per eye, sampled from the lid above it, on a gentle
    gradient - so the lid is the same colour as the face round it and has no
    stripes. Its edge runs between the two smoothed outline curves, so it is
    curved the way a lid is, with an antialiased lash line and a soft shadow on
    the eyeball below it.
    """
    import numpy as np

    out = np.asarray(open_frame.convert('RGB')).astype(float).copy()
    src = np.asarray(base.convert('RGB')).astype(float)

    for box, m in zip(EYES, models):
        x0, y0 = box[:2]
        w, h = m['size']
        top, bot = m['top'], m['bot']
        lo, hi = m['span']

        # The lid's own colour: the skin in a band just above the opening.
        band = []
        for x in range(int(lo) + 4, int(hi) - 4):
            t = int(round(top(x)))
            band.extend(src[y0 + t - 8:y0 + t - 3, x0 + x])
        skin = np.median(np.array(band), axis=0)

        # A little wider than the opening, so no rim of the open eye survives
        # round a closed lid; soft at its sides.
        reach = np.asarray(m['aperture'].filter(ImageFilter.MaxFilter(5))
                           .filter(ImageFilter.GaussianBlur(1))) / 255.0

        for x in range(w):
            t0, b0 = top(x) - 2, bot(x) + 1.5
            if x < lo - 3 or x > hi + 3 or b0 <= t0:
                continue
            edge = t0 + cover * (b0 - t0)
            for y in range(max(0, int(t0) - 1), min(h, int(b0) + 3)):
                gx, gy = x0 + x, y0 + y
                side = reach[y, x]
                if side <= 0:
                    continue

                lid = np.clip(edge - y + 0.5, 0, 1) * side
                if lid > 0:
                    # Round, not flat: the lid lies over an eyeball, so it is
                    # lit at its crown and falls off to the sides and towards
                    # its edge.
                    depth = np.clip((y - t0) / max(1.0, edge - t0), 0, 1)
                    across = (x - (lo + hi) / 2) / max(1.0, (hi - lo) / 2)
                    shade = skin * (1.05 - 0.10 * depth - 0.12 * across * across)
                    out[gy, gx] = out[gy, gx] * (1 - lid) + shade * lid

                if cover > 0.08:
                    d = abs(y - edge)
                    if d < 1.5:
                        k = (1.5 - d) / 1.5 * side
                        out[gy, gx] = out[gy, gx] * (1 - 0.8 * k) + np.array([74, 34, 40]) * 0.8 * k

                below = y - edge
                if 0 < below < 5 and cover < 1:
                    out[gy, gx] *= 1 - 0.2 * (1 - below / 5) * side

    return Image.fromarray(np.clip(out, 0, 255).astype('uint8'))


# ---- the rig: his arm and his head, cut out so they can move -------------
#
# Added 25 September 2026, when Dan called the smooth title "still shit" and
# chose rigging over a generated video. He was right about what was wrong:
# the gem moved and nothing else did, so it lifted off a dead-still palm and
# landed in one that never reacted - a sprite sliding past a statue.
#
# So two parts of the picture are cut out and moved by game.js: the THROWING
# ARM, about the shoulder, and the HEAD, about the neck. Each is a cut-out of
# the base with a soft outline and a long fade into the body at the joint, so
# the joint never shows a seam. Behind them is a PLATE - the base with the
# arm and head painted out - so the sliver a moved part uncovers is rock or
# cave, not a second arm. The plate is a diffusion fill: only ever seen a few
# pixels deep at an edge that is moving, which is where a fill can pass.

ARM_POLY = [
    (927, 490), (905, 496), (880, 509), (850, 526), (820, 540), (800, 546),
    (790, 542), (760, 537), (740, 535), (726, 539), (713, 547), (699, 552),
    (687, 556), (681, 565), (688, 577), (705, 583), (730, 590), (760, 597),
    (786, 598), (806, 594), (832, 590), (862, 584), (892, 575), (914, 567),
    (934, 562),
]
ARM_PIVOT = (918, 522)
# The fade into the body: fully arm to the left of the first x, fully body to
# the right of the second.
ARM_FADE = (888, 926)
# How far below the arm the rock is taken from to fill behind it.
ARM_TEXTURE_DROP = 70

HEAD_POLY = [
    (829, 420), (838, 392), (866, 362), (872, 332), (897, 302), (922, 280),
    (952, 270), (985, 264), (1016, 274), (1042, 278), (1074, 302), (1087, 342),
    (1096, 368), (1114, 398), (1121, 430), (1111, 462), (1082, 482),
    (1042, 496), (992, 503), (948, 503), (900, 491), (860, 468), (835, 446),
]
HEAD_PIVOT = (962, 505)
HEAD_FADE = (474, 502)          # fully head above the first y, body below the second


def soft_mask(size, poly, blur=1.4):
    m = Image.new('L', size, 0)
    ImageDraw.Draw(m).polygon(poly, fill=255)
    return m.filter(ImageFilter.GaussianBlur(blur))


def ramp(size, axis, a, b):
    """1 before a, falling to 0 at b, along x (axis 0) or y (axis 1)."""
    import numpy as np
    w, h = size
    coord = np.arange(w if axis == 0 else h, dtype=float)
    r = np.clip((b - coord) / (b - a), 0, 1)
    r = r * r * (3 - 2 * r)
    return np.tile(r, (h, 1)) if axis == 0 else np.tile(r[:, None], (1, w))


def diffuse_fill(img, hole, iterations=80):
    """Paints `hole` in from the pixels round it, coarse to fine, so a large
    region fills in seconds rather than needing thousands of passes."""
    import numpy as np
    h, w = hole.shape
    if min(h, w) > 24:
        known = (~hole).astype(float)
        # Downsample by two, averaging only the known pixels.
        hh, ww = h // 2 * 2, w // 2 * 2
        k4 = known[:hh, :ww].reshape(hh // 2, 2, ww // 2, 2)
        i4 = (img[:hh, :ww] * known[:hh, :ww, None]).reshape(hh // 2, 2, ww // 2, 2, 3)
        ksum = k4.sum(axis=(1, 3))
        small = i4.sum(axis=(1, 3)) / np.maximum(ksum, 1e-6)[..., None]
        small_hole = ksum < 0.5
        small = diffuse_fill(small, small_hole, iterations)
        up = np.repeat(np.repeat(small, 2, 0), 2, 1)
        full = np.zeros_like(img)
        full[:hh, :ww] = up
        full[hh:, :] = full[hh - 1:hh, :] if hh < h else full[hh:, :]
        full[:, ww:] = full[:, ww - 1:ww] if ww < w else full[:, ww:]
        img = img.copy()
        img[hole] = full[hole]
    else:
        img = img.copy()
        if (~hole).any():
            img[hole] = img[~hole].mean(axis=0)
    for _ in range(iterations):
        acc = (np.roll(img, 1, 0) + np.roll(img, -1, 0) + np.roll(img, 1, 1) + np.roll(img, -1, 1)) / 4
        img[hole] = acc[hole]
    return img


def grow(poly, size, px):
    import numpy as np
    m = Image.new('L', size, 0)
    ImageDraw.Draw(m).polygon(poly, fill=255)
    if px > 0:
        m = m.filter(ImageFilter.MaxFilter(px * 2 + 1))
    elif px < 0:
        m = m.filter(ImageFilter.MinFilter(-px * 2 + 1))
    return np.asarray(m) > 0


def tidy(mask, close=7):
    """Closed, so a shadowed fold is not a hole, then opened, so a speck of
    the right colour out in the rock does not come along."""
    m = Image.fromarray((mask * 255).astype('uint8'))
    m = m.filter(ImageFilter.MaxFilter(close)).filter(ImageFilter.MinFilter(close))
    m = m.filter(ImageFilter.MinFilter(5)).filter(ImageFilter.MaxFilter(5))
    return m


def arm_mask(rgb):
    """The arm by colour: glove white and sleeve red and white, near the drawn
    outline. The outline's own interior is not used, because it takes in the
    rock in the crook between forearm and sleeve. The cave behind him is never
    glove-white or sleeve-red."""
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    lo, hi = rgb.min(axis=2), rgb.max(axis=2)
    white = (lo > 115) & (hi - lo < 70) & (b > 0.8 * r)
    red = (r > 140) & (r - g > 60) & (r - b > 50)
    size = (rgb.shape[1], rgb.shape[0])
    near = grow(ARM_POLY, size, 14)
    # The hand, left of the cuff, also takes the drawn outline: the glove
    # fades into shadow at the fingertips, colour alone broke the index finger
    # up, and a fingertip left behind in the plate would stay put while the
    # hand moved. There is no rock inside the outline there to leak in.
    # Not the dark rock inside that outline, though: round the fingers the
    # rock's darkest channel sits at 41 to 53, and the glove's own shadowed
    # edge at 84 and up.
    hand = grow(ARM_POLY, size, 1) & (lo > 65)
    hand[:, 795:] = False
    return tidy((near & (white | red)) | hand, close=11)


def head_mask(rgb):
    """The head: its drawn outline, which sits inside the face, grown by hair
    colour far enough to take every spike and by skin colour just past the edge.

    Saturation is what separates him from the rock. Sunlit rock is tan - red
    over blue by about ninety - and looser tests took patches of it in."""
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    size = (rgb.shape[1], rgb.shape[0])
    yellow = (r > 180) & (g > 130) & (r - b > 130) & (b < 100)
    pink = (r > 170) & (r - g > 60) & (abs(g - b) < 45)
    m = grow(HEAD_POLY, size, 0) | (grow(HEAD_POLY, size, 26) & yellow) | (grow(HEAD_POLY, size, 8) & pink)
    return tidy(m)


def shoe_mask(rgb):
    """The shoe by colour - its red and its white sole and stripes - near the
    drawn outline, and not the trouser hem above it."""
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    lo, hi = rgb.min(axis=2), rgb.max(axis=2)
    white = (lo > 140) & (hi - lo < 75)
    red = (r > 130) & (r - g > 60) & (r - b > 50)
    size = (rgb.shape[1], rgb.shape[0])
    m = grow(SHOE_POLY, size, 6) & (white | red)
    m |= grow(SHOE_POLY, size, -6)
    # Above the hem line on the heel side, the trouser is white and the shoe
    # is red: keep the red, drop the white.
    hx, hy = SHOE_HEM
    m[:hy, hx:] &= red[:hy, hx:]
    return tidy(m, close=9)


def make_plate(rgb, hole, texture=None, shift=(0, 0), shade=1.0):
    """The picture with `hole` painted out, as an RGBA patch opaque only where
    it differs from the base. Where `texture` is set, real texture from `shift`
    pixels away is used rather than the diffusion fill."""
    import numpy as np
    ys, xs = np.nonzero(hole)
    x0, y0, x1, y1 = xs.min() - 8, ys.min() - 8, xs.max() + 9, ys.max() + 9
    filled = diffuse_fill(rgb[y0:y1, x0:x1], hole[y0:y1, x0:x1])

    if texture is not None:
        dx, dy = shift
        moved = np.roll(np.roll(rgb, -dy, axis=0), -dx, axis=1)[y0:y1, x0:x1]
        local = texture[y0:y1, x0:x1]
        soft = np.asarray(Image.fromarray((local * 255).astype('uint8'))
                          .filter(ImageFilter.GaussianBlur(3))) / 255.0
        soft = np.where(local, 1.0, soft)[..., None]
        filled = filled * (1 - soft) + moved * shade * soft

    edge = np.asarray(Image.fromarray((hole[y0:y1, x0:x1] * 255).astype('uint8'))
                      .filter(ImageFilter.MaxFilter(7)).filter(ImageFilter.GaussianBlur(2)))
    plate = Image.fromarray(np.dstack([np.clip(filled, 0, 255), edge]).astype('uint8'), 'RGBA')
    return plate, (int(x0), int(y0))


def rig_parts(base):
    """The arm and head cut-outs, and the plate behind them."""
    import numpy as np
    size = base.size
    rgb = np.asarray(base.convert('RGB')).astype(float)

    arm_m = arm_mask(rgb)
    head_m = head_mask(rgb)

    arm_a = np.asarray(arm_m.filter(ImageFilter.GaussianBlur(1.1))) / 255.0 * ramp(size, 0, *ARM_FADE)
    head_a = np.asarray(head_m.filter(ImageFilter.GaussianBlur(1.1))) / 255.0 * ramp(size, 1, *HEAD_FADE)

    # The plate is filled only where a part is wholly itself: in the fade into
    # the body it stays the body, because that is what shows there.
    arm_hole = np.asarray(arm_m.filter(ImageFilter.MaxFilter(7))) > 0
    arm_hole &= ramp(size, 0, *ARM_FADE) > 0.97
    head_hole = np.asarray(head_m.filter(ImageFilter.MaxFilter(7))) > 0
    head_hole &= ramp(size, 1, *HEAD_FADE) > 0.97
    hole = arm_hole | head_hole

    # Behind the ARM, real rock rather than the fill. A lifted hand uncovers a
    # patch the width of the hand, and a diffusion fill that size reads as a
    # blurred smudge. The rock a hand's depth below the arm is the same wall,
    # so its texture is lifted into the hole; the fill stays for the head,
    # which only ever uncovers a sliver at its outline.
    upper, upper_at = make_plate(rgb, hole, arm_hole, (0, ARM_TEXTURE_DROP))

    # The shoe, and the floor under it behind it.
    shoe_m = shoe_mask(rgb)
    shoe_hole = np.asarray(shoe_m.filter(ImageFilter.MaxFilter(7))) > 0
    floor = shoe_hole.copy()
    floor[:SHOE_FLOOR_LINE] = False
    # Darkened: the shoe casts a contact shadow on the floor under it, and the
    # floor a lifted toe uncovers is in that shadow - the lit floor copied from
    # below read as a light shelf under the sole.
    lower, lower_at = make_plate(rgb, shoe_hole, floor, (0, SHOE_FLOOR_DROP), shade=SHOE_SHADOW)

    def cut(alpha):
        ys, xs = np.nonzero(alpha > 0.004)
        bx0, by0, bx1, by1 = xs.min(), ys.min(), xs.max() + 1, ys.max() + 1
        rgba = np.dstack([rgb[by0:by1, bx0:bx1], alpha[by0:by1, bx0:bx1] * 255])
        return Image.fromarray(np.clip(rgba, 0, 255).astype('uint8'), 'RGBA'), (int(bx0), int(by0))

    arm, arm_at = cut(arm_a)
    head, head_at = cut(head_a)
    shoe, shoe_at = cut(np.asarray(shoe_m.filter(ImageFilter.GaussianBlur(1.1))) / 255.0)
    return (arm, arm_at), (head, head_at), (shoe, shoe_at), [(upper, upper_at), (lower, lower_at)]


# Transparent space round every frame on the sheet. A part drawn turned and
# smoothed samples a pixel or two beyond its own rectangle, and without this it
# picked up whatever was packed beside it - thin light lines across the rock.
GUTTER = 3


def pack(sheet_rows):
    """Lays rows of equal-sized frames down a sheet, a gutter round each.
    Returns the sheet and the origin of every frame, row by row."""
    g = GUTTER
    width = max(len(frames) * (frames[0].size[0] + g) + g for frames in sheet_rows)
    height = sum(frames[0].size[1] + g for frames in sheet_rows) + g
    sheet = Image.new('RGBA', (width, height), (0, 0, 0, 0))

    origins, y = [], g
    for frames in sheet_rows:
        w, h = frames[0].size
        row = []
        for i, f in enumerate(frames):
            x = g + i * (w + g)
            sheet.alpha_composite(f.convert('RGBA'), (x, y))
            row.append([x, y])
        origins.append(row)
        y += h + g
    return sheet, origins


def cmd_titleani():
    """Builds vip/titleani.png and vip/titleani.json for the smooth title."""
    art = ROOT / 'web/Rockford.Web/wwwroot/vip'
    gem_src = WORK / 'gem.png'
    if not gem_src.exists():
        print('no gem yet - generate build/vip-art/gem.png first')
        return 1

    gem = Image.open(gem_src).convert('RGBA')
    gem = gem.crop(gem.getbbox())
    gem = gem.resize((GEM_WIDTH, round(gem.height * GEM_WIDTH / gem.width)), Image.LANCZOS)

    base = Image.open(art / 'title.png').convert('RGB')

    models = [eye_model(base, box) for box in EYES]
    eyes = [track_frame(base, r, models).crop(FACE_BOX) for r in range(EYE_RISE + 1)]

    blink_rows = []
    for rise in BLINK_RISES:
        opened = track_frame(base, rise, models)
        blink_rows.append([lid_frame(base, opened, c, models).crop(FACE_BOX) for c in BLINK_COVERS])

    (arm, arm_at), (head, head_at), (shoe, shoe_at), plates = rig_parts(base)

    sheet, origins = pack([[gem], eyes, *blink_rows, [arm], [head], [shoe],
                           *[[p] for p, _ in plates]])
    gem_at, eye_at = origins[0][0], origins[1]
    blink_at = origins[2:2 + len(BLINK_RISES)]
    rest = [o[0] for o in origins[2 + len(BLINK_RISES):]]
    arm_src, head_src, shoe_src, plate_srcs = rest[0], rest[1], rest[2], rest[3:]

    layout = {
        # The renderer reads this and draws the title itself; a layout without
        # it is stepped through frame by frame the way the faithful build is.
        'smooth': True,
        'gem': {
            'src': [gem_at[0], gem_at[1], gem.width, gem.height],
            # The centre of the gem at each of the engine's nine toss counts.
            'arc': [list(p) for p in toss_arc()],
        },
        'eyes': {
            'at': list(FACE_BOX[:2]),
            'w': eyes[0].width, 'h': eyes[0].height,
            'frames': eye_at,
        },
        'blink': {
            'at': list(FACE_BOX[:2]),
            'w': eyes[0].width, 'h': eyes[0].height,
            'rises': BLINK_RISES,
            'covers': BLINK_COVERS,
            'frames': blink_at,
        },
        # The rig. `at` is where the part sits in the base picture, `pivot`
        # the joint it turns about, both in the base's own coordinates.
        'arm': {
            'src': [arm_src[0], arm_src[1], arm.width, arm.height],
            'at': list(arm_at), 'pivot': list(ARM_PIVOT),
            # Where the gem sits on his palm, at rest.
            'palm': list(PALM),
        },
        'head': {
            'src': [head_src[0], head_src[1], head.width, head.height],
            'at': list(head_at), 'pivot': list(HEAD_PIVOT),
        },
        'foot': {
            'src': [shoe_src[0], shoe_src[1], shoe.width, shoe.height],
            'at': list(shoe_at), 'pivot': list(FOOT_PIVOT),
            # The engine's four foot counts, as angles - the original's toe-tap.
            'counts': FOOT_ANGLES,
        },
        # What is behind the moving parts, drawn before them.
        'plates': [
            {'src': [src[0], src[1], img.width, img.height], 'at': list(at)}
            for src, (img, at) in zip(plate_srcs, plates)
        ],
    }

    art.mkdir(parents=True, exist_ok=True)
    sheet.save(art / 'titleani.png', optimize=True)
    json.dump(layout, open(art / 'titleani.json', 'w'))
    print(f'titleani.png {sheet.size} and titleani.json in {art}')
    return 0


def main():
    if len(sys.argv) < 2:
        print(__doc__.strip())
        return 2

    command = sys.argv[1]
    if command == 'refs':
        return cmd_refs()
    if command in ('draft', 'final'):
        return generate(command)
    if command == 'install':
        return cmd_install()
    if command == 'titleani':
        return cmd_titleani()

    print(__doc__.strip())
    return 2


if __name__ == '__main__':
    sys.exit(main() or 0)
