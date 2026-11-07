#!/bin/bash
# Every shot of the Apple-style cut at 1920 x 1080 into video/renders1080, then the edit.
cd "$(dirname "$0")/../.."
B="/c/Program Files/Blender Foundation/Blender 5.1/blender.exe"
O="$(pwd -W)/video/renders1080"
for d in opening hero3d shot4 shot5 shot6 shot7 shot8 shot9; do mkdir -p "video/renders1080/$d"; done
"$B" -b video/opendock_ad.blend --python video/scripts/render_range.py -- "OpenDock Ad" 21 150 1920 1080 "$O/opening" > video/renders1080/log_opening.txt 2>&1
"$B" -b video/opendock_ad.blend --python video/scripts/render_range.py -- "OpenDock Ad" 241 420 1920 1080 "$O/shot9" > video/renders1080/log_shot9.txt 2>&1
"$B" -b video/opendock_ad.blend --python video/scripts/render_scene.py -- "Shot 6" 1920 1080 "$O/shot6" > video/renders1080/log_shot6.txt 2>&1
"$B" -b video/opendock_ad.blend --python video/scripts/render_range_cycles.py -- "OpenDock Ad" 1000 1180 1920 1080 96 "$O/hero3d" > video/renders1080/log_hero.txt 2>&1
powershell -NoProfile -STA -File video/scripts/dock_render.ps1 -mode frames -outdir "$O/shot4" -count 90 -outWidth 1920 > video/renders1080/log_shot4.txt 2>&1
powershell -NoProfile -STA -File video/scripts/dock_render.ps1 -mode styles -outdir "$O/shot5" -count 90 -outWidth 1920 > video/renders1080/log_shot5.txt 2>&1
powershell -NoProfile -STA -File video/scripts/shot7.ps1 -outdir "$O/shot7" -outW 1920 > video/renders1080/log_shot7.txt 2>&1
powershell -NoProfile -STA -File video/scripts/shot8.ps1 -outdir "$O/shot8" -outW 1920 > video/renders1080/log_shot8.txt 2>&1
python -I video/scripts/assemble_apple.py 1920 1080
for d in opening hero3d shot4 shot5 shot6 shot7 shot8 shot9; do echo "$d $(ls video/renders1080/$d | wc -l)"; done
