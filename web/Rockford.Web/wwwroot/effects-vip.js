/*
 * VIP's local feedback: dirt fragments, a collection sparkle, a contact puff
 * when something lands, debris from a blast.
 *
 * WHAT THIS IS FOR, and what it is not. These exist to say WHERE something
 * happened and HOW it turned out. A puff under a landed boulder tells you the
 * rock has settled and which cell it settled in. A shower of dirt says the dig
 * went through. Anything that does not answer one of those two questions is
 * decoration, and decoration on top of a cave that is trying to kill you is
 * noise.
 *
 * So there are no continuous sparkles on idle treasure, no smoke that covers an
 * adjacent cell, and no screen shake on an ordinary event.
 *
 * ---------------------------------------------------------------------------
 *
 * IT NEVER TOUCHES Simulation.Random. Not once, not indirectly, not "just for
 * the angle". The original's generator is drawn from a fixed number of times in
 * fixed places and the whole fidelity argument rests on that; a front end that
 * borrowed a number from it would change the game while looking like it was
 * only drawing pictures. This has its own generator, seeded from nothing that
 * matters, and the simulation cannot observe it.
 *
 * FingerprintTests is what actually holds that true. This comment is a
 * signpost, not the guarantee.
 *
 * ---------------------------------------------------------------------------
 *
 * IT IS DRIVEN BY Observation, NOT BY SoundEvent. That distinction is the one
 * thing in issue #34 worth reading twice. SoundEvent reports which of the
 * driver's eight requests a priority driver served in a frame: requests merge,
 * service can trail the action, events carry no location at all, and one
 * request covers BOTH ordinary firing and an explosion. A front end that shook
 * the screen on every Blast would shake it on every shot.
 *
 * Observation says what happened and where. That is why it exists.
 */

/** The whole pool. Bounded, because a cave-wide chain reaction is a real input. */
const MAX = 420;

/** Particle fields, as parallel arrays - one allocation for the page's life. */
const px = new Float32Array(MAX);
const py = new Float32Array(MAX);
const vx = new Float32Array(MAX);
const vy = new Float32Array(MAX);
const life = new Float32Array(MAX);      // seconds remaining
const full = new Float32Array(MAX);      // seconds it started with
const size = new Float32Array(MAX);      // in cells
const kind = new Uint8Array(MAX);

let live = 0;

/** Cells per second squared. A fragment arcs; a sparkle does not. */
const GRAVITY = 26;

const DIRT = 0, SPARK = 1, PUFF = 2, DEBRIS = 3;

/*
 * Our own generator.
 *
 * A plain xorshift, seeded from a constant. Deterministic on purpose: two runs
 * of the same recording paint the same particles, which makes a screenshot
 * comparison meaningful and costs nothing - nobody can tell a fixed sequence of
 * dirt fragments from a random one.
 */
let seed = 0x9E3779B9;

function rnd() {
    seed ^= seed << 13; seed >>>= 0;
    seed ^= seed >> 17;
    seed ^= seed << 5; seed >>>= 0;
    return seed / 0x100000000;
}

function spread(amount) {
    return (rnd() - 0.5) * 2 * amount;
}

/**
 * Take a slot.
 *
 * When the pool is full it REPLACES THE OLDEST rather than dropping the newest.
 * A cave-wide chain reaction is the case that fills it, and in that case the
 * thing that just happened is the thing worth seeing - dropping it would make
 * the effects quietly stop exactly when the most is going on.
 */
let oldest = 0;

function take() {
    if (live < MAX) return live++;
    const slot = oldest;
    oldest = (oldest + 1) % MAX;
    return slot;
}

function emit(type, x, y, dx, dy, seconds, scale) {
    const i = take();
    kind[i] = type;
    px[i] = x; py[i] = y;
    vx[i] = dx; vy[i] = dy;
    life[i] = full[i] = seconds;
    size[i] = scale;
}

/** Everything the page has to forget when a cave changes under it. */
export function clear() {
    live = 0;
    oldest = 0;
    shake = 0;
}

// ---- the effects --------------------------------------------------------
//
// Cell coordinates throughout, converted to pixels only at paint. The field
// scrolls and the tile size changes with the window; carrying pixels here would
// mean every live particle was wrong the moment either did.

/** Dirt going through. A short, heavy, downward scatter. */
export function dug(x, y) {
    for (let i = 0; i < 7; i++) {
        emit(DIRT, x + 0.5 + spread(0.3), y + 0.5 + spread(0.3),
             spread(2.4), -1.2 - rnd() * 1.6, 0.26 + rnd() * 0.14, 0.055);
    }
}

