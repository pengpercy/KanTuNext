#!/usr/bin/env bash
# Renders the icon masters and derives every platform's icon from them (needs figcraft-cli, sips, python3).
#   build/resources/icon/kantunext-macos.svg -> kantunext-macos-1024.png  (macOS: 824px body, transparent margin)
#   build/resources/icon/kantunext.svg       -> kantunext-1024.png        (Windows/Linux: full-bleed body)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
I="$ROOT/build/resources/icon"
A="$ROOT/src/KanTuNext.Desktop/Assets"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
for name in kantunext kantunext-macos; do
    printf '{"command":"file.import","params":{"path":"%s/%s.svg","x":0,"y":0}}\n{"command":"file.export","params":{"ids":[{"$step":0}],"format":"png","path":"%s/%s-1024.png"}}\n' \
        "$I" "$name" "$I" "$name" > "$TMP/$name.jsonl"
    figcraft-cli run "$TMP/$name.jsonl" >/dev/null
done
sips -z 256 256 "$I/kantunext-1024.png" --out "$A/kantunext.png" >/dev/null
for s in 16 24 32 48 64 128 256; do sips -z $s $s "$I/kantunext-1024.png" --out "$TMP/$s.png" >/dev/null; done
python3 -I "$ROOT/tools/make_ico.py" "$A/kantunext.ico" "$TMP"/{16,24,32,48,64,128,256}.png
echo "icons updated"
