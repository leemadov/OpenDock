# Testing

Run `build.bat check` for app-filtering and colour-profile regressions plus:

- Dock coordinate round trips and popup bounds at 100%, 125%, 150% and 200% system scale, including monitor origins left of and above the primary display.
- Left/right edge mapping and fitting a crowded dock with magnification.
- Original live-glass default settings.
- File and directory renames, case-only renames, invalid/reserved names, destination collisions and preservation of file contents.

`build.bat check --ui` adds integration checks on the current Windows desktop. It briefly opens test windows, reserves/releases appbar space, and creates fixtures and screenshots inside `bin/qa/`. It does not load or save user preferences, hide the Windows taskbar, change startup entries or colour profiles, or register global shortcuts/window hooks. It checks:

- Bottom/left/right placements on every physically connected display with auto-hide on/off, native HWND bounds, reserved work areas and fallback for a disconnected display.
- Shared pin insertion/removal/order, running-app indicators on every dock, and Finder/previews/Downloads on the active dock's display.
- Repeated all-display/single-display transitions, idempotent reconciliation, and cleanup of removed docks' timers, rendering and subscriptions.
- Capture affinity on dock and Finder windows after switching Live glass off.
- Actual Finder list rows, selection across icon/list switches, and light/dark rendering.
- The rename dialog and its operation on a generated fixture file.
- Desktop & Dock settings and snapshots of each dock edge.

`build.bat check --preview` runs those checks and then leaves an isolated controller open. Close **OpenDock checks** to release appbar space and exit. Its position/view buttons change only in-memory test settings.

The desktop checks were run with two 1920×1080 displays at origins (0,0) and (1920,0). Other hardware layouts still need the following checks:

1. Select each display in Desktop & Dock. Check dock/menu placement, maximized-window bounds, Launchpad, Spotlight, Finder, previews and Downloads.
2. Arrange a display to the left/above the primary; use unequal resolutions and Windows scaling factors.
3. Disconnect/reconnect the selected display, change the primary display, and restart Explorer. Check fallback, restoration and appbar space on both screens.
4. On every edge, test magnification, drag to reorder/remove, file drops, running-app drag-hover and edge auto-hide. Open a full-screen app on each screen and check that only the selected screen's full-screen app hides the dock/menu bar.
5. Move an app window behind the dock and verify the original live blur shows that app. Live glass can hide sampling windows from screenshots and recordings. Turn it off and verify the dock, Finder, Settings and menus are visible in capture with wallpaper frost.
6. Rename from the context menu and F2; cancel, submit an invalid/existing name, and try a case-only rename. Verify multi-selection, copy/paste and drag/drop in both Finder views.

The app creates a dock on every display by default, with an option for one selected dock. The menu bar stays on the selected display. Per-monitor DPI awareness remains outside this change.

After installing, `bin\checks.exe --inspect <OpenDock process ID>` verifies that the actual running process has a visible dock HWND inside each monitor's bounds.
