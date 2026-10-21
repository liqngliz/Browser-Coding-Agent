#!/usr/bin/env node
// Runs a packaged build end to end from a neutral directory and asserts its HTTP surface.
//   node scripts/smoke.mjs --npm artifacts/npm/<pkg>.tgz        npx the actual tarball
//   node scripts/smoke.mjs --tool <tool-path>/browser-coding-agent
//   --no-browser   AutoStart=false: startup, health, models only (no Chrome needed)
//   --channel msedge, --port 5197, --timeout 420 (seconds to first /health/live)
// With a browser, Chrome runs headless against a fake chat page served here and one
// completion round-trips through the real adapter.
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { parseArgs } from 'node:util';

const { values: opt } = parseArgs({
  options: {
    npm: { type: 'string' }, tool: { type: 'string' }, 'no-browser': { type: 'boolean', default: false },
    channel: { type: 'string', default: 'chrome' }, port: { type: 'string', default: '5197' },
    timeout: { type: 'string', default: process.platform === 'win32' ? '420' : '300' },
  },
});
if (!opt.npm === !opt.tool) throw new Error('pass exactly one of --npm or --tool');

const win = process.platform === 'win32';
const port = Number(opt.port);
const base = `http://127.0.0.1:${port}`;
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'bca smoke '));  // space in path on purpose
const logPath = path.join(work, 'app.log');
let failures = 0;

const ok = msg => console.log(`  ok    ${msg}`);
const bad = msg => { console.log(`  FAIL  ${msg}`); failures++; };
const sleep = ms => new Promise(r => setTimeout(r, ms));

// Minimal stand-in for the M365 chat page: matches the adapter's fallback selectors
const FAKE_CHAT = `<!DOCTYPE html><html><body>
<div id="log"></div>
<textarea placeholder="Ask Microsoft 365 Copilot"></textarea>
<button aria-label="Send">Send</button>
<button aria-label="Stop generating" style="display:none">Stop</button>
<script>
  const composer = document.querySelector('textarea');
  const stop = document.querySelector("button[aria-label='Stop generating']");
  document.querySelector("button[aria-label='Send']").addEventListener('click', () => {
    const text = composer.value.trim();
    if (!text) return;
    composer.value = '';
    const user = document.createElement('div');
    user.setAttribute('data-message-author', 'user');
    user.textContent = text;
    document.getElementById('log').append(user);
    stop.style.display = 'inline-block';
    // Marked like M365 (markdown-reply, aria-hidden true -> false), so completion does not depend on a poll
    // catching the 1.5s Stop window on a slow runner
    const reply = document.createElement('div');
    reply.setAttribute('data-testid', 'markdown-reply');
    reply.setAttribute('aria-hidden', 'true');
    document.getElementById('log').append(reply);
    setTimeout(() => {
      reply.innerHTML = '<p></p>';
      reply.firstChild.textContent = 'echo: ' + text;
      reply.setAttribute('aria-hidden', 'false');
      stop.style.display = 'none';
    }, 1500);
  });
</script></body></html>`;

async function get(url, init) {
  try {
    const res = await fetch(url, { ...init, signal: AbortSignal.timeout(init?.timeoutMs ?? 5000) });
    return { status: res.status, body: await res.text() };
  } catch {
    return { status: 0, body: '' };
  }
}

function npxCli() {
  const dir = path.dirname(process.execPath);
  const candidates = [
    path.join(dir, 'node_modules', 'npm', 'bin', 'npx-cli.js'),
    path.join(dir, '..', 'lib', 'node_modules', 'npm', 'bin', 'npx-cli.js'),
  ];
  const found = candidates.find(fs.existsSync);
  if (!found) throw new Error(`npx-cli.js not found next to ${process.execPath}`);
  return found;
}

function killTree(child, signal) {
  if (child.exitCode !== null) return;
  if (win) spawnSync('taskkill', ['/T', '/F', '/PID', String(child.pid)], { stdio: 'ignore' });
  else try { process.kill(-child.pid, signal); } catch { /* group already gone */ }
}

const fake = http.createServer((_, res) => { res.writeHead(200, { 'content-type': 'text/html' }); res.end(FAKE_CHAT); });
await new Promise(r => fake.listen(0, '127.0.0.1', r));
const fakeUrl = `http://127.0.0.1:${fake.address().port}/chat`;

const appArgs = [
  '--urls', base,
  `--Browser:AutoStart=${!opt['no-browser']}`,
  '--Browser:Headless=true',
  `--Browser:Channel=${opt.channel}`,
  `--Browser:StartUrl=${fakeUrl}`,
  `--Browser:UserDataDirectory=${path.join(work, 'profile')}`,
];
const [cmd, args] = opt.npm
  ? [process.execPath, [npxCli(), '--yes', `--package=${path.resolve(opt.npm)}`, '--', 'browser-coding-agent', ...appArgs]]
  : [path.resolve(win && !opt.tool.endsWith('.exe') ? `${opt.tool}.exe` : opt.tool), appArgs];

console.log(`smoke: ${opt.npm ? 'npm ' + opt.npm : 'tool ' + opt.tool}, port ${port}, browser=${!opt['no-browser']}, cwd "${work}"`);
if ((await get(`${base}/health/live`)).status !== 0) {
  console.log(`  FAIL  port ${port} already answering`);
  process.exit(1);
}

const log = fs.openSync(logPath, 'w');
const app = spawn(cmd, args, { cwd: work, stdio: ['ignore', log, log], detached: !win, windowsHide: true });
let exited = null;
app.on('exit', (code, signal) => { exited = { code, signal }; });

