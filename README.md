# Slay the Spire 2 for iPhone and iPad (unofficial)

Build a native iOS version of **Slay the Spire 2** from **your own copy** of the game, and install it on your
own iPhone or iPad.

This repository contains **no game files**. The build script reads the game's data and code from a copy of the
game you own on Steam, so you need to own Slay the Spire 2. This is a fan project, not affiliated with or
endorsed by Mega Crit.

> **Don't share the app or `.ipa` you build.** It contains the game, so it's yours alone. Friends should build
> their own from this repository.

## What you get

- **The full game**, running natively with Metal (it's not an emulator or streaming).
- **Touch controls.** Tap to click, drag cards to play them, and touch and hold anything to see its tooltip.
- **Full-screen layout** that keeps the UI clear of the notch, the Dynamic Island and the rounded corners.
  Plays in either landscape direction.
- **Steam Cloud saves.** Sign in with Steam on first launch (Steam Guard supported) and your progress syncs
  with your PC. Sync happens at startup and in the background while you play. A **Steam Cloud** button on the
  main menu lets you check sync status and pull or push saves by hand.
  - **Offline play:** without a connection the game starts normally and saves on the device. Once Steam is
    reachable again, your progress uploads automatically. If another device saved something newer in the
    meantime, it loads the next time you're on the main menu. When both changed, the newer save wins and the
    other copy is kept as a backup on the device.
- **Multiplayer over direct IP**, on your home Wi-Fi or over [Tailscale](https://tailscale.com):
  - **Hosting:** Multiplayer → Host. A banner shows the address for others to join.
  - **Up to 6 players** when an iPhone or iPad hosts (the game's own limit is 4). Everyone needs this app;
    a PC can't join an iOS host. Players 5 and 6 sit beside the logs at rest sites and get their own relic
    in treasure rooms.
  - **Joining:** Multiplayer → Join, then enter the host's IP. The app remembers the last address.
  - **Names:** players see each other's Steam names. Names come from the iOS host, so with a PC host
    they show as "Host" / "Player 123456".
  - **Keeps running when you leave the app:** during a multiplayer game, pulling down Notification Center or
    switching apps doesn't pause the game for everyone else. The app uses iOS background audio (silent, and it
    mixes with your music) only while a multiplayer game is connected.
  - **With a PC:** the PC must host, and PC-hosted games stay at 4 players. Add `--fastmp` to the game's
    Steam launch options, then use Multiplayer → Host on the PC.
  - **Versions:** everyone needs the same game version.
  - **Steam:** friends lists, invites and Steam lobbies aren't available, because Steam doesn't exist on iOS.
- Sound and music through FMOD (they follow the silent switch).
- **Extra custom-run modifiers** (singleplayer only), at the bottom of Singleplayer → Custom → modifiers:
  - **Supercharged:** 20 extra Energy every turn.
  - **Perfected Deck:** after every other modifier, your whole deck turns into Perfected Strike+.

Tested with game version **v0.107.1** (Steam's public branch) on an iPhone 16 Pro Max running iOS 27.

## What you need

- **A Mac** (Apple silicon recommended) with about 15 GB free.
- **Xcode 26.x.** Xcode 27 can't be used yet (see [Why Xcode 26](#why-xcode-26)). You can download Xcode 26 for
  free at [developer.apple.com/download/all](https://developer.apple.com/download/all/) with any Apple ID, and
  keep it next to a newer Xcode, for example as `/Applications/Xcode-26.app`. Open it once so it finishes
  setting up.
- **The game files**, from either source:
  - **On the Mac:** install Slay the Spire 2 with Steam for Mac. The script finds it automatically.
  - **From a PC:** in Steam, right-click Slay the Spire 2 → Manage → Browse local files, then copy that whole
    folder to the Mac. It contains `SlayTheSpire2.pck` and a `data_sts2_windows_x86_64` folder.
- **An Apple ID.** A free one works, but the app then stops opening after 7 days until you build and install it
  again (that takes a minute). A paid Apple Developer account lasts a year.
- **An iPhone or iPad** on iOS 17 or later, with Developer Mode on (Settings → Privacy & Security → Developer
  Mode). Developer Mode appears there after the first install attempt.

## Build and install

1. Get this repository. Either:
   - **Download the ZIP:** click Code → Download ZIP on this page, then unzip it.
   - **Clone it:**

     ```sh
     git clone https://github.com/FlyingViet/sts2-ios-launch.git
     ```

   Open Terminal in that folder (`cd ~/Downloads/sts2-ios-launch-main`, or wherever it is).
2. In Xcode 26, go to Settings → Accounts and add your Apple ID.
3. Connect your iPhone or iPad by cable, unlock it, and tap **Trust** if asked.
4. Build and install:

   ```sh
   ./build.sh --install                                  # game installed with Steam on this Mac
   ./build.sh --game "/path/to/Slay the Spire 2" --install   # game folder copied from a PC
   ```

   The first build downloads about 2 GB of tools into `build/` and takes roughly 5–15 minutes, depending on
   your connection and Mac. Later builds only redo what changed.
5. The first time you open the app, iOS may say the developer isn't trusted. Go to Settings → General → VPN &
   Device Management, tap your Apple ID, and trust it.
6. Open the app and sign in to Steam to sync your saves, or skip that step.

**To renew a free-account install** before or after it expires, run the same command again. Your saves are
kept, and they're also in Steam Cloud.

### Without an Apple account in Xcode

`./build.sh --game "/path/to/Slay the Spire 2" --unsigned` makes `build/SlayTheSpire2.ipa`. Install that with
[Sideloadly](https://sideloadly.io) or [AltStore](https://altstore.io), which sign it with your Apple ID.

### Options

| Option | What it does |
| --- | --- |
| `--game <folder>` | Your Slay the Spire 2 folder. Default: the Steam for Mac install. |
| `--install` | Install on the connected device after building. |
| `--device <udid>` | Device to install on, if more than one is connected. |
| `--team <id>` | Apple team to sign with, if your Xcode has more than one. |
| `--bundle-id <id>` | App ID. Default: `com.sts2ios.<team id>`. |
| `--unsigned` | Make an unsigned `.ipa` instead (see above). |
| `--xcode <path>` | Which Xcode 26 to use, if it isn't found automatically. |
| `--icon game` | Use the game's own pixel-art icon instead of the Ironclad close-up. |
| `--clean` | Rebuild everything (downloaded tools are kept). |

## Why Xcode 26

The game runs on Godot 4.5, and Godot 4.5's iOS runtime doesn't use the UIScene app lifecycle that apps built
with Apple's iOS 27 SDK (Xcode 27) are required to adopt. Built with Xcode 27, the app crashes at launch.
Built with Xcode 26, the same app runs fine on iOS 27.

## Troubleshooting

- **Build logs** are in `build/logs/`. The script prints the end of the log for whichever step failed.
- **"Permission denied" running `./build.sh`:** run `bash build.sh …` instead.
- **Folder path with a space in it:** the build goes to `~/Library/Caches/sts2-ios-launch` instead of
  `build/`, because .NET's iOS linker can't handle spaces in paths. The script prints where the result is.
- **"patching sts2.dll failed":** your game version differs from the one this was made for. Switch Slay the
  Spire 2 to Steam's public branch (Properties → Betas → None), let it update, and copy the files again.
- **Signing or provisioning errors:** check your Apple ID is in Xcode 26 → Settings → Accounts, and that your
  device is connected, unlocked and trusted. With a free Apple ID, Apple allows 3 sideloaded apps at a time
  and 10 new app IDs a week.
- **No sound:** check the silent switch.
- **The app's own log:** Files app → On My iPhone → Slay the Spire 2 → `godot.log`.

## How it works

The game is made with Godot (Mega Crit's fork of Godot 4.5) and C#. Its data (`SlayTheSpire2.pck`) and code
(`sts2.dll`) don't depend on the platform, so the build assembles them into an iOS app:

1. **Engine:** exports an empty Godot 4.5.1 C# project ([`src/godot`](src/godot)) for iOS. That produces an
   Xcode project with the stock iOS engine and the FMOD and Spine extensions the game uses.
2. **Patching:** patches the game's `sts2.dll` with Mono.Cecil ([`src/patcher`](src/patcher)) to add hooks for
   Steam Cloud saves and direct-IP multiplayer, and to make a few fixes for iOS.
3. **Compiling:** compiles the game's code and the port's code ([`src/native`](src/native)) into a native iOS
   library with .NET NativeAOT, since iOS can't run .NET code just in time. The port's code includes a small
   Steam client used only for Cloud saves; your Steam password isn't stored, only a sign-in token in the iOS
   Keychain.
4. **Data:** adds the port's settings and scripts ([`src/pck`](src/pck)) to a copy of the game's `.pck`:
   - touch input
   - display and safe-area layout
   - the Steam Cloud screens
   - the multiplayer join and host screens
5. **App build:** builds and signs the app with Xcode.

These tools are downloaded from their official sources during the build. None of them are included here:

- [.NET SDK](https://dotnet.microsoft.com)
- [Godot](https://godotengine.org)
- [GDRE Tools](https://github.com/GDRETools/gdsdecomp)
- [fmod-gdextension](https://github.com/utopia-rise/fmod-gdextension), which includes FMOD, under
  [FMOD's license](https://www.fmod.com/licensing)
- [spine-godot](https://esotericsoftware.com/spine-godot), under the
  [Spine Runtimes license](https://esotericsoftware.com/spine-runtimes-license)

## Credits

- [Ekyso/StS2-Launcher](https://github.com/Ekyso/StS2-Launcher), the Android port, was the reference for the
  Steam Cloud sync.
- Slay the Spire 2 is by [Mega Crit](https://www.megacrit.com). Buy it on
  [Steam](https://store.steampowered.com/app/2868840/).

The code in this repository is MIT-licensed (see [LICENSE](LICENSE)). The game and its files are not covered by
that license.
