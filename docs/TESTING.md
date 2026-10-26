# Testing

`build.bat check` runs the app-filtering and colour-profile checks plus:

- Coordinate round trips and popup bounds at 100%, 125%, 150% and 200% scale, including negative monitor origins, crowded side docks, and maximized versus borderless fullscreen detection.
- File and directory renames, case-only renames, invalid/reserved names, destination collisions and preservation of contents.
- Release ordering, preview/stable channels, draft/downgrade rejection, asset origin and digest validation, PE checks and non-destructive staging.
- Custom icon normalization, image bounds, aspect ratio, source preservation and encoded mapping validation.

`build.bat check --ui` opens isolated integration windows and creates fixtures and screenshots in `bin/qa/`. It does not load or save user preferences, hide the Windows taskbar, change startup entries or colour profiles, or register global shortcuts/window hooks. It checks:

- Per-monitor-v2 process awareness, bottom/left/right docks on every connected display, auto-hide on/off, physical HWND bounds and actual reserved work areas.
- Floating docks without appbar registration; temporary empty display snapshots, monitor removal/restoration, single/all-display transitions and idempotent reconciliation.
- Shared pins/running apps, custom icon loading/reset on all displays, active-display Finder/previews/Downloads, and cleanup of removed dock timers/rendering/subscriptions.
- Native glass panes on both monitors, changing red/blue windows behind glass, actual blur across a colour boundary, icon z-order after hide/show, and capture affinity of zero. Finder/Settings glass is also checked against changing background windows. These use screen pixels, not just offscreen WPF renders.
- Finder numeric size sorting, folder ordering, icon/list selection, inline rename commit/cancel and preserved fixture contents, plus light/dark UI snapshots and the Software Update page.
- The real update helper against isolated executables in paths containing spaces, apostrophes and brackets: incorrect checksum leaves the original intact, successful update retains the old executable, restart succeeds, and rollback preserves both builds. No installed app or preferences are used by this helper check.

`build.bat check --glass` runs only the native composition test. `build.bat check --handoff` runs only the isolated updater replacement/restart/rollback test. `build.bat check --preview` keeps the UI test windows open after passing; close **OpenDock checks** to release appbar space and exit.

The desktop checks were run on Windows 11 with two 1920×1080 displays at (0,0) and (1920,0), both at 100% scale. Numeric checks cover multiple scales and negative origins; physical mixed-DPI and disconnect/sleep hardware scenarios still require manual checks:

1. Use unequal resolutions/scales and a display left of/above the primary. Move Finder and Settings between monitors. Check cursor alignment, icon size, menus, previews, Launchpad, Spotlight and Downloads.
2. Disconnect/reconnect displays, change the primary, resume from sleep, and restart Explorer. Verify one dock per connected display and correct work areas after restoration.
3. Test magnification, pin reordering/removal, file drops, running-app drag-hover and auto-hide on every edge. A fullscreen app should hide only the dock on its own monitor; ordinary maximized windows should leave a floating dock visible.
4. Move windows/video beneath glass and take a screenshot and recording. Default live glass must remain visible; only the explicitly enabled compatibility capture mode excludes sampling windows. With reserved space enabled, maximized windows stop above the dock, so wallpaper is actually behind that area.
5. Rename with F2/context menu in both views, Enter/Escape, invalid/existing names and case-only names. Check selection, copy/paste and drag/drop.
6. Choose/reset custom artwork, restart and check both docks. Check updates over a real network, install a newer release and restore the prior executable while preserving settings.

After installation, `bin\checks.exe --inspect <OpenDock process ID>` independently checks visible dock HWNDs inside each monitor and native glass pane visibility/capture affinity.
