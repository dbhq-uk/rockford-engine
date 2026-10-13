// Screenshots of the browser front end, for the README.
//
// Chrome's own --screenshot flag is no use here: it fires immediately, and
// --virtual-time-budget starves the WebAssembly boot so the canvas comes out
// black. This drives Chrome over the DevTools protocol instead and waits in real
// time, then sends keys to reach a given screen.
//
//   node tools/screenshot.mjs <url> <out.png> [waitSeconds] [key,key,...]
//
// Chrome has to be listening already:
//
//   google-chrome --headless=new --disable-gpu --no-sandbox --hide-scrollbars \
//     --window-size=1280,860 --remote-debugging-port=9333 about:blank &
//
// The three shots in docs/screenshots come from:
//
//   node tools/screenshot.mjs http://host/ title.png  26
//   node tools/screenshot.mjs http://host/ worlds.png 26 Enter
//   node tools/screenshot.mjs http://host/ play.png   26 Enter,Enter

import { writeFileSync } from 'node:fs';

const PORT = Number(process.env.CHROME_DEVTOOLS_PORT ?? 9333);
const [url, out, waitArg, keysArg] = process.argv.slice(2);

if (!url || !out) {
    console.error('usage: screenshot.mjs <url> <out.png> [waitSeconds] [key,key,...]');
    process.exit(2);
}

const wait = Number(waitArg ?? 26) * 1000;
const keys = (keysArg ?? '').split(',').filter(Boolean);

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

await send('Page.enable');
await send('Page.navigate', { url });
await sleep(wait);

for (const key of keys) {
    const vk = key === 'Enter' ? 13 : 0;
    const event = { key, code: key, windowsVirtualKeyCode: vk, nativeVirtualKeyCode: vk };
    await send('Input.dispatchKeyEvent', { type: 'keyDown', ...event });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', ...event });
    // The game runs at ten steps a second, so give it a beat to redraw.
    await sleep(1400);
}

const { data } = await send('Page.captureScreenshot', { format: 'png' });
writeFileSync(out, Buffer.from(data, 'base64'));
console.log(`written ${out}`);

ws.close();
await fetch(`http://127.0.0.1:${PORT}/json/close/${tab.id}`);