/** A collection. One brief flash outward, not a lingering twinkle. */
export function collected(x, y) {
    for (let i = 0; i < 9; i++) {
        const a = (i / 9) * Math.PI * 2 + spread(0.2);
        const speed = 2.2 + rnd() * 1.4;
        emit(SPARK, x + 0.5, y + 0.5,
             Math.cos(a) * speed, Math.sin(a) * speed, 0.3 + rnd() * 0.1, 0.045);
    }
}

/**
 * Something came to rest. A low puff at the base of the cell it landed in.
 *
 * This is the one effect the sound stream genuinely could not drive, because a
 * sound frame carries no location.
 */
export function landed(x, y) {
    for (let i = 0; i < 5; i++) {
        emit(PUFF, x + 0.5 + spread(0.45), y + 0.9,
             spread(1.5), -0.35 - rnd() * 0.4, 0.28 + rnd() * 0.12, 0.09);
    }
}

/** A blast. The only thing here allowed to move the camera, and barely. */
export function blasted(x, y) {
    for (let i = 0; i < 16; i++) {
        const a = rnd() * Math.PI * 2;
        const speed = 1.6 + rnd() * 4.2;
        emit(DEBRIS, x + 0.5 + spread(0.4), y + 0.5 + spread(0.4),
             Math.cos(a) * speed, Math.sin(a) * speed - 1.2,
             0.34 + rnd() * 0.2, 0.06 + rnd() * 0.03);
    }

    // Tiny, and reserved. An explosion is a substantial event; a shot is not,
    // and Observation is what lets those be told apart at all.
    shake = Math.min(shake + 0.22, 0.4);
}

/** A shot. A flick at the muzzle, no debris and no shake. */
export function fired(x, y) {
    for (let i = 0; i < 4; i++) {
        emit(SPARK, x + 0.5 + spread(0.25), y + 0.5 + spread(0.25),
             spread(1.6), spread(1.6), 0.13, 0.035);
    }
}

// ---- the camera ---------------------------------------------------------

let shake = 0;

/**
 * How far to nudge the view this frame, in cells.
 *
 * Decays on its own and is capped hard. The cap matters more than the amount:
 * a chain reaction raises it several times in one step, and without a ceiling
 * the picture would leave the screen.
 */
export function cameraOffset() {
    if (shake <= 0) return null;
    return { x: spread(shake) * 0.12, y: spread(shake) * 0.12 };
}

// ---- the loop -----------------------------------------------------------

/** Advance by a real elapsed time, in seconds. */
export function step(seconds) {
    if (seconds <= 0) return;

    // Clamped. A backgrounded tab returns with a gap of seconds, and without
    // this every live particle would jump to wherever a straight-line
    // integration put it - which for a blast is off the cave.
    const dt = Math.min(seconds, 0.05);

    shake = Math.max(0, shake - dt * 2.4);

    let i = 0;
    while (i < live) {
        life[i] -= dt;
        if (life[i] <= 0) {
            // Swap the last live one down, so the array stays packed and the
            // loop stays linear.
            live--;
            px[i] = px[live]; py[i] = py[live];
            vx[i] = vx[live]; vy[i] = vy[live];
            life[i] = life[live]; full[i] = full[live];
            size[i] = size[live]; kind[i] = kind[live];
            continue;
        }

        px[i] += vx[i] * dt;
        py[i] += vy[i] * dt;

        // A sparkle is weightless; everything else falls.
        if (kind[i] !== SPARK) vy[i] += GRAVITY * dt * (kind[i] === PUFF ? 0.22 : 1);

        i++;
    }
}

/** True while anything is still on screen, so the frame loop knows to keep going. */
export function busy() {
    return live > 0 || shake > 0;
}

const COLOURS = ['#7a5c3a', '#ffe9a8', '#b9b2a6', '#d8b083'];

/**
 * Paint what is alive.
 *
 * @param ctx      the field's own context
 * @param originX  the camera's left edge, in cells, fractional part included
 * @param originY  the camera's top edge
 * @param side     a cell's size in surface pixels
 */
export function paint(ctx, originX, originY, side) {
    if (live === 0) return;

    const was = ctx.globalAlpha;

    for (let i = 0; i < live; i++) {
        // Fades out over its own lifetime rather than a shared one, so a short
        // spark and a long fragment both end at nothing.
        const t = life[i] / full[i];
        ctx.globalAlpha = t * t;
        ctx.fillStyle = COLOURS[kind[i]];

        const s = Math.max(1, size[i] * side * (kind[i] === PUFF ? 0.6 + (1 - t) : 1));
        ctx.fillRect(
            Math.round((px[i] - originX) * side - s / 2),
            Math.round((py[i] - originY) * side - s / 2),
            Math.round(s), Math.round(s));
    }

    ctx.globalAlpha = was;
}

/** For the checks: how many are alive, without exposing the pool. */
export function count() {
    return live;
}
