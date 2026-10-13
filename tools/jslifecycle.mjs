// Checks that game.js gives back everything it takes.
//
//   node tools/jslifecycle.mjs
//
// There is no JavaScript test runner in this repository and the engine's xUnit
// suite cannot see any of this, so the resource-lifetime bugs in game.js had
// nothing watching them: a remount stacked a second set of window listeners on
// the first, an unretained ResizeObserver went on firing, and a looping theme
// outlived the component that started it.
//
// The module is imported against stub globals rather than a browser. That is
// enough for what is being asserted here, which is bookkeeping - who attached
// what, and whether it came off again - and not rendering or sound.

import { strict as assert } from 'node:assert';
import { pathToFileURL } from 'node:url';
import { readFileSync } from 'node:fs';

// ---- the stubs -----------------------------------------------------------

const listeners = [];

// The DOM identifies a registration by target, type, handler and the CAPTURE
// flag only - the other options are not part of the key. Adding an exact
// duplicate does nothing and removing something that is not there does nothing,
// and both of those matter here: watchOutside relies on the first to be
// idempotent, and unwatchOutside relies on the second to be safe when nothing
// was ever attached.
const capturing = options =>
    options === true || (typeof options === 'object' && options?.capture === true);

function makeTarget(name) {
    const find = (type, handler, options) => listeners.findIndex(l =>
        l.target === name && l.type === type && l.handler === handler
        && l.capture === capturing(options));

    return {
        name,
        addEventListener(type, handler, options) {
            if (find(type, handler, options) !== -1) return;
            listeners.push({ target: name, type, handler, capture: capturing(options) });
        },
        removeEventListener(type, handler, options) {
            const i = find(type, handler, options);
            if (i !== -1) listeners.splice(i, 1);
        },
        /** Calls whatever is listening, so a real gesture can be acted out. */
        fire(type, event) {
            for (const l of [...listeners])
                if (l.target === name && l.type === type)
                    l.handler({ preventDefault() { }, ...event });
        },
    };
}

let observers = 0;

class FakeResizeObserver {
    constructor() { this.connected = false; }
    observe() { this.connected = true; observers++; }
    disconnect() { if (this.connected) { this.connected = false; observers--; } }
}

// Images do not load by themselves here. Each one is parked so a check can
// decide WHEN it arrives - which is the whole point, because the bugs are about
// a load that lands after the component that asked for it has gone.
const pending = [];

class FakeImage {
    constructor() {
        this.naturalWidth = 320;
        this.naturalHeight = 200;
    }
    set src(value) { this._src = value; pending.push(this); }
    get src() { return this._src; }
}

/** Completes a parked image load, named so the order cannot be got wrong. */
function deliver(src) {
    const i = pending.findIndex(image => image.src === src);
    assert.notEqual(i, -1, `no load is pending for ${src}`);
    const [image] = pending.splice(i, 1);
    image.onload?.();
    return image;
}

// Enough of Web Audio for stopAllAudio to do its work and be seen doing it.
let audioStops = 0;
const pitches = [];

class FakeParam {
    constructor(value) { this.value = value; }
    setValueAtTime(v) { this.value = v; return this; }
    cancelScheduledValues() { return this; }
    cancelAndHoldAtTime() { return this; }
    linearRampToValueAtTime(v) { this.value = v; return this; }
    exponentialRampToValueAtTime(v) { this.value = v; return this; }
}

class FakeAudioContext {
    constructor() { this.state = 'running'; this.currentTime = 0; this.destination = {}; }
    createGain() { return { gain: new FakeParam(1), connect: n => n, disconnect() { } }; }
    createOscillator() {
        const frequency = new FakeParam(440);
        // Every pitch the speaker is asked for, in order. This is how the sound
        // frames are observed: the faithful path writes one per audible frame.
        frequency.setValueAtTime = v => { frequency.value = v; pitches.push(Math.round(v)); return frequency; };
        return {
            type: 'square', frequency,
            connect: n => n, start() { }, stop() { audioStops++; },
        };
    }
    // Both of these, and the state actually moves. The interruption policy
    // suspends the context to silence what is sounding without touching any
    // mute the player set, so a stub with no suspend cannot exercise it - and a
    // stub whose state never changes would let a double resume look fine.
    suspend() { this.state = 'suspended'; }
    resume() { this.state = 'running'; }
}

const draws = [];

// The box the canvas is given, mutable so the scaling check can drive fit() both
// ways. Everything else leaves it at 640 by 352, which is twice the faithful
// build's 320 by 176 field.
const box = { width: 640, height: 352 };

// The canvas is an event target as well as a drawing surface. It was not one
// here until a tap on the picture became Enter - which is what makes the game
// startable on a phone - and init() attaching to it failed against a stub that
// had no addEventListener. A real canvas has one; the stub has to as well, and
// through makeTarget so the listeners are counted with the rest.
const canvasEvents = makeTarget('canvas');

const canvas = {
    ...canvasEvents,
    parentElement: { getBoundingClientRect: () => ({ ...box }) },
    // surface() measures the shell to size the canvas. The existing checks never
    // got this far because they leave the sheet load parked; the title ones
    // deliver it, so the stub needs the whole of what init touches.
    closest: () => ({
        style: { setProperty() { } },
        getBoundingClientRect: () => ({ width: 640, height: 352 }),
    }),
    style: {},
    getContext: () => ({
        imageSmoothingEnabled: true,
        fillRect() { },
        // Recorded rather than ignored, so the title animation's four layers can
        // be counted and placed. A nine-argument call is a sub-rectangle blit and
        // a three-argument one is a whole picture.
        drawImage(...args) { draws.push(args); },
        clearRect() { },
        setTransform() { }, save() { }, restore() { },
        // The select screen outlines the highlighted world.
        strokeRect() { },
    }),
};

// The on-screen pad. Two round targets, 200 by 200, centred at (100,100) and
// (400,100) - so a point's offset from a centre is the direction it means.
const padBox = { left: 0, top: 0, width: 200, height: 200 };
const fireBox = { left: 300, top: 0, width: 200, height: 200 };

const makeControl = (name, box) => ({
    ...makeTarget(name),
    getBoundingClientRect: () => ({ ...box, right: box.left + box.width, bottom: box.top + box.height }),
    setPointerCapture() { },
    releasePointerCapture() { },
});

const padEl = makeControl('pad', padBox);
const fireEl = makeControl('fire', fireBox);

globalThis.window = makeTarget('window');
globalThis.window.innerWidth = 1280;
globalThis.window.devicePixelRatio = 1;
globalThis.window.innerHeight = 800;
globalThis.window.ResizeObserver = FakeResizeObserver;
globalThis.ResizeObserver = FakeResizeObserver;
globalThis.document = makeTarget('document');
globalThis.Image = FakeImage;
globalThis.AudioContext = FakeAudioContext;
globalThis.window.AudioContext = FakeAudioContext;

// ---- the checks ----------------------------------------------------------

const game = await import(
    pathToFileURL('web/Rockford.Web/wwwroot/game.js').href);

let failures = 0;

// A real keyboard event carries BOTH: `code` is the physical key and `key` is the
// character it produced. game.js reads the two for different purposes - held keys
// from `code` so a modifier cannot change them, taps from `key` so the menus can
// ask for Enter by name - so a stub that sends only one is not a keyboard.
const press = (key, code = key) => window.fire('keydown', { key, code });
const release = (key, code = key) => window.fire('keyup', { key, code });

async function check(name, fn) {
    try {
        // Awaited. Two of these used to return a promise into a synchronous
        // caller, so the assertion inside them was never observed and the check
        // passed whatever happened.
        await fn();
        console.log(`  ok    ${name}`);
    } catch (error) {
        failures++;
        console.log(`  FAIL  ${name}\n        ${error.message}`);
    }
}

console.log('game.js lifecycle');

game.init(canvas, 'sheet.png', 1);
const afterFirstInit = listeners.length;

await check('init attaches window listeners', () => {
    assert.ok(afterFirstInit > 0, 'nothing was attached at all');
    assert.equal(observers, 1, 'the canvas box is not being watched');
});

await check('a remount does not stack a second set', () => {
    game.init(canvas, 'sheet.png', 1);
    assert.equal(listeners.length, afterFirstInit,
        `${listeners.length} listeners after two inits, expected ${afterFirstInit}`);
    assert.equal(observers, 1, `${observers} observers after two inits, expected 1`);
});

await check('dispose gives everything back', () => {
    game.dispose();
    assert.equal(listeners.length, 0,
        `${listeners.length} listeners survived dispose: ` +
        listeners.map(l => `${l.target}.${l.type}`).join(', '));
    assert.equal(observers, 0, 'the ResizeObserver was not disconnected');
});

await check('dispose is safe to call twice', () => {
    game.dispose();
    assert.equal(listeners.length, 0);
});

await check('the document listeners come off too', () => {
    // watchOutside is what closes an open menu on a click elsewhere. It attaches
    // to the document rather than the window, and stopAllAudio - which dispose
    // calls - is what takes it off again.
    game.init(canvas, 'sheet.png', 1);
    game.watchOutside({ invokeMethodAsync: () => { } });
    assert.ok(listeners.some(l => l.target === 'document'),
        'watchOutside attached nothing');

    game.dispose();
    assert.equal(listeners.filter(l => l.target === 'document').length, 0,
        'a document listener survived dispose');
});

await check('production order leaves the outside handling attached', () => {
    // Home.razor's real sequence. It used to be watchOutside then init, and the
    // teardown inside init took both listeners straight back off again - so
    // clicking outside a menu stopped closing it on every single mount.
    game.dispose();
    game.init(canvas, 'sheet.png', 1);
    game.watchOutside({ invokeMethodAsync: () => { } });

    // Named rather than counted. A count fails the moment anything else attaches
    // to the document for its own reasons - which visibilitychange did, for
    // releasing a held finger when a phone is backgrounded - and a test that
    // breaks on an unrelated addition is a test that gets its number bumped
    // without being read.
    const onDocument = listeners.filter(l => l.target === 'document').map(l => l.type);
    assert.ok(onDocument.includes('pointerdown'),
        `the outside-press handler is not attached: ${onDocument.join(', ')}`);
    assert.ok(onDocument.includes('keydown'),
        `the Escape handler is not attached: ${onDocument.join(', ')}`);
});

await check('dispose silences the audio', () => {
    // The previous version of this file caught the loss of stopAllAudio only
    // sideways, through unwatchOutside - so replacing the call with
    // unwatchOutside alone passed every check while removing all audio teardown.
    game.dispose();
    game.init(canvas, 'sheet.png', 1);
    game.unlock();

    // The faithful theme is a tracker and playMusic answers false without data.
    // One pattern of one note is enough to get an oscillator running.
    game.loadMusic({
        order: [0],
        patterns: [[[0, 4], [0xFF, 0], [0xFF, 0], [0xFF, 0],
                    [0xFF, 0], [0xFF, 0], [0xFF, 0], [0xFF, 0]]],
        noteTable: [0x1000],
        substepSeconds: 0.02,
    });
    assert.ok(game.playMusic(), 'the theme did not start, so nothing is under test');

    const before = audioStops;
    game.dispose();

    assert.ok(audioStops > before,
        'nothing was stopped, so the audio teardown is not happening');
});

await check('a sheet that arrives after disposal is dropped', async () => {
    // The load cannot be cancelled, so it has to be ignored. Without the
    // generation guard the stale sheet replaced the NEXT component's, which
    // shows up as a cave drawn in the previous world's tiles.
    game.dispose();
    pending.length = 0;
    game.init(canvas, 'sheet.png', 1);

    const stale = game.setSheet('old-world.png', '#f00', 1);
    game.dispose();
    deliver('old-world.png');

    assert.equal(await stale, false, 'a stale sheet was applied');
});

await check('a live sheet is still applied', async () => {
    // The other half, so the guard cannot pass by rejecting everything.
    game.dispose();
    pending.length = 0;
    game.init(canvas, 'sheet.png', 1);

    const live = game.setSheet('new-world.png', '#00f', 8);
    deliver('new-world.png');

    assert.equal(await live, true, 'a live sheet was dropped');
});

await check('a picture that arrives after disposal paints nothing', async () => {
    game.dispose();
    pending.length = 0;
    game.init(canvas, 'sheet.png', 1);

    const shown = game.showPicture('old-title.png');

    // Disposed AND remounted, which is the case that matters. Disposal alone
    // leaves ctx null and the existing guard catches it; a remount hands the
    // module a fresh ctx, and a stale picture then paints over the new mount.
    game.dispose();
    game.init(canvas, 'sheet.png', 1);
    deliver('old-title.png');

    assert.equal(await shown, false, 'a stale picture painted over the new mount');
});

