// Canvas plumbing for the Rockford port.
//
// The simulation itself is the C# engine running in WebAssembly. This file only
// blits 16 by 16 tiles out of the original game's own sprite sheet and keeps a
// record of which keys are down. It holds no game rules.

const TILE = 16;
// The window on the cave, not the cave. The original renderer walks 20 by 11 of a
// 40 by 22 cave and scrolls it to follow the player.
const COLS = 20;
const ROWS = 11;
// The cave itself. draw() is handed all of it, because a view standing part way
// between two cells needs the row and column it is sliding off as well as the
// ones it is sliding on.
const CAVE_COLS = 40;
const CAVE_ROWS = 22;

/** Monotonic where there is one. The stubs in tools/jslifecycle.mjs drive it. */
const now = () => typeof performance === 'object' && performance?.now
    ? performance.now() : Date.now();

let canvas = null;
let sheetScale = 1;   // sheet pixels per original pixel; 8 for VIP atlases
let ctx = null;
let sheet = null;
let scale = 2;
const keys = new Set();

// Whether the page has been interacted with at all. Browsers refuse to start
// audio before that, and this is the record of it - see the listeners in init().
let gestured = false;

// Set once the assets are in, so the very first gesture starts the theme rather
// than the tick after it noticing. Browsers will not let audio start before the
// page has been interacted with at all - that is not a policy this can route
// around - so the first gesture is the earliest legal moment there is, and the
// point of this is to take it rather than to wait 100 ms for the next step.
let musicArmed = false;

// Everything init() attached, as functions that detach it again.
//
// A Blazor component can be disposed and remounted - navigate away and back, or
// hot-reload in development - and this module survives that, because releasing an
// IJSObjectReference does not unload the module it points at. Without a record of
// what was attached, every remount added another full set of listeners to the same
// window, and the old ones went on reading keys into a canvas that was gone.
let teardown = [];

// Bumped by every init and every dispose.
//
// Releasing an IJSObjectReference does not cancel JavaScript that is already in
// flight, and none of the loads below are cancellable anyway. So each one takes a
// copy of this on the way in and gives up if it has moved by the time the image
// arrives. Without it a sheet requested by a component that has since gone away
// lands in the module a moment later and replaces the one the NEW component is
// drawing with - which is a cave rendered in the previous world's tiles.
let generation = 0;

/**
 * Whether a key is meant for something on the page rather than for the game.
 *
 * Everything focusable here is a control - the sound glyphs, the Game menu and
 * its items, the cave stepper. A key aimed at one of those belongs to it.
 *
 * Deliberately a test of what has FOCUS rather than of which element the game
 * is in. Clicking the page background and carrying on playing has to keep
 * working, and that is the common case: focus sits on the body, which is not a
 * control, and the game reads the key.
 */
function aimedAtAControl(event) {
    const target = event.target;
    if (!target || typeof target.closest !== 'function') return false;

    // A canvas with tabindex is focusable and is NOT a control - it is the game.
    if (target === canvas) return false;

    return !!target.closest(
        'button, a[href], input, select, textarea, summary, [role="menuitem"], [role="menuitemradio"], [contenteditable="true"]');
}

/** Attaches a listener and remembers how to take it off again. */
function on(target, type, handler, options) {
    target.addEventListener(type, handler, options);
    teardown.push(() => target.removeEventListener(type, handler, options));
}

export function init(canvasElement, sheetUrl, atlasScale = 1, smoothMotion = false) {
    // Idempotent. init is what a remount calls, and it must not stack a second
    // set of everything on top of the first.
    dispose();
    clearInterrupt();
    const mine = ++generation;

    canvas = canvasElement;
    sheetScale = atlasScale;
    blending = smoothMotion;
    ctx = canvas.getContext('2d', { alpha: false });
    ctx.imageSmoothingEnabled = false;

    // Two key spaces, deliberately, because the two readers want different things.
    //
    // `keys` is what is HELD, and it is keyed on e.code - the physical key. e.key
    // is the character produced, so it changes under a modifier: with Shift down,
    // the key marked A reports 'A' and not 'a'. The game's own instructions are
    // "Arrows or WASD move. Shift or space with a direction fires", so on WASD
    // every direction was silently dropped for as long as fire was held. Worse,
    // press a, then Shift, then release both: the keyup arrives as 'A', deletes
    // something that was never added, and 'a' stays in the set with Rockford
    // walking left until the window loses focus.
    //
    // `tapped` is what was PRESSED once, for the menus, and it stays on e.key
    // because those match named keys - Enter, space, the arrows - none of which
    // change under a modifier, and Home.razor asks for them by those names.
    //
    // e.code is a physical position, so on a non-QWERTY layout KeyA is not the
    // key marked A. That is the right trade for WASD, which is a shape under the
    // left hand rather than four letters, and remapping is the real answer - see
    // the input adapter issue.
    on(window, 'keydown', e => {
        noteGesture();

        // Not while a control is being used.
        //
        // These listen on the window, so the arrows and space were being taken
        // from whatever had focus and preventDefault'd on top: tabbing to the
        // Game menu and pressing Down moved Rockford and left the menu alone,
        // and space on a focused button played the game instead of pressing it.
        // Everything keyboard-reachable in this page is a button.
        if (aimedAtAControl(e)) return;

        if (!keys.has(e.code)) tapped.add(e.key);
        keys.add(e.code);
        // Stop the arrows and space scrolling the page out from under the cave.
        if (e.code.startsWith('Arrow') || e.code === 'Space') e.preventDefault();
    });

    // The keyup is NOT gated the same way, and that asymmetry is deliberate.
    //
    // Focus can move between a key going down and coming up - pressing a button
    // with space, or tabbing away mid-hold. Refusing the release because the
    // target changed would leave the key held for ever, which is the exact
    // failure the touch pad's window-level release exists to prevent.
    on(window, 'keyup', e => keys.delete(e.code));

    // Any gesture opens the audio, not only one the game reads as input.
    //
    // The title screen says "Press Enter to begin", and Enter is not in
    // readInput's encoding: it is not a direction and it is not fire, so that
    // call returns 0 for it. Home.razor used to unlock on `raw != 0`, which
    // meant pressing the key the screen asked for did nothing, and the theme
    // did not start until the player first moved inside a cave. It read as the
    // music being broken, and on a run where somebody stood still it never
    // played at all.
    on(window, 'pointerdown', noteGesture, { passive: true });
    on(window, 'touchstart', noteGesture, { passive: true });

    // A tap on the picture is Enter, outside a cave.
    //
    // Every screen that asks to be pressed IS the canvas - the title, the world
    // select, the interlude - so this is what makes the game startable with no
    // keyboard at all. Gated on tapStarts, because during play a tap on the
    // picture must not be a keystroke: a hand resting on a phone screen would
    // otherwise play the game.
    on(canvas, 'pointerdown', e => {
        if (!tapStarts) return;
        e.preventDefault();
        tapped.add('Enter');
    });
    const letGo = () => {
        // A finger or a key held when the page goes away never gets its release,
        // so it would still be held on return.
        keys.clear();
        releaseAllPointers();

        // Forgotten rather than zeroed. The gamepad is polled, so nothing is
        // held across a hide - but claiming the stick was centred would make a
        // stick still pushed on return look like a fresh press.
        lastStick = null;
    };

    on(window, 'blur', letGo);

    // blur is not reliable for this on a phone. Switching apps, locking the
    // screen or pulling down a notification shade hides the page, and iOS in
    // particular does not promise a blur for it. pagehide covers the back/forward
    // cache on top of that.
    on(document, 'visibilitychange', () => { if (document.hidden) { interrupt(); letGo(); } });
    on(window, 'pagehide', () => { interrupt(); letGo(); });
    on(window, 'resize', fit);

    // The canvas sits in a grid cell, so its box changes with the layout and not
    // only with the window. Watch the box itself.
    if (canvas.parentElement && 'ResizeObserver' in window) {
        const observer = new ResizeObserver(fit);
        observer.observe(canvas.parentElement);
        teardown.push(() => observer.disconnect());
    }

    // The load phase has usually decoded this atlas already. Taken as it is,
    // rather than decoded a second time on the first frame of the first cave.
    if (pictures.has(sheetUrl)) {
        sheet = pictures.get(sheetUrl);
        fit();
        return Promise.resolve(true);
    }

    return new Promise(resolve => {
        const image = new Image();
        sheet = image;

        // Guarded, because the load can outlive the component that asked for it.
        // A page disposed while the sheet is still downloading would otherwise
        // paint into a canvas that is no longer on the document.
        image.onload = () => {
            if (mine !== generation) { resolve(false); return; }
            fit();
            resolve(true);
        };
        image.onerror = () => resolve(false);
        // The load phase's in-memory copy when there is one, so this is not a
        // second download of an atlas the bar has just fetched.
        image.src = localUrl(sheetUrl);
    });
}

/**
 * Detaches everything and silences everything. Called from DisposeAsync, and by
 * init on the way in.
 *
 * The audio context is deliberately left alive. It is expensive to build, a
 * remount can reuse it, and the two VIP modules hold references into it that a
 * close would invalidate - so this stops what is sounding rather than tearing
 * down the graph.
 */
export function dispose() {
    generation++;
    stopAllAudio();

    for (const off of teardown) off();
    teardown = [];

    keys.clear();
    tapped.clear();
    releaseAllPointers();
    lastStick = null;
    tapStarts = false;
    forgetTitle();
    leaveField();
    endCartoon();

    canvas = null;
    ctx = null;
    sheet = null;
}

// The cave floor. Palette index 0 of the world's own sheet, which the asset build
// makes transparent so sprites composite cleanly - so it has to be painted back
// here, or every dug-out cell comes out black instead of the world's own colour.
let floor = '#000';

export async function setSheet(sheetUrl, floorColour, atlasScale = 1) {
    const mine = generation;
    const img = await load(sheetUrl);
    if (!img || mine !== generation) return false;

    // After the guard, all of it. Setting the floor colour before the await left
    // a disposed component's world tinting the next one.
    if (floorColour) floor = floorColour;
    sheet = img;
    sheetScale = atlasScale;
    silhouette = null;          // a new world needs a new one
    return true;
}

/*
 * ---- outlines -----------------------------------------------------------
 *
 * Issue #70: a boulder has to read against the floor it is sitting on, and
 * neither the redraws nor the original's sixteen colours guarantee that. In a
 * world where the floor is brown and the rocks are brown, the thing that is
 * about to fall on you is a texture.
 *
 * OFF BY DEFAULT and a setting, because it is a legibility aid rather than a
 * correction: the picture without it is the picture the artwork intends, and
 * some people will find a permanent dark edge on every object worse than the
 * problem it solves.
 *
 * ONLY MOVABLE OBJECTS. Outlining the walls too would draw a grid over the
 * whole cave and defeat the purpose - the point is to separate the things that
 * move and matter from the background they move against, and if everything is
 * outlined then nothing is.
 *
 * The silhouette is baked ONCE per sheet rather than per tile per frame. The
 * alternative is a composite operation per sprite at sixty frames a second, on
 * a 2560-wide atlas.
 */
let outlines = false;
let silhouette = null;

/**
 * The SHEET POSITIONS worth outlining, as sx * 65536 + sy.
 *
 * Positions rather than runtime values, because the per-frame payload carries
 * sheet coordinates and nothing else - it is 880 cells ten times a second and
 * widening it to say what each one IS would cost more than this whole feature
 * saves. The page works the positions out once from TileSet, which is the thing
 * that knows, and sends them when the sheet changes.
 */
let outlinedTiles = new Set();

export function setOutlinedTiles(flat) {
    outlinedTiles = new Set();
    if (!flat) return;
    for (let i = 0; i + 1 < flat.length; i += 2) {
        outlinedTiles.add(flat[i] * 65536 + flat[i + 1]);
    }
}

export function setOutlines(on) {
    outlines = !!on;
    repaint?.();
}

/*
 * ---- lighting -----------------------------------------------------------
 *
 * Issue #37, and it is last in the roadmap on purpose.
 *
 * THE RULE IT IS BUILT AROUND: the cave stays readable with the lights off.
 * Darkness that conceals a hazard, a moving spotlight, heavy bloom, motion blur
 * and chromatic aberration all work against this game's spatial precision - a
 * player has to see a boulder two cells up and decide in a tenth of a second.
 * So this ADDS light and never removes it. There is no darkening pass, no
 * vignette and no ambient occlusion, and turning it off changes how the cave
 * feels rather than what can be seen in it.
 *
 * Two things only:
 *
 *   a contact shadow under anything that can fall, which is what gives an
 *   object weight and says which cell it is actually in
 *
 *   a restrained glow around the things that genuinely emit - fire, and an exit
 *   once it has opened, which is the single most important state change in a
 *   cave and currently looks like any other tile
 *
 * Both reuse the silhouette baked for the outlines rather than introducing a
 * second technology, which is what the issue asks for. A shadow is that mask
 * drawn low, soft and transparent; a glow is it drawn large and lit.
 */
let lighting = false;

export function setLighting(on) {
    lighting = !!on;
    repaint?.();
}

/** Sheet positions that emit, as sx * 65536 + sy, with their colour. */
let emissive = new Map();

/**
 * The emissive metadata, from the VIP manifest.
 *
 * Positions and colours, for the same reason the outline set is positions: the
 * per-frame payload carries sheet coordinates and nothing else.
 */
export function setEmissive(flat) {
    emissive = new Map();
    if (!flat) return;
    for (let i = 0; i + 2 < flat.length; i += 3) {
        emissive.set(flat[i] * 65536 + flat[i + 1], flat[i + 2]);
    }
}

/** Sheet positions that cast a contact shadow. */
let shadowed = new Set();

export function setShadowed(flat) {
    shadowed = new Set();
    if (!flat) return;
    for (let i = 0; i + 1 < flat.length; i += 2) {
        shadowed.add(flat[i] * 65536 + flat[i + 1]);
    }
}

const EMISSIVE_COLOURS = ['#ff7a2f', '#ffd24a', '#7ad0ff'];

/**
 * A copy of the sheet with every opaque pixel turned one colour.
 *
 * Drawn at four offsets under a sprite, it reads as an outline. Four rather
 * than eight: the diagonals cost 50% more draw calls for a corner nobody can
 * see at this size.
 */
function buildSilhouette() {
    if (silhouette || !sheet || typeof document === 'undefined') return silhouette;

    const c = document.createElement('canvas');
    c.width = sheet.naturalWidth || sheet.width;
    c.height = sheet.naturalHeight || sheet.height;

    const g = c.getContext('2d');
    if (!g) return null;

    g.drawImage(sheet, 0, 0);

    // Keeps the alpha and replaces the colour, which is what makes this a
    // silhouette rather than a tinted copy.
    g.globalCompositeOperation = 'source-in';
    g.fillStyle = '#05040a';
    g.fillRect(0, 0, c.width, c.height);

    silhouette = c;
    return silhouette;
}

