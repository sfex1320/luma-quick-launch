// Actual isolated Windows host, silent startup, no focus or physical input manipulation.
import { spawn, execFileSync } from 'node:child_process';
import { mkdtemp, readdir, readFile, writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import assert from 'node:assert/strict';
import { assertExecutableIdle } from './assert-executable-idle.mjs';
const root = path.resolve(import.meta.dirname, '../..');
if (!process.env.LUMA_TEST_EXE) throw new Error('An isolated validation executable is required');
const exe = path.resolve(process.env.LUMA_TEST_EXE);
if (exe.toLowerCase() === path.join(root, 'APP/Luma/Luma.exe').toLowerCase()) throw new Error('Do not manipulate the live user process');
assertExecutableIdle(exe);
const data = await mkdtemp(path.join(tmpdir(), 'luma-activation-diagnostic-'));
const host = spawn(exe, ['--startup'], { env: { ...process.env, LUMA_DATA_DIRECTORY: data }, windowsHide: true, stdio: 'ignore' });
const evidence = { result: 'pending', exe, data, checks: [] };
const wait = ms => new Promise(r => setTimeout(r, ms));
const inspect = () => JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-File', path.join(root, 'native/tests/Inspect-NativeWindow.ps1'), '-TargetProcessId', String(host.pid)], { encoding: 'utf8', windowsHide: true }));
const readLog = async () => {
  const logs = await readdir(path.join(data, 'logs'));
  return (await Promise.all(logs.filter(n => n.startsWith('host-')).map(n => readFile(path.join(data, 'logs', n), 'utf8')))).join('\n');
};
try {
  let snapshot;
  for (let i = 0; i < 30; i++) {
    await wait(500);
    assert.equal(host.exitCode, null);
    snapshot = inspect();
    if (snapshot.some(w => w.Title === 'Luma Hotzone') && (await readLog()).includes('phase=Hidden expanded=False')) break;
  }
  const hotspots = snapshot.filter(w => w.Title === 'Luma Hotzone' && w.Visible);
  assert(hotspots.length > 0, 'At least one non-fullscreen hotspot available');
  assert(!snapshot.some(w => w.Title === 'Luma Dock' && w.Visible && w.RegionType !== 1));
  evidence.checks.push('Silent isolated startup keeps the real native dock hidden');
  for (const zone of hotspots) {
    const before = (await readLog()).length;
    execFileSync('powershell.exe', ['-NoProfile', '-File', path.join(root, 'native/tests/Invoke-HotspotMoveMessage.ps1'), '-TargetProcessId', String(host.pid), '-WindowHandle', String(zone.Hwnd)], { windowsHide: true });
    let delta = '';
    for (let i = 0; i < 20; i++) {
      await wait(100); delta = (await readLog()).slice(before);
      if (delta.includes('驻留判定')) break;
    }
    assert.match(delta, /WM_MOUSEMOVE enter.*client=2,2.*inputSource=/);
    assert.match(delta, /驻留判定 allowed=False.*sampledCursor=.*expectedRect=.*physical=.*inputAgeMs=.*actualRect=/);
    assert(!delta.includes('浮岛唤出请求'), 'A move message outside the physical hotspot cannot reveal');
    assert(!inspect().some(w => w.Title === 'Luma Dock' && w.Visible && w.RegionType !== 1));
  }
  evidence.checks.push(`Synthetic enter rejected on ${hotspots.length} real hotspot HWNDs with physical pointer outside; no reveal`);
  evidence.checks.push('Entry and dwell record actual coordinates, bounds, input origin, idle age and decision');
  evidence.log = await readLog(); evidence.result = 'passed';
} catch (error) { evidence.error = String(error); throw error; }
finally {
  if (host.exitCode === null) host.kill();
  const output = path.join(root, 'docs/evidence/twentysecond-investigation');
  await mkdir(output, { recursive: true });
  await writeFile(path.join(output, 'activation-native.json'), JSON.stringify(evidence, null, 2));
  console.log(JSON.stringify(evidence, null, 2));
}
