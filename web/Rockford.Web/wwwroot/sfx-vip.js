// Rockford VIP sound effects.
//
// The faithful front end plays what the hardware played: one square wave at a
// pitch the game chose, in game.js. This is the other presentation. It takes the
// event rather than the pitch - a boulder landed, not 69 hertz - and synthesises
// a designed sound for it.
//
// Everything here is generated. There are no files to download, nothing to
// decode before the first cave, and every parameter can be tuned against the
// running game rather than regenerated and re-auditioned. That matters more than
// it sounds: these are eight one-shots between 80 and 400 milliseconds that have
// to fire in step with a 10 Hz simulation, and getting them right is a matter of
// many small adjustments.
//
// The event numbers are the driver's own request bits, from SoundEvent.cs.

export const EVENT = {
    NONE: 0x00,
    TREASURE_FALLING: 0x01,
    LOW_RUMBLE: 0x02,
    COLLECT: 0x04,
    BLAST: 0x08,
    BOULDER_FALLING: 0x10,
    WALL_GROWING: 0x20,
    VARIABLE_TONE: 0x40,
    TICK: 0x80,
};

let ctx = null;
let out = null;
let noise = null;
let muted = false;

const LEVEL = 0.5;

/**
 * What the player asked for, 0 to 1, multiplying LEVEL.
 *
 * LEVEL is this channel's own balance against the music and is not the player's
 * business; this is. Two numbers rather than one because changing the mix
 * should not move what the slider means, and moving the slider should not
 * rebalance the mix.
 */
let level = 1;

export function setLevel(value) {
    level = Math.min(1, Math.max(0, Number(value) || 0));
    if (out) out.gain.setValueAtTime(muted ? 0 : LEVEL * level, ctx.currentTime);
    return level;
}

// Every source that has been started and not yet finished, so mute and teardown
// can silence what is already sounding rather than only what has not begun. A
// blast runs 380 ms, which is four simulation steps - long enough to outlive the
// component that fired it.
const active = new Set();

// The two holding tones are not events that fire and finish, they are states
// that persist. They get a voice that is started once and left running, because
// retriggering one every frame is what turns a sustained alarm into a buzz.
let held = null;

export function attach(audioContext, destination) {
    ctx = audioContext;
    out = ctx.createGain();
    out.gain.value = LEVEL * level;
    out.connect(destination ?? ctx.destination);
    noise = buildNoise();
}

/**
 * Mutes the effects.
 *
 * The output gain as well as the guard in playEvent, or mute only stops the
 * sounds that have not started yet and whatever is in the air plays out.
 */
export function setMuted(value) {
    muted = value;
    if (muted) stopHeld();
    if (out) out.gain.setValueAtTime(muted ? 0 : LEVEL * level, ctx.currentTime);
}

/**
 * Stops everything sounding. For teardown and for switching presentation.
 *
 * Stopping the sources rather than closing the gain, because the gain has to
 * come back for the module to be reusable - and a 380 ms blast would come back
 * with it.
 */
export function stopAll() {
    stopHeld();
    for (const src of active) { try { src.stop(); } catch { } }
    active.clear();
}

