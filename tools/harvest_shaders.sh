#!/bin/bash
# Copies the shader cache the game built up on an iPhone/iPad into build/shader-cache, for build.sh to precompile
# into the next build (see tools/bake_shaders.py), and the pipelines it recorded (src/ios/metal_pipelines.m) into
# build/shader-cache/pipelines, for the app to warm up. Run it after playing a while: every effect you've seen is in
# there. Repeated runs add to what's already collected.
#
# Usage: tools/harvest_shaders.sh --bundle-id <id> [--device <udid>] [--out <folder>]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUNDLE_ID=""; DEVICE=""; OUT="$ROOT/build/shader-cache"
while [[ $# -gt 0 ]]; do
	case "$1" in
		--bundle-id) BUNDLE_ID="$2"; shift 2 ;;
		--device) DEVICE="$2"; shift 2 ;;
		--out) OUT="$2"; shift 2 ;;
		*) echo "usage: $0 --bundle-id <id> [--device <udid>] [--out <folder>]" >&2; exit 1 ;;
	esac
done
[[ -n "$BUNDLE_ID" ]] || { echo "pass --bundle-id (the one you built with; see build.sh's output)" >&2; exit 1; }

DC=(env -u DEVELOPER_DIR xcrun devicectl)
"${DC[@]}" list devices >/dev/null 2>&1 || DC=(xcrun devicectl)
if [[ -z "$DEVICE" ]]; then
	J="$(mktemp)"
	"${DC[@]}" list devices --json-output "$J" >/dev/null 2>&1 || true
	DEVICE="$(python3 -c '
import json, sys
try: devices = json.load(open(sys.argv[1]))["result"]["devices"]
except Exception: devices = []
for d in devices:
    hw, cp = d.get("hardwareProperties", {}), d.get("connectionProperties", {})
    if hw.get("reality") == "physical" and hw.get("platform") == "iOS" and cp.get("pairingState") == "paired" \
            and cp.get("tunnelState") != "unavailable":
        print(hw.get("udid") or d["identifier"]); break
' "$J")"
	rm -f "$J"
	[[ -n "$DEVICE" ]] || { echo "no connected iPhone/iPad found (connect it by cable and unlock it)" >&2; exit 1; }
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
"${DC[@]}" device copy from --device "$DEVICE" --domain-type appDataContainer --domain-identifier "$BUNDLE_ID" \
	--source Documents/shader_cache --destination "$TMP/shader_cache" > "$TMP/log" 2>&1 || { cat "$TMP/log" >&2; exit 1; }
n=$(find "$TMP/shader_cache" -name '*.cache' | wc -l | tr -d ' ')
mkdir -p "$OUT"
before=$(find "$OUT" -name '*.cache' | wc -l | tr -d ' ')
# A shader harvested again replaces the earlier copy, unless the earlier one suits more devices: Godot only loads
# shaders made for the device's GPU family or an older one, so the older family's copy wins; then the copy with
# more of the shader's variants (Godot adds variants to a file as the game uses them).
python3 - "$TMP/shader_cache" "$OUT" <<'PY'
import os, shutil, struct, sys
src, out = sys.argv[1], sys.argv[2]
def rank(path):
	try:
		data = open(path, "rb").read()
		count = struct.unpack_from("<I", data, 8)[0] if data[:4] == b"GDSC" else 0
		off, gpus, filled = 12, [], 0
		for _ in range(count):
			size = struct.unpack_from("<I", data, off)[0]
			if size:
				filled += 1
				gpus.append(struct.unpack_from("<I", data, off + 4 + 20 + 56 + 4)[0])  # container, reflection, profile.gpu
			off += 4 + size
		return (-(min(gpus) if gpus else 1 << 30), filled)
	except (OSError, struct.error):
		return (-(1 << 30), 0)
for d, _, names in os.walk(src):
	for n in names:
		if n.endswith(".cache"):
			a = os.path.join(d, n)
			b = os.path.join(out, os.path.relpath(a, src))
			if not os.path.exists(b) or rank(a) >= rank(b):
				os.makedirs(os.path.dirname(b), exist_ok=True)
				shutil.copy2(a, b)
PY
after=$(find "$OUT" -name '*.cache' | wc -l | tr -d ' ')
echo "Copied $n shader files from the device; $OUT now has $after ($((after - before)) new)."

# Recorded pipelines (builds from before the recorder have none): merged by record, keeping the earliest first use.
if "${DC[@]}" device copy from --device "$DEVICE" --domain-type appDataContainer --domain-identifier "$BUNDLE_ID" \
	--source Documents/port_pipelines/records.jsonl --destination "$TMP/records.jsonl" > "$TMP/log" 2>&1; then
	mkdir -p "$OUT/pipelines"
	python3 - "$TMP/records.jsonl" "$OUT/pipelines/records.jsonl" <<'PY'
import json, os, sys
new, out = sys.argv[1], sys.argv[2]
records = {}
for path in (out, new):
	if os.path.exists(path):
		for line in open(path, encoding="utf-8"):
			try:
				r = json.loads(line)
			except ValueError:
				continue
			if r.get("h") and (r["h"] not in records or r.get("t", 0) < records[r["h"]].get("t", 0)):
				records[r["h"]] = r
before = sum(1 for _ in open(out)) if os.path.exists(out) else 0
with open(out + ".tmp", "w") as f:
	for r in sorted(records.values(), key=lambda r: (r.get("t", 0), r["h"])):
		f.write(json.dumps(r, sort_keys=True, separators=(",", ":")) + "\n")
os.replace(out + ".tmp", out)
print(f"Recorded pipelines: {len(records)} ({len(records) - before} new).")
PY
else
	echo "No recorded pipelines on the device (a build from before the recorder?)."
fi
