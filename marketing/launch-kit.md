# GlassDock launch kit

Ready-to-paste posts for sharing GlassDock. Link: https://github.com/leemadov/OpenDock
Attach the ad (`video/renders/GlassDock_ad_1080p.mp4`) or a short GIF of it wherever video is allowed: it does most of the work.

## The pitch

**One line:** A free, open-source Mac-style desktop for Windows: Liquid Glass Dock, Finder, Control Center, Stage Manager and more.

**Short (about 50 words):**
GlassDock turns Windows 10 and 11 into a Mac-style desktop. A Liquid Glass Dock that really bends the wallpaper behind it, a menu bar, Finder, Spotlight, Control Center, Notification Center with widgets, Stage Manager, drag-to-tile, Quick Look and Mac screenshots. Free, open source (MIT), one small .exe, no installer.

**What makes it different** (lead with these, not the feature list):
1. The glass is real: the Dock and panels refract the wallpaper behind them like macOS 26, not a flat blur.
2. It's a whole desktop, not just a dock: Finder, Control Center, Stage Manager, Spotlight, widgets.
3. Free and open source, under 1 MB, with self-updates from GitHub.

## Show HN (Hacker News)

**Title:** Show HN: GlassDock – a Mac-style desktop for Windows, with real refracting glass

**Text:**
I wanted the macOS 26 look on my Windows PC without a pile of paid tools, so I built GlassDock: a Dock, menu bar, Finder, Spotlight, Control Center, Stage Manager and widgets in one small C#/WPF app (.NET Framework 4.8, no installer).

The part I'm proudest of is the glass. Instead of a blur, it captures what's behind each panel and bends it at the rim, so the wallpaper actually refracts around the Dock and each icon. Live glass windows exclude themselves from screen capture to avoid feedback.

Other bits: drag a window to a screen edge to tile it, Quick Look with Space in Finder, Ctrl+Shift+3/4/5 screenshots, a lock screen unlocked with Windows Hello, and Settings search that jumps to any option.

It's MIT-licensed. Happy to answer questions about the rendering or the Windows APIs it leans on.

## Reddit

Read each subreddit's self-promotion rules first; some want a flair, some only allow it on certain days. Reply to comments, since that matters more than the post.

**r/Windows11 / r/Windows10 — title:** I made a free, open-source Mac-style desktop for Windows (real Liquid Glass Dock, Finder, Stage Manager)

**r/desktops / r/unixporn-style showcase — title:** [Windows 11] macOS 26 Tahoe look with GlassDock: Liquid Glass Dock, Finder, Control Center

**Body:**
Hi! I've been building GlassDock, a free and open-source Mac-style desktop for Windows 10/11.

- A Dock with Liquid Glass that actually bends the wallpaper behind it (not just a blur)
- Menu bar, Finder with color tags, Spotlight, Launchpad
- Control Center, Notification Center with widgets, a Dynamic Island
- Stage Manager, drag windows to edges to tile, Quick Look (Space), Mac screenshots
- One small .exe, MIT-licensed, updates itself from GitHub

Video and download: https://github.com/leemadov/OpenDock
Feedback very welcome, especially bugs on multi-monitor and high-DPI setups.

**r/opensource / r/csharp — angle:** the tech (WPF, DWM thumbnails, the refraction shader done on the CPU, appbars). Lead with what you learned, link the repo at the end.

## X / Bluesky / Threads

1/ I built a Mac-style desktop for Windows. It's free and open source. 🧵 [ad video]
2/ The glass is real: the Dock bends the wallpaper behind it, like macOS 26's Liquid Glass. [close-up GIF of the Dock]
3/ It's a whole desktop: Finder, Spotlight, Control Center, Stage Manager, widgets, Quick Look, Mac screenshots.
4/ One small .exe, no installer, MIT license: https://github.com/leemadov/OpenDock

## Product Hunt

- **Name:** GlassDock
- **Tagline (60 chars):** A free Mac-style desktop for Windows, with real glass
- **Description:** GlassDock brings the macOS 26 look to Windows 10 and 11: a Liquid Glass Dock that refracts your wallpaper, a menu bar, Finder, Spotlight, Control Center, Notification Center widgets, Stage Manager, drag-to-tile, Quick Look and Mac-style screenshots. Free, open source, one small download.
- **First comment:** why you built it, what was hard (the glass), what's next. Launch on a Tuesday–Thursday, 12:01 AM Pacific.
- **Gallery:** the ad, then 4 stills: Dock close-up, Finder, Control Center, Stage Manager.

## Directories and press

- **AlternativeTo:** add GlassDock as an alternative to MyDockFinder, RocketDock, ObjectDock, Nexus Dock.
- **GitHub:** add topics (`windows`, `dock`, `macos`, `desktop`, `wpf`, `liquid-glass`, `customization`) and a social preview image (Settings → Social preview). Pin the ad near the top of the README.
- **Tip lines:** Neowin, XDA Developers, Windows Central, gHacks and Softpedia take tips about new free Windows apps. Send the one-line pitch, the video and the link.
- **YouTube:** Windows customization channels often review free tools; a short, polite email with the video works best.

## Watch out for

- Say "Mac-style" or "macOS-inspired". Don't use Apple's logo or the Mac's own icons, and don't call it "macOS for Windows": Apple enforces its marks.
- Windows SmartScreen warns about an unsigned .exe. Mention it up front ("click More info → Run anyway") or consider code signing.
- Have answers ready for "is it safe?" (open source, no telemetry, builds from source with `build.bat`) and "does it slow my PC?" (numbers help).
