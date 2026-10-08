#!/bin/bash
# Builds Slay the Spire 2 for iPhone/iPad from your own copy of the game. See README.md.
set -euo pipefail

TESTED_GAME_VERSION="v0.107.1"
GODOT_VERSION="4.5.1"
DOTNET_VERSION="9.0.318"
GDRE_VERSION="2.7.0"
FMOD_GDE_VERSION="6.1.0-4.5.0"
SPINE_VERSION="4.2"

ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/build"
# .NET's iOS linker step breaks on paths with spaces, so build elsewhere if this folder's path has one.
if [[ "$BUILD" == *" "* ]]; then BUILD="$HOME/Library/Caches/sts2-ios-launch"; fi
TOOLS="$BUILD/tools"
WORK="$BUILD/work"
LOGS="$BUILD/logs"

usage() {
	cat <<EOF
Usage: ./build.sh --game <folder> [options]

  --game <folder>     Your Slay the Spire 2 install: the game folder copied from a PC, or the Mac Steam
                      folder. Default: ~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2
  --team <id>         Apple team ID to sign with (Xcode > Settings > Accounts). Default: your only team in Xcode.
  --unsigned          Make an unsigned .ipa for Sideloadly/AltStore instead of signing with Xcode.
  --install           Install on the connected iPhone/iPad after building (signed builds only).
  --device <udid>     Which device to install on (default: the first connected one).
  --bundle-id <id>    App bundle ID. Default: com.sts2ios.<team id> (or com.sts2ios.app when unsigned).
  --xcode <path>      Xcode 26 app to build with (default: searched in /Applications and ~/Downloads).
  --icon game         Use the game's own pixel-art icon instead of rendering the Ironclad close-up.
  --clean             Delete build/work first (downloaded tools are kept).

Everything goes in build/ (or ~/Library/Caches/sts2-ios-launch if this folder's path contains a space).
EOF
}

die() { echo "error: $*" >&2; exit 1; }
[[ "$BUILD" != *" "* ]] || die "the build folder can't have spaces in its path: $BUILD"
# Paths inside the repo are shown relative to it.
show() { local p="${1#$ROOT/}"; echo "$p"; }
step() { echo "==> $*"; }

GAME_DIR="${STS2_GAME_DIR:-}"; TEAM=""; UNSIGNED=0; INSTALL=0; DEVICE=""; BUNDLE_ID=""; XCODE_APP="${XCODE_APP:-}"
ICON_MODE="ironclad"; CLEAN=0
while [[ $# -gt 0 ]]; do
	case "$1" in
		--game) GAME_DIR="$2"; shift 2 ;;
		--team) TEAM="$2"; shift 2 ;;
		--unsigned) UNSIGNED=1; shift ;;
		--install) INSTALL=1; shift ;;
		--device) DEVICE="$2"; shift 2 ;;
		--bundle-id) BUNDLE_ID="$2"; shift 2 ;;
		--xcode) XCODE_APP="$2"; shift 2 ;;
		--icon) ICON_MODE="$2"; shift 2 ;;
		--clean) CLEAN=1; shift ;;
		-h|--help) usage; exit 0 ;;
		*) usage; die "unknown option $1" ;;
	esac
done
[[ "$ICON_MODE" == "ironclad" || "$ICON_MODE" == "game" ]] || die "--icon must be 'game' (or omitted)"
[[ $UNSIGNED -eq 1 && $INSTALL -eq 1 ]] && die "--install needs a signed build; drop --unsigned"

# Runs a command with its output in build/logs/<name>.log. run_ok returns its status; run stops the build with
# the end of the log if it fails.
run_ok() { local name="$1"; shift; "$@" > "$LOGS/$name.log" 2>&1; }
run() {
	local name="$1"; shift
	if ! run_ok "$name" "$@"; then
		echo "---- last lines of $(show "$LOGS/$name.log") ----" >&2
		tail -n 40 "$LOGS/$name.log" >&2
		die "$name failed (full log: $(show "$LOGS/$name.log"))"
	fi
}

