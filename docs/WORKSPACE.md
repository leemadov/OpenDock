# Workspace feature preview

Open **Desktop Scenes & Dock Studio** from the Windows-logo menu or the dock's context menu. It is also under System Settings → General. The working capture-based glass remains the default; this preview does not bring back the withdrawn compositor implementation.

## Desktop Scenes

Name a scene and choose **Save Current Desktop**. It records visible, non-minimized external app windows, their monitor and relative work-area bounds, Finder folders, shared pins, independent monitor pins and the current dock theme. **Restore** reuses matching windows and opens available apps. Missing displays fall back to the primary display and positions are clamped into its work area. The result reports how many windows were restored.

Scenes do not save documents, unsaved content, browser tabs, app credentials or arbitrary command-line arguments. Apps control their own session restoration. Multiple windows of a singleton app may need opening manually. Unavailable apps are skipped; other running apps are never closed to restore a scene.

## Shelf

The purple tray icon opens the Shelf. Drop files or text onto its window, drop files onto its dock icon, or choose **Add Clipboard** for files, text, links and clipboard images. Select several entries and drag them together into another app, or choose **Copy Selected**. Remove unlinks an entry without deleting the original file. Text entries support 16,000 characters; the Shelf retains up to 200 entries within a bounded text budget. Clipboard images are copied into OpenDock's local data folder.

To hide its dock icon, right-click Shelf → **Remove from Dock**, or drag it away from the dock. This applies to every monitor, survives restarts, and keeps collected items. Restore it with **Show Shelf in Dock** under Desktop Scenes & Dock Studio. Shelf remains accessible from the logo menu and Spotlight `>open shelf` while its icon is hidden.

## Desktop commands

Press **Alt+Space** and type `>` to browse commands. Examples:

- `>tile window left`, `>tile window right`
- `>move window to next monitor`
- `>open shelf`, `>copy clipboard to shelf`
- `>scene:` followed by a saved scene name
- `>start 5 minute timer`

Window actions apply to the window from which Spotlight was opened. They use local Windows APIs; no AI service or external account is required.

## Independent monitors

Under **Monitor Roles**, choose a role and **Use Independent Pins**. This copies the shared pins initially. Pin, remove or reorder apps on that dock to customize it; other monitors keep their own pins. **Use Shared Pins** returns it to the common set. Role labels describe the intended use; they do not automatically guess which apps belong there. Sizes, materials and overall dock edge remain theme settings shared across displays.

## Living icons and window-specific drops

Shelf shows its item count, apps show multiple-window counts, Downloads shows incomplete `.crdownload`, `.part`, `.download` and `.partial` files, and Launchpad displays an optional countdown. Pause/resume controls are in Dock Studio. These are actual local counts and timers, not unread-message or universal media-status indicators.

Dropping files onto an app with several windows opens a destination picker. **Send files to** uses the standard Windows file-drop message when the window accepts it. **Activate & copy for** activates the selected window and stores the files in Shelf and the clipboard; paste or drag them into apps that use other drop protocols. OpenDock does not automatically paste files or submit uploads.

## Finder tabs and split view

Use **+** or **Ctrl+T** for another folder tab, **Ctrl+W** to close the active tab, and **Split** or **Ctrl+Shift+S** for an independent second pane. Each pane has its own path, history, search and selection. Use existing copy/paste or drag/drop between panes. Windows' file-operation dialogs provide transfer progress and collision decisions. Split view expands the window when monitor space permits.

## Dock Studio and updates

Size, spacing, appearance and placement previews stay in the settings panel until **Apply Preview**. Save named themes to switch layouts, magnification, hiding, icon-tile style and original-live/wallpaper glass modes. Choose/reset managed custom icon artwork for built-in and pinned apps; original source images remain untouched.

**Software Update** checks this project's GitHub releases only when requested, supports stable/preview filtering, and validates release URL, size, SHA-256 and PE header before staging. A helper waits for OpenDock to exit, atomically replaces its executable, retains a backup and restarts. Immediate startup failures restore the backup; **Restore Previous Build** handles later rollback. It does not change startup paths or delete settings. It is not a code-signing or full UI-health guarantee.

Workspace data is local in `%APPDATA%\OpenDock\workspace.json`; settings, workspace writes and executable replacements retain previous copies. Scene names and window titles may contain personal information; no workspace data is included in source archives or sent to GitHub.