await check('a hundred remounts leak nothing', () => {
    game.dispose();
    for (let i = 0; i < 100; i++) game.init(canvas, 'sheet.png', 1);
    assert.equal(listeners.length, afterFirstInit,
        `${listeners.length} listeners after 100 inits, expected ${afterFirstInit}`);
    assert.equal(observers, 1, `${observers} observers after 100 inits`);
    game.dispose();
});

// ---- the sound frames ----------------------------------------------------
//
// A step carries six driver frames and playSounds spreads them across it. The
// engine's xUnit suite proves the ENGINE produces them correctly; nothing proved
// the front end hands them over once, in order, and drops the stale ones.

const sleep = ms => new Promise(r => setTimeout(r, ms));

/** Sets up a running audio path and clears the pitch log. */
function sounding() {
    game.dispose();
    game.init(canvas, 'sheet.png', 1);
    game.unlock();
    game.setMuted(false);
    pitches.length = 0;
}

await check('a step hands over every frame, in order', async () => {
    sounding();

    // Six frames, six distinct divisors, oldest first.
    game.playSounds([4, 4, 4, 4, 4, 4], [0x100, 0x200, 0x400, 0x800, 0x1000, 0x2000], 0.1);
    await sleep(160);

    assert.deepEqual(pitches, [4661, 2330, 1165, 583, 291, 146],
        `got ${pitches.join(', ')}`);
});

await check('a silent frame takes its slot without speaking', async () => {
    sounding();

    game.playSounds([0, 4, 0, 4, 0, 0], [0, 0x200, 0, 0x800, 0, 0], 0.1);
    await sleep(160);

    assert.deepEqual(pitches, [2330, 583], `got ${pitches.join(', ')}`);
});

await check('a new step drops what the last one had not played', async () => {
    // The steps are 100 ms apart and a batch is spread across 100 ms, so the tail
    // of one lands among the head of the next. Without the cancel, an older pitch
    // goes back on the speaker after a newer one.
    sounding();

    game.playSounds([4, 4, 4, 4, 4, 4], [0x100, 0x100, 0x100, 0x100, 0x100, 0x100], 0.1);
    await sleep(40);
    game.playSounds([4, 4, 4, 4, 4, 4], [0x200, 0x200, 0x200, 0x200, 0x200, 0x200], 0.1);
    await sleep(160);

    // Everything after the switch is the newer pitch. The older one must not
    // reappear once the newer has started.
    const last = pitches.lastIndexOf(4661);
    const first = pitches.indexOf(2330);
    assert.ok(first !== -1, 'the second batch never played');
    assert.ok(last < first,
        `an older pitch played after a newer one: ${pitches.join(', ')}`);
});

await check('muting hands over nothing at all', async () => {
    sounding();
    game.setMuted(true);

    game.playSounds([4, 4, 4, 4, 4, 4], [0x100, 0x200, 0x400, 0x800, 0x1000, 0x2000], 0.1);
    await sleep(160);

    assert.deepEqual(pitches, [], `muted, but ${pitches.join(', ')} played`);
    game.setMuted(false);
});

// ---- the title animation -------------------------------------------------
//
// The title screen is not a still: Rockford blinks, taps a foot and tosses a
// diamond. The counters are TitleAnimation.cs and the rectangles come out of the
// executable; what is checked here is that game.js draws the layers at all, puts
// them where the layout says, and lets go of them when the screen changes.

const LAYOUT = {
    arm:     { at: [144, 98], w: 64, h: 39, frames: [[208, 113], [128, 129], [128, 86], [128, 43], [128, 0]] },
    diamond: { at: [144, 56], w: 24, h: 42, frames: [[0, 136], [32, 136], [64, 136], [96, 136], [0, 136], [0, 136], [0, 136], [0, 136], [0, 136]] },
    face:    { at: [176, 66], w: 48, h: 23, frames: [[64, 0], [64, 26], [64, 52], [64, 80], [64, 108], [64, 0], [64, 0], [64, 0], [64, 0]] },
    faceAlt: { at: [176, 66], w: 48, h: 23, frames: [[0, 108], [0, 80], [0, 52], [0, 26], [0, 0]] },
    foot:    { at: [160, 156], w: 32, h: 24, frames: [[256, 0], [256, 27], [256, 54], [256, 81]] },
};

const blits = () => draws.filter(d => d.length === 9);

// Image loads are parked, so a title has to be delivered rather than awaited -
// and each check uses its own urls, because load() caches by url and a second
// check would find the first one's picture already there.
let titles = 0;

async function armTitle({ frames = true } = {}) {
    const n = titles++;
    const base = `title-${n}.png`;
    const sheet = frames ? `titleani-${n}.png` : null;

    game.init(canvas, `sheet-${n}.png`, 1);
    deliver(`sheet-${n}.png`);

    // showTitle awaits the base before it asks for the sheet, so the second load
    // is not parked until the first has resolved. Deliver, yield, deliver.
    const shown = game.showTitle(base, sheet, frames ? LAYOUT : null);
    deliver(base);
    if (sheet) { await sleep(0); deliver(sheet); }

    assert.equal(await shown, true, 'the title did not come up');
    draws.length = 0;
}

await check('a frame draws the base picture and four layers over it', async () => {
    await armTitle();

    assert.equal(game.paintTitleFrame(2, 3, 1, false, 2), true);

    // One whole-picture draw for the base, then four sub-rectangles.
    assert.equal(draws.filter(d => d.length === 5).length, 1,
                 'the base picture was not redrawn');
    assert.equal(blits().length, 4, `expected four layers, got ${blits().length}`);
});

await check('each layer lands where the layout puts it', async () => {
    await armTitle();
    game.paintTitleFrame(0, 0, 0, false, 0);

    const placed = blits().map(d => [d[5], d[6]]);
    assert.deepEqual(placed, [[144, 98], [144, 56], [176, 66], [160, 156]],
                     `layers landed at ${JSON.stringify(placed)}`);
});

await check('a layer clamps the counter to its own frame count', async () => {
    // The arm has five frames and the counter runs to eight, the way 0xEF74 has
    // it. Without the clamp here a nine-frame counter reads past a five-entry
    // table and draws undefined.
    await armTitle();
    game.paintTitleFrame(8, 8, 8, false, 8);

    assert.deepEqual(blits()[0].slice(1, 3), LAYOUT.arm.frames[4],
                     'the arm did not clamp to its last frame');
    assert.deepEqual(blits()[3].slice(1, 3), LAYOUT.foot.frames[3],
                     'the foot did not clamp to its last frame');
});

await check('a build whose layout has one layer draws only that one', async () => {
    // VIP's title needs the toss and nothing else - its render already has the
    // arm in the right pose, and it has no face or foot frames. A layout with one
    // entry has to draw one layer rather than throwing on the missing three.
    const n = titles++;
    game.init(canvas, `sheet-${n}.png`, 1);
    deliver(`sheet-${n}.png`);

    const one = { diamond: LAYOUT.diamond };
    const shown = game.showTitle(`title-${n}.png`, `titleani-${n}.png`, one);
    deliver(`title-${n}.png`);
    await sleep(0);
    deliver(`titleani-${n}.png`);
    assert.equal(await shown, true);

    draws.length = 0;
    assert.equal(game.paintTitleFrame(3, 3, 3, false, 3), true);
    assert.equal(blits().length, 1, `expected one layer, got ${blits().length}`);
    assert.deepEqual(blits()[0].slice(5, 7), LAYOUT.diamond.at);
});

await check('the second set of faces is a different rectangle', async () => {
    await armTitle();
    game.paintTitleFrame(0, 0, 0, false, 0);
    const plain = blits()[2].slice(1, 3);

    draws.length = 0;
    game.paintTitleFrame(0, 0, 0, true, 0);
    const alt = blits()[2].slice(1, 3);

    assert.notDeepEqual(plain, alt, 'both faces came from the same place');
});

await check('a build with no frames shows the still and refuses to animate', async () => {
    // VIP has a title and no frames cut from it. It must not throw, and it must
    // not draw layers from a sheet it does not have.
    await armTitle({ frames: false });

    assert.equal(game.paintTitleFrame(2, 3, 1, false, 2), false);
    assert.equal(blits().length, 0, 'it drew layers without a sheet');
});

await check('leaving the title stops it being repainted', async () => {
    // repaint is the only thing a resize can call, so a title left armed would
    // come back over the select screen the moment the window changed size.
    await armTitle();

    const menu = game.showMenu('menu.png', 0);
    deliver('menu.png');
    await menu;

    draws.length = 0;
    assert.equal(game.paintTitleFrame(2, 3, 1, false, 2), false);
    assert.equal(blits().length, 0, 'the title drew over the select screen');
});

// ---- the theme starting ---------------------------------------------------
//
// Browsers will not let audio start before the page has been interacted with, so
// "as soon as the title shows" is not a thing any of this can do. What it can do
// is take the FIRST gesture rather than the tick after it, and not start a track
// that has not finished buffering.

await check('the theme does not start before the assets are in', async () => {
    game.dispose();
    game.init(canvas, 'sheet-music-a.png', 1);
    deliver('sheet-music-a.png');
    game.loadMusic({ order: [0], patterns: [[]], tempo: 1, notes: [440] });

    // A gesture with nothing armed opens the audio and plays nothing.
    press('Enter');

    assert.equal(game.musicPlaying(), false, 'the theme started before it was armed');
});

await check('the first gesture starts the theme, not the tick after it', async () => {
    game.dispose();
    game.init(canvas, 'sheet-music-b.png', 1);
    deliver('sheet-music-b.png');
    game.loadMusic({ order: [0], patterns: [[]], tempo: 1, notes: [440] });

    assert.equal(await game.whenMusicReady(), true, 'the pattern data should need no buffering');
    assert.equal(game.armMusic(), true);

    // No step runs between these two lines. That is the point.
    press('Enter');

    assert.equal(game.musicPlaying(), true, 'the gesture did not start the theme');
});

await check('a second gesture does not start it over', async () => {
    game.dispose();
    game.init(canvas, 'sheet-music-c.png', 1);
    deliver('sheet-music-c.png');
    game.loadMusic({ order: [0], patterns: [[]], tempo: 1, notes: [440] });
    game.armMusic();

    press('Enter');
    const first = game.musicPlaying();
    press('x', 'KeyX');
    window.fire('pointerdown', {});

    assert.equal(first, true);
    assert.equal(game.musicPlaying(), true, 'the theme was restarted by a later press');
});

await check('arming a theme that is already playing does nothing', async () => {
    // A remount can come back with the music still going - the audio graph
    // deliberately outlives the component. Arming then would hand the next
    // keypress a playMusic that stops and restarts the track under the player.
    game.dispose();
    game.init(canvas, 'sheet-music-e.png', 1);
    deliver('sheet-music-e.png');
    game.loadMusic({ order: [0], patterns: [[]], tempo: 1, notes: [440] });

    game.armMusic();
    press('Enter');
    assert.equal(game.musicPlaying(), true, 'the theme should be going by now');

    assert.equal(game.armMusic(), false, 'it armed over a theme that was already playing');
});

await check('a muted theme stays muted through a gesture', async () => {
    // setMusicEnabled(false) is the player saying no. Arming must not talk them
    // out of it on their next keypress.
    game.dispose();
    game.init(canvas, 'sheet-music-d.png', 1);
    deliver('sheet-music-d.png');
    game.loadMusic({ order: [0], patterns: [[]], tempo: 1, notes: [440] });
    game.setMusicEnabled(false);
    game.armMusic();

    press('Enter');

    assert.equal(game.musicPlaying(), false, 'a muted theme came back on a keypress');
    game.setMusicEnabled(true);
});

await check('a refused start does not spend the arming', async () => {
    // noteGesture cleared the flag and THEN called playMusic. playMusic refuses
    // for several ordinary reasons - the track is not in yet, the player has
    // music off, the browser would not resume - and the one arming was spent on
    // a theme that never started. No later gesture tried again and the session
    // was silent from then on. Astra, 19 September 2026.
    game.dispose();
    game.init(canvas, 'sheet-music-f.png', 1);
    deliver('sheet-music-f.png');
    game.loadMusic({ order: [0], patterns: [[]], tempo: 1, notes: [440] });

    // Armed while the answer to playMusic is no. Which refusal it is does not
    // matter - this one is reachable from the harness.
    game.setMusicEnabled(false);
    assert.equal(game.armMusic(), true);
    press('Enter');
    assert.equal(game.musicPlaying(), false, 'a refused theme played anyway');

    // The refusal lifts, and the next gesture must still start the theme.
    // setMusicEnabled(true) only clears the flag; it starts nothing itself.
    game.setMusicEnabled(true);
    press('x', 'KeyX');

    assert.equal(game.musicPlaying(), true, 'the arming was spent on the refusal');
});