# Fingerprint of files/folders/strings, to skip stages whose inputs didn't change.
fingerprint() {
	local x
	for x in "$@"; do
		if [[ -d "$x" ]]; then (cd "$x" && find . -type f ! -name '.DS_Store' | LC_ALL=C sort | xargs -I{} shasum "{}")
		elif [[ -f "$x" ]]; then shasum "$x"
		else echo "$x"; fi
	done | shasum | cut -d' ' -f1
}
up_to_date() { [[ -f "$WORK/$1.stamp" && "$(cat "$WORK/$1.stamp")" == "$2" ]]; }
mark_done() { echo "$2" > "$WORK/$1.stamp"; }

# ---------------------------------------------------------------------------------------------------- host
[[ "$(uname)" == "Darwin" ]] || die "this needs a Mac"
command -v python3 >/dev/null && python3 -c 1 2>/dev/null || die "python3 is missing: install Xcode first"
[[ $CLEAN -eq 1 ]] && rm -rf "$WORK"
mkdir -p "$TOOLS" "$WORK" "$LOGS"

# ---------------------------------------------------------------------------------------------------- Xcode
# Godot 4.5's iOS app doesn't use UIScene, which apps built with the iOS 27 SDK (Xcode 27) must: they crash
# at launch. Build with Xcode 26 (any 26.x); it can be installed next to a newer Xcode.
xcode_major() { defaults read "$1/Contents/Info" CFBundleShortVersionString 2>/dev/null | cut -d. -f1; }
if [[ -z "$XCODE_APP" ]]; then
	best=""; bestv=""
	for x in /Applications/Xcode*.app "$HOME/Applications"/Xcode*.app "$HOME/Downloads"/Xcode*.app \
		"$(dirname "$(dirname "$(xcode-select -p 2>/dev/null || echo /x/x)")")"; do
		[[ -d "$x" && "$(xcode_major "$x")" == "26" ]] || continue
		v="$(defaults read "$x/Contents/Info" CFBundleShortVersionString)"
		if [[ -z "$best" ]] || [[ "$(printf '%s\n%s\n' "$bestv" "$v" | sort -V | tail -1)" == "$v" ]]; then best="$x"; bestv="$v"; fi
	done
	[[ -n "$best" ]] || die "Xcode 26 not found. Download Xcode 26.x from https://developer.apple.com/download/all/
       (free Apple ID), put it in /Applications (e.g. as Xcode-26.app), open it once, then run this again.
       Xcode 27 can't be used yet (see README). Or point to it with --xcode /path/to/Xcode.app"
	XCODE_APP="$best"
fi
[[ "$(xcode_major "$XCODE_APP")" == "26" ]] || die "$XCODE_APP is not Xcode 26"
export DEVELOPER_DIR="$XCODE_APP/Contents/Developer"
xcodebuild -checkFirstLaunchStatus >/dev/null 2>&1 || die "Xcode needs its first-launch setup: open $XCODE_APP once (or run
       sudo DEVELOPER_DIR=\"$DEVELOPER_DIR\" xcodebuild -runFirstLaunch), then run this again"
IOS_SDK="$(xcrun --sdk iphoneos --show-sdk-path 2>/dev/null)" || die "the iOS SDK is missing from $XCODE_APP"
echo "Xcode: $XCODE_APP ($(defaults read "$XCODE_APP/Contents/Info" CFBundleShortVersionString))"

# ---------------------------------------------------------------------------------------------------- game files
if [[ -z "$GAME_DIR" ]]; then
	GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
	[[ -d "$GAME_DIR" ]] || { usage; die "pass --game <your Slay the Spire 2 folder>"; }
