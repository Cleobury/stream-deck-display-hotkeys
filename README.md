# Display Hotkeys — Stream Deck plugin

Save Windows display layouts as profiles and switch between them from a Stream Deck key **or a keyboard hotkey anywhere in Windows** (so you can swap from desk monitors to the TV without being at the deck).

Each profile remembers:

- which displays are on/off
- resolution, refresh rate, position and main display
- rotation
- HDR on/off
- Windows scaling (100%, 125%, …)

## Actions

- **Switch Display Profile**: switches to one profile. Set its name, save the current layout into it, pick an icon (Monitor, Dual, Ultrawide, TV, PC, Laptop, Projector, Gaming) and assign a keyboard hotkey. The key turns green when that profile is active.
- **Cycle Display Profiles**: each press, or its hotkey, moves to the next saved profile.

Hotkeys work whenever the Stream Deck app is running.

## Install / update

Needs the .NET 10 SDK.

```powershell
.\install.ps1
```

This generates the icons, builds `DisplayHotkeys.exe` into the `.sdPlugin\bin` folder, copies the plugin into `%APPDATA%\Elgato\StreamDeck\Plugins` and restarts Stream Deck.

Profiles and hotkeys are stored in `%APPDATA%\DisplayHotkeys`, so reinstalling keeps them.

## Layout

| Path | What |
| --- | --- |
| `DisplayHotkeys/` | C# plugin backend: Stream Deck WebSocket client, global hotkeys, Windows display (CCD) API |
| `com.lee-cleobury.displayhotkeys.sdPlugin/` | Plugin manifest, settings panel (`ui/settings.html`) and images |
| `make-icons.ps1` | Draws all the PNG icons |

## Command line (for testing)

```powershell
DisplayHotkeys.exe --show            # current layout
DisplayHotkeys.exe --selftest        # capture + ask Windows to validate, changes nothing
DisplayHotkeys.exe --list
DisplayHotkeys.exe --save Desk
DisplayHotkeys.exe --apply TV
```