await check('a theme refused after the fact is armed again', () => {
    // The VIP path is worse than a synchronous refusal: music-vip.js returns
    // true and the media play() promise rejects afterwards, so the caller
    // records a theme that is not playing and never retries. Read rather than
    // driven - the harness has no media element, and what matters is that the
    // refusal has somewhere to report to.
    const mod = readFileSync('web/Rockford.Web/wwwroot/music-vip.js', 'utf8');
    assert.match(mod, /export function play\(onFinished, onRefused\)/,
        'music-vip.js cannot tell the caller the browser refused');
    assert.match(mod, /if \(onRefused\) onRefused\(\)/,
        'the rejection path does not call it');

    const source = readFileSync('web/Rockford.Web/wwwroot/game.js', 'utf8');
    assert.match(source, /vipMusicPlaying = false; musicArmed = true;/,
        'a refused VIP theme does not put the arming back');
});

await check('the element is told which way the picture is going, not only the context', async () => {
    // fit() decides smoothing from whether the picture is being magnified or
    // minified. It was only telling the 2D context, which governs drawImage
    // INSIDE the canvas - the browser then scales the backing store to the CSS
    // size afterwards, and app.css set `image-rendering: pixelated` on every
    // canvas with nothing overriding it. So VIP's 2560-wide field came down to
    // the window by dropping pixels, which is the one thing redrawing 315 tiles
    // at 8x was meant to avoid.
    game.dispose();
    game.init(canvas, 'sheet-fit.png', 1);

    // 640 against a 320 surface is 2x. Magnifying, so the original's pixels stay
    // honest and the element must say so.
    assert.equal(canvas.style.imageRendering, 'pixelated',
        `magnified picture rendered as ${canvas.style.imageRendering}`);

    // Smaller than the surface is VIP's case. Minifying, so it must smooth.
    box.width = 160;
    box.height = 88;
    window.fire('resize');
    assert.equal(canvas.style.imageRendering, 'auto',
        `minified picture rendered as ${canvas.style.imageRendering}`);

    box.width = 640;
    box.height = 352;
    window.fire('resize');
    assert.equal(canvas.style.imageRendering, 'pixelated',
        'it did not go back when the picture was magnified again');
});

// ---- input -------------------------------------------------------------
//
// readInput returns the original's own encoding: horizontal in the low nibble,
// 4 for left and 8 for right; vertical in the next, 1 for up and 2 for down;
// and 256 for fire.

const LEFT = 4, RIGHT = 8, UP = 1 << 4, DOWN = 2 << 4, FIRE = 256;

await check('WASD still steers while fire is held', () => {
    game.dispose();
    game.init(canvas, 'sheet-input.png', 1);

    // What the title screen tells the player to do: a direction and Shift. With
    // Shift down a browser reports the letter key as 'A', so reading e.key threw
    // the direction away and Rockford fired on the spot without turning.
    press('Shift', 'ShiftLeft');
    press('A', 'KeyA');
    assert.equal(game.readInput(), LEFT | FIRE,
        'left plus fire did not come through on WASD');

    window.fire('blur');

    // And the same on the arrows, which never had the problem, so that the fix
    // is not swapping one broken half for the other.
    press('Shift', 'ShiftLeft');
    press('ArrowUp');
    assert.equal(game.readInput(), UP | FIRE,
        'up plus fire did not come through on the arrows');

    window.fire('blur');
});

await check('a direction does not stick when Shift arrives mid-hold', () => {
    game.dispose();
    game.init(canvas, 'sheet-input.png', 1);

    // Hold the key, then reach for fire, then let both go. The keyup for the
    // letter arrives as 'A' because Shift is still down, so keying the held set
    // on e.key deleted something that had never been added and left Rockford
    // walking left until the window lost focus.
    press('a', 'KeyA');
    assert.equal(game.readInput(), LEFT, 'left was not held');

    press('Shift', 'ShiftLeft');
    release('A', 'KeyA');
    release('Shift', 'ShiftLeft');

    assert.equal(game.readInput(), 0,
        'a direction survived its own keyup and is stuck on');
});

await check('either Shift fires, and so does space', () => {
    game.dispose();
    game.init(canvas, 'sheet-input.png', 1);

    press('Shift', 'ShiftRight');
    assert.equal(game.readInput(), FIRE, 'the right Shift does not fire');
    window.fire('blur');

    press(' ', 'Space');
    assert.equal(game.readInput(), FIRE, 'space does not fire');
    window.fire('blur');

    press('s', 'KeyS');
    assert.equal(game.readInput(), DOWN, 'S is not down');
    window.fire('blur');

    press('d', 'KeyD');
    assert.equal(game.readInput(), RIGHT, 'D is not right');
    window.fire('blur');
});

await check('the menus still get their taps by name', () => {
    // `tapped` stays on e.key, because Home.razor asks for "Enter", " ",
    // "ArrowLeft" and "ArrowRight" by those names and none of them change under
    // a modifier. Switching the held set to e.code must not have moved this too.
    game.dispose();
    game.init(canvas, 'sheet-input.png', 1);
    game.clearTaps();

    press('ArrowLeft');
    assert.equal(game.takeTap('ArrowLeft'), true, 'the arrow tap was lost');
    assert.equal(game.takeTap('ArrowLeft'), false, 'the tap was not consumed');

    press('Enter');
    assert.equal(game.takeTap('Enter'), true, 'the Enter tap was lost');
    window.fire('blur');
});

// ---- the field and the camera ------------------------------------------
//
// draw() is handed the whole 40 by 22 cave and the cell the 20 by 11 window sits
// on, and slides between one origin and the next. There is no rAF in Node, so
// the loop never starts here; a paint is forced by firing a resize, which is the
// one thing that reaches `repaint` from outside.

const CAVE_COLS = 40, CAVE_ROWS = 22, SIDE = 16;
const CAMERA_BLEND = 60;

// game.js reads performance.now() through a helper on every call, so replacing
// this drives its clock. Without it the blend is whatever the check took to run.
let clock = 0;
globalThis.performance = { now: () => clock };

// One callback at a time, which is all the loop ever has outstanding. Nothing
// runs by itself: a check advances the clock and then drains, so the number of
// frames the loop asks for is observable rather than a matter of timing.
let queued = null;
let handles = 0;
globalThis.requestAnimationFrame = fn => { queued = fn; return ++handles; };
globalThis.cancelAnimationFrame = () => { queued = null; };

/** Runs whatever the loop asked for, up to a cap, and says how many ran. */
function runFrames(limit = 20) {
    let ran = 0;
    while (queued && ran < limit) {
        const fn = queued;
        queued = null;
        clock += 16;   // one frame at 60 Hz
        fn();
        ran++;
    }
    return ran;
}

/** A cave with one sprite in it, at cave column/row, taken from sheet (sx, sy). */
function caveWith(column, row, sx = 32, sy = 48) {
    const tiles = new Int32Array(CAVE_COLS * CAVE_ROWS * 2).fill(-1);
    const i = (row * CAVE_COLS + column) * 2;
    tiles[i] = sx;
    tiles[i + 1] = sy;
    return tiles;
}

/** Forces one paint and returns the sprite blits it made. */
function paintOnce() {
    draws.length = 0;
    window.fire('resize');
    return draws.filter(d => d.length === 9);
}

await check('the window is cut out of the whole cave at the origin', () => {
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);   // VIP: blending on
    deliver('sheet-field.png');

    // Column 5, row 3 of the cave, with the window at the cave's top-left.
    game.draw(caveWith(5, 3), 0, 0);

    const [blit] = paintOnce();
    assert.ok(blit, 'nothing was drawn');
    assert.equal(blit[5], 5 * SIDE, 'wrong column on screen');
    assert.equal(blit[6], 3 * SIDE, 'wrong row on screen');

    // The same cell with the window moved three columns right is three columns
    // further left on screen. Sending the cave rather than the window is what
    // makes that the renderer's arithmetic rather than a second payload.
    game.draw(caveWith(5, 3), 3, 0);
    clock += CAMERA_BLEND;

    const [moved] = paintOnce();
    assert.equal(moved[5], 2 * SIDE, 'the origin was not applied');
});

await check('a cell outside the window is not drawn', () => {
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);   // VIP: blending on
    deliver('sheet-field.png');

    // Column 30 is well past the 20-wide window standing at column 0. It is in
    // the payload - the whole cave always is - and must not reach the canvas.
    game.draw(caveWith(30, 3), 0, 0);
    assert.equal(paintOnce().length, 0, 'a cell outside the window was drawn');
});

await check('the camera slides between origins instead of jumping a whole tile', () => {
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);   // VIP: blending on
    deliver('sheet-field.png');

    game.draw(caveWith(5, 3), 0, 0);
    clock += 1000;

    // One cell right. The view should be where it was, not where it is going.
    game.draw(caveWith(5, 3), 1, 0);
    const [start] = paintOnce();
    assert.equal(start[5], 5 * SIDE, 'the camera jumped the whole tile at once');

    // Half way through the blend it is half a tile across, which is the thing
    // that could not happen while the origin only ever arrived in whole cells.
    clock += CAMERA_BLEND / 2;
    const [middle] = paintOnce();
    assert.equal(middle[5], 5 * SIDE - SIDE / 2,
        `half way through the blend the sprite was at ${middle[5]}`);

    // And it lands on the cell rather than easing towards it for ever.
    clock += CAMERA_BLEND / 2;
    const [end] = paintOnce();
    assert.equal(end[5], 4 * SIDE, 'the camera did not arrive');

    clock += 1000;
    const [after] = paintOnce();
    assert.equal(after[5], 4 * SIDE, 'the camera drifted past its own target');
});

await check('a jump of more than a cell snaps rather than sailing across the cave', () => {
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);   // VIP: blending on
    deliver('sheet-field.png');

    game.draw(caveWith(12, 3), 0, 0);
    clock += 1000;

    // A new cave, a respawn, or a tab coming back from a stall. Sliding twenty
    // columns while the player is already playing at the far end is worse than
    // not sliding at all.
    game.draw(caveWith(12, 3), 10, 0);
    const [blit] = paintOnce();
    assert.equal(blit[5], 2 * SIDE, 'the camera slid across the cave instead of snapping');
});

await check('the paint loop runs while the view moves and stops when it settles', () => {
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);   // VIP: blending on
    deliver('sheet-field.png');

    // A snapshot that does not move the view asks for no frames at all. Objects
    // do not move between snapshots, so the picture would be identical.
    game.draw(caveWith(5, 3), 4, 0);
    assert.equal(runFrames(), 0, 'the loop ran with nothing moving');

    // One that does move it runs for the length of the blend and then stops. Sixty
    // milliseconds at sixteen a frame is a handful, not sixty a second for ever -
    // which on VIP would be a 2560 by 1408 clear and 264 blits each time.
    game.draw(caveWith(5, 3), 5, 0);
    const ran = runFrames();
    assert.ok(ran > 1 && ran <= 6, `the loop ran ${ran} frames for a 60 ms blend`);

    assert.equal(runFrames(), 0, 'the loop did not stop once the view had arrived');
});

const MOVE_BLEND = 50;

await check('a reported move slides the sprite back from where it came', () => {
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);   // VIP: blending on
    deliver('sheet-field.png');

    // A settling snapshot first. The very first draw of a cave is a jump by
    // definition - there is no previous view to have come from - and a jump drops
    // its moves, which is correct and is checked below.
    game.draw(caveWith(5, 3), 0, 0, [], true);
    clock += 1000;

    // The sprite is at (5, 4) now and the engine says it came from (5, 3). The
    // renderer must draw it at its NEW cell pulled back towards the old one -
    // never at the old one moving forward, and never past the new one.
    game.draw(caveWith(5, 4), 0, 0, [5, 3, 5, 4]);

    const [start] = paintOnce();
    assert.equal(start[6], 3 * SIDE,
        `at the instant the step lands it should be a whole tile back, was ${start[6]}`);

    clock += MOVE_BLEND / 2;
    const [middle] = paintOnce();
    assert.equal(middle[6], 4 * SIDE - SIDE / 2, 'half way through it should be half a tile back');

    clock += MOVE_BLEND / 2;
    const [end] = paintOnce();
    assert.equal(end[6], 4 * SIDE, 'it did not arrive at the cell the simulation put it in');

    clock += 1000;
    const [after] = paintOnce();
    assert.equal(after[6], 4 * SIDE, 'it drifted past the cell it had arrived at');
});

