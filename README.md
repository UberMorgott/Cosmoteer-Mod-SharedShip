# Shared Ship (Cosmoteer 0.30.5, Career co-op)

Mark one (or a few) of your ships as **shared**: every human player in the same Career game can then command it
(move/attack/strafe, direct control, weapons and targets, crew and resources, toggles, paint) and build/edit/repair on
it. The ship also shows up in each teammate's own-ship views (control groups, select-own, FTL readiness, comms).
Ships without the tag stay 100% vanilla.

## Usage
- Put `[S]` anywhere in the ship's name (ship info dialog), **or** select the ship(s) and press **Ctrl+Shift+H** to
  toggle the tag. Removing the tag unshares the ship. Any player who can command the ship can toggle it.
- The ship keeps its real owner (money, upkeep, fleet counts, ownership-based rules are the owner's).
- Construction assistance from teammates' ships follows the vanilla per-ship setting: set it to *Allied* if teammates'
  ships should help build/supply it.

## Install (every player)
1. Subscribe to / install **Yet Another Mod Loader** (workshop 3577650065) and follow its install steps.
2. Copy the `SharedShip` folder (`SharedShip.dll` + `mod.rules`) into
   `%USERPROFILE%\Saved Games\Cosmoteer\<steamid>\Mods\SharedShip`.
3. Enable it in the in-game Mods list and **trust** its DLL there (the loader refuses untrusted libraries).
4. **All players must run the exact same SharedShip version.** The permission check runs inside lockstep inputs; a
   player without the mod (or with another version) will desync.

## How it works
- Flag = ship name contains `[S]`. The name is synced through the vanilla `MPSetShipInfoInput`, so every client sees
  the same flag at the same tick; nothing else is stored.
- Extra permission (`SharedRules.IsSharedWith`): game mode is Career, the ship's owner and the requesting player are
  both human slots (`CareerSimModeManager.IsHumanPlayerAllegiance`), and the name has the tag. AI factions, PvP and
  creative are untouched.
- Harmony prefixes on `CommandManager.IsCommandableBy / CanExecuteCommandsFrom / CanExecuteCrewCommandsFrom` and the
  three `...LocalPlayer` getters, plus a postfix on `SimShipsManager.LocalPlayerShips`.
- Cosmoteer.dll is ReadyToRun, so callers may contain inlined copies of those tiny methods. The mod therefore scans
  Cosmoteer.dll's IL and also patches every caller (transitively through small wrappers such as
  `Ship.IsLocalPlayerShip`), redirecting the calls to `SharedRules`. About 150 methods, ~0.5 s once at startup; the
  count is written to `SharedShip.log` next to the DLL.

## Known limits
- Two players building on the same shared ship at the same moment can override each other's pending blueprint edits
  (each build input is applied in order).
- Build/repair costs are charged to the player who issues the build, not the owner.
- Crew transfer between teammates' ships and the shared ship, resource trade, and money stay vanilla (owner-based).
- `GetShipsForPlayer` is not changed (sim code counts ships per owner), so owner-only systems (e.g. fleet limits,
  owner's ship counters) still see the shared ship only under its owner.
- Hotkey Ctrl+Shift+H is fixed; it is ignored while a dialog is open.

## Build
`dotnet build -c Release` (needs `assembly-publicizer` global tool; lib\ is generated from the installed game).
References the loader's own `0Harmony.dll` without copying it.
