import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { once } from 'node:events';
import path from 'node:path';
import { assertExecutableIdle } from './assert-executable-idle.mjs';

test('refuses an active exact executable before lifecycle shutdown and preserves its process', async () => {
  const exe = path.join(process.env.SystemRoot, 'System32', 'ping.exe');
  const fixture = spawn(exe, ['-t', '127.0.0.1'], { windowsHide: true, stdio: 'ignore' });
  await once(fixture, 'spawn');
  try {
    assert.throws(() => assertExecutableIdle(exe), /already running/i);
    assert.throws(() => assertExecutableIdle(exe.toUpperCase()), /already running/i);
    const result = spawnSync(process.execPath, [path.join(import.meta.dirname, 'native-lifecycle-smoke.mjs')], {
      env: { ...process.env, LUMA_TEST_EXE: exe }, encoding: 'utf8', windowsHide: true, timeout: 15000,
    });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /already running/i);
    assert.equal(fixture.exitCode, null);
    assert.doesNotThrow(() => process.kill(fixture.pid, 0));
    // Same executable name at another full path must not match this running fixture.
    assert.doesNotThrow(() => assertExecutableIdle(path.join(process.env.TEMP, 'luma-idle-fixture', 'ping.exe')));
  } finally {
    const ended = once(fixture, 'exit');
    fixture.kill(); // Only the PID spawned by this test; never another user's matching process.
    await ended;
  }
});
