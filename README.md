# Shared Ship (Cosmoteer 0.30.5, Career co-op)

**Steam Workshop:** <https://steamcommunity.com/sharedfiles/filedetails/?id=3828324355>
(texts for all 8 languages in [`docs/workshop/`](docs/workshop); published with
`E:\DEV\cosmoteer\Energy\tools\workshop-publish` from the installed mod folder, which holds the hidden `.workshop` ID file).

Mark one (or a few) of your ships as **shared**: for every human player in the same Career game it then behaves as
their own ship. They can command it (move/attack/strafe, direct control, weapons and targets, crew and resources,
toggles, paint) and build/edit/repair/salvage on it, and the game treats it as theirs ship-to-ship too: its crew helps
build and repair their ships (and theirs help it) under *My Ships Only* construction assistance, construction resources
flow both ways between it and their ships, crew haul between them, and it is never offered as a trade partner. The
ship also shows up in each teammate's own-ship views (control groups, select-own, FTL readiness, comms, crew picking).
Two private ships of different players stay separate. Ships without the tag stay 100% vanilla.

Languages: every language the game ships (en, de, es, fr, ja, pt-br, ru, zh-cn), in `strings/`.

## Usage

- Select ship(s), open the ship menu **Transfer ship control...** (Career with 2+ players) and pick
  **Everyone (shared ship)**; on already shared ships the entry reads **Make private**. Shortcut: **Ctrl+Shift+H**.
  Putting `[S]` anywhere in the ship name (ship info dialog) does the same. Any player who can command the ship can
  toggle it.
- **Myself** in the same menu takes the selected ship(s): a teammate's shared ship becomes your private ship.
- Picking a player in that menu (vanilla) transfers the ship to them; a shared ship also becomes private (tag
  removed). Any player with shared access may do this; other players' non-shared ships stay non-transferable.
- The ship keeps its real owner for per-player accounting (money, upkeep, fleet counts, crew counts and crew limit).
- Construction assistance: *My Ships Only* on the shared ship helps every teammate's ships, and on a teammate's ship
  it helps the shared ship, exactly like between one player's own ships.
- Resource trades from the shared ship pay or charge the player who makes the trade, not the owner.

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
  three `...LocalPlayer` getters, a postfix on `SimShipsManager.LocalPlayerShips`, and a prefix/postfix on
  `MPSetShipInfoInput.Execute` that cancels teammates' direct-control commands when the tag is removed.
- Ship-to-ship ownership: a postfix on `Ship.IsSamePlayerAs(Ship)` adds `SharedRules.IsSharedPair` (ships of two
  different Career humans, at least one shared). That one vanilla predicate drives Owned construction/repair assist,
  construction resource sourcing and crew-carried resources, buyable-ship exclusion and hauling routes. Postfixes on
  `SimModeManager.CanSalvage` (own-ship salvage) and `CrewSoul.IsLocalPlayerCrew` (UI) cover the two places that
  compare owners directly. Trade money is routed to the issuing player in `ResourceTransferJob`.
- `MPTransferShipControlInput.Execute` is replaced by a copy that also accepts senders with shared access and strips
  the tag on transfer; a postfix on `CareerGameModeManager.CreateModeSpecificGui` adds the menu entries.
- Cosmoteer.dll is ReadyToRun, so some precompiled methods contain inlined copies of those checks. The mod reads the
  exact list from the image's R2R inlining table (CrossModuleInlineInfo) and patches those methods too (21 in 0.30.5,
  all MPInput.Execute / UI helpers, none generic). Total ~0.2 s at startup; every patched inliner is logged to
  `SharedShip.log` next to the DLL.
- All-or-nothing: if any patch or the inlining-table read fails, all of the mod's patches are removed, the game runs
  vanilla and the log says `SharedShip DISABLED` (teammates still running the mod would then desync).

## Known limits

- Removing the tag cancels teammates' direct control only; other queued orders they gave stay until replaced.
- Two players building on the same shared ship at the same moment can override each other's pending blueprint edits
  (each build input is applied in order).
- Build/repair costs are charged to the player who issues the build, not the owner.
- Crew stay per-player: crew moved between a teammate's ship and the shared ship change owner and count against the
  receiving owner's crew limit (vanilla), and homeless crew only adopt their own owner's ships. Lifting this would let
  players exceed the crew limit by shuffling crew through the shared ship.
- A shared ship cannot salvage parts of a teammate's private ship (vanilla checks the salvaging ship's owner).
- `GetShipsForPlayer` is not changed (sim code counts ships per owner), so owner-only systems (e.g. fleet limits,
  owner's ship counters) still see the shared ship only under its owner.
- Hotkey Ctrl+Shift+H is fixed; it is ignored while a dialog is open.

## Build

`dotnet build -c Release` (needs `assembly-publicizer` global tool; lib\ is generated from the installed game).
References the loader's own `0Harmony.dll` without copying it.