await check('a sprite the engine did not report moving does not slide', () => {
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);   // VIP: blending on
    deliver('sheet-field.png');

    // This is the boulder's one-step commit before a drop. The tile CHANGES from
    // resting to falling without going anywhere, the engine reports nothing, and
    // inventing the motion from the tile change would start the fall a whole step
    // early - which is exactly the rule the port went to trouble to get right.
    game.draw(caveWith(5, 4), 0, 0, []);

    const [blit] = paintOnce();
    assert.equal(blit[6], 4 * SIDE, 'something slid that was never reported as moving');
});

await check('a move is dropped when the view jumps, not replayed against a new cave', () => {
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);   // VIP: blending on
    deliver('sheet-field.png');

    game.draw(caveWith(5, 4), 0, 0, []);
    clock += 1000;

    // A new cave or a respawn. The grid is entirely different, so a move reported
    // against the old one would slide a sprite in from a cell that means nothing.
    game.draw(caveWith(15, 4), 10, 0, [15, 3, 15, 4]);

    const [blit] = paintOnce();
    assert.equal(blit[5], 5 * SIDE, 'the camera did not snap');
    assert.equal(blit[6], 4 * SIDE, 'a move survived a jump and slid against the new cave');
});

await check('the loop keeps painting for a move even when the view does not scroll', () => {
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);   // VIP: blending on
    deliver('sheet-field.png');

    game.draw(caveWith(5, 4), 4, 0, []);
    assert.equal(runFrames(), 0, 'the loop ran with nothing moving at all');

    // Same origin, so the camera has nothing to do. The boulder still does.
    game.draw(caveWith(5, 5), 4, 0, [5, 4, 5, 5]);
    const ran = runFrames();
    assert.ok(ran > 1 && ran <= 6, `the loop ran ${ran} frames for a 50 ms blend`);

    assert.equal(runFrames(), 0, 'the loop did not stop once the move had arrived');
});

await check('the faithful build does not slide anything', () => {
    // The faithful presentation is the reference VIP is judged against. Its
    // picture has to stay on the simulation's own grid and its own clock, or the
    // comparison is being made against something that has already been smoothed.
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, false);   // faithful
    deliver('sheet-field.png');

    game.draw(caveWith(5, 3), 0, 0, []);
    clock += 1000;

    // A move AND a scroll, both of which VIP would slide.
    game.draw(caveWith(5, 4), 1, 0, [5, 3, 5, 4]);

    const [blit] = paintOnce();
    assert.equal(blit[5], 4 * SIDE, 'the faithful build slid the camera');
    assert.equal(blit[6], 4 * SIDE, 'the faithful build slid an object');

    // And it must not spin an animation loop it has no use for.
    assert.equal(runFrames(), 0, 'the faithful build started a paint loop');
});

await check('a fresh cave snaps even when it frames a cell from the last one', () => {
    // The camera-distance test cannot see this: a respawn or a new cave can
    // legitimately open within one cell of where the last one ended, and the view
    // would then slide across a grid that had been replaced underneath it.
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);
    deliver('sheet-field.png');

    game.draw(caveWith(5, 3), 0, 0, []);
    clock += 1000;

    // One cell of scroll, one reported move - and the host saying it is a new cave.
    game.draw(caveWith(5, 4), 1, 0, [5, 3, 5, 4], true);

    const [blit] = paintOnce();
    assert.equal(blit[5], 4 * SIDE, 'the camera slid into a new cave');
    assert.equal(blit[6], 4 * SIDE, 'a move slid against a new cave');
});

await check('a move whose destination is off the window is still drawn as it arrives', () => {
    // The painter walks a surrounding row and column, so a sprite that has moved
    // into the margin is drawn while it is still pulled back into view rather than
    // blinking at the edge.
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);
    deliver('sheet-field.png');

    game.draw(caveWith(19, 3), 0, 0, []);
    clock += 1000;

    // Column 20 is one past the 20-wide window standing at column 0.
    game.draw(caveWith(20, 3), 0, 0, [19, 3, 20, 3]);

    const [blit] = paintOnce();
    assert.ok(blit, 'the sprite vanished the instant it left the window');
    assert.equal(blit[5], 19 * SIDE, 'it was not drawn where it came from');
});

await check('a cell that is both a source and a destination keeps both sprites', () => {
    // A chain of falling boulders: one lands in the cell the one below it just
    // left. Neither may erase the other - there is one floor fill and then
    // destination-keyed drawing, with no per-source clear.
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);
    deliver('sheet-field.png');

    const two = new Int32Array(CAVE_COLS * CAVE_ROWS * 2).fill(-1);
    for (const [column, row] of [[5, 4], [5, 5]]) {
        const i = (row * CAVE_COLS + column) * 2;
        two[i] = 32; two[i + 1] = 48;
    }

    game.draw(two, 0, 0, []);
    clock += 1000;

    // (5,3)->(5,4) and (5,4)->(5,5). The middle cell is both.
    game.draw(two, 0, 0, [5, 3, 5, 4, 5, 4, 5, 5]);

    const blits = paintOnce();
    assert.equal(blits.length, 2, `expected both sprites, drew ${blits.length}`);
    assert.deepEqual(blits.map(b => b[6]).sort((a, b) => a - b), [3 * SIDE, 4 * SIDE],
        'the two sprites did not both slide from their own source');
});

await check('a picture stops the field being painted over it', async () => {
    game.dispose();
    game.init(canvas, 'sheet-field.png', 1, true);   // VIP: blending on
    deliver('sheet-field.png');
    game.draw(caveWith(5, 3), 0, 0);

    const picture = game.showPicture('interlude.png');
    deliver('interlude.png');
    await picture;

    // A resize now repaints the picture. If the field survived, cells would be
    // drawn over the top of it - which is what the rAF loop would be doing on
    // every frame of the title and select screens.
    assert.equal(paintOnce().length, 0, 'the field was painted over a picture');
});

// ---- the touch pad -------------------------------------------------------
//
// The thing that makes the game playable without a keyboard. readInput's
// encoding is the original's: 4 left, 8 right, 1 up and 2 down in the next
// nibble, 256 fire.

/** A press at a point, on whichever control owns it. */
const touch = (el, x, y, id = 1) =>
    el.fire('pointerdown', { pointerId: id, clientX: x, clientY: y });
const slide = (el, x, y, id = 1) =>
    el.fire('pointermove', { pointerId: id, clientX: x, clientY: y });
const lift = (el, id = 1) => el.fire('pointerup', { pointerId: id });

await check('the pad steers by where the thumb is', () => {
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    assert.equal(game.attachPad(padEl, fireEl), true, 'the pad did not attach');

    touch(padEl, 20, 100);                      // hard left of centre
    assert.equal(game.readInput(), LEFT, 'left was not read');
    lift(padEl);
    assert.equal(game.readInput(), 0, 'the direction survived the lift');

    touch(padEl, 180, 100);
    assert.equal(game.readInput(), RIGHT, 'right was not read');
    lift(padEl);

    touch(padEl, 100, 20);
    assert.equal(game.readInput(), UP, 'up was not read');
    lift(padEl);

    touch(padEl, 100, 180);
    assert.equal(game.readInput(), DOWN, 'down was not read');
    lift(padEl);
});

await check('a thumb resting in the middle is not a direction', () => {
    // A thumb is wide and a stationary hand must not twitch the player.
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    game.attachPad(padEl, fireEl);

    touch(padEl, 100, 100);
    assert.equal(game.readInput(), 0, 'the dead zone is not dead');
    lift(padEl);
});

await check('sliding between directions without lifting', () => {
    // The reason this is one pad and not four buttons.
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    game.attachPad(padEl, fireEl);

    touch(padEl, 20, 100);
    assert.equal(game.readInput(), LEFT);

    slide(padEl, 180, 100);
    assert.equal(game.readInput(), RIGHT, 'the slide did not change direction');

    slide(padEl, 180, 20);
    assert.equal(game.readInput(), RIGHT | UP, 'a diagonal was not read');

    lift(padEl);
});

await check('a direction and fire are two fingers, not one', () => {
    // Firing IS a direction plus fire - it is the whole mechanic - so the two
    // have to be independent pointers rather than one overwriting the other.
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    game.attachPad(padEl, fireEl);

    touch(padEl, 20, 100, 1);
    touch(fireEl, 400, 100, 2);
    assert.equal(game.readInput(), LEFT | FIRE, 'holding both did not read as both');

    // Lifting one must not lift the other.
    lift(fireEl, 2);
    assert.equal(game.readInput(), LEFT, 'letting go of fire dropped the direction');

    lift(padEl, 1);
    assert.equal(game.readInput(), 0);
});

await check('the keyboard and the pad are the same input', () => {
    // A tablet with a keyboard is both. Whichever is touched last should work,
    // with no mode to be in.
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    game.attachPad(padEl, fireEl);

    press('ArrowUp');
    touch(fireEl, 400, 100, 2);
    assert.equal(game.readInput(), UP | FIRE, 'a key and the pad did not combine');

    release('ArrowUp');
    lift(fireEl, 2);
});

await check('a finger held when the page goes away does not stay held', () => {
    // pointerup never arrives in that case, so the direction would be stuck on
    // return - the same reason the keys are cleared on blur.
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    game.attachPad(padEl, fireEl);

    touch(padEl, 20, 100);
    assert.equal(game.readInput(), LEFT);

    window.fire('blur');
    assert.equal(game.readInput(), 0, 'the pad was still held after blur');
});

await check('a tap on the picture is Enter, but only outside a cave', () => {
    // Every screen that asks to be pressed is the canvas, so this is what lets a
    // phone get past the title at all. During play it must NOT be a keystroke,
    // or a hand resting on the screen plays the game.
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    game.clearTaps();

    game.tapToStart(false);
    canvas.fire('pointerdown', { pointerId: 1, clientX: 10, clientY: 10 });
    assert.equal(game.takeTap('Enter'), false, 'a tap during play counted as Enter');

    game.tapToStart(true);
    canvas.fire('pointerdown', { pointerId: 1, clientX: 10, clientY: 10 });
    assert.equal(game.takeTap('Enter'), true, 'a tap outside a cave was not Enter');
});

await check('the pad offers the menus a tap as well as a hold', () => {
    // The world select reads taps rather than held keys, so left and right on
    // the pad have to give it one.
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    game.attachPad(padEl, fireEl);
    game.clearTaps();

    touch(padEl, 20, 100);
    assert.equal(game.takeTap('ArrowLeft'), true, 'the pad gave the menu no tap');
    lift(padEl);

    touch(padEl, 180, 100);
    assert.equal(game.takeTap('ArrowRight'), true);
    lift(padEl);
});

await check('a second finger on one control does not release the first', () => {
    // A phone collects extra fingers by accident - the heel of a hand, a second
    // thumb. Every pointer used to share one direction and one fire flag, so
    // lifting either of two fingers on FIRE turned fire off while the other was
    // still pressed.
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    game.attachPad(padEl, fireEl);

    touch(fireEl, 400, 100, 1);
    touch(fireEl, 420, 120, 2);
    assert.equal(game.readInput(), FIRE);

    lift(fireEl, 1);
    assert.equal(game.readInput(), FIRE, 'lifting one finger released the other');

    lift(fireEl, 2);
    assert.equal(game.readInput(), 0);

    // And the same on the pad.
    touch(padEl, 20, 100, 3);
    touch(padEl, 180, 100, 4);
    assert.equal(game.readInput(), RIGHT, 'the second thumb should be the one steering');

    lift(padEl, 4);
    assert.equal(game.readInput(), LEFT, 'lifting the second thumb lost the first');
    lift(padEl, 3);
});

await check('a finger the element never released is released by the window', () => {
    // setPointerCapture can throw, and where it does the element never sees the
    // pointerup that follows - the finger is held for ever. The window listens
    // as a net.
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    game.attachPad(padEl, fireEl);

    touch(padEl, 20, 100, 7);
    assert.equal(game.readInput(), LEFT);

    window.fire('pointerup', { pointerId: 7 });
    assert.equal(game.readInput(), 0, 'the window did not catch the release');
});