fi
[[ -d "$GAME_DIR" ]] || die "no such folder: $GAME_DIR"
GAME_DIR="$(cd "$GAME_DIR" && pwd)"
STS2_DLL="$(find "$GAME_DIR" -maxdepth 6 -name sts2.dll -type f | head -1 || true)"
[[ -n "$STS2_DLL" ]] || die "sts2.dll not found under $GAME_DIR: is this the Slay the Spire 2 folder?"
GAME_DATA="$(dirname "$STS2_DLL")"
PCK="$(find "$GAME_DIR" -maxdepth 6 -name 'SlayTheSpire2.pck' -type f | head -1 || true)"
[[ -n "$PCK" ]] || PCK="$(find "$GAME_DIR" -maxdepth 6 -name '*.pck' -type f -size +500M | head -1 || true)"
[[ -n "$PCK" ]] || die "SlayTheSpire2.pck not found under $GAME_DIR"
for f in GodotSharp.dll SmartFormat.dll; do [[ -f "$GAME_DATA/$f" ]] || die "$f missing next to sts2.dll"; done
GAME_VERSION="$TESTED_GAME_VERSION"
INFO="$(find "$GAME_DIR" -maxdepth 6 -name release_info.json -type f | head -1 || true)"
if [[ -n "$INFO" ]]; then GAME_VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$INFO")"; fi
echo "Game: $GAME_VERSION"
echo "      $PCK"
echo "      $GAME_DATA"
if [[ "$GAME_VERSION" != "$TESTED_GAME_VERSION" ]]; then
	echo "warning: this was tested with $TESTED_GAME_VERSION (Steam's public branch). Other versions may fail to"
	echo "         patch, and multiplayer needs every player on the same version."
fi
SHORT_VERSION="${GAME_VERSION#v}"

# ---------------------------------------------------------------------------------------------------- signing
if [[ $UNSIGNED -eq 0 && -z "$TEAM" ]]; then
	TEAMS="$(defaults export com.apple.dt.Xcode - 2>/dev/null | python3 -c '
import plistlib, sys
try: d = plistlib.loads(sys.stdin.buffer.read())
except Exception: d = {}
seen = {}
for teams in d.get("IDEProvisioningTeamByIdentifier", {}).values():
    for t in teams:
        seen[t["teamID"]] = t.get("teamName", "") + (" (Personal Team)" if t.get("isFreeProvisioningTeam") else "")
for k, v in seen.items(): print(k, v)
')"
	case "$(printf '%s' "$TEAMS" | grep -c . || true)" in
		0) die "no Apple account in Xcode. Either add your Apple ID in Xcode > Settings > Accounts (a free one works)
       and run this again, or build with --unsigned and install the .ipa with Sideloadly/AltStore" ;;
		1) TEAM="$(printf '%s' "$TEAMS" | cut -d' ' -f1)" ;;
		*) printf 'Teams in Xcode:\n%s\n' "$TEAMS"; die "pick one with --team <id>" ;;
	esac
fi
if [[ $UNSIGNED -eq 1 ]]; then
	EXPORT_TEAM="AAAAAAAAAA"; BUNDLE_ID="${BUNDLE_ID:-com.sts2ios.app}"
	echo "Signing: none (unsigned .ipa)"
else
	EXPORT_TEAM="$TEAM"; BUNDLE_ID="${BUNDLE_ID:-com.sts2ios.$(echo "$TEAM" | tr '[:upper:]' '[:lower:]')}"
	echo "Signing: team $TEAM"
fi
echo "Bundle ID: $BUNDLE_ID"

# ---------------------------------------------------------------------------------------------------- tools
export PATH="$TOOLS/dotnet:$PATH" DOTNET_ROOT="$TOOLS/dotnet" NUGET_PACKAGES="$TOOLS/nuget" DOTNET_CLI_HOME="$TOOLS/dotnet-home" \
	DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
GODOT="$TOOLS/godot/Godot_mono.app/Contents/MacOS/Godot"
GDRE="$TOOLS/gdre/Godot RE Tools.app/Contents/MacOS/Godot RE Tools"

