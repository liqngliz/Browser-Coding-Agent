#!/usr/bin/env node
// Stages and packs the npm package, then asserts the tarball contents.
//   node scripts/package-npm.mjs --version 1.2.3 [--out artifacts/npm]
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { parseArgs } from 'node:util';

const { values } = parseArgs({ options: { version: { type: 'string' }, out: { type: 'string', default: 'artifacts/npm' } } });
if (!values.version) throw new Error('--version is required');

const repo = path.resolve(import.meta.dirname, '..');
const stage = path.join(repo, 'artifacts', 'npm-stage');
const out = path.resolve(repo, values.out);
const win = process.platform === 'win32';

fs.rmSync(stage, { recursive: true, force: true });
fs.mkdirSync(out, { recursive: true });
fs.cpSync(path.join(repo, 'npm', 'browser-coding-agent'), stage, { recursive: true });
for (const f of ['README.md', 'LICENSE']) fs.copyFileSync(path.join(repo, f), path.join(stage, f));

// UseAppHost=false: an apphost resolves its framework from DOTNET_ROOT or a system install, the private runtime is in neither
execFileSync('dotnet', ['publish', path.join(repo, 'src', 'BrowserCodingAgent.Api'), '-c', 'Release',
  '--self-contained', 'false', '-p:UseAppHost=false', '-p:PlaywrightPlatform=all',
  `-p:Version=${values.version}`, '-o', path.join(stage, 'app')], { stdio: 'inherit' });

const pkgPath = path.join(stage, 'package.json');
const pkg = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
pkg.version = values.version;
fs.writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n');

const npm = win ? 'npm.cmd' : 'npm';
const [report] = JSON.parse(execFileSync(npm, ['pack', '--json', '--pack-destination', out],
  { cwd: stage, encoding: 'utf8', shell: win }));

const files = new Map(report.files.map(f => [f.path.replaceAll('\\', '/'), f]));
const required = [
  'package.json', 'browser-coding-agent.cjs', 'README.md', 'LICENSE',
  'app/BrowserCodingAgent.Api.dll', 'app/BrowserCodingAgent.Api.deps.json',
  'app/BrowserCodingAgent.Api.runtimeconfig.json', 'app/appsettings.json', 'app/replacements.json', 'app/Microsoft.Playwright.dll',
  'app/.playwright/package/cli.js', 'app/.playwright/node/LICENSE',
  'app/.playwright/node/darwin-arm64/node', 'app/.playwright/node/darwin-x64/node', 'app/.playwright/node/win32_x64/node.exe',
];
const executable = ['browser-coding-agent.cjs', 'app/.playwright/node/darwin-arm64/node', 'app/.playwright/node/darwin-x64/node'];
const forbidden = /(^|\/)(profiles|UserData|Default\/Cookies)(\/|$)|(^|\/)\.env(\.|$)|\.nupkg$/;

const problems = [
  ...required.filter(p => !files.has(p)).map(p => `missing ${p}`),
  ...(win ? [] : executable.filter(p => files.has(p) && !(files.get(p).mode & 0o111)).map(p => `not executable: ${p}`)),
  ...[...files.keys()].filter(p => forbidden.test(p)).map(p => `forbidden ${p}`),
];
if (pkg.version !== report.version) problems.push(`packed version ${report.version} != ${pkg.version}`);

const tgz = path.join(out, report.filename);
const mb = n => (n / 1024 / 1024).toFixed(1);
console.log(`${tgz}: ${files.size} files, ${mb(report.size)} MB packed, ${mb(report.unpackedSize)} MB unpacked, ${report.integrity}`);
if (problems.length) {
  console.error(problems.map(p => `  FAIL ${p}`).join('\n'));
  process.exit(1);
}