// One-shot key reads for the menus, where holding a key should not scroll through
// five worlds in half a second.
const tapped = new Set();

export function takeTap(key) {
    if (!tapped.has(key)) return false;
    tapped.delete(key);
    return true;
}

/**
 * Consumes any pending key, for a "press anything" screen.
 *
 * Clears the whole set rather than one entry: the point is that the next screen
 * starts from nothing pressed, and leaving a stray tap behind would have the
 * interlude dismissed and the next cave take a step from the same keystroke.
 */
export function takeAnyTap() {
    if (tapped.size === 0) return false;
    tapped.clear();
    return true;
}

/**
 * Throws away pending taps.
 *
 * Gameplay reads held keys rather than taps, so `tapped` fills up with every
 * movement key pressed during a cave and never drains. A "press anything" screen
 * opening on that backlog dismisses itself on its first tick - about a tenth of
 * a second - without the player touching anything. Call this when such a screen
 * opens.
 */
export function clearTaps() { tapped.clear(); }

// ---- the original's cartoon ---------------------------------------------------
//
// A 48 by 48 window (at 48 or 384 pixels a frame, see frameSize) of six frames that plays over the frozen cave when a cave ends,
// near the player. What the original does, and where, is in docs/FORMATS.md, "When
// each cartoon plays"; its timing and placement rules are in cartoon.js, which has
// no DOM so that plain Node can hold it to them (tools/jscartoon.mjs).
//
// Its own layer rather than the cave's canvas: nothing repaints the cave while it
// plays, but a resize would, and a cartoon drawn into the cave would go with it.
//
// Imported on first use and with the same query string game.js carries, so a deploy
// cannot leave one version of the rules beside another version of this file.

let cartoonRules = null;
let cartoonRun = null;

/**
 * Starts a cartoon. `row` is the strip's row: 0 WIN, 1 LOSE, 2 TIME. The player and
 * the origin are cave cells. Resolves true once it is playing, false if it could not
 * be started - in which case nothing was left behind and the host carries on.
 *
 * Claims the cartoon at once, so cartoonPlaying() is true across the load and the
 * host is not stepped while the strip arrives.
 */
export async function startCartoon(url, row, playerX, playerY, originX, originY) {
    if (!canvas) return false;
    endCartoon();

    const mine = generation;
    const run = cartoonRun = { layer: null, raf: 0 };

    try {
        cartoonRules ??= await import(`./cartoon.js${new URL(import.meta.url).search}`);
        const img = await load(url);
        if (!img || mine !== generation || cartoonRun !== run) {
            if (cartoonRun === run) cartoonRun = null;
            return false;
        }

        const { SIZE, MIN_MS, frameAt, finished, boxFor, frameSize } = cartoonRules;
        // 48 in the faithful build and 384 in VIP, from the strip itself. The window
        // on the cave is 48 field pixels whichever, so only the layer's own pixels
        // change; VIP's are smooth art and are scaled smoothly.
        const src = frameSize(img.width);
        const layer = document.createElement('canvas');
        layer.className = 'cartoon';
        layer.width = src;
        layer.height = src;
        layer.setAttribute('aria-hidden', 'true');
        const layerCtx = layer.getContext('2d');
        layerCtx.imageSmoothingEnabled = src > SIZE;
        layerCtx.imageSmoothingQuality = 'high';
        // The page's canvas rule is `pixelated`, which would shrink VIP's art by dropping
        // pixels; the faithful frames are scaled up, where it is right.
        layer.style.imageRendering = src > SIZE ? 'auto' : 'pixelated';
        canvas.parentElement.appendChild(layer);
        run.layer = layer;

        // Whatever was pressed to get here - the movement keys that were held when
        // the cave ended - is not a request to skip it.
        tapped.clear();

        const box = boxFor({ x: playerX, y: playerY }, { x: originX, y: originY });
        const started = now();
        let shown = -1;

        const step = () => {
            if (cartoonRun !== run) return;

            const elapsed = now() - started;
            // A key is only looked at after the first second, as in the original,
            // so one pressed earlier is still waiting for it.
            if (finished(elapsed, elapsed >= MIN_MS && takeAnyTap())) {
                endCartoon();
                return;
            }

            // Placed every frame, which costs nothing and follows a resize.
            const k = canvas.clientWidth / (COLS * TILE);
            layer.style.left = `${canvas.offsetLeft + box.x * k}px`;
            layer.style.top = `${canvas.offsetTop + box.y * k}px`;
            layer.style.width = `${SIZE * k}px`;
            layer.style.height = `${SIZE * k}px`;

            const frame = frameAt(elapsed);
            if (frame !== shown) {
                layerCtx.drawImage(img, frame * src, row * src, src, src, 0, 0, src, src);
                shown = frame;
            }
            run.raf = requestAnimationFrame(step);
        };

        step();
        return true;
    } catch {
        if (cartoonRun === run) endCartoon();
        return false;
    }
}

/** Whether a cartoon is playing, or loading. The host is not stepped while it is. */
export function cartoonPlaying() {
    return cartoonRun !== null;
}

/**
 * Ends it and takes its layer down. The taps are cleared, because the key that
 * ended it must not also dismiss the world screen that follows or take a step.
 */
export function endCartoon() {
    const run = cartoonRun;
    if (!run) return;
    cartoonRun = null;
    if (run.raf) cancelAnimationFrame(run.raf);
    run.layer?.remove();
    tapped.clear();
}

// The play field is 320 by 176, which is what the original drew below its HUD.
// The title and menu pictures are the full 320 by 200.
let surfaceW = COLS * TILE;
let surfaceH = ROWS * TILE;

/** Greatest common divisor, for reducing a surface to its shape. See fit. */
function gcd(a, b) {
    while (b) [a, b] = [b, a % b];
    return a;
}

// Fill the space the canvas has, keeping the aspect ratio.
//
// This used to take the largest whole-number scale, which is the right answer for
// 16 pixel art because every source pixel becomes a clean block. It is the wrong
// answer for the screen: stepping 2x to 3x needs half as much room again, so on
// most windows it stopped at 2x and left the cave floating in a sea of nothing.
// Filling the box wins, and the high-resolution sprite set removes the reason the
// whole-number rule existed.
function fit() {
    if (!canvas || !ctx) return;

    // Measure the box the canvas actually has rather than guessing at the chrome.
    const box = canvas.parentElement?.getBoundingClientRect();
    const availableW = Math.floor(box?.width || window.innerWidth);
    const availableH = Math.floor(box?.height || window.innerHeight);

    // No floor of 1 here. The original's 320-wide field is always scaled up and
    // would never hit it, but VIP's is 2560 wide and has to be allowed to scale
    // down - clamped at 1 it simply overflowed the window.
    scale = Math.min(availableW / surfaceW, availableH / surfaceH);

    const shownW = Math.floor(surfaceW * scale);
    const shownH = Math.floor(surfaceH * scale);

    // PUBLISH THE BOX. The select screen puts real buttons over the portraits,
    // and they have to sit exactly where the picture is - which is this, and
    // which only this function knows. Anything else measuring the canvas
    // separately would be a second answer to a question with one.
    canvas.parentElement?.style?.setProperty('--shown-w', shownW + 'px');
    canvas.parentElement?.style?.setProperty('--shown-h', shownH + 'px');

    // THE BACKING STORE IS SIZED FOR THE SCREEN, NOT FOR THE SOURCE ART.
    //
    // It used to be the surface outright, which for VIP means 2560 by 1408 -
    // three and a half million pixels cleared and composited for a picture
    // occupying a fifth of that on a phone. Three things were conflated and are
    // now separate: the WORLD is 20 by 11 tiles, the SOURCE is whatever the
    // atlas holds, and the BACKING STORE is what this decides.
    //
    // Never larger than the surface, because there is no source above it - an
    // 8x atlas at 3x device pixels would be upscaling its own art. And capped,
    // because a phone reporting 3 or 4 wants a sensible picture rather than the
    // largest arithmetic will allow.
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_PIXEL_RATIO);

    // Quantised to whole steps of the surface's own reduced shape, rather than
    // rounding the two axes independently - which gives neither the shape nor
    // clean tiles. A 1280 by 704 field asked for at 1244 rounds to 1244 by 684,
    // and 1244:684 is not 20:11: the browser then stretches the picture to the
    // CSS box, and every tile starts a fraction of a pixel in and seams against
    // its neighbour. Stepping in units of 20 by 11 keeps both exact.
    const step = gcd(surfaceW, surfaceH);
    const unitW = surfaceW / step;
    const unitH = surfaceH / step;

    // DOWN to the last whole step, and against BOTH axes. Rounding to the
    // nearest overshoots the budget it is meant to respect: a 377 by 208 box
    // shows the field at 377 by 207 and asked for a 380 by 209 backing store,
    // which is more pixels than the screen has in either direction. Nineteen
    // units is the honest answer and the sum has to floor to get it. A step
    // costs at most 20 backing pixels of sharpness; the alternative is a cap
    // that does not cap. Astra, 19 September 2026.
    //
    // Both axes, because the box is not the field's shape and either can be the
    // binding one. And never above `step`, because that is the source itself -
    // an 8x atlas at 3x device pixels would be upscaling its own art.
    //
    // At least one, so a box too small for a single step still gets a canvas.
    // That is the one case that can exceed the displayed size, and there is no
    // smaller whole answer: the alternative is no picture at all.
    const units = Math.max(1, Math.min(
        step,
        Math.floor(Math.round(shownW * dpr) / unitW),
        Math.floor(Math.round(shownH * dpr) / unitH),
    ));

    // Everything downstream keeps drawing in SURFACE units. A transform is the
    // whole of the change: paintField still works in atlas pixels, the menu box
    // is still a fraction of the surface, and nothing else has to know.
    surfaceScale = units / step;

    canvas.width = unitW * units;
    canvas.height = unitH * units;
    canvas.style.width = `${shownW}px`;
    canvas.style.height = `${shownH}px`;

    // After the size, because setting canvas.width resets the context.
    ctx.setTransform(surfaceScale, 0, 0, surfaceScale, 0, 0);

    // Publish the drawn width so the header and footer can sit on the same two
    // edges as the picture. They used to be capped at a fixed 68rem while the
    // canvas sized itself to the window, so the wordmark, the HUD and the
    // controls all floated inside the game's edges by whatever the difference
    // happened to be - 167px each side at 1440. The canvas is the only element
    // that knows its own width, and it only knows it here.
    const shell = canvas.closest('.shell');
    if (shell) shell.style.setProperty('--stage', `${shownW}px`);

    // Nearest-neighbour keeps the original's pixels honest when magnified, and
    // ruins the redrawn art when minified. Which one applies is decided by which
    // way the picture is going, not by which presentation is running - the title
    // screens are large pictures in both.
    //
    // That rule was right and only half of it was being applied. The context flag
    // governs drawImage INSIDE the canvas; the browser then scales the backing
    // store to the CSS size afterwards, and `image-rendering` on the element is
    // the only thing with a say over that second step. app.css sets `pixelated`
    // on every canvas, so VIP's 2560-wide field was taken down to the window by
    // dropping pixels - which is the exact thing redrawing 315 tiles at 8x was
    // meant to avoid. Both halves now read from the same test.
    // Two different scalings and each gets its own answer.
    //
    // imageSmoothingEnabled governs drawImage INSIDE the canvas, which now
    // minifies by surfaceScale. image-rendering governs the browser taking the
    // backing store to the CSS size afterwards, which is still decided by which
    // way the picture is going overall.
    ctx.imageSmoothingEnabled = surfaceScale < 1;
    ctx.imageSmoothingQuality = 'high';
    canvas.style.imageRendering = scale < 1 ? 'auto' : 'pixelated';

    // Setting canvas.width clears the canvas, so a resize wipes whatever was on
    // it. The play screen redraws ten times a second and never notices; the title
    // and select screens are painted once and stay, so a late resize left them
    // black. The ResizeObserver fires whenever the layout settles, which is after
    // the picture is drawn as soon as the picture is big enough to load slowly.
    if (repaint) repaint();
}

// How to redraw whatever is currently on screen. Set by whichever of the three
// screens painted last.
let repaint = null;

/**
 * How much of a device pixel ratio is worth honouring.
 *
 * A phone reporting 3 or 4 would otherwise ask for a backing store several
 * times the size of the picture anybody can see, and the cost is real: every
 * pixel is cleared and composited sixty times a second while anything is
 * moving. Two is the point past which nobody can tell.
 */
const MAX_PIXEL_RATIO = 2;

/** Backing-store pixels per surface pixel. See fit. */
let surfaceScale = 1;

function surface(w, h) {
    if (surfaceW === w && surfaceH === h) return;
    surfaceW = w;
    surfaceH = h;
    fit();
}

/**
 * Decoded pictures, most recently used last.
 *
 * BOUNDED, which it was not. Every image ever loaded stayed here for the life of
 * the page: five VIP atlases are 2.0 to 2.2 MB each on disk and about 82 MB of
 * RGBA once decoded, plus the title and select screens at 1536 by 960. A session
 * that visits every world holds all of it, and on a phone that is the difference
 * between running and being killed by the browser.
 *
 * Small on purpose. What is actually needed at once is the current world's
 * atlas and whichever screen is up; the rest is a cache and reloading one is a
 * fetch from the disk cache, not from the network.
 */
const PICTURE_CACHE = 4;

const pictures = new Map();

/** Marks a picture as the most recently used, which is what Map order gives. */
function touch(url, img) {
    pictures.delete(url);
    pictures.set(url, img);

    // Map iterates in insertion order, so the first key is the oldest.
    while (pictures.size > PICTURE_CACHE) {
        const oldest = pictures.keys().next().value;
        pictures.delete(oldest);
    }
}

function load(url) {
    if (pictures.has(url)) {
        const img = pictures.get(url);
        touch(url, img);
        return Promise.resolve(img);
    }
    return new Promise(resolve => {
        const img = new Image();
        img.onload = async () => {
            // Decoded before it is handed over, not on the first drawImage. A
            // 2560-wide atlas decoded lazily is a hundred-millisecond stall in
            // the middle of whatever frame first draws it.
            try { await img.decode(); } catch { /* onload already fired: usable */ }
            touch(url, img);
            resolve(img);
        };
        img.onerror = () => resolve(null);
        img.src = localUrl(url);
    });
}

// ---- the load phase -----------------------------------------------------
//
// EVERYTHING IS FETCHED BEHIND THE PROGRESS BAR, and nothing after it.
//
// The bar used to be Blazor's alone, and it went away the moment the runtime
// was ready - which was before a single picture had been asked for. What came
// next was init waiting on a three-megabyte atlas, then the title, then the
// animation sheet, with a black canvas on screen the whole time: 4.4 seconds
// of it measured over Tailscale on 25 September 2026, and a great deal more on
// a phone. Then the world track was left to arrive the first time a world was
// finished, and every world after the first fetched its atlas on the way in.
//
// Now every file the game will ever show or play is fetched here, once, with
// the bytes counted into the bar, and kept as a blob URL. After the bar is gone
// the network is not touched again: load() and the audio elements are pointed
// at the local copies. Blazor's phase fills the first part of the same bar and
// this fills the rest, so it reads as one wait rather than two.