await check('backgrounding the page lets go of everything', () => {
    // blur is not reliable for this on a phone: switching apps, locking the
    // screen or pulling down a shade hides the page, and iOS does not promise a
    // blur for it.
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    game.attachPad(padEl, fireEl);

    touch(padEl, 20, 100, 1);
    touch(fireEl, 400, 100, 2);
    assert.equal(game.readInput(), LEFT | FIRE);

    globalThis.document.hidden = true;
    document.fire('visibilitychange');
    assert.equal(game.readInput(), 0, 'a hidden page kept the fingers held');
    globalThis.document.hidden = false;

    // And pagehide, for the back/forward cache.
    touch(padEl, 180, 100, 3);
    assert.equal(game.readInput(), RIGHT);
    window.fire('pagehide');
    assert.equal(game.readInput(), 0, 'pagehide kept the finger held');
});

await check('sliding out of the middle chooses a world', () => {
    // "Slide the pad to choose a world" is what the touch hint says. Taps used
    // to be offered on pointerdown alone, so a thumb landing in the dead centre
    // and sliding left gave the menu nothing at all.
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    game.attachPad(padEl, fireEl);
    game.clearTaps();

    touch(padEl, 100, 100);                  // dead centre - no direction
    assert.equal(game.takeTap('ArrowLeft'), false);

    slide(padEl, 20, 100);                   // out to the left
    assert.equal(game.takeTap('ArrowLeft'), true, 'the slide chose nothing');

    // And it is one choice per turn, not one per move event.
    slide(padEl, 25, 100);
    assert.equal(game.takeTap('ArrowLeft'), false, 'sliding on re-chose');

    slide(padEl, 180, 100);
    assert.equal(game.takeTap('ArrowRight'), true, 'turning the other way chose nothing');
    lift(padEl);
});

await check('attaching nothing is refused rather than throwing', () => {
    game.dispose();
    game.init(canvas, 'sheet-pad.png', 1);
    assert.equal(game.attachPad(null, null), false);
});

// ---- the interruption policy ----------------------------------------------

await check('hiding the page latches an interruption', () => {
    // There was no policy at all: game.js dropped the held keys on blur and the
    // cave went on advancing. A hidden tab still gets timers - throttled to
    // whatever the browser allows - so the simulation crawls on in the
    // background and the ordinary result is coming back to a dead player.
    game.dispose();
    game.init(canvas, 'sheet-pause.png', 1);
    deliver('sheet-pause.png');

    assert.equal(game.wasInterrupted(), false, 'it started interrupted');

    globalThis.document.hidden = true;
    document.fire('visibilitychange');
    globalThis.document.hidden = false;

    assert.equal(game.wasInterrupted(), true, 'hiding the page did not latch');
});

await check('coming back is not consent', () => {
    // Becoming visible again must NOT clear it. A player who switched away
    // mid-cave is not ready the instant they switch back, and the window they
    // would be robbed of is exactly the one where a boulder is already falling.
    game.dispose();
    game.init(canvas, 'sheet-pause-b.png', 1);
    deliver('sheet-pause-b.png');

    globalThis.document.hidden = true;
    document.fire('visibilitychange');
    globalThis.document.hidden = false;
    document.fire('visibilitychange');

    assert.equal(game.wasInterrupted(), true, 'becoming visible resumed the game by itself');

    game.resume();
    assert.equal(game.wasInterrupted(), false, 'an explicit resume did not clear it');
});

await check('pagehide latches it too', () => {
    // blur is not reliable on a phone and neither is visibilitychange alone:
    // pagehide is what covers the back/forward cache and the app switcher.
    game.dispose();
    game.init(canvas, 'sheet-pause-c.png', 1);
    deliver('sheet-pause-c.png');

    window.fire('pagehide');
    assert.equal(game.wasInterrupted(), true, 'pagehide did not latch');
    game.resume();
});

await check('an interruption still drops the held keys', () => {
    // The old behaviour has to survive the new one. A key held when the page
    // goes away must not still be held on return, or the player resumes into a
    // direction they are not pressing.
    game.dispose();
    game.init(canvas, 'sheet-pause-d.png', 1);
    deliver('sheet-pause-d.png');

    press('ArrowLeft');
    assert.notEqual(game.readInput(), 0, 'the key was not registered at all');

    globalThis.document.hidden = true;
    document.fire('visibilitychange');
    globalThis.document.hidden = false;

    assert.equal(game.readInput(), 0, 'a hidden page kept the key held');
    game.resume();
});

await check('an interruption silences the audio and a resume restores it', () => {
    // The first version of these checks asserted the latch and the input and
    // never once looked at the audio, which is half of what the policy
    // promises. Astra, 19 September 2026.
    game.dispose();
    game.init(canvas, 'sheet-pause-e.png', 1);
    deliver('sheet-pause-e.png');
    game.loadMusic({ order: [0], patterns: [[]], tempo: 1, notes: [440] });
    game.armMusic();
    press('Enter');
    assert.equal(game.musicPlaying(), true, 'nothing was sounding to silence');

    window.fire('pagehide');
    assert.equal(game.audioState(), 'suspended', 'the audio kept running while away');

    game.resume();
    assert.equal(game.audioState(), 'running', 'a resume did not wake the audio');
});

await check('sound cannot come back before the player does', () => {
    // Hide mid-cave, come back, and toggle the music off and on while the
    // Resume button is still up. Every path that wanted sound called
    // audio.resume() directly and none of them knew about the pause, so the
    // cave stayed held and the sound returned - the one combination the policy
    // exists to prevent.
    game.dispose();
    game.init(canvas, 'sheet-pause-f.png', 1);
    deliver('sheet-pause-f.png');
    game.loadMusic({ order: [0], patterns: [[]], tempo: 1, notes: [440] });
    game.armMusic();
    press('Enter');

    window.fire('pagehide');
    assert.equal(game.audioState(), 'suspended', 'it did not silence in the first place');

    // The player fiddling with the controls while paused.
    game.setMusicEnabled(false);
    game.setMusicEnabled(true);
    game.playMusic();
    game.unlock();

    assert.equal(game.audioState(), 'suspended', 'the sound came back before the player did');

    game.resume();
    assert.equal(game.audioState(), 'running', 'and then it would not come back at all');
});

// ---- how big the backing store is ----------------------------------------

await check('the backing store is sized for the screen, not for the art', () => {
    // VIP's surface is 2560 by 1408 and used to be the canvas outright - three
    // and a half million pixels cleared and composited for a picture occupying
    // a fraction of that.
    game.dispose();
    game.init(canvas, 'sheet-res.png', 8, true);   // VIP: an 8x atlas
    deliver('sheet-res.png');

    box.width = 640; box.height = 352;
    game.draw(caveWith(5, 3), 0, 0, []);

    // The surface is 20 by 11 tiles of 16 at 8x.
    assert.equal(canvas.width < 2560, true,
        `the backing store is still the full ${canvas.width}`);

    // Shown at 640 on a 1x device, so that is what it should be near.
    assert.equal(canvas.width, 640, `wanted 640 backing pixels, got ${canvas.width}`);
    assert.equal(canvas.style.width, '640px', 'the displayed size changed');
});

await check('a denser screen gets more pixels, up to a cap', () => {
    game.dispose();
    game.init(canvas, 'sheet-res.png', 8, true);
    deliver('sheet-res.png');
    box.width = 640; box.height = 352;

    // A resize, not another draw: surface() returns early when the dimensions
    // have not changed, so fit() - which is what reads the ratio - would not run
    // and the check would be reading the previous answer.
    game.draw(caveWith(5, 3), 0, 0, []);

    window.devicePixelRatio = 2;
    window.fire('resize');
    assert.equal(canvas.width, 1280, 'a 2x screen did not get 2x pixels');

    // Capped: a phone reporting 3 or 4 would otherwise ask for a backing store
    // several times the size of the picture anybody can see.
    window.devicePixelRatio = 4;
    window.fire('resize');
    assert.equal(canvas.width, 1280, 'the device pixel ratio was not capped');

    window.devicePixelRatio = 1;
});

await check('it never asks for more pixels than the art has', () => {
    // The faithful build's surface is 320 wide. Showing it at 1280 must not
    // make a 1280-wide backing store: there is no source above 320, so the
    // extra pixels would be upscaling its own art inside the canvas.
    game.dispose();
    game.init(canvas, 'sheet-res.png', 1, false);
    deliver('sheet-res.png');

    box.width = 1280; box.height = 704;
    game.draw(caveWith(5, 3), 0, 0, []);

    assert.equal(canvas.width, 320, `wanted the surface's own 320, got ${canvas.width}`);
    assert.equal(canvas.style.width, '1280px', 'the picture is not filling the box');

    box.width = 640; box.height = 352;
});

await check('the backing store keeps the surface shape exactly', () => {
    // An awkward box, which is the ordinary case - a window is not a multiple
    // of the cave. Rounding the two axes independently gave 1244 by 684, and
    // 1244:684 is not 20:11: the browser stretches that to the CSS box, and
    // every tile starts a fraction of a pixel in. It also made the release
    // smoke test - which recognises a cave by its shape - fail to see one.
    game.dispose();
    game.init(canvas, 'sheet-res.png', 8, true);
    deliver('sheet-res.png');

    // A draw, because surface() is what puts the VIP surface in force. An init
    // alone leaves whatever the previous check set.
    box.width = 640; box.height = 352;
    game.draw(caveWith(5, 3), 0, 0, []);
    assert.equal(canvas.width, 640, 'the faithful surface is still in force - it caps at 320');

    for (const width of [1244, 1000, 813, 640, 377, 101]) {
        box.width = width; box.height = Math.round(width * 11 / 20);
        window.fire('resize');
        assert.equal(canvas.width * 11, canvas.height * 20,
            `${width} gave ${canvas.width} by ${canvas.height}, which is not 20:11`);
    }

    box.width = 640; box.height = 352;
});

await check('the backing store never exceeds what the screen can show', () => {
    // A cap that does not cap. Rounding to the NEAREST whole step overshoots
    // the budget it exists to respect: a 377 by 208 box shows the field at 377
    // by 207 and asked for 380 by 209 - more pixels than the screen has, in
    // both directions at once. Astra found it; the sum has to floor, and it has
    // to floor against both axes because either can be the binding one.
    game.dispose();
    game.init(canvas, 'sheet-res.png', 8, true);
    deliver('sheet-res.png');

    // A draw, not just an init. surface() is what makes this VIP's 2560 by
    // 1408, and until it runs the surface is whatever the previous check left -
    // the faithful 320, where the source cap hides the overshoot and this says
    // ok on a build that has it. Found by reverting the fix and watching it
    // pass anyway.
    box.width = 640; box.height = 352;
    game.draw(caveWith(5, 3), 0, 0, []);
    assert.equal(canvas.width, 640, 'the faithful surface is still in force - it caps at 320');

    for (const dpr of [1, 2]) {
        window.devicePixelRatio = dpr;

        for (const [w, h] of [[377, 208], [1244, 700], [640, 352], [999, 401], [813, 448]]) {
            box.width = w; box.height = h;
            window.fire('resize');

            const shownW = parseInt(canvas.style.width, 10);
            const shownH = parseInt(canvas.style.height, 10);
            assert.equal(canvas.width <= shownW * dpr, true,
                `${w}x${h} at ${dpr}x: ${canvas.width} backing pixels for ${shownW} shown`);
            assert.equal(canvas.height <= shownH * dpr, true,
                `${w}x${h} at ${dpr}x: ${canvas.height} backing pixels for ${shownH} shown`);
        }
    }

    window.devicePixelRatio = 1;
    box.width = 640; box.height = 352;
});

await check('the picture still lands in the right place at any backing size', () => {
    // Everything draws in SURFACE units and a transform does the rest, so the
    // arithmetic in paintField must not have to know any of this.
    game.dispose();
    game.init(canvas, 'sheet-res.png', 1, true);
    deliver('sheet-res.png');
    box.width = 320; box.height = 176;      // 1:1, so the numbers are readable

    game.draw(caveWith(5, 3), 0, 0, []);
    const [blit] = paintOnce();
    assert.equal(blit[5], 5 * SIDE, 'wrong column');
    assert.equal(blit[6], 3 * SIDE, 'wrong row');

    box.width = 640; box.height = 352;
});

