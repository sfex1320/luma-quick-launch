// Actual Windows host lifecycle; no bridge or shell mocks. Set LUMA_TEST_EXE to a built host.
import { spawn, execFileSync } from 'node:child_process';
import { mkdtemp, access, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';
import { assertExecutableIdle } from './assert-executable-idle.mjs';

const root = path.resolve(import.meta.dirname, '../..');
const exe = process.env.LUMA_TEST_EXE || path.join(root, 'APP/native/Luma/Luma.exe');
assertExecutableIdle(exe);
const data = await mkdtemp(path.join(tmpdir(), 'luma-lifecycle-'));
const env = { ...process.env, LUMA_DATA_DIRECTORY: data };
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
const start = args => spawn(exe, args, { env, windowsHide: true, stdio: 'ignore' });
const exit = child => new Promise((resolve, reject) => {
  if (child.exitCode !== null) return resolve(child.exitCode);
  const timer = setTimeout(() => reject(new Error(`Process ${child.pid} did not exit`)), 15000);
  child.once('exit', code => { clearTimeout(timer); resolve(code); });
  child.once('error', error => { clearTimeout(timer); reject(error); });
});
const windows = child => JSON.parse(execFileSync('powershell.exe', [
  '-NoProfile', '-File', path.join(root, 'native/tests/Inspect-NativeWindow.ps1'),
  '-TargetProcessId', String(child.pid),
], { encoding: 'utf8', windowsHide: true }));
const checks = [];
let host;
try {
  assert.equal(await exit(start(['--shutdown', '--settings', '--show-dock'])), 0);
  await assert.rejects(access(path.join(data, 'state.json')));
  checks.push('No-instance --shutdown exits without creating application state');
  host = start(['--startup']);
  let snapshot;
  for (let i = 0; i < 40; i++) {
    await wait(500);
    assert.equal(host.exitCode, null);
    snapshot = windows(host);
    if (snapshot.some(window => window.Title === 'Luma Hotzone')) break;
  }
  assert(snapshot.some(window => window.Title === 'Luma Hotzone'), 'Real host hotzone initialized');
  // WPF preloads WebView in a shown HWND whose native region is empty (NULLREGION=1).
  assert(!snapshot.some(window => window.Title === 'Luma Dock' && window.Visible && window.RegionType !== 1));
  assert(!snapshot.some(window => window.Title.includes('设置') && window.Visible));
  checks.push('First --startup initializes resident host without showing dock or settings');
  assert.equal(await exit(start(['--startup'])), 0);
  await wait(700);
  assert.equal(host.exitCode, null);
  assert(!windows(host).some(window => window.Title === 'Luma Dock' && window.Visible && window.RegionType !== 1));
  checks.push('Second --startup exits without activating existing dock');
  const hostExit = exit(host);
  assert.equal(await exit(start(['--shutdown'])), 0);
  assert.equal(await hostExit, 0);
  assert.equal(await exit(start(['--shutdown'])), 0);
  checks.push('Exact executable --shutdown gracefully exits host and repeated command is no-op');
  const result = { result: 'passed', date: new Date().toISOString(), exe, data, checks };
  await writeFile(path.join(data, 'lifecycle-result.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
} finally {
  if (host && host.exitCode === null) host.kill();
}