/** Absolute URL -> blob URL, for everything the load phase fetched. */
const local = new Map();

function absolute(url) { return new URL(url, document.baseURI).href; }

/** The local copy of a file if the load phase fetched it, otherwise the URL. */
export function localUrl(url) {
    // Nothing preloaded means nothing to swap, and no document to resolve
    // against either in the Node harness that drives this module.
    if (local.size === 0 || !url) return url;
    return local.get(absolute(url)) ?? url;
}

/** The share of the bar Blazor's runtime download fills before this takes over. */
const RUNTIME_SHARE = 40;

/**
 * Fetches every file in `urls` - plus VIP's audio when `vipAudio` - into
 * memory, driving the boot panel's bar from the bytes as they arrive, then
 * decodes the pictures in `decodeFirst` so the first screen is instant.
 *
 * Never rejects. A file that fails is left to load from the network the way it
 * always did, which is slower but is not broken.
 */
export async function preloadAll(urls, vipAudio, decodeFirst, bootRoot, percentEl) {
    const list = [...urls];

    if (vipAudio) {
        // The same module instance setAudioMode imports later, because the URL -
        // query string included - is the same, and the module map is keyed on it.
        const v = new URL(import.meta.url).search;
        const music = await import(`./music-vip.js${v}`);
        list.push(...music.sources());
    }

    const wanted = [...new Set(list.map(absolute))].filter(u => !local.has(u));
    const total = new Map();      // url -> expected bytes, once the headers say
    const got = new Map();        // url -> bytes so far
    let shown = RUNTIME_SHARE;

    const report = () => {
        let expected = 0, loaded = 0;
        for (const u of wanted) {
            const have = got.get(u) ?? 0;
            loaded += have;
            // A file whose headers have not arrived counts as a megabyte, so the
            // bar cannot race to the end while most of the requests are still
            // queued. It is corrected the moment the real length is known.
            expected += total.get(u) ?? Math.max(have, 1 << 20);
        }
        const share = expected ? loaded / expected : 1;
        // Monotonic. A length arriving can make the fraction go backwards, and a
        // progress bar that retreats reads as something going wrong.
        shown = Math.max(shown, RUNTIME_SHARE + (100 - RUNTIME_SHARE) * share);
        if (bootRoot) bootRoot.style.setProperty('--boot-progress', `${shown.toFixed(1)}%`);
        if (percentEl) percentEl.textContent = `${Math.floor(shown)}%`;
    };
    report();

    await Promise.all(wanted.map(async u => {
        try {
            const response = await fetch(u);
            if (!response.ok || !response.body) throw new Error(`${response.status}`);

            const length = Number(response.headers.get('Content-Length'));
            if (length > 0) total.set(u, length);

            const reader = response.body.getReader();
            const chunks = [];
            let have = 0;
            for (;;) {
                const { done, value } = await reader.read();
                if (done) break;
                chunks.push(value);
                have += value.length;
                got.set(u, have);
                report();
            }

            total.set(u, have);
            const type = response.headers.get('Content-Type') ?? '';
            local.set(u, URL.createObjectURL(new Blob(chunks, { type })));
        } catch {
            // Counted as done so the bar still finishes. The file itself will be
            // fetched the old way when it is first needed.
            total.set(u, got.get(u) ?? 0);
        }
        report();
    }));

    // Decoded while the bar is still up, so the title, its animation, the world
    // select and the first cave all paint on the frame they are asked for.
    await Promise.all(decodeFirst.map(load));

    shown = 100;
    report();
    return true;
}

/** Fades the boot panel out, and settles once it has gone. */
export function finishBoot(bootRoot) {
    if (!bootRoot) return Promise.resolve(true);
    bootRoot.classList.add('boot--leaving');
    if (motionIsReduced()) return Promise.resolve(true);
    return new Promise(resolve => setTimeout(() => resolve(true), 320));
}

// The title and the world-select screen are full pictures out of the game's own
// files, drawn at their native 320 by 200 rather than rebuilt.
function paintPicture(img) {
    ctx.fillStyle = '#000';
    ctx.fillRect(0, 0, surfaceW, surfaceH);
    ctx.drawImage(img, 0, 0, surfaceW, surfaceH);
}

export async function showPicture(url) {
    const mine = generation;
    const img = await load(url);
    if (!img || !ctx || mine !== generation) return false;

    // A picture is not a cave. Stop the field loop, or it goes on painting cells
    // over the top of whatever this is.
    leaveField();

    // The picture's own size, not a fixed 320 by 200. Both presentations use the
    // same 8:5 shape, but VIP's screens are 1536 by 960 and drawing those into a
    // 320-wide surface would throw away almost everything they are for.
    repaint = () => paintPicture(img);
    surface(img.naturalWidth, img.naturalHeight);
    paintPicture(img);
    return true;
}

// ---- the title animation ------------------------------------------------
//
// The original's title screen is not a still. Rockford blinks, taps a foot and
// tosses a diamond up and catches it, and the frames come out of TITLEANI.SCM -
// see TitleAnimation.cs for the counters and tools/webassets.py for how the
// rectangles are read out of the executable.
//
// Four layers over the base picture. Nothing is composited ahead of time, because
// the face has two sets of expressions and the arm runs at a different length from
// the gem, so the combinations multiply; drawing four rectangles at 8.6 frames a
// second costs nothing.
let title = null;
let titleFrame = null;

// The layer order is the original's call order at 0xF2D0 - arm, gem, face, foot.
// It matters where they overlap: the gem's rectangle reaches down over the top of
// the arm's, so the arm has to go down first.
const TITLE_LAYERS = ['arm', 'diamond', 'face', 'foot'];

function paintTitle() {
    if (!title) return;
    if (title.layout?.smooth) { paintTitleSmooth(performance.now()); return; }

    paintPicture(title.base);
    if (!titleFrame) return;

    const { sheet, layout } = title;

    for (const name of TITLE_LAYERS) {
        const layer = layout[name === 'face' && titleFrame.alt ? 'faceAlt' : name];
        if (!layer) continue;

        // Each layer clamps the shared counter to its own length, the way 0xEF74
        // does for the arm. Without it a nine-frame counter would index past a
        // five-frame table.
        const i = Math.min(titleFrame[name], layer.frames.length - 1);
        const [sx, sy] = layer.frames[i];
        const [dx, dy] = layer.at;

        ctx.drawImage(sheet, sx, sy, layer.w, layer.h, dx, dy, layer.w, layer.h);
    }
}

/**
 * Shows the title picture, and arms the animation when there are frames for it.
 *
 * A layout is authored in its own picture's coordinates - the faithful build's
 * against 320 by 200 and VIP's against 1536 by 960 - so there is no scaling to do
 * here. The canvas surface is the picture's own size either way.
 */
export async function showTitle(baseUrl, sheetUrl, layout) {
    const mine = generation;
    const base = await load(baseUrl);
    if (!base || !ctx || mine !== generation) return false;

    const sheet = sheetUrl ? await load(sheetUrl) : null;
    if (mine !== generation) return false;

    forgetTitle();
    title = { base, sheet, layout };
    titleFrame = null;

    leaveField();
    repaint = paintTitle;
    surface(base.naturalWidth, base.naturalHeight);
    paintTitle();
    if (layout?.smooth && sheet) startTitleLoop();
    return true;
}

/** One frame of the animation. The engine owns the counters. */
export function paintTitleFrame(arm, diamond, face, alt, foot) {
    if (!title || !title.sheet) return false;

    titleFrame = { arm, diamond, face, alt, foot };

    if (title.layout?.smooth) {
        noteTitleCounts(diamond, foot, alt);
        // Drawn by the display loop, except with reduced motion, where there
        // is no loop and each count is drawn as it arrives.
        if (!titleRaf) paintTitle();
        return true;
    }

    paintTitle();
    return true;
}

/** Dropped when the title screen goes, so a resize does not repaint it. */
function forgetTitle() {
    title = null;
    titleFrame = null;
    if (titleRaf) cancelAnimationFrame(titleRaf);
    titleRaf = 0;
    titleCounts.clear();
    titleNewest = -1;
    titlePhase = 0;
    titleLastPaint = 0;
    titleShownIndex = -1;
    blinkStart = -1;
    airStart = -1;
    tossRising = true;
}

// ---- VIP's title, drawn every display frame -------------------------------
//
// The faithful title steps through the original's frames at its 8.6 a second,
// which is right for a 320 by 200 picture. VIP's is 1536 by 960 and the same
// steps read as a gem teleporting up a ladder (Dan, 25 September 2026: "really
// shit"). So VIP draws the title every display frame and interpolates the
// engine's counters between the frames it is given. The RULES are untouched -
// the same toss and foot counters, the same draw from the generator, the same
// twenty seconds; this is only the picture between two counts.
//
// ITS OWN CLOCK, following the counts about a frame and a half behind. The
// counts arrive on the 10 Hz step - a new one on five steps out of six - and
// interpolating from each arrival stutters.
//
// A FOLLOWER, NOT A METRONOME. The first version ran at exactly 7/60 s a count
// and re-synced whenever it caught the newest count. Measured on 25 September
// 2026 the counts arrive every 120.5 ms on average, 87 to 275 ms apart, so it
// caught up every few seconds and each re-sync threw the picture back more
// than a count - the gem jerking backwards along its path, and snapping from
// the top of the throw into his hand after the first stall. Now the clock
// runs a little fast when it is behind and a little slow when it is ahead,
// never jumps and never runs backwards.

/** 0xF2C1 waits seven interrupts between frames. */
const TITLE_FRAME_MS = 7000 / 60;

/** How far behind the newest count the picture runs, in frames. */
const TITLE_LAG = 1.5;

/** From the engine: its nine toss counts, and in the first four the gem is
 *  still in his hand. */
const TITLE_RELEASE = 3;

const BLINK_MS = 210;

let titleRaf = 0;
const titleCounts = new Map();      // frame number -> { toss, foot, alt }
let titleNewest = -1;
let titlePhase = 0;                 // the count the picture is at, fractional
let titleLastPaint = 0;             // performance.now() of the last paint
let titleShownIndex = -1;
let blinkStart = -1;
let airStart = -1;
let tossRising = true;              // which way the toss count last moved

function noteTitleCounts(toss, foot, alt) {
    titleNewest++;
    titleCounts.set(titleNewest, { toss, foot, alt });
    titleCounts.delete(titleNewest - 12);
}

/** Moves the picture's clock on by one display frame. */
function advanceTitleClock(now) {
    const dt = titleLastPaint ? Math.min(100, now - titleLastPaint) : 0;
    titleLastPaint = now;
    if (titleNewest < 0) return;

    const target = titleNewest - TITLE_LAG;
    // Far behind - the page was hidden, or the loop paused - is the one case
    // that jumps, because sweeping through a backlog of counts is worse.
    if (titlePhase < titleNewest - 6) { titlePhase = Math.max(0, target); return; }

    const rate = Math.min(1.8, Math.max(0.4, 1 + 0.6 * (target - titlePhase)));
    titlePhase = Math.min(titleNewest, titlePhase + (dt / TITLE_FRAME_MS) * rate);
}

function startTitleLoop() {
    if (titleRaf || motionIsReduced()) return;
    const frame = now => {
        if (!title || !title.layout?.smooth) { titleRaf = 0; return; }
        paintTitleSmooth(now);
        titleRaf = requestAnimationFrame(frame);
    };
    titleRaf = requestAnimationFrame(frame);
}

const clamp01 = v => Math.min(1, Math.max(0, v));
const easeInOut = t => t * t * (3 - 2 * t);

const easeIn = t => t * t;
const easeOut = t => 1 - (1 - t) * (1 - t);
const mix = (a, b, t) => a + (b - a) * t;
const span = (v, from, to) => clamp01((v - from) / (to - from));

/**
 * The throwing arm's angle, in degrees, at toss count `c`. Negative lifts the
 * hand. Added 25 September 2026 with the rig (Dan: "still shit" - the gem had
 * been lifting off a palm that never moved).
 *
 * On the way up the hand dips to wind up, swings up hard and lets go at the
 * top of the swing - the count the original's gem leaves his hand - and
 * settles. On the way down it rises to meet the gem, gives as the gem lands,
 * and settles. Both legs are at rest at the bottom and at the top of the toss,
 * so the two join without a jump whichever way the count is going.
 */
function armAngle(c, rising) {
    const r = TITLE_RELEASE;
    if (rising) {
        if (c <= 1) return mix(0, 5, easeInOut(span(c, 0, 1)));
        if (c <= r) return mix(5, -16, easeIn(span(c, 1, r)));
        if (c <= r + 2) return mix(-16, 0, easeInOut(span(c, r, r + 2)));
        return 0;
    }
    if (c >= r + 1.5) return 0;
    if (c >= r) return mix(0, -7, easeInOut(span(r + 1.5 - c, 0, 1.5)));
    if (c >= r / 2) return mix(-7, 6, easeOut(span(r - c, 0, r / 2)));
    return mix(6, 0, easeInOut(span(r / 2 - c, 0, r / 2)));
}

/** A point turned about a pivot. */
function turn([x, y], [px, py], degrees) {
    const t = degrees * Math.PI / 180, cos = Math.cos(t), sin = Math.sin(t);
    return [px + (x - px) * cos - (y - py) * sin, py + (x - px) * sin + (y - py) * cos];
}

/**
 * Where the gem's centre is at a continuous toss count.
 *
 * With a rig, it sits on his palm and goes where the hand goes until the count
 * at which he lets go, then flies a gravity arc from the palm to the top. The
 * arc leaves from wherever the hand released it on the way up, and comes down
 * to wherever the hand has risen to meet it on the way down, so the gem is in
 * the palm at both ends of its flight.
 *
 * Without one - the layout before the rig - the original's own path in the
 * hand and the same arc after.
 */
function gemAt(layout, c, rising) {
    const arc = layout.gem.arc;
    const last = arc.length - 1;
    const arm = layout.arm;

    const inHand = count => arm
        ? turn(arm.palm, arm.pivot, armAngle(count, rising))
        : (() => {
            const i = Math.min(Math.floor(count), TITLE_RELEASE - 1);
            const f = count - i;
            return [mix(arc[i][0], arc[i + 1][0], f), mix(arc[i][1], arc[i + 1][1], f)];
        })();

    if (c <= TITLE_RELEASE) return inHand(c);

    // Thrown, so it slows to the top and speeds up coming down: the count runs
    // back down at the rate it went up, and height against time is a parabola.
    const u = (c - TITLE_RELEASE) / (last - TITLE_RELEASE);
    const h = 1 - (1 - u) * (1 - u);
    const [x0, y0] = inHand(TITLE_RELEASE), [x1, y1] = arc[last];
    return [mix(x0, x1, u), mix(y0, y1, h)];
}

