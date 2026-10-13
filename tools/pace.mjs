// Measures how fast the simulation actually runs, in the browser.
//
// The cave clock is the only honest reference: the engine takes a second off it
// every sixty interrupts, and the host loop is what decides how long sixty
// interrupts take in the real world. So watch the HUD's time fall and compare it
// against the wall clock. A correct build reads 1000 ms per game second.
//
// This is how issue #13 was found - both builds measured 1112 ms, eleven per
// cent slow - and it is how the fix is checked rather than assumed.
//
// It counts a fixed number of CLOCK TRANSITIONS and times those, rather than
// counting transitions inside a fixed wall time. The HUD shows whole seconds, so
// the second way throws away the fraction at each end: a clock ticking every
// 999 ms still reports a hundred ticks in a hundred seconds and reads as exactly
// right. Timing the transitions themselves has no such quantisation, because
// both ends of the measurement are the same kind of event.
//
//   node tools/pace.mjs <url> [ticks]
//
// Chrome has to be listening already:
//
//   google-chrome --headless=new --disable-gpu --no-sandbox --mute-audio \
//     --window-size=1280,860 --remote-debugging-port=9333 about:blank &

const PORT = Number(process.env.CHROME_DEVTOOLS_PORT ?? 9333);
const [url, ticksArg] = process.argv.slice(2);

if (!url) {
    console.error('usage: pace.mjs <url> [ticks]');
    process.exit(2);
}

const ticks = Number(ticksArg ?? 60);

const tab = await (await fetch(`http://127.0.0.1:${PORT}/json/new?about:blank`,
    { method: 'PUT' })).json();

const ws = new WebSocket(tab.webSocketDebuggerUrl);
await new Promise(resolve => ws.addEventListener('open', resolve, { once: true }));

let id = 0;
const send = (method, params = {}) => new Promise(resolve => {
    const mine = ++id;
    const onMessage = event => {
        const message = JSON.parse(event.data);
        if (message.id !== mine) return;
        ws.removeEventListener('message', onMessage);
        resolve(message.result ?? {});
    };
    ws.addEventListener('message', onMessage);
    ws.send(JSON.stringify({ id: mine, method, params }));
});

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

const evaluate = async expression => {
    const { result } = await send('Runtime.evaluate',
        { expression, returnByValue: true, awaitPromise: true });
    return result?.value;
};

const press = async key => {
    const vk = key === 'Enter' ? 13 : 0;
    const event = { key, code: key, windowsVirtualKeyCode: vk, nativeVirtualKeyCode: vk };
    await send('Input.dispatchKeyEvent', { type: 'keyDown', ...event });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', ...event });
    await sleep(1400);
};

await send('Page.enable');
await send('Runtime.enable');
await send('Page.navigate', { url });

// The WebAssembly boot is the slow part, and it is slower on a cold cache than
// any fixed wait would want to be. Poll for the title screen instead.
const readTime = `(() => {
    const hud = [...document.querySelectorAll('.hud span')]
        .find(s => s.textContent.trim().startsWith('time'));
    return hud ? Number(hud.querySelector('b').textContent) : null;
})()`;

for (let i = 0; i < 60; i++) {
    if (await evaluate(`document.querySelector('canvas') !== null`)) break;
    await sleep(1000);
}
await sleep(3000);

await press('Enter');   // title -> the world chooser
await press('Enter');   // chooser -> a cave

if (await evaluate(readTime) === null) {
    console.error('never reached a cave - the HUD has no time');
    process.exit(1);
}

/**
 * Waits for the HUD clock to change, and answers when it did.
 *
 * The timestamp is taken in the PAGE, from performance.now, and the poll runs
 * there too - a poll driven over the DevTools socket would put a round trip
 * between the change and the reading of it, which is the same order as the thing
 * being measured.
 */
const nextTick = async (timeoutMs = 4000) => await evaluate(`(async () => {
    const read = () => {
        const hud = [...document.querySelectorAll('.hud span')]
            .find(s => s.textContent.trim().startsWith('time'));
        return hud ? Number(hud.querySelector('b').textContent) : null;
    };
    const from = read();
    const deadline = performance.now() + ${timeoutMs};
    while (performance.now() < deadline) {
        const now = read();
        if (now !== from) return { at: performance.now(), value: now };
        await new Promise(r => setTimeout(r, 2));
    }
    return null;
})()`);

// Start on a transition, so the measurement is boundary to boundary.
const start = await nextTick();
if (start === null) {
    console.error('the clock never ticked - did the cave not start?');
    process.exit(1);
}

let end = start;
for (let i = 0; i < ticks; i++) {
    const tick = await nextTick();
    if (tick === null) {
        console.error(`the clock stopped after ${i} ticks - did the player die?`);
        process.exit(1);
    }
    end = tick;
}

const elapsed = end.at - start.at;
const counted = start.value - end.value;

if (counted !== ticks) {
    console.error(`counted ${ticks} transitions but the clock fell ${counted} - ` +
                  `something else moved it`);
    process.exit(1);
}

const per = elapsed / ticks;
console.log(`${url}`);
console.log(`  ${ticks} game seconds in ${elapsed.toFixed(1)} ms`);
console.log(`  ${per.toFixed(1)} ms per game second (1000 is correct, ` +
            `${((per / 1000 - 1) * 100).toFixed(1)}% out)`);

ws.close();
await fetch(`http://127.0.0.1:${PORT}/json/close/${tab.id}`);