fetch() { curl -fL --retry 3 --progress-bar -o "$2" "$1" || die "download failed: $1"; }

if [[ ! -x "$TOOLS/dotnet/dotnet" || "$("$TOOLS/dotnet/dotnet" --version 2>/dev/null)" != "$DOTNET_VERSION" ]]; then
	step "Downloading .NET SDK $DOTNET_VERSION"
	fetch https://dot.net/v1/dotnet-install.sh "$TOOLS/dotnet-install.sh"
	run dotnet-install bash "$TOOLS/dotnet-install.sh" --version "$DOTNET_VERSION" --install-dir "$TOOLS/dotnet" --no-path
fi
if [[ ! -f "$TOOLS/godot/.ok-$GODOT_VERSION" ]]; then
	step "Downloading Godot $GODOT_VERSION (.NET)"
	rm -rf "$TOOLS/godot" && mkdir -p "$TOOLS/godot"
	fetch "https://github.com/godotengine/godot/releases/download/$GODOT_VERSION-stable/Godot_v$GODOT_VERSION-stable_mono_macos.universal.zip" "$TOOLS/godot.zip"
	run godot-unzip ditto -x -k "$TOOLS/godot.zip" "$TOOLS/godot" && rm "$TOOLS/godot.zip"
	touch "$TOOLS/godot/._sc_" # self-contained: editor settings stay in build/tools/godot
	step "Downloading the Godot iOS export template"
	(cd "$TOOLS/godot" && run godot-template python3 "$ROOT/tools/rangezip.py" \
		"https://github.com/godotengine/godot/releases/download/$GODOT_VERSION-stable/Godot_v$GODOT_VERSION-stable_mono_export_templates.tpz" \
		templates/ios.zip)
	touch "$TOOLS/godot/.ok-$GODOT_VERSION"
fi
if [[ ! -f "$TOOLS/gdre/.ok-$GDRE_VERSION" ]]; then
	step "Downloading GDRE Tools $GDRE_VERSION"
	rm -rf "$TOOLS/gdre" && mkdir -p "$TOOLS/gdre"
	fetch "https://github.com/GDRETools/gdsdecomp/releases/download/v$GDRE_VERSION/GDRE_tools-v$GDRE_VERSION-macos.zip" "$TOOLS/gdre.zip"
	run gdre-unzip ditto -x -k "$TOOLS/gdre.zip" "$TOOLS/gdre" && rm "$TOOLS/gdre.zip"
	[[ -x "$GDRE" ]] || die "unexpected GDRE download layout"
	touch "$TOOLS/gdre/.ok-$GDRE_VERSION"
fi
if [[ ! -f "$TOOLS/fmod/.ok-$FMOD_GDE_VERSION" ]]; then
	step "Downloading fmod-gdextension $FMOD_GDE_VERSION"
	rm -rf "$TOOLS/fmod" "$TOOLS/fmod-zip" && mkdir -p "$TOOLS/fmod-zip"
	fetch "https://github.com/utopia-rise/fmod-gdextension/releases/download/$FMOD_GDE_VERSION/addons.zip" "$TOOLS/fmod.zip"
	run fmod-unzip ditto -x -k "$TOOLS/fmod.zip" "$TOOLS/fmod-zip" && rm "$TOOLS/fmod.zip"
	mv "$TOOLS/fmod-zip/fmod" "$TOOLS/fmod" && rm -rf "$TOOLS/fmod-zip"
	mv "$TOOLS/fmod/libs/iOS" "$TOOLS/fmod/libs/ios" # the .gdextension refers to libs/ios
	rm -rf "$TOOLS/fmod/libs/android" "$TOOLS/fmod/libs/linux" "$TOOLS/fmod/libs/windows"
	touch "$TOOLS/fmod/.ok-$FMOD_GDE_VERSION"
