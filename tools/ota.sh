#!/usr/bin/env bash
# Over-the-air updates for the port's scripts (src/pck/port/*.gd). See src/pck/port/ota.gd for the app side.
#
#   tools/ota.sh id        print the compatibility id of this checkout (build.sh embeds it in the app)
#   tools/ota.sh keygen    create the signing key (once, for whoever publishes updates)
#   tools/ota.sh publish [--dry-run] ["notes"]
#                          pack src/pck/port/*.gd, sign it and upload it to this repo's "ota" GitHub release
#
# A pack is only applied by apps whose compiled code has the same compatibility id: a hash of everything that isn't
# a port script (src/native, src/patcher, src/godot, src/ios, src/pck/override.cfg, src/ota/public.pem). Change any of
# those and installed apps need a rebuild instead; they show a notice saying so.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
KEY="${STS2_OTA_KEY:-$HOME/.config/sts2-ios-launch/ota_private.pem}"
PUB="$ROOT/src/ota/public.pem"
TAG="ota"
die() { echo "error: $*" >&2; exit 1; }

# Content hash of the files above, with paths relative to the repo so every checkout computes the same id.
compat_id() {
	(cd "$ROOT" && find src/native src/patcher src/godot src/ios src/pck/override.cfg src/ota/public.pem -type f \
		! -name '.DS_Store' | LC_ALL=C sort | xargs shasum -a 256) | shasum -a 256 | cut -c1-16
}

# Files that go in a pack: the port's scripts, except this updater itself (it runs before any pack is mounted).
pack_files() {
	(cd "$ROOT/src/pck" && find port -type f -name '*.gd' ! -name 'ota.gd' | LC_ALL=C sort)
}

find_godot() {
	local g
	for g in "$ROOT/build/tools/godot/Godot_mono.app/Contents/MacOS/Godot" \
		"$HOME/Library/Caches/sts2-ios-launch/tools/godot/Godot_mono.app/Contents/MacOS/Godot"; do
		[[ -x "$g" ]] && { echo "$g"; return; }
	done
	die "Godot not found: run ./build.sh once first"
}

case "${1:-}" in
id)
	compat_id
	;;
keygen)
	[[ -f "$KEY" ]] && die "$KEY already exists"
	mkdir -p "$(dirname "$KEY")" && chmod 700 "$(dirname "$KEY")"
	openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$KEY" 2>/dev/null
	chmod 600 "$KEY"
	openssl pkey -in "$KEY" -pubout -out "$PUB"
	echo "Private key: $KEY (keep it safe; never commit it)"
	echo "Public key:  ${PUB#$ROOT/} (commit it; apps built from now on trust packs signed with this key)"
	;;
publish)
	shift
	DRY=0
	[[ "${1:-}" == "--dry-run" ]] && { DRY=1; shift; }
	NOTES="${1:-}"
	[[ -f "$KEY" ]] || die "no signing key at $KEY (tools/ota.sh keygen)"
	[[ "$(openssl pkey -in "$KEY" -pubout)" == "$(cat "$PUB")" ]] || die "$KEY doesn't match ${PUB#$ROOT/}"
	GODOT="$(find_godot)"
	DOTNET="$ROOT/build/tools/dotnet"
	[[ -d "$DOTNET" ]] && export PATH="$DOTNET:$PATH" DOTNET_ROOT="$DOTNET" # the .NET editor stalls without it
	OUT="$(mktemp -d)"
	trap 'rm -rf "$OUT"' EXIT
	SEQ="$(date -u +%s)"
	ID="$(compat_id)"
	FILE="port-$SEQ.pck"

	# Syntax-check every script with the same Godot version the app runs.
	mkdir -p "$OUT/check/port"
	printf '[application]\nconfig/name="ota-check"\n' > "$OUT/check/project.godot"
	while read -r f; do cp "$ROOT/src/pck/$f" "$OUT/check/$f"; done < <(pack_files)
	while read -r f; do
		log="$(perl -e 'alarm 120; exec @ARGV' "$GODOT" --headless --path "$OUT/check" --check-only --script "res://$f" 2>&1 || true)"
		if grep -qE "SCRIPT ERROR|Parse Error|Failed to load script" <<< "$log"; then
			echo "$log" | grep -E "ERROR|Error" >&2
			die "$f doesn't compile"
		fi
	done < <(pack_files)

	# Build the pack with Godot's own packer, so the app's engine reads it.
	{
		echo 'extends SceneTree'
		echo 'func _init():'
		echo "	var p := PCKPacker.new()"
		echo "	if p.pck_start(\"$OUT/$FILE\") != OK: quit(1); return"
		while read -r f; do echo "	if p.add_file(\"res://$f\", \"$ROOT/src/pck/$f\") != OK: quit(1); return"; done < <(pack_files)
		echo '	quit(0 if p.flush() == OK else 1)'
	} > "$OUT/pack.gd"
	perl -e 'alarm 120; exec @ARGV' "$GODOT" --headless --script "$OUT/pack.gd" > "$OUT/pack.log" 2>&1 \
		|| { cat "$OUT/pack.log" >&2; die "packing failed"; }
	[[ -s "$OUT/$FILE" ]] || die "packing produced no file"

	python3 - "$OUT" "$FILE" "$SEQ" "$ID" "$NOTES" <<'PY'
import hashlib, json, os, sys
out, name, seq, cid, notes = sys.argv[1:]
data = open(os.path.join(out, name), "rb").read()
manifest = {"format": 1, "seq": int(seq), "compat": cid, "file": name, "size": len(data),
            "sha256": hashlib.sha256(data).hexdigest(), "notes": notes}
open(os.path.join(out, "ota.json"), "w").write(json.dumps(manifest, indent=1, sort_keys=True))
PY
	openssl dgst -sha256 -sign "$KEY" -out "$OUT/ota.json.sig" "$OUT/ota.json"
	openssl dgst -sha256 -verify "$PUB" -signature "$OUT/ota.json.sig" "$OUT/ota.json" > /dev/null
	echo "Pack: $FILE ($(wc -c < "$OUT/$FILE" | tr -d ' ') bytes, $(pack_files | wc -l | tr -d ' ') scripts, compat $ID)"
	cat "$OUT/ota.json"; echo
	if [[ $DRY -eq 1 ]]; then
		mkdir -p "$ROOT/build/ota" && cp "$OUT/ota.json" "$OUT/ota.json.sig" "$OUT/$FILE" "$ROOT/build/ota/"
		echo "Dry run: files in build/ota/, nothing uploaded"
		exit 0
	fi
	REPO="$(cd "$ROOT" && gh repo view --json nameWithOwner -q .nameWithOwner)"
	gh release view "$TAG" -R "$REPO" > /dev/null 2>&1 || gh release create "$TAG" -R "$REPO" --title "Script updates" \
		--notes "Signed over-the-air updates for the port's scripts, downloaded by the app. Not a build to install." --latest=false
	# Pack first, then the manifest that points to it; old packs are removed afterwards.
	gh release upload "$TAG" -R "$REPO" "$OUT/$FILE" --clobber
	gh release upload "$TAG" -R "$REPO" "$OUT/ota.json" "$OUT/ota.json.sig" --clobber
	for old in $(gh release view "$TAG" -R "$REPO" --json assets -q '.assets[].name' | grep -E '^port-[0-9]+\.pck$' | grep -vx "$FILE"); do
		gh release delete-asset "$TAG" "$old" -R "$REPO" -y
	done
	echo "Published to https://github.com/$REPO/releases/tag/$TAG"
	;;
*)
	sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'
	exit 1
	;;
esac