await check('the picture cache is bounded', async () => {
    // Five VIP atlases are about 82 MB of RGBA once decoded, and every image
    // ever loaded used to stay for the life of the page.
    //
    // The cache is private, so this is measured by what a LOAD does: a cached
    // picture resolves without parking an image, and an evicted one parks a
    // fresh one. That is the whole observable difference and it is the one that
    // matters for memory.
    game.dispose();
    game.init(canvas, 'sheet-cache-init.png', 1);
    deliver('sheet-cache-init.png');

    const parked = () => pending.length;

    // init loads its sheet directly rather than through the cache, so the cache
    // has to be primed through the path that uses it.
    const first = game.showPicture('sheet-cache-first.png');
    deliver('sheet-cache-first.png');
    await first;

    // Re-showing it while it is still cached parks nothing.
    const before = parked();
    const again = game.showPicture('sheet-cache-first.png');
    assert.equal(parked(), before, 'a cached picture was fetched again');
    await again;

    // Push more distinct pictures through than the cache holds.
    for (let i = 0; i < 8; i++) {
        const url = `sheet-cache-${i}.png`;
        const showing = game.showPicture(url);
        deliver(url);
        await showing;
    }

    // The first one is long gone, so asking for it parks a new load.
    const evicted = parked();
    const third = game.showPicture('sheet-cache-first.png');
    assert.equal(parked(), evicted + 1, 'an evicted picture was served from the cache');
    deliver('sheet-cache-first.png');
    await third;
});

// ---- the gamepad ---------------------------------------------------------
//
// Polled rather than pushed, so the stub is a function that hands back whatever
// the check wants the pad to be doing.

/** A standard-layout pad. Axes default to centred and no button pressed. */
function stubPad({ axes = [0, 0, 0, 0], pressed = [] } = {}) {
    return {
        connected: true,
        mapping: 'standard',
        axes,
        buttons: Array.from({ length: 16 }, (_, i) => ({ pressed: pressed.includes(i), value: 0 })),
    };
}

const noPads = () => [];
let padsNow = noPads;

// Node has its own navigator and it is getter-only, so a plain assignment
// throws. defineProperty replaces it outright, which is what a browser stub
// wants anyway.
const setNavigator = value =>
    Object.defineProperty(globalThis, 'navigator',
        { value, configurable: true, writable: true });

setNavigator({ getGamepads: () => padsNow() });

await check('the D-pad steers', () => {
    game.dispose();
    game.init(canvas, 'sheet-gp.png', 1);

    padsNow = () => [stubPad({ pressed: [14] })];      // left
    assert.equal(game.readInput(), LEFT);

    padsNow = () => [stubPad({ pressed: [15] })];      // right
    assert.equal(game.readInput(), RIGHT);

    padsNow = () => [stubPad({ pressed: [12] })];      // up
    assert.equal(game.readInput(), UP);

    padsNow = () => [stubPad({ pressed: [13] })];      // down
    assert.equal(game.readInput(), DOWN);

    padsNow = noPads;
    assert.equal(game.readInput(), 0);
});

await check('the stick steers, past a dead zone', () => {
    game.dispose();
    game.init(canvas, 'sheet-gp.png', 1);

    // A worn stick resting off centre must not walk the player into a wall.
    padsNow = () => [stubPad({ axes: [0.2, -0.2, 0, 0] })];
    assert.equal(game.readInput(), 0, 'the dead zone is not dead');

    padsNow = () => [stubPad({ axes: [-0.9, 0, 0, 0] })];
    assert.equal(game.readInput(), LEFT);

    // Negative is UP in the standard mapping, which is the easiest thing here
    // to get backwards.
    padsNow = () => [stubPad({ axes: [0, -0.9, 0, 0] })];
    assert.equal(game.readInput(), UP, 'the vertical axis is inverted');

    padsNow = () => [stubPad({ axes: [0, 0.9, 0, 0] })];
    assert.equal(game.readInput(), DOWN);

    padsNow = noPads;
});

await check('a direction and fire together, which is the whole mechanic', () => {
    game.dispose();
    game.init(canvas, 'sheet-gp.png', 1);

    padsNow = () => [stubPad({ axes: [-0.9, 0, 0, 0], pressed: [0] })];
    assert.equal(game.readInput(), LEFT | FIRE);

    // Any face button or shoulder, because which one is "the button" is a matter
    // of platform habit and this game has one action.
    for (const button of [1, 2, 3, 4, 5, 6, 7]) {
        padsNow = () => [stubPad({ pressed: [button] })];
        assert.equal(game.readInput(), FIRE, `button ${button} did not fire`);
    }

    padsNow = noPads;
});

await check('the D-pad wins over a thumb resting on the stick', () => {
    // Resting a thumb on the stick must not fight the pad the player is using.
    game.dispose();
    game.init(canvas, 'sheet-gp.png', 1);

    padsNow = () => [stubPad({ axes: [0.9, 0, 0, 0], pressed: [14] })];
    assert.equal(game.readInput(), LEFT, 'the stick overrode the D-pad');

    padsNow = noPads;
});

await check('the pad and the keyboard are the same input', () => {
    game.dispose();
    game.init(canvas, 'sheet-gp.png', 1);

    press('ArrowUp');
    padsNow = () => [stubPad({ pressed: [0] })];
    assert.equal(game.readInput(), UP | FIRE, 'a key and a pad did not combine');

    release('ArrowUp');
    padsNow = noPads;
});

await check('the menus get one tap per push, not one per poll', () => {
    // The world select reads takeTap. Holding the stick left for a tenth of a
    // second would otherwise scroll through every world there is.
    game.dispose();
    game.init(canvas, 'sheet-gp.png', 1);
    game.clearTaps();

    // One neutral poll first, which is what a real page does: the loop is
    // running before the player touches anything. Until that poll the pad's
    // state is unknown, and an unknown state is adopted rather than treated as
    // a push - see lastStick.
    game.readInput();

    padsNow = () => [stubPad({ pressed: [14] })];
    game.readInput();
    assert.equal(game.takeTap('ArrowLeft'), true, 'the pad gave the menu no tap');

    game.readInput();
    game.readInput();
    assert.equal(game.takeTap('ArrowLeft'), false, 'holding it re-chose');

    // Let go and push again - that is a new choice.
    padsNow = noPads;
    game.readInput();
    padsNow = () => [stubPad({ pressed: [14] })];
    game.readInput();
    assert.equal(game.takeTap('ArrowLeft'), true, 'a second push chose nothing');

    padsNow = noPads;
    game.readInput();
});

await check('a face button confirms a screen', () => {
    // Every screen that asks to be pressed wants Enter, and a pad has no Enter.
    game.dispose();
    game.init(canvas, 'sheet-gp.png', 1);
    game.clearTaps();

    game.tapToStart(true);          // outside a cave, which is when it confirms
    game.readInput();               // the neutral poll a real page makes first

    padsNow = () => [stubPad({ pressed: [0] })];
    game.readInput();
    assert.equal(game.takeTap('Enter'), true, 'the pad could not confirm');

    game.readInput();
    assert.equal(game.takeTap('Enter'), false, 'holding it confirmed twice');

    padsNow = noPads;
    game.readInput();
});

await check('a stick still pushed when the page comes back is not a fresh press', () => {
    // THIS CHECK ASSERTED THE OPPOSITE AND PASSED. Its title said the press must
    // not count and its assertion required that it did, which is worse than no
    // check at all: a green suite endorsing the defect. Astra, 19 September 2026.
    //
    // The pad is polled, so nothing is held across a hide. Claiming the stick
    // was CENTRED, which is what resetting to neutral says, makes a stick still
    // over look like a new push. The memory is forgotten instead, and the next
    // poll adopts what it finds without emitting an edge for it.
    game.dispose();
    game.init(canvas, 'sheet-gp.png', 1);
    game.clearTaps();

    game.readInput();               // neutral, so the pad's state is known
    padsNow = () => [stubPad({ pressed: [14] })];
    game.readInput();
    assert.equal(game.takeTap('ArrowLeft'), true, 'the first push chose nothing');

    globalThis.document.hidden = true;
    document.fire('visibilitychange');
    globalThis.document.hidden = false;

    // Still pushed, never released. This is not a new choice.
    game.readInput();
    assert.equal(game.takeTap('ArrowLeft'), false,
        'coming back still holding the stick chose a world');

    // And letting go and pushing again IS one.
    padsNow = noPads;
    game.readInput();
    padsNow = () => [stubPad({ pressed: [14] })];
    game.readInput();
    assert.equal(game.takeTap('ArrowLeft'), true, 'a genuine new push chose nothing');

    padsNow = noPads;
    game.readInput();
});

await check('firing in a cave does not queue a confirmation', () => {
    // Fire is the most-pressed button in the game. Queuing an Enter for every
    // shot left one in the tap set, and "Choose world" then consumed it and
    // started a world without the player confirming anything.
    game.dispose();
    game.init(canvas, 'sheet-gp.png', 1);
    game.clearTaps();

    game.tapToStart(false);                 // in a cave
    game.readInput();                       // neutral first
    padsNow = () => [stubPad({ pressed: [0] })];
    game.readInput();
    assert.equal(game.takeTap('Enter'), false, 'a shot queued a confirmation');

    // Outside a cave it still confirms, which is what it is for.
    padsNow = noPads;
    game.readInput();
    game.tapToStart(true);
    padsNow = () => [stubPad({ pressed: [0] })];
    game.readInput();
    assert.equal(game.takeTap('Enter'), true, 'the pad could not confirm a screen');

    padsNow = noPads;
    game.readInput();
});

await check('a pad laid out some other way is not read as if it were standard', () => {
    // Every index and sign here is the standard mapping. Applying them to a pad
    // reporting something else does not degrade to no input, it produces the
    // WRONG input - a trigger read as left. Not working beats working wrongly.
    game.dispose();
    game.init(canvas, 'sheet-gp.png', 1);

    // An empty mapping means the browser does not know the layout. Refused,
    // which costs a pad that might have been fine - see the note in readGamepad
    // for why that is the right way round for now.
    padsNow = () => [{ ...stubPad({ pressed: [14] }), mapping: '' }];
    assert.equal(game.readInput(), 0, 'a pad of unknown layout was read as standard');

    padsNow = () => [{ ...stubPad({ pressed: [14] }), mapping: 'xr-standard' }];
    assert.equal(game.readInput(), 0, 'an XR device was read as a gamepad');

    padsNow = () => [{ ...stubPad({ pressed: [14] }), mapping: 'standard' }];
    assert.equal(game.readInput(), LEFT, 'a standard pad was refused');

    padsNow = noPads;
    game.readInput();
});

await check('no gamepad support at all is not an error', () => {
    game.dispose();
    game.init(canvas, 'sheet-gp.png', 1);

    setNavigator({});
    assert.equal(game.readInput(), 0, 'a browser without the API threw or misread');

    setNavigator({ getGamepads: () => { throw new Error('nope'); } });
    assert.equal(game.readInput(), 0, 'a throwing getGamepads was not caught');

    setNavigator({ getGamepads: () => [null, undefined] });
    assert.equal(game.readInput(), 0, 'an empty slot was not skipped');

    setNavigator({ getGamepads: () => padsNow() });
});

// The stub pad is module-level, so it has to be put back before anything that
// reads input for another reason. It was left connected, and every keyboard
// check after this point was reading a pad holding left.
padsNow = noPads;
game.dispose();

// ---- what the keys are bound to, and who gets them ------------------------

await check('a key aimed at a control belongs to the control', () => {
    // These listen on the window, so the arrows and space were being taken from
    // whatever had focus and preventDefault'd on top: tabbing to the Game menu
    // and pressing Down moved Rockford and left the menu alone.
    game.dispose();
    game.init(canvas, 'sheet-scope.png', 1);

    const button = { closest: sel => (sel.includes('button') ? button : null) };
    window.fire('keydown', { key: 'ArrowLeft', code: 'ArrowLeft', target: button });
    assert.equal(game.readInput(), 0, 'a key meant for a button reached the game');

    // And the page background is not a control, because clicking the background
    // and carrying on playing has to keep working.
    const body = { closest: () => null };
    window.fire('keydown', { key: 'ArrowLeft', code: 'ArrowLeft', target: body });
    assert.equal(game.readInput(), LEFT, 'a key on the background was refused');
    window.fire('keyup', { key: 'ArrowLeft', code: 'ArrowLeft', target: body });
});

await check('the canvas is the game, not a control', () => {
    // It carries tabindex and is focusable, so a naive "is it focusable" test
    // would have refused every key pressed while the game had focus.
    game.dispose();
    game.init(canvas, 'sheet-scope.png', 1);

    window.fire('keydown', { key: 'ArrowUp', code: 'ArrowUp', target: canvas });
    assert.equal(game.readInput(), UP, 'a key on the canvas was refused');
    window.fire('keyup', { key: 'ArrowUp', code: 'ArrowUp', target: canvas });
});