// One second of white noise, made once and replayed. Generating a fresh buffer
// per explosion would allocate on the frame the explosion happens, which is the
// worst possible moment.
function buildNoise() {
    const buffer = ctx.createBuffer(1, ctx.sampleRate, ctx.sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
    return buffer;
}

function noiseSource(playbackRate = 1) {
    const src = ctx.createBufferSource();
    src.buffer = noise;
    src.loop = true;
    src.playbackRate.value = playbackRate;
    // A random offset per hit, so repeated boulders do not replay the same
    // sample of noise and read as a loop.
    src.start(0, Math.random() * 0.9);
    return src;
}

function env(attack, decay, peak = 1, when) {
    const g = ctx.createGain();
    const now = when ?? ctx.currentTime;
    g.gain.setValueAtTime(0, now);
    g.gain.linearRampToValueAtTime(peak, now + attack);
    // Exponential, because a linear fade on a percussive sound reads as a fade
    // rather than a decay. It cannot reach zero, hence the floor.
    g.gain.exponentialRampToValueAtTime(0.0001, now + attack + decay);
    g.connect(out);
    return { node: g, endsAt: now + attack + decay };
}

function stopAt(source, when) {
    source.stop(when + 0.02);

    // Every one-shot goes through here, so this is the one place that has to
    // know about it. Dropped again when it ends, so the set holds what is
    // actually sounding rather than everything the cave ever played.
    active.add(source);
    source.onended = () => active.delete(source);
}

// ---- the events ----------------------------------------------------------

// Treasure taken. The signature sound of the game, and the one the player hears
// most, so it has to stay pleasant after two hundred repetitions: short, bright,
// and a rising fifth rather than a flat blip.
function collect(when) {
    const { node, endsAt } = env(0.004, 0.09, 0.5, when);

    for (const [ratio, level] of [[1, 1], [1.5, 0.45]]) {
        const osc = ctx.createOscillator();
        osc.type = 'triangle';
        const now = when ?? ctx.currentTime;
        osc.frequency.setValueAtTime(2330 * ratio * 0.75, now);
        osc.frequency.exponentialRampToValueAtTime(2330 * ratio, now + 0.05);

        const mix = ctx.createGain();
        mix.gain.value = level;
        osc.connect(mix).connect(node);
        osc.start(now);
        stopAt(osc, endsAt);
    }
}

// Firing, and an explosion. One sound serves both in the original, which is
// right rather than thrifty - firing is an explosion. Noise burst over a sub
// drop, with the filter closing as it decays so the tail darkens.
function blast(when) {
    const { node, endsAt } = env(0.002, 0.38, 0.7, when);
    const now = when ?? ctx.currentTime;

    const src = noiseSource(1);
    const filter = ctx.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.setValueAtTime(2600, now);
    filter.frequency.exponentialRampToValueAtTime(180, now + 0.35);
    filter.Q.value = 1.2;
    src.connect(filter).connect(node);
    stopAt(src, endsAt);

    // The sub is what makes it land rather than hiss.
    const sub = ctx.createOscillator();
    sub.type = 'sine';
    sub.frequency.setValueAtTime(140, now);
    sub.frequency.exponentialRampToValueAtTime(38, now + 0.3);
    const subGain = ctx.createGain();
    subGain.gain.value = 0.8;
    sub.connect(subGain).connect(node);
    sub.start(now);
    stopAt(sub, endsAt);
}

// A boulder landing or rolling. `variant` carries the original's own two-pitch
// draw, which is free character: a stream of falling rocks that all sound
// identical reads as a machine gun, and the game already solved that.
function boulder(variant, when) {
    const { node, endsAt } = env(0.002, 0.16, 0.55, when);
    const now = when ?? ctx.currentTime;
    const tune = variant ? 1 : 0.88;

    const src = noiseSource(0.7 * tune);
    const filter = ctx.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.setValueAtTime(1100 * tune, now);
    filter.frequency.exponentialRampToValueAtTime(140, now + 0.14);
    src.connect(filter).connect(node);
    stopAt(src, endsAt);

    const thud = ctx.createOscillator();
    thud.type = 'sine';
    thud.frequency.setValueAtTime(120 * tune, now);
    thud.frequency.exponentialRampToValueAtTime(48 * tune, now + 0.12);
    const thudGain = ctx.createGain();
    thudGain.gain.value = 0.9;
    thud.connect(thudGain).connect(node);
    thud.start(now);
    stopAt(thud, endsAt);
}

// Treasure landing. The original puts this up at 6-9 kHz, which is the one place
// its pitch choice is doing real work: it says "metal, small, not a rock". Kept
// bright and very short.
function treasureFalling(variant, when) {
    // The peak here is well above the others on purpose. A bandpass this narrow
    // throws away most of the noise it is given, so the number that reaches the
    // speaker is a fraction of the number written here. These levels were set by
    // rendering each event offline and measuring it, not by reading the code.
    const { node, endsAt } = env(0.001, 0.07, 1.3, when);
    const tune = variant ? 1 : 0.78;

    const src = noiseSource(1.8 * tune);
    const filter = ctx.createBiquadFilter();
    filter.type = 'bandpass';
    filter.frequency.value = 5200 * tune;
    filter.Q.value = 2.5;
    src.connect(filter).connect(node);
    stopAt(src, endsAt);
}

// The expanding wall. No transient at all - it is a thing growing, not a thing
// hitting something. A slow swell is what makes it unnerving.
function wallGrowing(when) {
    // Resonant and low, so most of the noise is filtered away - hence the peak.
    const { node, endsAt } = env(0.06, 0.3, 0.9, when);
    const now = when ?? ctx.currentTime;

    const src = noiseSource(0.35);
    const filter = ctx.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.setValueAtTime(200, now);
    filter.frequency.linearRampToValueAtTime(700, now + 0.3);
    filter.Q.value = 3;
    src.connect(filter).connect(node);
    stopAt(src, endsAt);
}

// Walking. The original puts this at 52 and 58 hertz and leaves the speaker on,
// so it is a continuous rattle for as long as the player keeps moving.
//
// Short and quiet here, which is a judgement rather than a transcription. It
// fires on every step taken - up to ten a second - and the 450 ms decay this
// started with stacked five deep into a drone that sat under everything else.
// A tenth of a second reads as a footstep at walking pace and still runs
// together into a rumble at a run, which is the character the original has.
function lowRumble(variant, when) {
    const { node, endsAt } = env(0.005, 0.11, 0.28, when);
    const now = when ?? ctx.currentTime;
    const base = variant ? 58 : 53;

    const osc = ctx.createOscillator();
    osc.type = 'sawtooth';
    osc.frequency.value = base;
    const filter = ctx.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.value = 220;
    osc.connect(filter).connect(node);
    osc.start(now);
    stopAt(osc, endsAt);
}

// ---- the holding tones ---------------------------------------------------
//
// These two are pressure rather than events. They are pitched and they pulse,
// and making them pretty would remove the thing they are for.

function startHeld(hz) {
    if (held && held.hz === hz) return;
    stopHeld();

    // No instant here, deliberately. The held tone is a STATE the driver turns
    // on and off, not an event that happens at a moment, and scheduling its
    // start in the future would leave a gap between the frame that asked for it
    // and the sound.
    const now = ctx.currentTime;
    const osc = ctx.createOscillator();
    osc.type = 'square';
    osc.frequency.value = hz;

    const gain = ctx.createGain();
    gain.gain.value = 0;
    osc.connect(gain).connect(out);
    osc.start(now);

    held = { hz, osc, gain };
}

function pulseHeld(when) {
    if (!held) return;
    const now = when ?? ctx.currentTime;
    held.gain.gain.cancelScheduledValues(now);
    held.gain.gain.setValueAtTime(0.16, now);
    held.gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.06);
}