try {
  const deadline = Date.now() + Number(opt.timeout) * 1000;
  let live = false;
  while (!live && exited === null && Date.now() < deadline) {
    live = (await get(`${base}/health/live`)).status === 200;
    if (!live) await sleep(2000);
  }
  live ? ok('/health/live 200') : bad(exited ? `app exited early ${JSON.stringify(exited)}` : `no /health/live within ${opt.timeout}s`);

  if (live) {
    const models = await get(`${base}/v1/models`);
    models.status === 200 && models.body.includes('m365-active') ? ok('/v1/models lists m365-active') : bad(`/v1/models ${models.status} ${models.body}`);

    const openapi = await get(`${base}/openapi/v1.json`);
    openapi.status === 200 && openapi.body.includes('/v1/chat/completions') ? ok('/openapi/v1.json lists /v1/chat/completions') : bad(`/openapi/v1.json ${openapi.status}`);
    const swagger = await get(`${base}/swagger/index.html`);
    swagger.status === 200 && swagger.body.includes('swagger') ? ok('/swagger UI served') : bad(`/swagger/index.html ${swagger.status}`);

    if (opt['no-browser']) {
      const ready = await get(`${base}/health/ready`);
      ready.status === 503 && ready.body.includes('Browser is not running') ? ok('/health/ready 503 without browser') : bad(`/health/ready ${ready.status} ${ready.body}`);
    } else {
      let ready;
      for (let i = 0; i < 30 && ready?.status !== 200; i++) {
        ready = await get(`${base}/health/ready`);
        if (ready.status !== 200) await sleep(2000);
      }
      ready.status === 200 ? ok('/health/ready 200 against fake chat page') : bad(`/health/ready ${ready.status} ${ready.body}`);

      const token = `SMOKE_${Date.now()}`;
      const chat = await get(`${base}/v1/chat/completions`, {
        method: 'POST', headers: { 'content-type': 'application/json' }, timeoutMs: 90000,
        body: JSON.stringify({ model: 'm365-active', messages: [{ role: 'user', content: `ping ${token}` }] }),
      });
      let content = '';
      try { content = JSON.parse(chat.body).choices[0].message.content; } catch { /* reported below */ }
      // The echo also carries appendToPrompt from the bundled replacements.json
      chat.status === 200 && content.startsWith(`echo: ping ${token}`) ? ok('chat completion round trip') : bad(`chat ${chat.status} ${chat.body}`);

      // Later turn, streamed: history is resent, only the newest user message reaches the page
      const streamed = await get(`${base}/v1/chat/completions`, {
        method: 'POST', headers: { 'content-type': 'application/json' }, timeoutMs: 90000,
        body: JSON.stringify({
          model: 'm365-active', stream: true, stream_options: { include_usage: true }, temperature: 0,
          messages: [
            { role: 'system', content: 'Be terse.' },
            { role: 'user', content: `ping ${token}` },
            { role: 'assistant', content: content },
            { role: 'user', content: `pong ${token}` },
          ],
        }),
      });
      const events = streamed.body.split('\n\n').filter(e => e.startsWith('data: ')).map(e => e.slice(6));
      let text = '';
      try {
        text = events.filter(e => e !== '[DONE]').map(e => JSON.parse(e))
          .map(c => c.choices?.[0]?.delta?.content ?? '').join('');
      } catch { /* reported below */ }
      streamed.status === 200 && events.at(-1) === '[DONE]' && text.startsWith(`echo: pong ${token}`) && !text.includes(`ping ${token}`)
        ? ok('streamed later turn sends only the new message')
        : bad(`stream ${streamed.status} text=${JSON.stringify(text)} ${streamed.body.slice(0, 300)}`);
    }
  }

  if (opt.npm) {
    const runtimeRoot = path.join(os.homedir(), '.browser-coding-agent', 'dotnet');
    const runtimes = fs.existsSync(runtimeRoot) ? fs.readdirSync(runtimeRoot).filter(d => !d.includes('.tmp-')) : [];
    runtimes.length ? ok(`private runtime at ${path.join(runtimeRoot, runtimes[0])}`) : bad(`no private runtime under ${runtimeRoot}`);
  }

  // Ctrl+C reaches the whole foreground process group; emulate that on POSIX
  killTree(app, 'SIGINT');
  let closed = false;
  for (let i = 0; i < 30 && !closed; i++) {
    closed = (await get(`${base}/health/live`, { timeoutMs: 1000 })).status === 0;
    if (!closed) await sleep(500);
  }
  closed ? ok(`port released after ${win ? 'taskkill /T' : 'SIGINT'}`) : bad('app still answering 15s after shutdown signal');
} finally {
  killTree(app, 'SIGKILL');
  fake.close();
  fs.closeSync(log);
  if (failures) console.log(`--- app log (last 60 lines) ---\n${fs.readFileSync(logPath, 'utf8').split('\n').slice(-60).join('\n')}`);
  if (process.env.SMOKE_LOG_DIR) fs.mkdirSync(process.env.SMOKE_LOG_DIR, { recursive: true });
  if (process.env.SMOKE_LOG_DIR) fs.cpSync(logPath, path.join(process.env.SMOKE_LOG_DIR, `${opt.npm ? 'npm' : 'tool'}-${Date.now()}.log`));
  fs.rmSync(work, { recursive: true, force: true, maxRetries: 5 });
}

console.log(failures ? `${failures} check(s) failed` : 'all checks passed');
process.exit(failures ? 1 : 0);