fi
if [[ ! -f "$TOOLS/spine/.ok-$SPINE_VERSION-$GODOT_VERSION" ]]; then
	step "Downloading spine-godot $SPINE_VERSION"
	rm -rf "$TOOLS/spine" "$TOOLS/spine-zip" && mkdir -p "$TOOLS/spine-zip"
	fetch "https://spine-godot.s3.eu-central-1.amazonaws.com/$SPINE_VERSION/$GODOT_VERSION-stable/spine-godot-extension-$SPINE_VERSION-$GODOT_VERSION-stable.zip" "$TOOLS/spine.zip"
	run spine-unzip ditto -x -k "$TOOLS/spine.zip" "$TOOLS/spine-zip" && rm "$TOOLS/spine.zip"
	SP="$(dirname "$(find "$TOOLS/spine-zip" -name spine_godot_extension.gdextension | head -1 || true)")"
	[[ -d "$SP/ios" && -d "$SP/macos" ]] || die "unexpected spine-godot download layout"
	mkdir -p "$TOOLS/spine" && cp -R "$SP/ios" "$SP/macos" "$SP/spine_godot_extension.gdextension" "$TOOLS/spine/"
	rm -rf "$TOOLS/spine-zip"
	touch "$TOOLS/spine/.ok-$SPINE_VERSION-$GODOT_VERSION"
fi
TOOLS_ID="godot $GODOT_VERSION dotnet $DOTNET_VERSION gdre $GDRE_VERSION fmod $FMOD_GDE_VERSION spine $SPINE_VERSION"
PCK_ID="$(stat -f '%z %m' "$PCK") $PCK"

