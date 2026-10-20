#!/usr/bin/env node
// Runs the framework-dependent app in ./app on a private ASP.NET Core runtime.
// Runtime installs on first run (not postinstall: npm hides lifecycle output) into
// ~/.browser-coding-agent/dotnet/<version>-<platform>-<arch>, separate from browser profiles.
'use strict';
const { execFileSync, spawn } = require('child_process');
const crypto = require('crypto'), fs = require('fs'), os = require('os'), path = require('path');

const RUNTIME_VERSION = '10.0.13';  // tested ASP.NET Core patch; bump per release
const SUPPORTED = ['darwin-arm64', 'darwin-x64', 'win32-x64'];

const win = process.platform === 'win32';
const target = `${process.platform}-${process.arch}`;
const appDll = path.join(__dirname, 'app', 'BrowserCodingAgent.Api.dll');
const runtimeRoot = path.join(os.homedir(), '.browser-coding-agent', 'dotnet');
const runtimeDir = path.join(runtimeRoot, `${RUNTIME_VERSION}-${target}`);
const muxer = path.join(runtimeDir, win ? 'dotnet.exe' : 'dotnet');

function fail(message) {
  console.error(`browser-coding-agent: ${message}`);
  process.exit(1);
}

function runtimeUsable(dir) {
  return fs.existsSync(path.join(dir, win ? 'dotnet.exe' : 'dotnet')) &&
         fs.existsSync(path.join(dir, 'shared', 'Microsoft.AspNetCore.App', RUNTIME_VERSION));
}

async function installRuntime() {
  console.error(`browser-coding-agent: first run, installing ASP.NET Core ${RUNTIME_VERSION} runtime into ${runtimeDir}`);
  fs.mkdirSync(runtimeRoot, { recursive: true });

  // Unique staging dir + rename: an interrupted or concurrent install never leaves a half-valid runtimeDir
  const staging = `${runtimeDir}.tmp-${process.pid}-${crypto.randomBytes(4).toString('hex')}`;
  const scriptName = win ? 'dotnet-install.ps1' : 'dotnet-install.sh';
  const script = path.join(os.tmpdir(), `${path.basename(staging)}-${scriptName}`);

  try {
    const res = await fetch(`https://dot.net/v1/${scriptName}`);
    if (!res.ok) throw new Error(`download of ${scriptName} failed: HTTP ${res.status}`);
    fs.writeFileSync(script, Buffer.from(await res.arrayBuffer()), { mode: 0o755 });

    const args = win
      ? ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script,
         '-Runtime', 'aspnetcore', '-Version', RUNTIME_VERSION, '-InstallDir', staging, '-NoPath']
      : [script, '--runtime', 'aspnetcore', '--version', RUNTIME_VERSION, '--install-dir', staging, '--no-path'];
    execFileSync(win ? 'powershell' : 'bash', args, { stdio: ['ignore', 'inherit', 'inherit'] });

    if (!runtimeUsable(staging))
      throw new Error(`installer finished but Microsoft.AspNetCore.App ${RUNTIME_VERSION} is missing from ${staging}`);

    try {
      fs.renameSync(staging, runtimeDir);
    } catch (err) {
      if (!runtimeUsable(runtimeDir)) throw err;  // another launcher won the race
    }
  } finally {
    fs.rmSync(staging, { recursive: true, force: true });
    fs.rmSync(script, { force: true });
  }
  console.error('browser-coding-agent: runtime ready\n');
}

async function main() {
  if (!SUPPORTED.includes(target) && process.env.BROWSER_CODING_AGENT_ALLOW_UNSUPPORTED !== '1')
    fail(`unsupported platform ${target}. Supported: ${SUPPORTED.join(', ')}.`);
  if (!fs.existsSync(appDll))
    fail(`application not found at ${appDll}; the package is incomplete.`);

  if (!runtimeUsable(runtimeDir)) {
    try {
      await installRuntime();
    } catch (err) {
      fail(`runtime install failed: ${err.message}\n` +
           (win ? 'Requires PowerShell and network access to dot.net.'
                : 'Requires bash, curl or wget, and network access to dot.net.'));
    }
  }

  const child = spawn(muxer, [appDll, ...process.argv.slice(2)], { stdio: 'inherit' });
  const signals = ['SIGINT', 'SIGTERM', 'SIGHUP'];
  const forward = sig => { if (child.exitCode === null) child.kill(sig); };
  for (const sig of signals) process.on(sig, forward);

  child.on('error', err => fail(`failed to start ${muxer}: ${err.message}`));
  child.on('exit', (code, signal) => {
    for (const sig of signals) process.removeListener(sig, forward);
    if (signal) process.kill(process.pid, signal);  // re-raise so the caller sees the same termination
    else process.exit(code ?? 1);
  });
}

main();