function stopHeld() {
    if (!held) return;
    try { held.osc.stop(); } catch { }
    held = null;
}

// ---- the entry point -----------------------------------------------------

/**
 * Play one frame of the driver.
 *
 * @param {number} event   the request that was served, a value from EVENT
 * @param {number} divisor the pitch the hardware would have used, or 0
 *
 * The divisor is still passed, for two reasons. The two-pitch events encode the
 * original's own random draw in it, which is worth keeping. And the holding
 * tones are silent between their eighth-tick pulses, so a zero divisor on a live
 * event means "holding, not speaking" rather than "finished".
 */
/**
 * Play one of the driver's events.
 *
 * @param at the instant on the AUDIO clock it is meant to happen at. Optional,
 *           defaulting to now, so a caller that does not care can ignore it.
 *
 * THIS IS THE POINT OF #36. Every synth below used to open with
 * `const now = ctx.currentTime` - the moment the JavaScript timer happened to
 * fire. A step spreads six frames over 160 ms and dispatches them with
 * setTimeout, so the spacing between the game's sounds was being decided by
 * whatever else the browser was doing: a long paint, a garbage collection or a
 * busy tab moved them, and under load the driver's own rhythm came apart.
 *
 * The audio clock does not care what the main thread is doing. Handed the
 * instant, a frame dispatched 8 ms late still SOUNDS on time.
 *
 * The engine's event order and divisors are untouched. This changes when a
 * frame is scheduled for, never which frame it is.
 */
export function playEvent(event, divisor, at = 0) {
    if (muted || !ctx || ctx.state !== 'running') return;

    // Never in the past. A timer that fired so late the instant has gone is a
    // stall, and Web Audio treats a past timestamp as "now" anyway - this says
    // so rather than relying on it.
    const when = Math.max(at || 0, ctx.currentTime);

    if (event !== EVENT.TICK && event !== EVENT.VARIABLE_TONE) stopHeld();

    switch (event) {
        case EVENT.COLLECT:
            return collect(when);
        case EVENT.BLAST:
            return blast(when);
        case EVENT.BOULDER_FALLING:
            // 0x4400 is the higher of the pair.
            return boulder(divisor === 0x4400, when);
        case EVENT.TREASURE_FALLING:
            return treasureFalling(divisor === 0x80, when);
        case EVENT.WALL_GROWING:
            return wallGrowing(when);
        case EVENT.LOW_RUMBLE:
            return lowRumble(divisor === 0x5000, when);
        case EVENT.TICK:
            startHeld(1193);
            if (divisor) pulseHeld(when);
            return;
        case EVENT.VARIABLE_TONE:
            if (divisor) { startHeld(1193180 / divisor); pulseHeld(when); }
            return;
        default:
            return;
    }
}
