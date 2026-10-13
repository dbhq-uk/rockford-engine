// The original's cartoon: its timing and where it sits. No DOM in this file, so
// plain Node can hold it to the original (tools/jscartoon.mjs); the drawing is in
// game.js. What the original does, and the addresses it does it at, are in
// docs/FORMATS.md, "When each cartoon plays".

/** A frame is 48 by 48 pixels. */
export const SIZE = 48;

/** Six frames to an animation, looped. */
export const FRAMES = 6;

/** 9 ticks of the 60 Hz timer (0x1E0CE). */
export const FRAME_MS = 150;

/** 60 ticks: a key is not looked at before this (0x1E118). */
export const MIN_MS = 1000;

/** 360 ticks: it ends by itself here (0x1E126). */
export const MAX_MS = 6000;

/**
 * The size of one frame in a strip that is `width` pixels across. The faithful
 * strip is 288 wide and its frames 48; VIP's is the same sheet at eight to one,
 * 2304 wide with frames of 384. The window is 48 either way, in field pixels.
 */
export function frameSize(width) {
    return Math.round(width / FRAMES);
}

/** The field the cartoon sits in, in 16-pixel cells: 20 by 11. */
const FIELD_W = 320;
const FIELD_H = 176;
const CELL = 16;

/** How far in from the field's edge the window is kept: the original's clamps. */
const INSET_NEAR = 15;
const INSET_FAR = 17;

/** Which of the six frames shows `elapsed` milliseconds in. */
export function frameAt(elapsed) {
    return Math.floor(elapsed / FRAME_MS) % FRAMES;
}

/**
 * Whether it is over. Any key ends it, but only once it has run a second; either
 * way it stops at six.
 */
export function finished(elapsed, pressed) {
    return elapsed >= MAX_MS || (pressed && elapsed >= MIN_MS);
}

/**
 * Where the window's top left sits, in field pixels at 16 to the cell.
 *
 * Centred on the player's cell, 17 pixels left of and above it as the original
 * has it (0x1E222), and held inside the field by the original's own margins:
 * 15 pixels at the top and left, 17 at the bottom and right. `player` and
 * `origin` are cave cells; the origin is the cell at the field's top left.
 */
export function boxFor(player, origin) {
    const x = (player.x - origin.x) * CELL - 17;
    const y = (player.y - origin.y) * CELL - 17;
    return {
        x: Math.max(INSET_NEAR, Math.min(FIELD_W - SIZE - INSET_FAR, x)),
        y: Math.max(INSET_NEAR, Math.min(FIELD_H - SIZE - INSET_FAR, y)),
    };
}