/** Draws a rig part from the sheet, turned about its pivot. */
function drawPart(sheet, part, degrees, lift = 0) {
    const [sx, sy, sw, sh] = part.src;
    const [px, py] = part.pivot;
    ctx.save();
    ctx.translate(px, py + lift);
    ctx.rotate(degrees * Math.PI / 180);
    ctx.translate(-px, -py);
    ctx.drawImage(sheet, sx, sy, sw, sh, part.at[0], part.at[1], sw, sh);
    ctx.restore();
}

function nearestIndex(list, value) {
    let best = 0;
    for (let i = 1; i < list.length; i++) {
        if (Math.abs(list[i] - value) < Math.abs(list[best] - value)) best = i;
    }
    return best;
}

/** Where every counter starts: gem in the hand, foot flat, eyes open. */
const TITLE_REST = Object.freeze({ toss: 0, foot: 0, alt: false });

function paintTitleSmooth(now) {
    paintPicture(title.base);
    if (!title.sheet) return;

    // Before the engine's first count the picture is the rest position, not
    // the base alone - the base has no gem in it, and for the half second
    // before the first count arrived the gem was simply missing.
    if (titleNewest < 0) {
        titleCounts.set(0, TITLE_REST);
    }

    const { sheet, layout } = title;
    const reduced = motionIsReduced();

    // Which two counts the picture is between, and how far.
    const newest = Math.max(0, titleNewest);
    advanceTitleClock(now);
    let phase = reduced || titleNewest < 0 ? newest : titlePhase;
    phase = Math.min(Math.max(phase, newest - 11), newest);
    const k = Math.floor(phase);
    const a = titleCounts.get(k) ?? titleCounts.get(titleNewest) ?? TITLE_REST;
    const b = titleCounts.get(k + 1) ?? a;
    let f = phase - k;

    // A jump of more than one count is the animation starting over, not a
    // movement, and is drawn as the new position rather than swept to.
    const step = (x, y) => Math.abs(y - x) > 1 ? y : x + (y - x) * f;
    const toss = step(a.toss, b.toss);
    const foot = step(a.foot, b.foot);
    if (b.toss !== a.toss) tossRising = b.toss > a.toss;

    // A blink starts the moment the alternate expression does. The original
    // shows it as a second set of faces for the rest of the toss; drawn that
    // way at this size it was a slow squint, so here it is what it stands for.
    if (k !== titleShownIndex) {
        const prev = titleCounts.get(k - 1);
        if (!reduced && a.alt && prev && !prev.alt) blinkStart = now;
        titleShownIndex = k;
    }

    // A photoreal render, not pixel art: turned parts are drawn smoothed, or
    // their edges step as they rotate.
    ctx.save();
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = 'high';

    // ---- what is behind the moving parts: rock, cave and floor
    for (const plate of layout.plates ?? []) {
        const [px, py, pw, ph] = plate.src;
        ctx.drawImage(sheet, px, py, pw, ph, plate.at[0], plate.at[1], pw, ph);
    }

    // ---- the shoe, turned about its heel: the original's toe-tap, with the
    // engine's foot count as the angle between its four positions
    const shoe = layout.foot;
    const fi = Math.min(Math.floor(foot), shoe.counts.length - 2);
    const angle = shoe.counts[fi] + (shoe.counts[fi + 1] - shoe.counts[fi]) * (foot - fi);
    drawPart(sheet, shoe, angle);

    // ---- the gem's position, which the eyes and the head follow
    const arc = layout.gem.arc;
    const [gx, gy] = gemAt(layout, toss, tossRising);
    const handY = arc[0][1], topY = arc[arc.length - 1][1];
    const look = clamp01((handY - gy) / (handY - topY));

    // ---- the arm
    if (layout.arm) drawPart(sheet, layout.arm, armAngle(toss, tossRising));

    // The head tips back towards the gem as it climbs - it is up and to his
    // right - and lifts a little, which is most of what looking up is.
    const headTilt = -2.4 * look, headLift = -2.5 * look;
    if (layout.head) {
        drawPart(sheet, layout.head, headTilt, headLift);
        const [px, py] = layout.head.pivot;
        ctx.translate(px, py + headLift);
        ctx.rotate(headTilt * Math.PI / 180);
        ctx.translate(-px, -py);
    }

    // ---- the eyes, or a lid coming down over them
    let cover = 0;
    if (blinkStart >= 0) {
        const t = (now - blinkStart) / BLINK_MS;
        if (t >= 1) blinkStart = -1;
        else cover = t < 0.4 ? easeInOut(t / 0.4) : t < 0.55 ? 1 : 1 - easeInOut((t - 0.55) / 0.45);
    }

    const eyes = layout.eyes;
    const rise = look * (eyes.frames.length - 1);
    let face;
    if (cover > 0.1) {
        const blink = layout.blink;
        const row = blink.frames[nearestIndex(blink.rises, rise)];
        face = row[nearestIndex(blink.covers, cover)];
    } else {
        face = eyes.frames[Math.round(rise)];
    }
    ctx.drawImage(sheet, face[0], face[1], eyes.w, eyes.h, eyes.at[0], eyes.at[1], eyes.w, eyes.h);

    // Out of the head's frame before the gem, which is not on his head.
    ctx.restore();
    ctx.save();
    ctx.imageSmoothingEnabled = true;
    ctx.imageSmoothingQuality = 'high';

    // ---- the gem, last, in front of everything
    //
    // One slow turn while it is in the air, eased so it leaves the hand and
    // lands in it upright. Timed from the release rather than read off the
    // count, because the count runs backwards on the way down and a turn
    // driven by it would unwind.
    const airborne = toss > TITLE_RELEASE;
    if (airborne && airStart < 0) airStart = now;
    if (!airborne) airStart = -1;
    const airMs = (2 * (arc.length - 1 - TITLE_RELEASE) + 1) * TITLE_FRAME_MS;
    const turn = reduced || airStart < 0 ? 0 : easeInOut(clamp01((now - airStart) / airMs)) * Math.PI * 2;

    const [sx, sy, sw, sh] = layout.gem.src;
    ctx.save();
    ctx.translate(gx, gy);
    ctx.rotate(turn);
    ctx.drawImage(sheet, sx, sy, sw, sh, -sw / 2, -sh / 2, sw, sh);
    ctx.restore();

    // A glint at the top of the throw, where the gem hangs for a moment.
    const glint = reduced ? 0 : clamp01(1 - Math.abs(toss - (arc.length - 1)) * 1.1);
    if (glint > 0) paintGlint(gx + sw * 0.18, gy - sh * 0.22, sw * 0.42 * glint, glint);
    ctx.restore();
}

function paintGlint(x, y, size, strength) {
    ctx.save();
    ctx.globalCompositeOperation = 'lighter';
    ctx.globalAlpha = 0.85 * strength;
    const g = ctx.createRadialGradient(x, y, 0, x, y, size);
    g.addColorStop(0, 'rgba(255,255,255,1)');
    g.addColorStop(0.25, 'rgba(190,225,255,0.55)');
    g.addColorStop(1, 'rgba(120,180,255,0)');
    ctx.fillStyle = g;
    // A four-point star: two thin diamonds across each other.
    for (const [w, h] of [[size * 0.16, size], [size, size * 0.16]]) {
        ctx.beginPath();
        ctx.moveTo(x, y - h); ctx.lineTo(x + w, y); ctx.lineTo(x, y + h); ctx.lineTo(x - w, y);
        ctx.closePath();
        ctx.fill();
    }
    ctx.restore();
}

// Where the five world boxes sit, as fractions of the picture, so one set of
// numbers works at 320 wide and at 1536 wide alike.
//
// The default is MENU_I's own geometry: 48 square, 64 apart, starting at 8,48.
// VIP's select screen is laid out differently - it has the room to breathe that
// sixteen colours and 200 lines never did - so it passes its own.
const ORIGINAL_MENU_BOX =
    { x: 8 / 320, y: 48 / 200, w: 48 / 320, h: 48 / 200, step: 64 / 320 };

let menuBox = ORIGINAL_MENU_BOX;

export function setMenuBox(x, y, w, h, step) {
    // `dom` says the page is drawing the selection itself. Only the VIP build
    // sets a menu box at all, and only the VIP build has the overlay.
    menuBox = { x, y, w, h, step, dom: true };
}

// The selection is drawn by the PAGE now, not here - see the .worlds overlay in
// Home.razor. A rectangle stroked onto a canvas cannot be tabbed to, cannot be
// tapped without hit-testing coordinates by hand, and cannot tell a screen
// reader which world it is on.
//
// Kept for the faithful build, which has no menu.json and therefore no overlay:
// its select screen is MENU_I's own 320 by 200 picture and the ring is part of
// how that screen looks.
function paintSelection(selected) {
    if (menuBox.dom) return;

    const x = (menuBox.x + menuBox.step * selected) * surfaceW;
    const y = menuBox.y * surfaceH;
    const w = menuBox.w * surfaceW;
    const h = menuBox.h * surfaceH;

    ctx.strokeStyle = '#ffd24a';
    ctx.lineWidth = Math.max(2, Math.round(surfaceW / 320) * 2);
    ctx.strokeRect(x - 1, y - 1, w + 2, h + 2);
}

// A later page of the select screen: its own portraits, drawn over the boxes the
// picture already carries the 1988 faces in.
//
// INSIDE THE FRAME. MENU_I's box is 48 pixels with a one-pixel white ring as its
// outermost row and column, and the 1988 faces live in the 46 inside it. A portrait
// is a 48 pixel box off Aleksi Sahala's select screen, ring and all (or, without
// his art, a 1987 cartoon frame), so it is CROPPED by one pixel all round to fit - not
// scaled, which would resample every pixel of it - and the picture's own frame is
// left standing round it.
//
// A null slot is blanked, frame and all, so a page with fewer than five worlds
// reads as a short page rather than as boxes whose pictures are missing. The blank
// is the picture's own backdrop, sampled just left of the first box - a point that
// is background on MENU_I, which is the only picture with pages.
function paintPortraits(faces) {
    const [r, g, b] = ctx.getImageData(
        Math.round(surfaceW * 4 / 320), Math.round(surfaceH * 72 / 200), 1, 1).data;

    for (let i = 0; i < faces.length; i++) {
        const x = (menuBox.x + menuBox.step * i) * surfaceW;
        const y = menuBox.y * surfaceH;
        const w = menuBox.w * surfaceW;
        const h = menuBox.h * surfaceH;

        if (faces[i]) {
            const px = w / 48, py = h / 48;       // one portrait pixel, on screen
            ctx.drawImage(faces[i], 1, 1, 46, 46, x + px, y + py, w - 2 * px, h - 2 * py);
        } else {
            ctx.fillStyle = `rgb(${r}, ${g}, ${b})`;
            ctx.fillRect(x, y, w, h);
        }
    }
}

// `selected` is a BOX, 0 to 4, not a menu position - the caller knows which page
// it is on, and passes that page's portraits, or null for the first page, whose
// portraits are part of the picture.
export async function showMenu(url, selected, portraits = null) {
    const mine = generation;
    const img = await load(url);
    const faces = portraits
        ? await Promise.all(portraits.map(p => (p ? load(p) : Promise.resolve(null))))
        : null;
    if (!img || !ctx || mine !== generation) return false;

    // The title screen is behind us. Without this a resize on the select screen
    // repaints the title, because repaint is the only thing a resize can call.
    forgetTitle();
    leaveField();

    repaint = () => {
        paintPicture(img);
        if (faces) paintPortraits(faces);
        paintSelection(selected);
    };
    surface(img.naturalWidth, img.naturalHeight);
    repaint();
    return true;
}

// ---- the field ----------------------------------------------------------
//
// The simulation runs at 10 Hz, and the page used to paint once per step. So the
// picture was a slideshow at the simulation's own rate, and the camera - which
// moves a whole 16-pixel tile per eligible step - jumped that whole tile at once.
//
// draw() now records a snapshot and a requestAnimationFrame loop paints between
// them.
//
// Objects slide too now, but only the ones the ENGINE said moved, and only
// backwards from where they already are.
//
// That direction matters and it is the whole safety argument. A sprite is drawn
// at its new cell, offset back towards the cell it came from, and the offset
// shrinks to nothing. It is never drawn ahead of the simulation and never
// somewhere it has not yet reached, so a boulder cannot kill you from a cell its
// picture has not arrived at. The picture is briefly behind; it is never ahead.
//
// Three things follow from taking the moves from the engine rather than from
// comparing two snapshots, and all three are the reason #38 exists:
//
//   - A tile that merely CHANGED does not move. A boulder that becomes a falling
//     boulder without going anywhere - the original's one-step commit before a
//     drop - reports nothing, so nothing slides. Manufacturing that motion from
//     the tile change would have invented a fall a whole step early.
//   - A blast, a death and a transformation are not moves, so they resolve the
//     instant the snapshot says so. Nothing hides live danger behind an
//     animation.
//
//     That was asserted here before it was true. A move is reported the moment it
//     happens and the rest of the scan runs afterwards, so a player can walk into
//     a cell and be blasted in the same step - the move is real, and the cell then
//     holds an explosion. Home.razor now drops any move whose destination no
//     longer holds the same FAMILY of thing, so the explosion snaps and only the
//     survivors slide. See docs/astra-review-2026-09-19.md.
//   - Two identical boulders side by side cannot be confused for one another,
//     because nothing is being inferred.
//
// What the engine does not report is not blended. The worms and the amoeba still
// snap, and deliberately: a worm grows at the head and erases at the tail, so
// nothing in it moves, and amoeba growth is creation rather than movement.
// Reporting either as a move would be a lie. See docs/PORT-PLAN.md.

/**
 * Whether this build slides anything at all.
 *
 * Off by default, and turned on by init() for a VIP build only.
 *
 * The faithful presentation exists to be the reference VIP is judged against -
 * ROCKFORD-VIP.md is explicit about it - and a reference whose picture has been
 * smoothed is not one. The simulation is untouched either way; what would move is
 * the thing the comparison is made with, which is the one thing it must not do.
 *
 * Caught by Astra on 19 September 2026. Both the camera blend and the object
 * blend had shipped unconditional, so the faithful build had been sliding since
 * the camera work landed.
 */
let blending = false;

/** Milliseconds to slide the view across, against a 100 ms simulation step. */
const CAMERA_BLEND = 60;

/**
 * Milliseconds to slide a moved object across.
 *
 * Shorter than the step on purpose. Blending across the full 100 ms would keep
 * every object a whole tick behind the simulation for the whole tick, which is
 * exactly long enough for a falling boulder to be lethal while its picture is
 * still most of a tile short. At 50 ms it is caught up by halfway and the
 * remaining half of every step is honest.
 *
 * A number to judge by playing rather than by reasoning. It is here, alone, so
 * that changing it is one edit.
 */
