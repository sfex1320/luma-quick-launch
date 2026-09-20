# Native window usability implementation — 2026-09-19

## Scope and result

- `App` and `DockVisibilityState` implement Hidden → AwaitingExpanded → Visible. Queued collapsed layout messages cannot cancel a reveal while awaiting the first expanded acknowledgement; native collapse checks start after that acknowledgement. Hidden clients cannot reveal themselves by sending layout alone.
- Dock WebView messages use one async gate covering devicePixelRatio query and router handling, preserving received ordering.
- Every enumerated monitor receives its own native hotspot. A single dock is moved to the activated monitor using physical desktop coordinates and `SetWindowPos`; negative origins are never reconstructed from mixed-DPI WPF origins. Hovering another screen can switch the active dock.
- Fullscreen detection is scoped to the foreground window's monitor and excludes Progman, WorkerW and taskbar windows. Captioned maximized applications are not treated as fullscreen. Hotspots on other monitors remain available.
- DWM system backdrop remains disabled on the carrier HWND. The region is the union of panel rectangles with a bounded shadow halo (28 CSS px left/top/right, 36 bottom), plus existing connecting channels; it is never the full carrier rectangle.
- Independent `SearchWindow` uses shared WebView2 environment with its own client (`search`), URL `https://luma.local/?view=search&mode=native`, focus and lifecycle. `OpenSettings("search")` opens this window without navigating settings. `App.CloseSearch()` closes it. Ctrl+Alt+Space is registered through a message-only HWND, and tray search / second-instance `--search` are wired.
- All three WebViews pass WebView2 native file AdditionalObjects to the router. Settings and search disable status-bar URL overlays.

## Validation

Command, from `native/`:

```powershell
& 'C:\Users\96311\AppData\Local\Microsoft\dotnet\dotnet.exe' test tests/Luma.Host.Tests/Luma.Host.Tests.csproj --no-restore --verbosity minimal
```

Latest output: **94 passed, 0 failed, 0 skipped; 3 seconds** (net10.0-windows).

The new `WindowLifecycleTests` cases cover stale collapsed reports during reveal, repeated reveal/hide acknowledgements, negative-origin mixed-DPI hotspot geometry, shell-desktop/maximized-window fullscreen classification, and bounded shadow geometry.

## Integration boundaries

This task did not publish or operate the desktop. Real dual-monitor hover cycles, independent search activation, global hotkey conflict handling, WebView2 focus/Esc, visible shadow compositing, and mixed-DPI window movement still require the parent task's published-package integration run. Unit tests are not evidence of those Windows integration outcomes.

Hotkey registration failure is logged (e.g. another application owns Ctrl+Alt+Space); tray and `--search` remain available. Fullscreen suppression intentionally follows the current foreground application, not hidden/background fullscreen windows. The shadow halo is a small extra native hit area around panels, rather than a separate click-through shadow HWND. Published window-region checks must account for the documented halo instead of expecting the exact CSS border rectangle.

## Published cold-start regression follow-up

Parent integration reproduced an immediate cold-start host crash: `ArgumentNullException` from `Enumerable.OfType`, `DockWindow.cs:143`. WebView2 returns **null** AdditionalObjects for ordinary `postMessage` on the tested runtime. The prior unit suite did not exercise this real COM event payload and therefore did not establish cold-start safety.

Fixed all three WebView message handlers to call the shared null-safe `WebMessageFiles.ExtractPaths`. Source, JSON and native-file extraction now occur inside exception handling; Settings/Search async handlers await routing and log errors instead of leaking async-void exceptions. Dock releases its async gate only after successful acquisition. Added a regression asserting null, empty, and non-file objects produce no native paths.

Also snapshot AwaitingExpanded **before** `ApplySync`: a synchronous LayoutReady callback can request reveal, but the initial sync frame that caused that request must not acknowledge it.

Focused verification after these edits:
`dotnet test tests/Luma.Host.Tests/Luma.Host.Tests.csproj --no-restore --filter FullyQualifiedName~WindowLifecycleTests --verbosity minimal`
Result: **11 passed, 0 failed, 0 skipped; 54 ms**. Published cold-start and hover/search integration are being rerun by the parent task; this focused test result does not replace them.

## First-hover diagnostics and relocation race

Parent smoke evidence `luma-native-smoke-501677f6-ec68-4362-8a18-9dbc1c6aa8eb/logs/host-20260919.log` ended after initial collapsed sync, so it did not establish whether OS mouse input, fullscreen suppression, or reveal handshake caused the missed activation. Added event-only logs for native enter/leave, dwell expiration, reveal requests/suppression, foreground fullscreen target on reveal and fullscreen changes, and native handshake phases. No unchanged one-second poll logging was added.

Code inspection found a concrete race: `RelocateHotspot` stopped dwell and cleared hover while native `_trackingLeave` could remain true on the existing HWND. A cursor still inside would not produce a second enter event. Relocation now performs one immediate hit-test and resumes dwell if the cursor still hits an enabled hotspot. This runs only on relocation events, not continuously.

Focused window tests after diagnostic/relocation changes: **11 passed, 0 failed, 59 ms**. Actual first-hover reproducibility remains the parent integration run's responsibility.

## Direct hotspot click

Native hotspots now expose an `Activated` event on `WM_LBUTTONUP`. EdgeActivation cancels pending dwell and immediately requests the clicked monitor's dock, while honoring pause and re-checking monitor-specific fullscreen suppression. The existing 180 ms hover behavior is unchanged. This is a product interaction, not a test-only activation endpoint.

Focused window tests after this change: **11 passed, 0 failed, 55 ms**. The parent smoke suite can distinguish direct native-hotspot activation from optional strict physical hover tests; success via clicking must not be reported as proof of physical-hover stability under concurrent user cursor movement.