await check('a key released over a control is still released', () => {
    // Focus can move between a key going down and coming up - pressing a button
    // with space, or tabbing away mid-hold. Refusing the release because the
    // target changed would hold the key for ever.
    game.dispose();
    game.init(canvas, 'sheet-scope.png', 1);

    const body = { closest: () => null };
    const button = { closest: sel => (sel.includes('button') ? button : null) };

    window.fire('keydown', { key: 'ArrowLeft', code: 'ArrowLeft', target: body });
    assert.equal(game.readInput(), LEFT);

    window.fire('keyup', { key: 'ArrowLeft', code: 'ArrowLeft', target: button });
    assert.equal(game.readInput(), 0, 'the key was stuck because focus had moved');
});

await check('the bindings can be changed', () => {
    game.dispose();
    game.init(canvas, 'sheet-scope.png', 1);

    game.setBindings({ left: ['KeyJ'], fire: ['KeyF'] });

    press('j', 'KeyJ');
    press('f', 'KeyF');
    assert.equal(game.readInput(), LEFT | FIRE, 'the new bindings were not read');

    // What was not rebound keeps its default.
    release('j', 'KeyJ');
    release('f', 'KeyF');
    press('ArrowUp');
    assert.equal(game.readInput(), UP, 'an untouched action lost its default');
    release('ArrowUp');

    // And the old key for a rebound action is no longer that action.
    press('a', 'KeyA');
    assert.equal(game.readInput(), 0, 'the old binding still works');
    release('a', 'KeyA');

    game.setBindings(null);
});

await check('a binding that would make the game unplayable is refused', () => {
    // A stored binding that made a direction impossible would be a game that
    // cannot be played and cannot be fixed from inside itself.
    game.dispose();
    game.init(canvas, 'sheet-scope.png', 1);

    for (const bad of [{ left: [] }, { left: 'KeyJ' }, { left: [7] }, { left: [''] }]) {
        game.setBindings(bad);
        assert.deepEqual(game.getBindings().left, game.DEFAULT_BINDINGS.left,
            `${JSON.stringify(bad)} was accepted`);
    }

    game.setBindings({ nonsense: ['KeyJ'] });
    assert.equal('nonsense' in game.getBindings(), false, 'an unknown action was stored');

    game.setBindings(null);
    assert.deepEqual(game.getBindings(), { ...game.DEFAULT_BINDINGS },
        'null did not reset to the shipped set');
});

await check('a code no keyboard produces is refused', () => {
    // "NotARealCode" was accepted, which binds the action to nothing and makes
    // that direction unpressable - with no way to press anything that would undo
    // it. The shape is checked rather than a list of every code in the spec,
    // because the list goes stale and an unfamiliar keyboard should still bind.
    game.dispose();
    game.init(canvas, 'sheet-bind.png', 1);

    game.setBindings({ left: ['NotARealCode'] });
    assert.deepEqual(game.getBindings().left, game.DEFAULT_BINDINGS.left);

    // And something real but unusual is accepted.
    game.setBindings({ left: ['Numpad4'] });
    assert.deepEqual(game.getBindings().left, ['Numpad4']);

    game.setBindings(null);
});

await check('two actions cannot share a key', () => {
    // readInput resolves left before right, so binding both to one key makes
    // right impossible - and unfixable from inside the game.
    game.dispose();
    game.init(canvas, 'sheet-bind.png', 1);

    game.setBindings({ left: ['KeyJ'], right: ['KeyJ'] });
    assert.deepEqual(game.getBindings().left, ['KeyJ'], 'the first claim should hold');
    assert.deepEqual(game.getBindings().right, game.DEFAULT_BINDINGS.right,
        'the colliding action should fall back');

    // And a rebind that takes a key off another action's DEFAULT leaves that
    // action with what is left rather than a key it no longer owns.
    game.setBindings({ fire: ['KeyA'] });
    assert.equal(game.getBindings().left.includes('KeyA'), false,
        'left kept a key that fire had taken');
    assert.equal(game.getBindings().left.includes('ArrowLeft'), true,
        'left lost the key it still had');

    game.setBindings(null);
});

await check('the keys the page needs cannot be taken', () => {
    // Tab moves focus and Escape closes the menu. Binding movement to either
    // leaves the page's own use running as well, so one press does two things.
    game.dispose();
    game.init(canvas, 'sheet-bind.png', 1);

    for (const reserved of ['Tab', 'Escape', 'F5', 'F11']) {
        game.setBindings({ up: [reserved] });
        assert.deepEqual(game.getBindings().up, game.DEFAULT_BINDINGS.up,
            `${reserved} was allowed`);
    }

    game.setBindings(null);
});

await check('rebinding mid-press does not strand the key', () => {
    // Anything held under the old bindings is not held under the new ones, and
    // nothing would ever release it.
    game.dispose();
    game.init(canvas, 'sheet-scope.png', 1);

    press('a', 'KeyA');
    assert.equal(game.readInput(), LEFT);

    game.setBindings({ left: ['KeyJ'] });
    assert.equal(game.readInput(), 0, 'a key held across a rebind stayed held');

    game.setBindings(null);
});

// ---- how loud, and how much motion ---------------------------------------

await check('reduced motion stops the renderer sliding', () => {
    // The half a CSS media query cannot reach: prefers-reduced-motion in
    // app.css governs the page's own animations and has no view of a blend
    // computed in game.js.
    game.dispose();
    game.init(canvas, 'sheet-a11y.png', 1, true);   // VIP: blending on
    deliver('sheet-a11y.png');

    game.setReducedMotion(false);
    game.draw(caveWith(5, 3), 0, 0, []);
    clock += 1000;
    game.draw(caveWith(5, 4), 1, 0, [5, 3, 5, 4]);

    let [blit] = paintOnce();
    assert.equal(blit[6], 3 * SIDE, 'the sprite did not slide with motion on');

    // Drain what the sliding phase queued, or the assertion below reads its
    // leftovers rather than what reduced motion did.
    runFrames();

    game.setReducedMotion(true);
    game.draw(caveWith(5, 3), 0, 0, []);
    clock += 1000;
    game.draw(caveWith(5, 4), 1, 0, [5, 3, 5, 4]);

    [blit] = paintOnce();
    assert.equal(blit[6], 4 * SIDE, 'the sprite slid with reduced motion on');
    assert.equal(blit[5], 4 * SIDE, 'the camera slid with reduced motion on');

    // And it starts no animation loop it has no use for.
    assert.equal(runFrames(), 0, 'reduced motion still ran the paint loop');

    game.setReducedMotion(null);
});

await check('reduced motion has three answers, not two', () => {
    // Null defers to the system, because a player who has already told their
    // machine should not have to say it again.
    game.dispose();
    game.init(canvas, 'sheet-a11y.png', 1, true);

    assert.equal(game.setReducedMotion(true), true);
    assert.equal(game.setReducedMotion(false), false);

    // No matchMedia here, so the system answer is "no".
    assert.equal(game.setReducedMotion(null), false, 'null did not defer');
    assert.equal(game.setReducedMotion('yes please'), false, 'a junk value was taken');
});

await check('the two channels have their own levels', () => {
    game.dispose();
    game.init(canvas, 'sheet-a11y.png', 1);

    assert.equal(game.setLevel('music', 40), 0.4);
    assert.equal(game.setLevel('effects', 0), 0);
    assert.equal(game.setLevel('effects', 100), 1);

    // Out of range is clamped rather than refused: a slider cannot produce it,
    // but a stored value from a future version could.
    assert.equal(game.setLevel('music', 500), 1);
    assert.equal(game.setLevel('music', -20), 0);
    assert.equal(game.setLevel('music', 'loud'), 0, 'a junk level was not clamped');

    assert.equal(game.setLevel('nonsense', 50), null, 'an unknown channel was accepted');
});

await check('the level reaches the module that makes the sound', () => {
    // The first version sent the music level to sfx-vip.js and called an export
    // neither module had. Optional chaining swallowed it and the slider moved a
    // number and nothing else - a control that looks like the need is met.
    // Astra, 19 September 2026.
    //
    // Checked by reading the modules rather than by driving audio: what matters
    // is that the export exists and that game.js asks the right one for it.
    const source = readFileSync('web/Rockford.Web/wwwroot/game.js', 'utf8');

    assert.match(source, /if \(vipMusic\) vipMusic\.setLevel\(level\)/,
        'the music level does not reach music-vip.js');
    assert.match(source, /if \(vip\) vip\.setLevel\(level\)/,
        'the effects level does not reach sfx-vip.js');
    assert.match(source, /const open = effectsGain\(\)/,
        'the faithful speaker does not read the level');

    for (const [file, name] of [['sfx-vip.js', 'effects'], ['music-vip.js', 'music']]) {
        const mod = readFileSync(`web/Rockford.Web/wwwroot/${file}`, 'utf8');
        assert.match(mod, /export function setLevel/,
            `${file} has no setLevel for the ${name} channel to call`);
    }
});

await check('a level is separate from a mute', () => {
    // "Not now" and "this loud when it is on" are different questions. Turning
    // the effects down to 20 and then muting should give 20 back on unmute.
    game.dispose();
    game.init(canvas, 'sheet-a11y.png', 1);

    game.setMuted(false);
    game.setLevel('effects', 100);
    const full = game.effectsGain();
    assert.ok(full > 0, 'a full level is silent');

    game.setLevel('effects', 20);
    const fifth = game.effectsGain();

    // A ratio rather than a number: the gain is the speaker's OWN balance
    // multiplied by the player's level, and asserting the raw figure would
    // freeze the mix - changing how loud the square wave sits against the music
    // would then break a test about the slider.
    assert.ok(Math.abs(fifth - full * 0.2) < 1e-9,
        `a fifth of the level gave ${fifth} against a full ${full}`);

    game.setMuted(true);
    assert.equal(game.effectsGain(), 0, 'muted is not silent');

    game.setMuted(false);
    assert.ok(Math.abs(game.effectsGain() - fifth) < 1e-9,
        'unmuting did not give back the level that was set');

    game.setLevel('effects', 0);
    assert.equal(game.effectsGain(), 0, 'a zero level is not silent');

    game.setLevel('effects', 80);
});

// ---- the settings store --------------------------------------------------
//
// Not game.js, but the same entry point: this file is what CI runs over the
// front end's JavaScript, and a second runner for one small module would be a
// check nobody remembers to wire up. That is the hole site/tests/ sat in.

/** A localStorage that behaves, and one that does not. */
/**
 * Every setting at its default. Named rather than repeated, so adding a field
 * is one edit here instead of five deepEqual literals that each have to be
 * found - which is how a test suite comes to be updated without being read.
 */
// A SECOND COPY of settings.js's DEFAULTS, and deliberately so: a test that
// imported the thing it is testing would pass whatever that thing said. Adding
// a setting therefore means adding it here, and six checks fail until it is -
// which is the reminder working rather than a chore.
const DEFAULT_SETTINGS = {
    music: true, effects: true, vocal: true, bindings: null,
    musicLevel: 70, effectsLevel: 80, reducedMotion: null,
    outlines: false, lighting: true,
};

function fakeStorage() {
    const held = new Map();
    return {
        getItem: k => (held.has(k) ? held.get(k) : null),
        setItem: (k, v) => held.set(k, String(v)),
        removeItem: k => held.delete(k),
        _held: held,
    };
}

globalThis.localStorage = fakeStorage();

const settings = await import(
    pathToFileURL('web/Rockford.Web/wwwroot/settings.js').href);

console.log('\nsettings.js');

await check('an empty store is the behaviour the game already had', () => {
    localStorage = fakeStorage();
    const s = settings.load();
    assert.deepEqual(s, DEFAULT_SETTINGS);
});

await check('a choice survives a reload', () => {
    localStorage = fakeStorage();
    settings.load();

    settings.set('music', false);
    settings.set('vocal', false);

    // What a reload is: a fresh read of the same storage.
    const after = settings.load();
    assert.equal(after.music, false);
    assert.equal(after.vocal, false);
    assert.equal(after.effects, true, 'a setting nobody touched should be its default');
});

await check('a corrupt value costs the preferences, not the game', () => {
    // A stored blob that is not JSON, or not an object, or from a shape this
    // version cannot read. Every one of them must come back as the defaults
    // rather than throw - the game worked without any of this yesterday.
    for (const bad of ['not json at all', '[]', 'null', '"a string"', '{"v":99,"music":false}']) {
        localStorage = fakeStorage();
        localStorage.setItem('rockford.settings', bad);
        assert.deepEqual(settings.load(), DEFAULT_SETTINGS,
            `${bad} did not fall back`);
    }
});