const MOVE_BLEND = 50;

// The last snapshot: resolved sprite positions for the whole cave, and the cell
// the view's top-left corner sits on.
let field = null;

// Where the view was before that snapshot arrived, and when it arrived.
let cameraFrom = { x: 0, y: 0 };
let cameraAt = 0;

let frames = 0;

/**
 * True once everything has arrived, or never left.
 *
 * Both blends, because either one still running is a reason to keep painting.
 * The moves finish first at 50 ms against the camera's 60, so in practice the
 * camera is what holds the loop open - but a step that moves something without
 * scrolling has to keep it open too, and that used to be every step.
 */
function everythingStill() {
    // Particles keep the loop awake on their own. Without this a puff under a
    // landed boulder would be drawn once and then frozen until something else
    // moved, which in a settled cave is never.
    if (effects && effects.busy()) return false;

    if (!field || !slidingAllowed()) return true;

    const elapsed = now() - cameraAt;
    const cameraMoving = (cameraFrom.x !== field.ox || cameraFrom.y !== field.oy)
        && elapsed < CAMERA_BLEND;
    const objectsMoving = field.moves.size > 0 && elapsed < MOVE_BLEND;

    return !cameraMoving && !objectsMoving;
}

/**
 * Runs the paint loop until the view settles, then stops until the next
 * snapshot restarts it.
 *
 * Stopping matters. Nothing moves between snapshots except during a blend, so
 * once the camera and the moves have arrived the picture is identical from one
 * animation frame to the next, and painting it sixty times a second is sixty
 * times the work for no change at all. On VIP that work is a 2560 by 1408 clear
 * plus 264 blits. So the loop runs while something is moving and the rate falls
 * back to the simulation's own ten a second when nothing is - which, in a cave
 * where the player is standing still and nothing is falling, is every step.
 *
 * Idempotent, and a no-op where there is no rAF - the checks in
 * tools/jslifecycle.mjs run in plain Node and drive paints directly.
 */
function startFrames() {
    if (frames || everythingStill() || typeof requestAnimationFrame !== 'function') return;


    let last = now();

    const tick = () => {
        // Real elapsed time, not a fixed step. The frame rate is whatever the
        // display gives and a particle has to age at the same rate on all of
        // them.
        const at = now();
        if (effects) effects.step((at - last) / 1000);
        last = at;

        paintField();

        // Painted first, so the frame that lands exactly on the target is drawn
        // before the loop gives up. Stopping a frame early leaves the view half a
        // pixel short until the next step, which is a stutter at the end of every
        // scroll.
        if (everythingStill()) { frames = 0; return; }
        frames = requestAnimationFrame(tick);
    };

    frames = requestAnimationFrame(tick);
}

function stopFrames() {
    if (!frames) return;
    if (typeof cancelAnimationFrame === 'function') cancelAnimationFrame(frames);
    frames = 0;
}

/**
 * Leaves the cave. The title and the select screen are single pictures and the
 * loop must not go on painting a field over the top of them.
 */
function leaveField() {
    stopFrames();
    field = null;
}

/**
 * tiles: two entries per cell of the WHOLE 40 by 22 cave, row major - the
 * sprite's x and y ON THE SHEET. A negative x means nothing to draw. They are
 * positions rather than tile numbers because the sheet's twelve bands are not on
 * a 16-pixel grid.
 *
 * originColumn, originRow: the cell the window's top-left corner sits on, from
 * Viewport. Whole cells - the sliding between them happens here.
 *
 * moves: four numbers per move the ENGINE reported - from column, from row, to
 * column, to row - flattened, because that crosses the interop boundary as one
 * array of numbers rather than a list of objects. Only moves; landings, blasts
 * and shots are for the effects layer and are not sent here.
 *
 * freshCave: this grid is not a continuation of the last one. The host knows;
 * the renderer cannot tell.
 */
export function draw(tiles, originColumn = 0, originRow = 0, moves = null, freshCave = false,
                     events = null) {
    if (!ctx || !sheet) return;

    // The located events VIP draws its feedback from. Only VIP has the module
    // at all - a faithful build never imports it, so there is nothing to spend
    // and nothing to accidentally show.
    if (effects) {
        if (freshCave) effects.clear();
        spawnEffects(events);
    }

    // The whole field at sheet resolution: 320 by 176 on the original sheets,
    // 2560 by 1408 on VIP's. fit() scales that down to the window afterwards,
    // which is the right order - drawing small and scaling up would throw the
    // detail away before it ever reached the screen.
    const side = TILE * sheetScale;

    // Snap rather than slide.
    //
    // Two reasons, and the second used to be the only one, which was a guess.
    //
    // `freshCave` is the host saying so outright. A respawn or a new cave can
    // legitimately frame within one cell of where the last one ended, and inferring
    // "new cave" from camera distance missed exactly that case - the view would
    // then slide across a grid that had been replaced underneath it. Astra, 19
    // September 2026.
    //
    // The distance test stays as well, for the case the host cannot see: a tab
    // coming back from a stall, where the camera has genuinely moved several cells
    // since the last paint.
    const jumped = !field
        || freshCave
        || Math.abs(originColumn - field.ox) > 1
        || Math.abs(originRow - field.oy) > 1;

    cameraFrom = jumped
        ? { x: originColumn, y: originRow }
        : { x: field.ox, y: field.oy };
    cameraAt = now();

    // Keyed by destination, because that is what the painter has in its hand when
    // it reaches a cell. A Map rather than a sparse array: a step moves a handful
    // of things out of 880 cells, and this is rebuilt ten times a second.
    //
    // Dropped entirely on a jump. A new cave or a respawn has a whole new grid,
    // and a move reported against the old one would slide a sprite in from a cell
    // that means nothing now.
    const blending = new Map();
    if (moves && !jumped) {
        for (let i = 0; i + 3 < moves.length; i += 4) {
            const fromColumn = moves[i], fromRow = moves[i + 1];
            const toColumn = moves[i + 2], toRow = moves[i + 3];

            // A move to where it already was is not a move. Nothing reports one
            // today; this is here so that a future instrumentation mistake shows
            // up as no animation rather than as a divide by zero of meaning.
            if (fromColumn === toColumn && fromRow === toRow) continue;

            blending.set(toRow * CAVE_COLS + toColumn,
                         { dx: fromColumn - toColumn, dy: fromRow - toRow });
        }
    }

    field = { tiles, ox: originColumn, oy: originRow, side, moves: blending };

    repaint = paintField;
    surface(COLS * side, ROWS * side);
    paintField();
    startFrames();
}

/** Where the view is at this instant, in cells, with a fraction. */
/**
 * Whether the renderer slides anything at this instant.
 *
 * Two conditions and they answer different questions. `blending` is the BUILD -
 * the faithful presentation is the reference VIP is judged against and its
 * picture stays on the simulation's own grid. Reduced motion is the PLAYER, and
 * it is the half a CSS media query cannot reach: prefers-reduced-motion in
 * app.css governs the page's own animations and has no view of a blend computed
 * here. Closes #56 as well as its own line in #66.
 */
function slidingAllowed() {
    return blending && !motionIsReduced();
}

function cameraOrigin() {
    const t = slidingAllowed() && CAMERA_BLEND > 0
        ? Math.min(1, Math.max(0, (now() - cameraAt) / CAMERA_BLEND))
        : 1;

    // Smoothstep. The view eases in and settles rather than starting and stopping
    // dead ten times a second, which reads as a twitch rather than a scroll.
    const e = t * t * (3 - 2 * t);

    return {
        x: cameraFrom.x + (field.ox - cameraFrom.x) * e,
        y: cameraFrom.y + (field.oy - cameraFrom.y) * e,
    };
}

/**
 * How far a moved sprite still has to travel, as a fraction of the way back to
 * where it came from. 1 at the instant the snapshot lands, 0 once it has arrived.
 */
function moveLag() {
    if (!slidingAllowed() || field.moves.size === 0) return 0;

    const t = MOVE_BLEND > 0
        ? Math.min(1, Math.max(0, (now() - cameraAt) / MOVE_BLEND))
        : 1;

    // The same smoothstep the camera uses, so a boulder falling while the view
    // scrolls does not ease on a different curve from the ground it is falling
    // past.
    return 1 - t * t * (3 - 2 * t);
}

function paintField() {
    if (!field || !ctx || !sheet) return;

    const { tiles, side } = field;
    const origin = cameraOrigin();
    const lag = moveLag();

    // A blast, and only a blast, may nudge the view. Applied HERE rather than
    // inside cameraOrigin, because the camera's own latches are the engine's
    // and a presentation wobble must never reach them - the view still moves a
    // whole cell at a time exactly as 0xFF87 does.
    if (effects) {
        const nudge = effects.cameraOffset();
        if (nudge) { origin.x += nudge.x; origin.y += nudge.y; }
    }

    ctx.fillStyle = floor;
    ctx.fillRect(0, 0, surfaceW, surfaceH);

    // The whole cell the view has reached, and how far past it the view has slid.
    const cellX = Math.floor(origin.x);
    const cellY = Math.floor(origin.y);
    const offsetX = (origin.x - cellX) * side;
    const offsetY = (origin.y - cellY) * side;

    // One cell early and one late on each axis, so the row and column sliding in
    // from the edge are drawn rather than missing. Anything outside the cave is
    // skipped, and the floor colour is already down.
    for (let y = -1; y <= ROWS; y++) {
        const row = cellY + y;
        if (row < 0 || row >= CAVE_ROWS) continue;

        for (let x = -1; x <= COLS; x++) {
            const column = cellX + x;
            if (column < 0 || column >= CAVE_COLS) continue;

            const cell = row * CAVE_COLS + column;
            const i = cell * 2;
            const sx = tiles[i];
            if (sx < 0) continue;

            // Drawn at its new cell, pulled BACK towards the old one, by an amount
            // that shrinks to nothing. Never drawn ahead of where the simulation
            // has already put it.
            let backX = 0, backY = 0;
            if (lag > 0) {
                const move = field.moves.get(cell);
                if (move) {
                    backX = move.dx * side * lag;
                    backY = move.dy * side * lag;
                }
            }

            const dx = Math.round(x * side - offsetX + backX);
            const dy = Math.round(y * side - offsetY + backY);
            const key = sx * 65536 + tiles[i + 1];

            // THE CONTACT SHADOW, under everything that can fall. The mask
            // drawn low, soft and faint: it is what tells you which cell an
            // object is actually in, which matters most for the one thing in
            // this game that arrives from above.
            //
            // Under the sprite and under the outline, so neither is dulled by
            // it.
            if (lighting && shadowed.has(key)) {
                const mask = buildSilhouette();
                if (mask) {
                    const was = ctx.globalAlpha;
                    ctx.globalAlpha = 0.28;
                    ctx.drawImage(mask,
                                  sx * sheetScale, tiles[i + 1] * sheetScale, side, side,
                                  dx, dy + Math.max(1, Math.round(side / 14)),
                                  side, side);
                    ctx.globalAlpha = was;
                }
            }

            // The outline, under the sprite. Four offsets rather than eight:
            // the diagonals cost 50% more draw calls for a corner nobody can
            // see at this size.
            if (outlines && outlinedTiles.has(sx * 65536 + tiles[i + 1])) {
                const mask = buildSilhouette();
                if (mask) {
                    const w = Math.max(1, Math.round(side / 48));
                    for (const [ox, oy] of [[-w, 0], [w, 0], [0, -w], [0, w]]) {
                        ctx.drawImage(mask,
                                      sx * sheetScale, tiles[i + 1] * sheetScale, side, side,
                                      dx + ox, dy + oy, side, side);
                    }
                }
            }

            ctx.drawImage(sheet,
                          sx * sheetScale, tiles[i + 1] * sheetScale, side, side,
                          dx, dy, side, side);

            // THE GLOW, over the sprite. Only the things that genuinely emit -
            // fire, and an exit once it is open, which is the single most
            // important state change in a cave and otherwise looks like any
            // other tile.
            //
            // Additive and small. `lighter` cannot make anything darker, which
            // is the property the whole feature rests on: with the lights off
            // the cave is exactly as readable, and with them on nothing is
            // hidden.
            if (lighting && emissive.has(key)) {
                const was = ctx.globalCompositeOperation;
                const wasAlpha = ctx.globalAlpha;

                ctx.globalCompositeOperation = 'lighter';
                ctx.globalAlpha = 0.22;

                // Two passes: the sprite over itself for the lit surface, and a
                // soft radial for the spill. A blur would cost a filter on
                // every tile; a gradient is one fill.
                ctx.drawImage(sheet,
                              sx * sheetScale, tiles[i + 1] * sheetScale, side, side,
                              dx, dy, side, side);

                const cx = dx + side / 2;
                const cy = dy + side / 2;
                const glow = ctx.createRadialGradient(cx, cy, side * 0.1, cx, cy, side * 0.9);
                glow.addColorStop(0, EMISSIVE_COLOURS[emissive.get(key)] || EMISSIVE_COLOURS[0]);
                glow.addColorStop(1, 'rgba(0,0,0,0)');

                ctx.globalAlpha = 0.18;
                ctx.fillStyle = glow;
                ctx.fillRect(dx - side * 0.4, dy - side * 0.4, side * 1.8, side * 1.8);

                ctx.globalCompositeOperation = was;
                ctx.globalAlpha = wasAlpha;
            }
        }
    }

    // OVER the tiles and under nothing. A particle that could be covered by the
    // cell it is describing would be pointing at something it had hidden.
    //
    // It is given the camera's own fractional origin, so the effects scroll with
    // the cave rather than sitting still on the screen while it moves beneath
    // them.
    if (effects) effects.paint(ctx, origin.x, origin.y, side);
}

// Returns horizontal, vertical and fire in the original's own encoding, so the
// C# side does not have to know anything about browser key names.
export function readInput() {
    // e.code throughout - see the keydown listener. The arrows are spelled the
    // same either way; the letters and space are not, and Shift is two keys.
    //
    // The pad is ORed in rather than checked instead, so a device with both just
    // works and there is no mode to be in.
    //
    // Note it is an OR and not a "most recent wins": left still beats right and
    // up still beats down, whichever arrived first and whichever device it came
    // from. That is the original's own precedence and this does not disturb it -
    // holding left on a keyboard and right on the pad gives left, the same as
    // holding both arrows always did.
    const stick = readGamepad();

    let h = 0;
    if (held('left') || pad.h === LEFT || stick.h === LEFT) h = 4;
    else if (held('right') || pad.h === RIGHT || stick.h === RIGHT) h = 8;

    let v = 0;
    if (held('up') || pad.v === UP || stick.v === UP) v = 1;
    else if (held('down') || pad.v === DOWN || stick.v === DOWN) v = 2;

    const fire = held('fire') || pad.fire || stick.fire;
    return h | (v << 4) | (fire ? 256 : 0);
}

