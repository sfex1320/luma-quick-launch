import { execFileSync } from 'node:child_process';
import path from 'node:path';

// A different LUMA_DATA_DIRECTORY does not isolate the executable-scoped shutdown event.
// Refuse before tests launch or send commands to a path with an existing process.
export function assertExecutableIdle(executablePath) {
  const exe = path.resolve(executablePath);
  const script = `
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$target = [System.IO.Path]::GetFullPath($env:LUMA_GUARD_EXECUTABLE)
$name = [System.IO.Path]::GetFileName($target)
$matches = @(Get-CimInstance Win32_Process | Where-Object {
  $_.Name -ieq $name -and (
    [string]::IsNullOrWhiteSpace($_.ExecutablePath) -or
    [System.IO.Path]::GetFullPath($_.ExecutablePath) -ieq $target
  )
} | Select-Object ProcessId, ExecutablePath)
ConvertTo-Json -InputObject $matches -Compress
`;
  // Fail closed on enumeration errors, timeout, unreadable same-name paths, or malformed output.
  const matches = JSON.parse(execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
    env: { ...process.env, LUMA_GUARD_EXECUTABLE: exe }, encoding: 'utf8', windowsHide: true, timeout: 10000,
  }));
  if (!Array.isArray(matches)) throw new Error('Could not confirm executable is idle. Test refused.');
  if (matches.length) throw new Error(`Executable already running (or its same-name process path cannot be inspected); test refused before sending commands: ${exe}. PID(s): ${matches.map(process => process.ProcessId).join(', ')}`);
}