# ---------------------------------------------------------------------------------------------------- icon
FP="$(fingerprint "$ROOT/src/icon" "$PCK_ID" "$ICON_MODE" "$TOOLS_ID")"
if ! up_to_date icon "$FP" || [[ ! -f "$WORK/icon.png" ]]; then
	step "Making the app icon from your game files"
	rm -f "$WORK/icon.png"
	if [[ "$ICON_MODE" == "ironclad" ]]; then
		I="$WORK/icon-render"; rm -rf "$I" && mkdir -p "$I"
		if run_ok icon-recover "$GDRE" --headless --recover="$PCK" --output="$I" \
				--include="res://animations/character_select/ironclad/*" --include="res://.godot/imported/characterselect_ironclad.*" \
			&& rm -rf "$I/addons" "$I/project.godot" && mkdir -p "$I/addons/spine" "$I/.godot" \
			&& cp -R "$TOOLS/spine/." "$I/addons/spine/" && cp "$ROOT/src/icon/"* "$I/" \
			&& echo "res://addons/spine/spine_godot_extension.gdextension" > "$I/.godot/extension_list.cfg" \
			&& { (cd "$I" && "$GODOT" --headless --path . --import > "$LOGS/icon-import.log" 2>&1) || true; } \
			&& (cd "$I" && perl -e 'alarm 180; exec @ARGV' "$GODOT" --path . --script res://render.gd -- "$WORK/icon.png" > "$LOGS/icon-render.log" 2>&1) \
			&& [[ -f "$WORK/icon.png" ]]; then
			:
		else
			echo "    (couldn't render the Ironclad icon; using the game's own icon. Details in $(show "$LOGS")/icon-*.log)"
			rm -f "$WORK/icon.png"
		fi
	fi
	if [[ ! -f "$WORK/icon.png" ]]; then
		rm -rf "$WORK/icon-game"
		run icon-extract "$GDRE" --headless --extract="$PCK" --include="res://images/icon_1024.png" --output="$WORK/icon-game"
		cp "$WORK/icon-game/images/icon_1024.png" "$WORK/icon.png"
	fi
	mark_done icon "$FP"
fi

# ---------------------------------------------------------------------------------------------------- Xcode project
# Export an empty Godot C# project (named like the game's assembly) as an Xcode project: this provides the stock
# Godot 4.5.1 iOS engine with FMOD and Spine. The game's code and data are swapped in afterwards.
X="$WORK/xcode"
FP="$(fingerprint "$ROOT/src/godot" "$ROOT/src/ios" "$WORK/icon.png" "$EXPORT_TEAM $BUNDLE_ID $SHORT_VERSION plist-bgaudio" "$TOOLS_ID")"
if ! up_to_date export "$FP" || [[ ! -d "$X/sts2.xcodeproj" ]]; then
	step "Exporting the Godot iOS project"
	P="$WORK/proj"; rm -rf "$P" && mkdir -p "$P/addons"
	cp "$ROOT/src/godot/"* "$P/" && rm "$P/export_presets.cfg.in"
	sed -e "s|@EXPORT_PATH@|$X/sts2.ipa|" -e "s|@TEMPLATE@|$TOOLS/godot/templates/ios.zip|" -e "s|@TEAM@|$EXPORT_TEAM|" \
		-e "s|@BUNDLE_ID@|$BUNDLE_ID|" -e "s|@VERSION@|$SHORT_VERSION|" "$ROOT/src/godot/export_presets.cfg.in" > "$P/export_presets.cfg"
	cp -R "$TOOLS/fmod" "$P/addons/fmod" && cp -R "$TOOLS/spine" "$P/addons/spine"
	cp "$WORK/icon.png" "$P/icon.png"
	# The .NET editor needs dotnet on PATH (set above), else it blocks on a hidden alert. Import crashes at exit: harmless.
	(cd "$P" && "$GODOT" --headless --path . --import > "$LOGS/godot-import.log" 2>&1) || true
	rm -rf "$X" && mkdir -p "$X"
	(cd "$P" && run godot-export "$GODOT" --headless --path . --export-release iOS "$X/sts2.ipa")
	[[ -d "$X/sts2.xcodeproj" ]] || die "Godot export produced no Xcode project (see $(show "$LOGS/godot-export.log"))"
	sed -i '' 's/"Apple Distribution"/"Apple Development"/g' "$X/sts2.xcodeproj/project.pbxproj"
	/usr/libexec/PlistBuddy -c "Add :godot_cmdline array" -c "Add :godot_cmdline:0 string --force-steam=off" \
		-c "Add :godot_cmdline:1 string --log-file" -c "Add :godot_cmdline:2 string user://godot.log" \
		-c "Add :NSLocalNetworkUsageDescription string Slay the Spire 2 uses your local network to host and join multiplayer games." \
		-c "Add :UIBackgroundModes array" -c "Add :UIBackgroundModes:0 string audio" \
		"$X/sts2/sts2-Info.plist"
	# UIBackgroundModes audio: only used during multiplayer, to keep the game running in the background
	# (src/native/Port/PortKeepAlive.cs); otherwise the app is suspended as usual.
	# fmod-gdextension's iOS export plugin doesn't generate its plugin loader under Godot 4.5. Ours also swaps the
	# extension's racy async file callbacks for blocking reads from the pck (see the file).
	cat "$ROOT/src/ios/fmod_plugins_stub.cpp" >> "$X/sts2/dummy.cpp"
	mark_done export "$FP"
fi
FRAMEWORK="$X/sts2/dylibs/ExportRelease/sts2_aot.xcframework/ios-arm64/sts2.framework"
[[ -d "$FRAMEWORK" ]] || die "unexpected Godot export layout: $FRAMEWORK missing"

# ---------------------------------------------------------------------------------------------------- game code
# IL-patch the game's sts2.dll (Steam Cloud saves, direct-IP multiplayer, iOS fixes) and stock GodotSharp, then
# compile them with the port's code to one native iOS library (NativeAOT).
mkdir -p "$WORK/game-data"
rsync -a --delete --include='*.dll' --exclude='*' "$GAME_DATA/" "$WORK/game-data/"
FP="$(fingerprint "$ROOT/src/patcher" "$ROOT/src/native" "$WORK/game-data" "$IOS_SDK" "$TOOLS_ID")"
if ! up_to_date native "$FP" || [[ ! -f "$WORK/native-out/sts2native.dylib" ]]; then
	step "Restoring stock GodotSharp $GODOT_VERSION"
	run restore dotnet restore "$WORK/proj/sts2.csproj"
	GODOTSHARP="$NUGET_PACKAGES/godotsharp/$GODOT_VERSION/lib/net8.0/GodotSharp.dll"
	[[ -f "$GODOTSHARP" ]] || die "GodotSharp $GODOT_VERSION not restored"
	step "Patching the game's code"
	rm -rf "$WORK/patcher" "$WORK/patched" && mkdir -p "$WORK/patched" && cp -R "$ROOT/src/patcher" "$WORK/patcher"
	(cd "$WORK/patcher" && run patch-godotsharp dotnet run -c Release -- "$GODOTSHARP" "$WORK/patched/GodotSharp.dll")
	if ! (cd "$WORK/patcher" && run patch-sts2 dotnet run -c Release --no-build -- "$WORK/game-data/sts2.dll" "$WORK/patched/sts2.dll"); then
		die "patching sts2.dll failed (game version $GAME_VERSION; tested with $TESTED_GAME_VERSION)"
	fi
	step "Compiling the game for iOS (NativeAOT, takes a few minutes)"
	rm -rf "$WORK/native" "$WORK/native-out" && cp -R "$ROOT/src/native" "$WORK/native"
	(cd "$WORK/native" && run native-publish dotnet publish -c Release -o "$WORK/native-out" \
		-p:GameDataDir="$WORK/game-data" -p:PatchedDir="$WORK/patched" -p:IosSdkPath="$IOS_SDK")
	[[ -f "$WORK/native-out/sts2native.dylib" ]] || die "NativeAOT produced no library (see $(show "$LOGS/native-publish.log"))"
	mark_done native "$FP"
fi
cp "$WORK/native-out/sts2native.dylib" "$FRAMEWORK/sts2"
install_name_tool -id @rpath/sts2.framework/sts2 "$FRAMEWORK/sts2" 2>/dev/null

# ---------------------------------------------------------------------------------------------------- game data
# Add the port's scripts and settings (src/pck) to a copy of the game's pck, at the same res:// paths.
FP="$(fingerprint "$ROOT/src/pck" "$PCK_ID" "$TOOLS_ID")"
if ! up_to_date pck "$FP" || [[ ! -f "$WORK/sts2.pck" ]]; then
	step "Adding the port's files to the game data (about 2 GB)"
	PF=()
	while IFS= read -r f; do PF+=("--patch-file=$f=res://${f#$ROOT/src/pck/}"); done < <(find "$ROOT/src/pck" -type f ! -name '.DS_Store')
	rm -f "$WORK/sts2.pck"
	run pck-patch "$GDRE" --headless --pck-patch="$PCK" --output="$WORK/sts2.pck" "${PF[@]}"
	[[ -f "$WORK/sts2.pck" ]] || die "pck patch produced no file (see $(show "$LOGS/pck-patch.log"))"
	mark_done pck "$FP"
fi
rm -f "$X/sts2.pck" && cp -c "$WORK/sts2.pck" "$X/sts2.pck" 2>/dev/null || cp "$WORK/sts2.pck" "$X/sts2.pck"

# ---------------------------------------------------------------------------------------------------- app
step "Building the app with Xcode"
XB=(xcodebuild -project "$X/sts2.xcodeproj" -scheme sts2 -configuration Release -destination generic/platform=iOS
	-derivedDataPath "$WORK/derived")
if [[ $UNSIGNED -eq 1 ]]; then
	run xcodebuild "${XB[@]}" CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY="" build
else
	XB+=(-allowProvisioningUpdates -allowProvisioningDeviceRegistration DEVELOPMENT_TEAM="$TEAM" build)
	if ! "${XB[@]}" > "$LOGS/xcodebuild.log" 2>&1; then
		if grep -qiE "personal development team|increased-memory-limit|Increased Memory Limit" "$LOGS/xcodebuild.log"; then
			# Free (personal) teams can't use the increased memory limit entitlement.
			echo "    (your team can't use the increased memory limit; building without it)"
			/usr/libexec/PlistBuddy -c "Delete :com.apple.developer.kernel.increased-memory-limit" "$X/sts2/sts2.entitlements" || true
			run xcodebuild "${XB[@]}"
		else
			echo "---- errors from $(show "$LOGS/xcodebuild.log") ----" >&2; grep -E "error:|BUILD FAILED" "$LOGS/xcodebuild.log" | tail -n 20 >&2
			die "Xcode build failed. If it's about provisioning: connect your iPhone/iPad by cable, unlock it, and
       make sure your Apple ID is in Xcode > Settings > Accounts"
		fi
	fi
fi
APP="$WORK/derived/Build/Products/Release-iphoneos/sts2.app"
[[ -d "$APP" ]] || die "no app built (see $(show "$LOGS/xcodebuild.log"))"

if [[ $UNSIGNED -eq 1 ]]; then
	step "Packaging the .ipa"
	rm -rf "$WORK/ipa" "$BUILD/SlayTheSpire2.ipa" && mkdir -p "$WORK/ipa/Payload"
	cp -c -R "$APP" "$WORK/ipa/Payload/" 2>/dev/null || cp -R "$APP" "$WORK/ipa/Payload/"
	(cd "$WORK/ipa" && run ipa zip -qr -1 "$BUILD/SlayTheSpire2.ipa" Payload)
	rm -rf "$WORK/ipa"
	echo
	echo "Done: $(show "$BUILD/SlayTheSpire2.ipa")"
	echo "Install it with Sideloadly or AltStore (see README). It's built from your copy of the game: keep it to yourself."
	exit 0
fi

echo
echo "Done: $(show "$APP")"
if [[ $INSTALL -eq 1 ]]; then
	# Prefer the default (newest) Xcode's devicectl: it supports the newest iOS versions.
	DC=(env -u DEVELOPER_DIR xcrun devicectl)
	"${DC[@]}" list devices >/dev/null 2>&1 || DC=(xcrun devicectl)
	if [[ -z "$DEVICE" ]]; then
		"${DC[@]}" list devices --json-output "$WORK/devices.json" >/dev/null 2>&1 || true
		DEVICE="$(python3 -c '
import json, sys
try: devices = json.load(open(sys.argv[1]))["result"]["devices"]
except Exception: devices = []
for d in devices:
    hw, cp = d.get("hardwareProperties", {}), d.get("connectionProperties", {})
    if hw.get("reality") == "physical" and hw.get("platform") == "iOS" and cp.get("pairingState") == "paired":
        print(hw.get("udid") or d["identifier"]); break
' "$WORK/devices.json")"
		[[ -n "$DEVICE" ]] || die "no paired iPhone/iPad found: connect it by cable, unlock it and tap Trust"
	fi
	step "Installing on $DEVICE"
	# Wi-Fi connections to the device sometimes drop during the 2 GB copy; retry a couple of times.
	for attempt in 1 2 3; do
		if run_ok install "${DC[@]}" device install app --device "$DEVICE" "$APP"; then break; fi
		if [[ $attempt -eq 3 ]]; then
			tail -n 20 "$LOGS/install.log" >&2
			die "install failed: keep the device unlocked and nearby (a cable is most reliable), then run this again"
		fi
		echo "    (install interrupted; retrying)"; sleep 15
	done
	echo "Installed. First launch: if iOS says the developer isn't trusted, go to Settings > General >"
	echo "VPN & Device Management and trust your Apple ID."
fi