// ---- what the keys are bound to ------------------------------------------
//
// These were written into readInput. That was fine while there was one layout
// and no way to change it, and it stopped being fine for a specific reason:
// e.code is a PHYSICAL position, so KeyA is where QWERTY's A is and where
// AZERTY's Q is. It is the right default - WASD is a shape under the left hand
// rather than four letters - and it is only defensible if the shape can be
// moved.
//
// Every action takes a list, because the arrows and WASD are both the default
// and a rebind should be able to keep one while changing the other.

/** The keys the game shipped with, and what a reset goes back to. */
export const DEFAULT_BINDINGS = Object.freeze({
    left: ['ArrowLeft', 'KeyA'],
    right: ['ArrowRight', 'KeyD'],
    up: ['ArrowUp', 'KeyW'],
    down: ['ArrowDown', 'KeyS'],
    fire: ['Space', 'ShiftLeft', 'ShiftRight'],
});

let bindings = { ...DEFAULT_BINDINGS };

/**
 * What a KeyboardEvent.code can actually be, near enough.
 *
 * Checking the SHAPE rather than keeping a list of every code in the spec: the
 * list would go stale and a keyboard with a key this does not know about should
 * be bindable. What it refuses is a string no keyboard will ever produce -
 * "NotARealCode" was accepted before, and it binds an action to nothing, which
 * makes that direction unpressable.
 */
const PLAUSIBLE_CODE =
    /^(Key[A-Z]|Digit[0-9]|Numpad[A-Za-z0-9]+|Arrow(Up|Down|Left|Right)|(Shift|Control|Alt|Meta)(Left|Right)|F[1-9][0-9]?|Space|Enter|Backspace|Delete|Insert|Home|End|Page(Up|Down)|Comma|Period|Slash|Semicolon|Quote|Backquote|Minus|Equal|Bracket(Left|Right)|Backslash|IntlBackslash|CapsLock)$/;

/**
 * Keys the page itself needs, which a binding may not take.
 *
 * Tab moves focus and Escape closes the menu - see the document listener at the
 * bottom of this file. Binding movement to either leaves the page's own use of
 * it running as well, so one press does two things and the player cannot tell
 * which they asked for. F5 and F11 belong to the browser.
 */
const RESERVED_CODES = new Set(['Tab', 'Escape', 'F5', 'F11', 'F12']);

/** True when any key bound to an action is down. */
function held(action) {
    const codes = bindings[action];
    if (!codes) return false;
    for (const code of codes) if (keys.has(code)) return true;
    return false;
}

/**
 * Replaces the bindings, keeping the defaults for anything not supplied.
 *
 * Validated rather than trusted: an action nobody knows about is ignored, and a
 * list that is empty or not a list of strings falls back to the default for
 * that action rather than leaving it unpressable. A stored binding that made a
 * direction impossible would be a game that cannot be played and cannot be
 * fixed from inside itself.
 */
export function setBindings(wanted) {
    const next = { ...DEFAULT_BINDINGS };

    if (wanted && typeof wanted === 'object') {
        // Every code already spoken for, so no two actions can share one.
        //
        // Sharing is not merely odd, it is unfixable from inside the game:
        // readInput resolves left before right, so binding both to the same key
        // makes right impossible and leaves no way to press anything that would
        // undo it. Astra, 19 September 2026.
        const taken = new Set();

        for (const action of Object.keys(DEFAULT_BINDINGS)) {
            const codes = wanted[action];
            if (!Array.isArray(codes) || codes.length === 0) continue;

            const usable = codes.every(c =>
                typeof c === 'string'
                && PLAUSIBLE_CODE.test(c)
                && !RESERVED_CODES.has(c)
                && !taken.has(c));

            // All or nothing per action. Half a rebind is a state the player did
            // not choose and cannot reason about.
            if (!usable) continue;

            next[action] = [...codes];
            for (const c of codes) taken.add(c);
        }

        // An action left on its default must not now collide with a rebind that
        // took one of its keys - the default would win on some presses and not
        // others depending on which action is tested first.
        for (const action of Object.keys(next)) {
            if (wanted[action] !== undefined && next[action] !== DEFAULT_BINDINGS[action]) continue;

            const free = next[action].filter(c => !taken.has(c));
            if (free.length > 0) {
                next[action] = free;
                for (const c of free) taken.add(c);
            }
        }
    }

    bindings = next;

    // Anything held under the old bindings is not held under the new ones.
    // Rebinding mid-press would otherwise leave a direction stuck on a key the
    // game no longer reads, with nothing able to release it.
    keys.clear();

    return { ...bindings };
}

/** What is bound now. */
export function getBindings() {
    return { ...bindings };
}

// ---- the gamepad ---------------------------------------------------------
//
// A third source, read the same way the other two are: ORed into readInput at
// the simulation boundary, in the original's own encoding, with no analog
// movement and no different diagonal rules. A stick past the dead zone is a
// direction exactly as a thumb past the dead zone is.
//
// POLLED, not listened to. The Gamepad API has no button events worth using -
// it hands you a snapshot and expects you to ask - so this reads once per call
// to readInput, which is once per simulation step, which is where every other
// input is read. That is what keeps them all the same thing.
//
// WRITTEN WITHOUT A CONTROLLER TO TEST IT ON. The mappings below are the
// "standard" layout, which is what a browser reports for anything it recognises,
// and the shapes are conventional - but this has never been held. The touch pad
// had four real defects that only came out under review, almost all about input
// arriving and leaving in ways the happy path does not cover, and a gamepad
// written blind deserves the same suspicion.

/**
 * How far a stick must leave centre before it means a direction.
 *
 * Sticks rest off-centre and drift with age, and a worn one can sit at 0.2
 * indefinitely. Too small and the player walks into a wall while nobody is
 * touching it; too large and the control feels dead. Conventional is 0.4 to 0.5
 * for a digital read like this one.
 */
const STICK_DEAD_ZONE = 0.45;

/**
 * A trigger or face button counts as pressed above this.
 *
 * Analog triggers report a fraction, and a resting trigger is not always 0.
 */
const BUTTON_THRESHOLD = 0.5;

/**
 * The standard layout's indices, from the Gamepad API's own mapping.
 *
 * Fire is every face button rather than one: which of the four is "the button"
 * is a matter of platform habit, and a game with one action should answer to
 * whichever the player presses. The shoulders are included because a thumb on
 * the stick and a finger on a shoulder is how a lot of people hold one.
 */
const GAMEPAD_FIRE = [0, 1, 2, 3, 4, 5, 6, 7];
const GAMEPAD_DPAD = { up: 12, down: 13, left: 14, right: 15 };

const NO_STICK = { h: null, v: null, fire: false };

/**
 * What the pad was doing last time it was read, or NULL for "not known".
 *
 * The menus want a one-shot TAP rather than held state - the world select reads
 * takeTap - so a direction has to be offered on the edge. Without this, holding
 * the stick left for a tenth of a second scrolls through every world there is.
 *
 * Null is the important part and it is not the same as neutral. Resetting to
 * neutral on a hide said "nothing was held", so coming back to the page still
 * holding the stick looked like a fresh push and chose a world nobody asked
 * for. Null says "I do not know what was held", and the next poll adopts
 * whatever it finds without emitting an edge for it. Astra, 19 September 2026 -
 * who also caught that the check written for this asserted the wrong behaviour
 * and passed.
 */
let lastStick = null;

/**
 * Reads whichever connected pad is reporting.
 *
 * Every connected one rather than the first: a slot can hold a disconnected
 * entry, browsers differ on whether they compact the list, and a player with two
 * things plugged in should be able to use either.
 */
function readGamepad() {
    if (typeof navigator !== 'object' || typeof navigator.getGamepads !== 'function')
        return NO_STICK;

    let pads;
    try { pads = navigator.getGamepads(); } catch { return NO_STICK; }
    if (!pads) return NO_STICK;

    let h = null, v = null, fire = false;

    for (const gp of pads) {
        if (!gp || !gp.connected) continue;

        // Only a pad the browser says is laid out the way this assumes.
        //
        // Everything below is the "standard" mapping's indices and axis signs.
        // Applying them to a pad that reports something else does not degrade to
        // no input, it produces the WRONG input - a trigger read as left, a
        // shoulder read as fire.
        //
        // Strict on purpose, and the trade is worth stating because it is not
        // free. An empty `mapping` means the browser does not know the layout,
        // which some browsers report for pads that would in fact have worked -
        // so this refuses a pad that might have been fine. That is the right way
        // round while nobody has tested any of this with a controller in their
        // hands: a pad that does nothing is a puzzle somebody can report, and a
        // pad that walks left when you pull a trigger is a puzzle nobody can
        // describe. Loosen it with evidence, not with optimism.
        if (gp.mapping !== 'standard') continue;

        const axes = gp.axes || [];
        const buttons = gp.buttons || [];

        const pressed = i => {
            const b = buttons[i];
            if (!b) return false;
            // A button is an object with .pressed in every browser that matters,
            // and a bare number in some older ones.
            return typeof b === 'object' ? (b.pressed || b.value > BUTTON_THRESHOLD)
                                         : b > BUTTON_THRESHOLD;
        };

        // The D-pad first: it is unambiguous where a stick is a judgement.
        if (pressed(GAMEPAD_DPAD.left)) h = LEFT;
        else if (pressed(GAMEPAD_DPAD.right)) h = RIGHT;

        if (pressed(GAMEPAD_DPAD.up)) v = UP;
        else if (pressed(GAMEPAD_DPAD.down)) v = DOWN;

        // Then the left stick, only where the D-pad said nothing - so resting a
        // thumb on the stick cannot fight the pad the player is actually using.
        if (h === null && typeof axes[0] === 'number') {
            if (axes[0] <= -STICK_DEAD_ZONE) h = LEFT;
            else if (axes[0] >= STICK_DEAD_ZONE) h = RIGHT;
        }

        if (v === null && typeof axes[1] === 'number') {
            // Negative is UP in the standard mapping, which is the opposite of
            // what the name suggests and the easiest thing here to get backwards.
            if (axes[1] <= -STICK_DEAD_ZONE) v = UP;
            else if (axes[1] >= STICK_DEAD_ZONE) v = DOWN;
        }

        for (const i of GAMEPAD_FIRE) if (pressed(i)) { fire = true; break; }

        // First pad that says anything wins, so two connected controllers do not
        // cancel each other out.
        if (h !== null || v !== null || fire) break;
    }

    // The edges, for the screens that read taps rather than held state - but
    // only against a state we actually saw. A null memory is adopted silently.
    if (lastStick !== null) {
        if (h !== lastStick.h) offerMenuTap(h);

        // Only outside a cave, the same rule the canvas tap follows. Fire is the
        // most-pressed button in the game, and queuing an Enter for every shot
        // left one sitting in the tap set - "Choose world" then consumed it and
        // started a world without the player confirming anything.
        if (tapStarts && fire && !lastStick.fire) tapped.add('Enter');
    }

    lastStick = { h, v, fire };

    return { h, v, fire };
}

// ---- the touch pad -------------------------------------------------------
//
// A phone has no keyboard, and until this the game could not be started on one
// at all: a tap opened the audio and nothing else, so the title screen's "press
// Enter" was the end of the road.
//
// Three things, and the third is the one that is easy to get wrong.
//
//   - A thumb pad. One square rather than four buttons, because a finger slides
//     between directions and four separate targets make that a miss. Where the
//     touch is relative to the centre picks the direction, and the middle is
//     dead so resting a thumb is not a move.
//   - A fire button, as its own target.
//   - BOTH AT ONCE. Firing is a direction plus fire - it is the whole mechanic -
//     so the two have to be independent pointers. Tracked by pointerId, which is
//     what makes a second finger a second input rather than a replacement for
//     the first.
//
// Pointer events rather than touch events: the same code then covers a stylus
// and a mouse, and the capture below is what keeps a finger that slides off the
// pad still held rather than silently released.

const LEFT = 'l', RIGHT = 'r', UP = 'u', DOWN = 'd';

const pad = { h: null, v: null, fire: false };

/**
 * Rebuilds the pad from the pointers still down.
 *
 * Not "clear what this pointer was doing", which is what it used to be. Two
 * fingers on FIRE and lifting either one turned fire off while the other was
 * still pressed, and the same for two fingers on the pad. A phone gets extra
 * fingers by accident constantly - the heel of a hand, a second thumb - so the
 * state has to be derived from what is held rather than tracked by the last
 * event to arrive. Astra, 19 September 2026.
 */
function recomputePad() {
    let h = null, v = null, fire = false;

    for (const held of pointers.values()) {
        if (held.kind === 'fire') { fire = true; continue; }
        // Last direction pointer wins, which is what a second thumb landing on
        // the pad means.
        if (held.h !== null) h = held.h;
        if (held.v !== null) v = held.v;
    }

    pad.h = h;
    pad.v = v;
    pad.fire = fire;
}

/** Whether a tap on the picture counts as Enter. See tapToStart. */
let tapStarts = false;

/** Which pointer is doing what, so two fingers do not overwrite each other. */
const pointers = new Map();

/**
 * How far from the centre a touch has to be before it means a direction, as a
 * fraction of the pad's half-width.
 *
 * A thumb resting in the middle should be no direction at all, and a thumb is
 * wide: too small a dead zone and the pad twitches under a stationary hand.
 */
const PAD_DEAD_ZONE = 0.28;

/**
 * The world select reads one-shot taps rather than held keys, so the pad has to
 * offer it one. The canvas tap is what confirms.
 */
function offerMenuTap(h) {
    if (h === LEFT) tapped.add('ArrowLeft');
    else if (h === RIGHT) tapped.add('ArrowRight');
}

function directionFrom(element, event) {
    const box = element.getBoundingClientRect();
    const x = (event.clientX - (box.left + box.width / 2)) / (box.width / 2);
    const y = (event.clientY - (box.top + box.height / 2)) / (box.height / 2);

    // Each axis decided on its own, so a diagonal is a diagonal. The engine
    // takes horizontal and vertical separately and has its own rule about which
    // wins - that is its decision to make, not this one's.
    return {
        h: Math.abs(x) < PAD_DEAD_ZONE ? null : (x < 0 ? LEFT : RIGHT),
        v: Math.abs(y) < PAD_DEAD_ZONE ? null : (y < 0 ? UP : DOWN),
    };
}

function releasePointer(id) {
    if (!pointers.delete(id)) return;
    recomputePad();
}

/**
 * THE PAGE WENT AWAY. Latched, and only an intentional resume clears it.
 *
 * Dropping the held keys was all that used to happen here, and the cave went on
 * advancing. A hidden tab still gets timers - throttled to whatever that browser
 * allows, which is a second or worse - so the simulation crawls on in the
 * background and the ordinary result is coming back to a dead player. StepPacer
 * stops a catch-up BURST after a stall, which is a different problem: it does
 * not stop the steps happening in the first place.
 *
 * Latched rather than tracking document.hidden, because becoming visible again
 * is not consent. A player who switched to another tab mid-cave is not ready the
 * instant they switch back, and the window they would be robbed of is exactly
 * the one where a boulder is already falling.
 *
 * The host polls this on its own loop rather than being called back, which is
 * late by at most one step and cannot race the step in flight.
 */