await check('only the fields we know about, with the types we expect', () => {
    // Taking the stored object wholesale would let anything that ever wrote to
    // this key decide what the settings are.
    localStorage = fakeStorage();
    localStorage.setItem('rockford.settings', JSON.stringify({
        v: 1, music: 'no', effects: false, vocal: true, somethingElse: 'hello',
    }));

    const s = settings.load();
    assert.equal(s.music, true, 'a string was taken for a boolean');
    assert.equal(s.effects, false, 'a good value beside a bad one was discarded');
    assert.equal('somethingElse' in s, false, 'an unknown field was kept');
});

await check('an unknown name is a no-op rather than a stored field', () => {
    localStorage = fakeStorage();
    settings.load();

    assert.equal(settings.set('typo', true), false);
    assert.equal(settings.set('music', 'yes'), false, 'the wrong type was accepted');
    assert.equal('typo' in settings.all(), false);
});

await check('storage that throws does not take the game with it', () => {
    // Safari in private browsing has thrown on write; an iframe with
    // third-party storage blocked throws on read; a browser with storage off
    // has no localStorage at all. None of those should stop anything.
    localStorage = {
        getItem() { throw new Error('nope'); },
        setItem() { throw new Error('nope'); },
        removeItem() { throw new Error('nope'); },
    };

    assert.deepEqual(settings.load(), DEFAULT_SETTINGS);
    assert.equal(settings.set('music', false), true, 'a failed write should still apply');
    assert.equal(settings.all().music, false, 'the choice should hold for this session');

    delete globalThis.localStorage;
    assert.deepEqual(settings.load(), DEFAULT_SETTINGS,
        'no localStorage at all should be the defaults');
    globalThis.localStorage = fakeStorage();
});

await check('every value is written, not only the changed one', () => {
    // The component applies all three on startup rather than just the disabled
    // ones, because game.js keeps its mute state across a remount. That relies
    // on the store always answering for every field, including the defaults.
    localStorage = fakeStorage();
    settings.load();
    settings.set('music', false);

    const written = JSON.parse(localStorage.getItem('rockford.settings'));
    assert.equal(written.music, false);
    assert.equal(written.effects, true, 'an untouched setting was not written');
    assert.equal(written.vocal, true, 'an untouched setting was not written');
    assert.equal(written.v, 1, 'the version was not written');
});

await check('a stored value nobody has changed is still answered for', () => {
    // load() must report every field whatever the blob contains, or the
    // component cannot apply what it did not find.
    localStorage = fakeStorage();
    localStorage.setItem('rockford.settings', JSON.stringify({ v: 1, music: false }));

    const s = settings.load();
    assert.deepEqual(s, { ...DEFAULT_SETTINGS, music: false },
        'a partial blob did not come back complete');
});

await check('clearing puts everything back', () => {
    localStorage = fakeStorage();
    settings.load();
    settings.set('effects', false);

    settings.clear();
    assert.deepEqual(settings.all(), DEFAULT_SETTINGS);
    assert.equal(localStorage.getItem('rockford.settings'), null, 'the key survived a clear');
});

// ---- the effects layer --------------------------------------------------
//
// Issue #34. These exist because the two things that could go wrong with a
// particle system in this game are both invisible until they bite: it can grow
// without limit under a chain reaction, and it can quietly reach for the
// simulation's random generator.

const effects = await import('../web/Rockford.Web/wwwroot/effects-vip.js');

await check('a chain reaction cannot grow the pool without limit', () => {
    effects.clear();

    // Far more blasts than a cave can hold, all in one step.
    for (let i = 0; i < 500; i++) effects.blasted(i % 40, i % 22);

    assert.ok(effects.count() <= 420,
        `the pool reached ${effects.count()}, which is not bounded`);
    assert.ok(effects.count() > 0, 'the pool emptied itself instead of capping');
});

await check('the newest event survives a full pool', () => {
    effects.clear();
    for (let i = 0; i < 500; i++) effects.blasted(0, 0);

    // The thing that just happened is the thing worth seeing. Replacing the
    // oldest rather than dropping the newest is what keeps that true when the
    // most is going on.
    effects.landed(39, 21);
    assert.ok(effects.count() > 0);
});

await check('particles age out and the loop stops asking for frames', () => {
    effects.clear();
    effects.dug(3, 4);
    assert.ok(effects.busy(), 'nothing was spawned');

    // Longer than the longest lifetime, in steps the clamp will accept.
    for (let i = 0; i < 200; i++) effects.step(0.05);

    assert.equal(effects.count(), 0, 'particles outlived their lifetime');
    assert.ok(!effects.busy(), 'the frame loop would never stop');
});

await check('a long stall does not throw every particle off the cave', () => {
    effects.clear();
    effects.blasted(10, 5);

    // A backgrounded tab comes back with a gap of seconds. Unclamped, a
    // straight-line integration puts a blast's debris hundreds of cells away.
    effects.step(12);

    assert.ok(effects.count() > 0, 'a single long step killed everything');
});

await check('a fresh cave forgets what the last one was showing', () => {
    effects.clear();
    effects.blasted(1, 1);
    assert.ok(effects.count() > 0);

    effects.clear();
    assert.equal(effects.count(), 0);
    assert.equal(effects.cameraOffset(), null, 'the shake survived the cave');
});

await check('only a blast moves the camera', () => {
    // A shot is not a substantial event, and Observation is the whole reason
    // firing and exploding can be told apart at all. SoundEvent uses one
    // request for both, so a front end driven by it would shake on every shot.
    effects.clear();
    effects.fired(5, 5);
    effects.dug(5, 5);
    effects.collected(5, 5);
    effects.landed(5, 5);
    assert.equal(effects.cameraOffset(), null, 'something other than a blast shook the view');

    effects.blasted(5, 5);
    assert.notEqual(effects.cameraOffset(), null, 'a blast did not shake the view');
});

await check('the shake decays rather than persisting', () => {
    effects.clear();
    effects.blasted(5, 5);

    for (let i = 0; i < 100; i++) effects.step(0.05);
    assert.equal(effects.cameraOffset(), null, 'the view is still shaking');
});

await check('it never reaches for the simulation generator', () => {
    // Not a style point. The original's generator is drawn from a fixed number
    // of times in fixed places, and the whole fidelity argument rests on that.
    const source = readFileSync(
        new URL('../web/Rockford.Web/wwwroot/effects-vip.js', import.meta.url), 'utf8');

    // COMMENTS STRIPPED FIRST. The first version of this check matched the
    // file's own comment explaining that it does not do this, which is a check
    // that reads prose rather than code - and it would have gone on passing if
    // the sentence were deleted and the call added.
    const code = source
        .replace(/\/\*[\s\S]*?\*\//g, '')
        .replace(/^\s*\/\/.*$/gm, '');

    for (const forbidden of ['Math.random', 'Simulation.Random', 'simRandom']) {
        assert.ok(!code.includes(forbidden),
            `effects-vip.js reaches for ${forbidden}`);
    }

    // And it has a generator of its own, so "no Math.random" is not passing by
    // having no randomness at all.
    assert.ok(code.includes('function rnd'), 'there is no independent generator');
});

// ---- audio scheduling ---------------------------------------------------
//
// Issue #36. The spacing between the game's sounds used to be decided by
// whatever else the browser was doing.

await check('a frame carries the instant it is meant to sound at', () => {
    const sfx = readFileSync('web/Rockford.Web/wwwroot/sfx-vip.js', 'utf8');
    const code = sfx.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');

    assert.ok(code.includes('export function playEvent(event, divisor, at = 0)'),
        'playEvent does not take an instant');

    // Never in the past: a timer that fired after the moment has gone is a
    // stall, and saying so beats relying on Web Audio to treat it as now.
    assert.ok(code.includes('Math.max(at || 0, ctx.currentTime)'),
        'a late frame is not clamped to now');
});

await check('the step decides its spacing once, from the audio clock', () => {
    const game = readFileSync('web/Rockford.Web/wwwroot/game.js', 'utf8');
    const code = game.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');

    // One reading of currentTime for the whole step, and each frame offset from
    // it - rather than each synth reading the clock whenever its timer fired.
    assert.ok(code.includes('const base = audio.currentTime;'),
        'the step does not fix a base instant');
    assert.ok(code.includes('const when = base + i * slot;'),
        'frames are not offset from the base');
    assert.ok(code.includes('vip.playEvent(event, divisor, when)'),
        'the instant does not reach the synth');
});

await check('the sting has its own gain and hands over from the theme', () => {
    const music = readFileSync('web/Rockford.Web/wwwroot/music-vip.js', 'utf8');
    const code = music.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');

    // It used to run straight into the theme's node, so the two were one
    // control and there was nothing to hand over between.
    assert.ok(code.includes('stingGain.connect(out)'), 'the sting has no gain of its own');
    assert.ok(code.includes('createMediaElementSource(sting).connect(stingGain)'),
        'the sting still runs into the theme gain');
    assert.ok(code.includes('handover();'), 'starting a sting does not hand over');

    // And the handover is NOT a duck. A duck is 310 ms for one effect; this
    // steps the theme back for a phrase, because the sting deliberately
    // outlives the 1.2 second hold and plays on over the next cave.
    assert.ok(!code.includes('function handover() {\n    duck()'),
        'the handover is just a duck');
});

// ---- outlines -----------------------------------------------------------
//
// Issue #70. A legibility aid, off by default, and only on the things that move.

await check('outlines default to off and survive a round trip', () => {
    localStorage = fakeStorage();

    // Nobody should have a permanent dark edge on every object imposed on them;
    // anyone who needs it will go and find it.
    assert.equal(settings.load().outlines, false);

    settings.set('outlines', true);
    assert.equal(settings.load().outlines, true);
});

await check('a settings blob written before outlines existed still loads', () => {
    localStorage = fakeStorage();
    // With the version marker a real blob carries. Without it load() rejects
    // the whole thing and falls back, which is correct and is not what this
    // test is about.
    localStorage.setItem('rockford.settings', JSON.stringify({ v: 1, music: false }));

    // "Not stated" has to mean the default rather than false-by-accident, or
    // adding a setting silently changes what an existing player sees.
    const s = settings.load();
    assert.equal(s.outlines, false);
    assert.equal(s.music, false, 'the rest of the blob was lost');
});

await check('only the things that move and matter are outlined', () => {
    const game = readFileSync('web/Rockford.Web/wwwroot/game.js', 'utf8');
    const code = game.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');

    // Positions, not values: the per-frame payload carries sheet coordinates
    // and widening it to say what each cell IS would cost more than the whole
    // feature saves.
    assert.ok(code.includes('export function setOutlinedTiles(flat)'),
        'the renderer decides for itself which tiles to outline');
    assert.ok(code.includes('outlinedTiles.has('), 'the set is not consulted');

    // If everything were outlined then nothing would be - the point is to
    // separate what moves from what it moves against.
    assert.ok(!code.includes('if (outlines) {\n                const mask'),
        'the outline is drawn unconditionally');
});

// ---- lighting -----------------------------------------------------------
//
// Issue #37. The rule it is built around: the cave stays readable with the
// lights off.

await check('lighting is on by default and outlines are not', () => {
    localStorage = fakeStorage();
    const s = settings.load();

    // Different defaults on purpose. Lighting ADDS light and never removes it,
    // so it is a look; outlines put a permanent dark edge on every object,
    // which is an aid somebody should go and find.
    assert.equal(s.lighting, true);
    assert.equal(s.outlines, false);
});

await check('nothing in the lighting pass can darken the cave', () => {
    const game = readFileSync('web/Rockford.Web/wwwroot/game.js', 'utf8');
    const code = game.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '');

    // THE RULE. Darkness that conceals a hazard works against this game's
    // spatial precision - a player has to see a boulder two cells up and
    // decide in a tenth of a second. The glow is additive, and these are the
    // operations that would take light away.
    for (const forbidden of ['multiply', 'destination-out', 'destination-in', 'darken']) {
        assert.ok(!code.includes(`globalCompositeOperation = '${forbidden}'`),
            `the renderer uses ${forbidden}, which can only darken`);
    }

    assert.ok(code.includes("globalCompositeOperation = 'lighter'"),
        'the glow is not additive');
});

await check('the closed exit does not glow', () => {
    const tiles = readFileSync('web/Rockford.Web/TileSet.cs', 'utf8');
    const code = tiles.replace(/\/\/\/[^\n]*/g, '').replace(/^\s*\/\/.*$/gm, '');

    // The glow IS the state change. An exit that glowed while shut would be
    // telling the player it was open.
    assert.ok(code.includes('RuntimeTile.ExitOpen'), 'the open exit is not lit');
    assert.ok(!code.includes('RuntimeTile.ExitClosed'), 'the closed exit is lit');
});

console.log(failures === 0
    ? '\nall good'
    : `\n${failures} failed`);

process.exit(failures === 0 ? 0 : 1);