let interrupted = false;

function interrupt() {
    interrupted = true;

    // Silence everything that is sounding, without touching what the player
    // chose. Suspending the context is the right primitive for that: it stops
    // the oscillators and the streamed theme together, keeps every gain and
    // every mute exactly as it was, and resume() puts it all back. Muting
    // instead would have to be unpicked afterwards and would fight the mute the
    // player may have set themselves.
    if (audio && audio.state === 'running') audio.suspend();
}

/** True once the page has been hidden, until the host acknowledges a resume. */
export function wasInterrupted() { return interrupted; }

/**
 * What the audio graph is doing, for the checks.
 *
 * Exported rather than inferred, because "is it silent" is not answerable from
 * the outside: every gain and every mute is exactly where the player left it
 * during a pause, and the context's own state is the only thing that moves.
 */
export function audioState() { return audio ? audio.state : 'none'; }

/**
 * A fresh mount is not interrupted.
 *
 * This module outlives the component, so without it an interruption latched
 * before a remount would leave the new component paused over a cave it never
 * paused - with a Resume button as the only way in, for a page the player never
 * left. Found by the lifecycle checks, which mount repeatedly in one process.
 */
function clearInterrupt() { interrupted = false; }

/** The player asked to carry on. */
export function resume() {
    interrupted = false;
    wakeAudio();
}

/**
 * Wakes the audio, UNLESS the page is waiting for an intentional resume.
 *
 * Every path that wanted sound used to call `audio.resume()` directly, and none
 * of them knew about the pause. So: hide the page mid-cave, come back, and
 * toggle music off and on while the Resume button is still up - the cave stays
 * held and the sound comes back, which is the one combination the policy exists
 * to prevent. Astra, 19 September 2026.
 *
 * Returns whether the audio is awake, so a caller can tell the difference
 * between "started" and "refused for now".
 */
function wakeAudio() {
    if (!audio) return false;
    if (interrupted) return false;
    if (audio.state === 'suspended') audio.resume();
    return true;
}

/** Drops every finger. For blur, for hiding, and for dispose. */
function releaseAllPointers() {
    pointers.clear();
    recomputePad();
}

/**
 * Wires the on-screen controls up. Called from the component, which owns the
 * elements; this module owns what they mean.
 *
 * Everything attached here goes through `on`, so dispose() takes it off again
 * with the rest - a remount must not stack a second set.
 */
export function attachPad(padElement, fireElement) {
    if (!padElement || !fireElement) return false;

    const press = (element, kind) => {
        on(element, 'pointerdown', e => {
            noteGesture();
            e.preventDefault();

            // Capture, so a thumb that slides off the pad mid-cave is still
            // held. Without it the direction drops the moment the finger leaves
            // the element, which on a small screen is constantly.
            try { element.setPointerCapture(e.pointerId); } catch { /* not fatal */ }

            if (kind === 'fire') {
                pointers.set(e.pointerId, { kind, h: null, v: null });
                recomputePad();
                return;
            }

            const { h, v } = directionFrom(element, e);
            pointers.set(e.pointerId, { kind, h, v });
            recomputePad();
            offerMenuTap(h);
        });

        // Sliding between directions without lifting, which is the point of a
        // pad rather than four buttons.
        on(element, 'pointermove', e => {
            if (kind !== 'pad') return;
            const held = pointers.get(e.pointerId);
            if (!held) return;

            const { h, v } = directionFrom(element, e);
            const turned = h !== held.h;

            held.h = h;
            held.v = v;
            recomputePad();

            // The menus read a TAP, and sliding from the dead centre out to the
            // left is how someone with a thumb chooses a world. Only on the
            // change, or one slide would scroll through five of them.
            if (turned) offerMenuTap(h);
        });

        for (const type of ['pointerup', 'pointercancel', 'lostpointercapture'])
            on(element, type, e => releasePointer(e.pointerId));
    };

    // The same three on the window, as a net.
    //
    // setPointerCapture is in a try because it can throw, and where it does the
    // element never sees the pointerup that follows - the finger is simply held
    // for ever. Listening on the window as well costs nothing and is the
    // difference between a rare failure and a stuck direction. Astra, 19
    // September 2026.
    for (const type of ['pointerup', 'pointercancel'])
        on(window, type, e => releasePointer(e.pointerId));

    press(padElement, 'pad');
    press(fireElement, 'fire');
    return true;
}

/**
 * A tap on the picture is Enter.
 *
 * The title says "press Enter to begin" and the select screen wants a confirm,
 * and neither is reachable on a phone. This is what makes the game startable
 * without a keyboard at all - and it is deliberately the canvas rather than a
 * button, because every screen that asks for a press is the canvas.
 *
 * Only outside a cave: during play a tap on the picture must not be a keystroke,
 * or resting a hand on the screen plays the game.
 */
export function tapToStart(enabled) {
    tapStarts = enabled;
}

// ---- sound ---------------------------------------------------------------
//
// The original drives a PC speaker: one square wave at a time, gated on and off.
// A single oscillator through a gain node is the same shape, so nothing here
// tries to be cleverer than the hardware was.

let audio = null;
let osc = null;
let gate = null;
let muted = false;        // the sound effects
let musicOff = false;     // the theme, the sting and the world track

function ensureAudio() {
    if (audio) return true;
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return false;

    audio = new Ctx();
    gate = audio.createGain();
    gate.gain.value = 0;
    gate.connect(audio.destination);

    osc = audio.createOscillator();
    osc.type = 'square';
    osc.frequency.value = 440;
    osc.connect(gate);
    osc.start();
    return true;
}

/**
 * A gesture happened, so open the audio and start the theme if it is waiting.
 *
 * In the handler rather than on the next simulation step, for two reasons.
 * Safari refuses a resume that is deferred out of the gesture's own turn, so
 * doing it here is the reliable place; and the theme then starts on the press
 * itself rather than up to a tenth of a second later, which is the difference
 * between the music answering the key and trailing it.
 */
function noteGesture() {
    gestured = true;
    if (!musicArmed) return;

    // Spent only if the theme ACTUALLY started. playMusic refuses for several
    // ordinary reasons - the player has music off, the track is not in yet, the
    // browser would not resume - and clearing the flag on a refusal spent the
    // one arming on nothing: no later gesture tried again, and the theme stayed
    // silent for the rest of the session. Astra, 19 September 2026.
    musicArmed = !playMusic();
}

/**
 * Says the assets are in and the theme may start on the next gesture.
 *
 * Called when the loading phase finishes. Before that a gesture still opens the
 * audio; it just has nothing loaded to play, and starting a half-buffered track
 * is how the theme came in stuttering on a slow line.
 */
export function armMusic() {
    musicArmed = !musicPlaying();
    return musicArmed;
}

// Browsers will not start audio until the page has been interacted with, so the
// first key or click is what actually opens it.
export function unlock() {
    if (!ensureAudio()) return false;
    wakeAudio();
    return true;
}

/**
 * Opens the audio if the page has been interacted with, and says whether it did.
 *
 * The caller asks once a tick until it gets a true, and never again after that,
 * so this costs nothing past the first gesture. It exists as one call rather
 * than a `sawGesture` the caller then acts on because the unlock has to happen
 * inside the same turn as the gesture on Safari, where a resume deferred to a
 * later tick is refused.
 */
export function unlockIfGestured() {
    if (!gestured) return false;
    return unlock();
}

/** Whether the theme is sounding, so the UI can render its control from truth. */
export function musicPlaying() {
    if (enhanced && vipMusic) return vipMusic.playing();
    return musicTimer !== null;
}

/**
 * Mutes the SOUND EFFECTS, and only those.
 *
 * The footer carries two glyphs, one per channel, and each one mutes its own.
 * This used to call vipMusic.setMuted as well, which took the music master gain
 * to zero - so muting the effects silenced the theme, the cave sting and the
 * world track too, and the music glyph went on claiming the music was on. The
 * theme's own channel is setMusicEnabled below.
 */
// ---- how loud, and how much motion ---------------------------------------

/**
 * The two channels, 0 to 1, applied on top of the mute rather than instead of
 * it. Mute is "not now"; level is "this loud when it is on", and a player who
 * turns the music down to 20 and then mutes should get 20 back when they unmute
 * rather than full.
 */
let musicLevel = 0.7;
let effectsLevel = 0.8;

/** The effects channel's own ceiling, which the level scales. */
const EFFECTS_CEILING = 1;

/**
 * Sets a channel's level, 0 to 100.
 *
 * Applied immediately where the channel exists, and remembered either way -
 * the audio graph is built on the first gesture, so a level chosen before that
 * has to survive until there is something to apply it to.
 */
export function setLevel(channel, percent) {
    const level = Math.min(1, Math.max(0, (Number(percent) || 0) / 100));

    // Each channel has TWO implementations - the faithful one in this file and
    // the VIP one in its own module - and both have to hear about it. The first
    // version of this sent the music level to `vip`, which is sfx-vip.js, and
    // called an export neither module had; optional chaining swallowed it and
    // the slider moved a number and nothing else. Astra, 19 September 2026.
    if (channel === 'music') {
        musicLevel = level;
        if (vipMusic) vipMusic.setLevel(level);
        return level;
    }

    if (channel === 'effects') {
        effectsLevel = level;
        if (vip) vip.setLevel(level);
        // The faithful speaker has no standing gain to set: it opens the gate
        // for each note and closes it again, so the level is read there. See
        // playDivisor.
        return level;
    }

    return null;
}

/**
 * What the faithful speaker's gate should open to for a note.
 *
 * 0.05 is the square wave's own balance - a PC speaker through an oscillator is
 * loud - and the level multiplies it. Mute wins over both.
 */
export function effectsGain() {
    return muted ? 0 : SPEAKER_LEVEL * effectsLevel;
}

/** The faithful speaker's own balance, which the player's level multiplies. */
const SPEAKER_LEVEL = 0.05;

/**
 * Whether the renderer should move less than it otherwise would.
 *
 * Three answers, not two. Null defers to the operating system, because a player
 * who has already told their machine should not have to say it again - and the
 * CSS prefers-reduced-motion rules in app.css cannot reach a blend computed
 * here, which is the whole reason this exists.
 */
let reducedMotion = null;

export function setReducedMotion(value) {
    reducedMotion = value === true || value === false ? value : null;
    return motionIsReduced();
}

/**
 * The system's own answer, queried once and then watched.
 *
 * motionIsReduced is called several times per animation frame - the camera, the
 * sprites and the loop's own stopping test each ask - and calling matchMedia
 * every time is work for an answer that changes about never. The query object
 * keeps itself up to date, so this stays correct if the player changes the
 * setting with the page open.
 */
let systemMotionQuery;

function systemPrefersReducedMotion() {
    if (systemMotionQuery === undefined) {
        try {
            systemMotionQuery = typeof matchMedia === 'function'
                ? matchMedia('(prefers-reduced-motion: reduce)')
                : null;
        } catch {
            systemMotionQuery = null;
        }
    }

    return systemMotionQuery ? systemMotionQuery.matches : false;
}

export function motionIsReduced() {
    if (reducedMotion !== null) return reducedMotion;
    return systemPrefersReducedMotion();
}

export function setMuted(value) {
    muted = value;
    if (muted && gate) gate.gain.cancelScheduledValues(audio.currentTime);
    if (muted && gate) gate.gain.setValueAtTime(0, audio.currentTime);
    if (vip) vip.setMuted(value);
}

/**
 * The theme glyph's channel: the looping theme, the cave sting and the
 * world-complete track.
 *
 * A fanfare is music. Turning the music off has to take the stings with it, or
 * pressing it during the world-complete screen leaves 82 seconds of track
 * playing under a control that says the music is off.
 *
 * Stopping rather than muting, because the interlude waits on
 * worldStingPlaying() - which reads the element, not the gain - and a track
 * muted rather than stopped would hold that screen in silence until it ran out.
 */
export function setMusicEnabled(on) {
    musicOff = !on;
    if (!musicOff) return;
    stopMusic();
    if (vipMusic) vipMusic.stopAll();
}

// ---- the two presentations -----------------------------------------------
//
// Faithful plays what the hardware played, from the pitch. VIP plays a designed
// sound per event. Both read the same driver pass, which is why the engine hands
// over the event and the divisor together rather than either alone.
//
// The VIP module is loaded on demand and shares this AudioContext. A second
// context would work but would drift against this one, and two clocks is exactly
// the wrong thing to have under a game that fires sounds on a 10 Hz tick.

let vip = null;

/**
 * VIP's local feedback, or null on a faithful build.
 *
 * Imported with the other two enhanced modules and off the same build query, so
 * the three cannot come from different deploys - which has happened, and threw
 * on the first sound made.
 */
let effects = null;

/** Turn the flattened kind/column/row triples into particles. */
function spawnEffects(events) {
    if (!events || !effects) return;

    for (let i = 0; i + 2 < events.length; i += 3) {
        const x = events[i + 1];
        const y = events[i + 2];

        switch (events[i]) {
            case 0: effects.dug(x, y); break;
            case 1: effects.collected(x, y); break;
            case 2: effects.landed(x, y); break;
            case 3: effects.blasted(x, y); break;
            case 4: effects.fired(x, y); break;
        }
    }
}
let vipMusic = null;

/**
 * Hands the current levels to whichever VIP modules are loaded.
 *
 * The modules arrive after the levels are set - setAudioMode imports them on
 * demand - so a level chosen at startup would otherwise apply to nothing and
 * the module would come up at its own default.
 */
function pushLevelsToVip() {
    if (vip) vip.setLevel(effectsLevel);
    if (vipMusic) vipMusic.setLevel(musicLevel);
}
let enhanced = false;

/** SoundEvent.LowRumble - walking. The one sound that does not duck the music. */
const FOOTSTEP = 0x02;

export async function setAudioMode(mode) {
    const wanted = mode === 'vip';
    if (wanted === enhanced) return enhanced;

    // Whichever theme is playing belongs to the mode being left, so it stops. The
    // two arrangements are different lengths of the same tune and crossfading them
    // would just sound like a mistake.
    const wasPlaying = musicTimer !== null || (vipMusic && vipMusicPlaying);
    stopMusic();

    // Everything the outgoing presentation still had in the air goes with it. A
    // queued frame would otherwise be played by whichever mode was in force when
    // its timer came due rather than the one that scheduled it, and a sting or a
    // world track started in VIP would play on over the faithful speaker.
    cancelPendingSounds();
    if (vipMusic) { vipMusic.stopSting(); vipMusic.stopWorldSting(); }
    if (vip) vip.stopAll();
    silenceSpeaker();

    enhanced = wanted;

    if (enhanced && !vip && ensureAudio()) {
        // The same cache-buster this module was loaded with, passed on.
        //
        // Home.razor imports game.js as ./game.js?v={assembly version}. These
        // two were imported bare, so a deploy could hand a browser a fresh
        // game.js and leave it holding a cached sfx-vip.js or music-vip.js from
        // a previous build. That is not hypothetical: it shipped, and it threw
        // "vipMusic.duck is not a function" the moment anything made a noise,
        // because the new game.js called a function the old module did not have.
        //
        // import.meta.url carries whatever query this module was fetched with,
        // so reusing it keeps the three files on the same build by construction
        // rather than by remembering to bump them together.
        const v = new URL(import.meta.url).search;
        [vip, vipMusic, effects] = await Promise.all([
            import(`./sfx-vip.js${v}`),
            import(`./music-vip.js${v}`),
            import(`./effects-vip.js${v}`),
        ]);
        vip.attach(audio, audio.destination);
        vip.setMuted(muted);
        vipMusic.attach(audio, audio.destination, localUrl);
        if (music) vipMusic.load(music);

        // The modules arrive AFTER the levels are chosen - this import is on
        // demand and the stored settings are applied during startup - so
        // without this they come up at their own defaults and the slider the
        // player already moved means nothing.
        pushLevelsToVip();
    }

    if (wasPlaying) playMusic();
    return enhanced;
}

/**
 * One frame of the driver, routed to whichever presentation is on.
 *
 * `event` is the request the engine served, `divisor` the pitch the hardware
 * would have used. Faithful needs only the second; VIP needs the first and reads
 * the second for the original's own two-pitch draw.
 */
export function playSound(event, divisor, holdSeconds, when = 0) {
    // Every effect ducks the music, in one place rather than at each call site,
    // so nothing can be added later that plays over the theme without pushing it
    // out of the way first.
    //
    // A silent frame does not duck. It still goes THROUGH, because silence is
    // information - it is the driver saying the holding tone has stopped - but
    // ducking on it would hold the music down for the frames where nothing is
    // happening, which is the opposite of what ducking is for.
    //
    // The footstep is the exception, and it has to be. It fires on every step the
    // player takes, which is up to ten a second, against a duck that takes 310 ms
    // to recover - so walking anywhere would hold the music down for as long as
    // the player kept moving. It is also the one sound nobody needs to hear over
    // the music: they can see they are walking.
    if (divisor && event !== FOOTSTEP && vipMusic) vipMusic.duck();
    if (enhanced && vip) return vip.playEvent(event, divisor, when);
    return playDivisor(divisor, holdSeconds);
}

// The frames of the current step that have not been played yet, and a counter
// that invalidates them wholesale.
//
// Both are needed. Clearing the timers stops a frame that has not fired;
// the counter stops one that fired between teardown starting and finishing.
const pendingFrames = new Set();
let soundGeneration = 0;

function cancelPendingSounds() {
    for (const id of pendingFrames) clearTimeout(id);
    pendingFrames.clear();
    soundGeneration++;
}

/**
 * Shuts the faithful speaker.
 *
 * It holds its gate open for the length of the frame it is playing, so without
 * this the last tone outlives whatever stopped it - across a presentation
 * switch, where it would then be heard over the VIP mix.
 */
function silenceSpeaker() {
    if (!gate || !audio) return;
    gate.gain.cancelScheduledValues(audio.currentTime);
    gate.gain.setValueAtTime(0, audio.currentTime);
}

/**
 * One step's worth of sound frames, oldest first.
 *
 * The driver runs on a 60 Hz interrupt and a step is one pass of the 10 Hz main
 * loop, so a step carries six frames rather than one. They are spread evenly
 * across the step: playing them all at the same instant would stack six
 * oscillator changes on one timestamp and only the last would be audible, which
 * is the bug this replaced by another name.
 *
 * Silent frames still take their slot, and they are still DELIVERED. A frame
 * with no divisor is the driver saying nothing is playing, and the VIP holding
 * tones need to hear that: their oscillator is started once and left running,
 * and silence is what tells it to stop. Dropping those frames left the alarm's
 * oscillator alive after the alarm had finished, through teardown and all.
 */
export function playSounds(events, divisors, stepSeconds) {
    if (muted || !audio || audio.state !== 'running') return;

    const n = divisors.length;
    if (!n) return;

    // Whatever the last step had left unplayed is dropped. Its frames are spread
    // across a whole step from the moment that call was made, and the next call
    // lands slightly sooner than that - so without this the tail of one batch
    // fires between the frames of the next and puts an older pitch back on the
    // speaker. The driver holds one tone at a time and the newest wins.
    cancelPendingSounds();

    const slot = stepSeconds / n;
    const generation = soundGeneration;

    // THE WHOLE STEP'S SPACING, DECIDED ONCE, ON THE AUDIO CLOCK.
    //
    // The frames are still dispatched by setTimeout - the faithful path drives
    // a square-wave gate and genuinely wants to be called - but each one now
    // carries the instant it is MEANT to sound at, fixed here from one reading
    // of currentTime.
    //
    // That is #36's point. Before this the spacing between the game's sounds was
    // whatever the main thread allowed: a long paint, a collection, or a busy
    // tab moved a frame, and under load the driver's own rhythm came apart. A
    // frame dispatched 8 ms late now still sounds on time.
    //
    // The engine's event order and divisors are untouched. This changes WHEN a
    // frame is scheduled for, never which frame it is.
    const base = audio.currentTime;

    for (let i = 0; i < n; i++) {
        const at = i * slot * 1000;
        const when = base + i * slot;
        const event = events[i], divisor = divisors[i];

        if (at === 0) { playSound(event, divisor, slot, when); continue; }

        // Held and tagged, so teardown can cancel them. Without this a frame
        // scheduled a moment before the component went away still fired, opened
        // the speaker gate or started a VIP effect, and left it ringing over
        // whatever came next.
        const id = setTimeout(() => {
            pendingFrames.delete(id);
            if (generation !== soundGeneration) return;
            playSound(event, divisor, slot, when);
        }, at);
        pendingFrames.add(id);
    }
}

// divisor is the PIT channel 2 value the game wrote. 0 means silence.
export function playDivisor(divisor, holdSeconds) {
    if (muted || !audio || audio.state !== 'running') return;
    if (!divisor) return;

    const hz = 1193180 / divisor;
    // Below about 20 Hz there is no pitch left, only a rattle, and the oscillator
    // would just click. The game's explosion and rock sounds sit down there on
    // purpose, so they are lifted into the range where a speaker cone actually
    // moves rather than being dropped.
    const audible = hz < 30 ? hz * 4 : hz;

    const now = audio.currentTime;
    osc.frequency.setValueAtTime(audible, now);
    // The level, not a constant. This is the one place the faithful speaker is
    // made audible, so it is the one place the player's choice has to land.
    const open = effectsGain();

    gate.gain.cancelScheduledValues(now);
    gate.gain.setValueAtTime(open, now);
    gate.gain.setValueAtTime(open, now + holdSeconds * 0.8);
    gate.gain.linearRampToValueAtTime(0, now + holdSeconds);
}

// ---- the theme -----------------------------------------------------------
//
// The tune is a tracker, and it is monophonic. A bank of 22 patterns of eight
// events, an order list saying which pattern comes next, and one square wave.
// There are no chords: the two extra lists in the data are for a Tandy's sound
// chip, and on any ordinary PC the game plays the third list on the speaker.
//
// A note byte of 0xFF does not mean rest. It means "nothing new here", and the
// gate counter from the last real note keeps running. When that counter reaches
// zero the speaker is gated off. A new note pre-empts it. That pre-emption is the
// whole articulation model and it is what gives the tune its rhythm.

let music = null;
let musicOsc = null;
let musicGate = null;
let musicTimer = null;

let vipMusicPlaying = false;

export function loadMusic(data) {
    music = data;
    if (vipMusic) vipMusic.load(data);
}

/**
 * Settles when the theme is ready to play without buffering.
 *
 * VIP streams a 2 MB track and has something to wait for. The faithful build
 * synthesises its theme from the pattern data loadMusic was handed, so there is
 * nothing to buffer and this is already true by the time it is asked.
 */
export async function whenMusicReady() {
    if (enhanced && vipMusic) return vipMusic.whenReady();
    return music !== null && music !== undefined;
}

export function stopMusic() {
    if (musicTimer) { clearInterval(musicTimer); musicTimer = null; }
    if (musicGate) musicGate.gain.setValueAtTime(0, audio.currentTime);
    if (musicOsc) { try { musicOsc.stop(); } catch { } musicOsc = null; }
    if (vipMusic) { vipMusic.stop(); vipMusicPlaying = false; }
}

/**
 * Chooses the VIP theme's cut: 'instrumental' or 'vocal'.
 *
 * Returns the cut in force afterwards, which is not always the one asked for -
 * before the VIP module is loaded there is nothing to switch, and the caller
 * needs to know that rather than assume.
 */
export function setMusicCut(which) {
    if (!vipMusic) return null;
    return vipMusic.setCut(which);
}

export function musicCut() {
    return vipMusic ? vipMusic.currentCut() : null;
}

/**
 * The cave-complete sting, which is what the original played at 0xEE0C.
 *
 * VIP only. The faithful presentation's equivalent is four bars of the 1988
 * coda, which is First Star's composition and not ours to ship - so in that mode
 * the moment stays silent until somebody decides otherwise.
 *
 * The theme's channel, not the effects', because a fanfare is music. Checked
 * here rather than inside the module, which is where every other sound in this
 * file checks it.
 */
export function playSting() {
    if (musicOff || !enhanced || !vipMusic) return false;
    if (!ensureAudio()) return false;
    wakeAudio();
    return vipMusic.playSting();
}

export function stopSting() {
    if (vipMusic) vipMusic.stopSting();
}

/** The whole track, for finishing a world. Held on screen until dismissed. */
export function playWorldSting() {
    if (musicOff || !enhanced || !vipMusic) return false;
    if (!ensureAudio()) return false;
    wakeAudio();
    return vipMusic.playWorldSting();
}

/**
 * Whether the world track is still going.
 *
 * Answers false with the music off or in the faithful presentation, because in
 * both cases nothing is playing and the interlude would otherwise wait for a
 * track that was never started.
 */
export function worldStingPlaying() {
    return vipMusic ? vipMusic.worldStingPlaying() : false;
}

export function fadeWorldSting() {
    if (vipMusic) vipMusic.fadeWorldSting();
}

/** Puts the theme back if the world track displaced it. */
export function resumeTheme() {
    if (vipMusic) vipMusic.resumeTheme();
}

/**
 * Stops everything that can still be making noise.
 *
 * Disposing the component's IJSObjectReference does not unload this module or
 * stop its media elements, so without this a looping theme survives the
 * component that started it.
 */
export function stopAllAudio() {
    // First, or a frame already queued starts a sound after everything below it
    // has been stopped.
    cancelPendingSounds();

    stopMusic();
    if (vipMusic) vipMusic.stopAll();
    if (vip) vip.stopAll();
    silenceSpeaker();

    unwatchOutside();
}

export function playMusic() {
    // The one gate every theme-start path goes through, including the automatic
    // one on first load. This module outlives the component that imported it, so
    // a page that is torn down and rebuilt comes back with the music preference
    // the player last set rather than starting the theme over the top of it.
    if (musicOff) return false;

    if (!ensureAudio()) return false;
    // The speaker path plays the 1987 pattern data and needs it. The VIP path
    // streams its own track and does not, and a VIP build does not ship the data.
    if (!(enhanced && vipMusic) && !music) return false;
    wakeAudio();
    stopMusic();

    // The theme follows the sound mode. The same 22 patterns either way - one
    // played as the PC speaker played it, one arranged over drums.
    if (enhanced && vipMusic) {
        vipMusicPlaying = vipMusic.play(
            () => { vipMusicPlaying = false; },

            // Refused after the fact - a suspended context, or a gesture the
            // browser decided had been lost. Put the arming back, so the next
            // press tries again rather than the theme being silent for good.
            () => { vipMusicPlaying = false; musicArmed = true; },
        );
        return vipMusicPlaying;
    }

    musicGate = audio.createGain();
    musicGate.gain.value = 0;
    musicGate.connect(audio.destination);

    musicOsc = audio.createOscillator();
    musicOsc.type = 'square';
    musicOsc.frequency.value = 440;
    musicOsc.connect(musicGate);
    musicOsc.start();

    let pos = 0;         // where we are in the order list
    let step = 0;        // 0..7 inside the current pattern
    let held = 0;        // the gate counter, in sub-steps

    const LEVEL = 0.045;

    musicTimer = setInterval(() => {
        // A SUSPENDED CONTEXT STILL GETS THIS TIMER. setInterval has nothing to
        // do with the AudioContext, so a long pause used to walk the whole
        // order list in silence and reach the end - and `stopMusic` tears down
        // the oscillator and this timer, which resuming the context cannot put
        // back. The theme was simply gone after a pause long enough to outlast
        // it. Holding the position is the whole fix. Astra, 19 September 2026.
        if (audio.state === 'suspended') return;

        if (pos >= music.order.length) { stopMusic(); return; }

        const now = audio.currentTime;
        const event = music.patterns[music.order[pos]][step];
        const note = event[0];
        const gate = event[1];

        if (note !== 0xFF) {
            musicOsc.frequency.setValueAtTime(1193180 / music.noteTable[note], now);
            musicGate.gain.setValueAtTime(LEVEL, now);
            held = gate;
        } else if (held > 0 && --held === 0) {
            musicGate.gain.setValueAtTime(0, now);
            held = 1;
        }

        if (++step === 8) { step = 0; pos++; }
    }, music.substepSeconds * 1000);

    return true;
}

// ---- dismissing the footer menus ------------------------------------------
//
// A panel that only closes by clicking its own button again is the last obvious
// thing missing from the footer: every other menu anyone has used closes when
// you click away from it or press Escape.
//
// pointerdown rather than click, so the panel is gone before the thing underneath
// it reacts. Capture phase, so a row inside the panel still gets its own click -
// the handler ignores anything inside a .menu and lets it through.

let outsideRef = null;

function onPointerDown(e) {
    if (!outsideRef) return;
    if (e.target.closest && e.target.closest('.menu')) return;
    outsideRef.invokeMethodAsync('CloseMenus');
}

function onKeyDown(e) {
    if (outsideRef && e.key === 'Escape') outsideRef.invokeMethodAsync('CloseMenus');
}

/**
 * Starts telling the component to close its menus on an outside press or Escape.
 *
 * Idempotent: called once per component, but a second call replaces the reference
 * rather than stacking a second pair of listeners on the document.
 */
export function watchOutside(dotNetRef) {
    outsideRef = dotNetRef;
    document.addEventListener('pointerdown', onPointerDown, true);
    document.addEventListener('keydown', onKeyDown, true);
    return true;
}

export function unwatchOutside() {
    outsideRef = null;
    document.removeEventListener('pointerdown', onPointerDown, true);
    document.removeEventListener('keydown', onKeyDown, true);
}
