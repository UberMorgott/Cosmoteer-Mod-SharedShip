# Shared Ship (Cosmoteer 0.30.5, Career co-op)

**Steam Workshop:** <https://steamcommunity.com/sharedfiles/filedetails/?id=3828324355>
(texts for all 8 languages in [`docs/workshop/`](docs/workshop); published with
`E:\DEV\cosmoteer\Energy\tools\workshop-publish` from the installed mod folder, which holds the hidden `.workshop` ID file).

Mark one (or a few) of your ships as **shared**: for every human player in the same Career game it then behaves as
their own ship. They can command it (move/attack/strafe, direct control, weapons and targets, crew and resources,
toggles, paint) and build/edit/repair/salvage on it, and the game treats it as theirs ship-to-ship too: its crew helps
build and repair their ships (and theirs help it) under *My Ships Only* construction assistance, construction resources
flow both ways between it and their ships, crew haul and move freely between them, it salvages their ships where they
could salvage their own, and it is never offered as a trade partner. The ship also shows up in each teammate's own-ship
views (control groups, select-own, FTL readiness, comms, crew picking). Two private ships of different players stay
separate. Ships without the tag stay 100% vanilla.

Every player keeps their own wallet: whoever does something pays for it, whoever sells or demolishes gets the money, and
a purchase cancelled before it arrives is refunded to whoever paid. A shared ship's crew counts toward everyone's crew
limit, split equally.

Languages: every language the game ships (en, de, es, fr, ja, pt-br, ru, zh-cn), in `strings/`.

## Usage

- Select ship(s), open the ship menu **Transfer ship control...** (Career with 2+ players) and pick
  **Everyone (shared ship)**; on already shared ships the entry reads **Make private**. Shortcut: **Ctrl+Shift+H**.
  Putting `[S]` anywhere in the ship name (ship info dialog) does the same. Any player who can command the ship can
  toggle it.
- **Myself** in the same menu takes the selected ship(s): a teammate's shared ship becomes your private ship.
- Picking a player in that menu (vanilla) transfers the ship to them; a shared ship also becomes private (tag
  removed). Any player with shared access may do this; other players' non-shared ships stay non-transferable.
- Construction assistance: *My Ships Only* on the shared ship helps every teammate's ships, and on a teammate's ship
  it helps the shared ship, exactly like between one player's own ships.
- A player who leaves keeps their ships (vanilla keeps each ship's owner slot): their shared ships stay shared and
  commandable by everyone else, their private ships wait for them and are theirs again when they rejoin.

## Wallets

- Building, activating blueprints (also ones another player placed), instant builds, hiring, techs: vanilla already
  charges the player who sends the input (`MPUpdateShipInput`, `CommHireTab`, `CommTechsTab`), on any ship.
- Trades (buy/sell/sell-back jobs): paid from and into the wallet of the player who made the trade, kept per job.
- Demolishing gives resources (vanilla), and selling them pays whoever sells.
- Pre-purchases (a blueprint buys its resources from nearby stations up front): refunded to whoever paid when they are
  cancelled (blueprint removed, construction cleared, station leaves, purchase expires, cancelled buy sold back).

## Crew limit

Vanilla: one fame-based limit for everyone; a player's crew = crew on the ships they own + their homeless crew + the
crew counts saved per sector when the fleet left it. With the mod a shared ship's crew counts split equally among all
players of the career (crew / N each, the remainder one each to the lowest slots), so moving crew onto a shared ship
is not a way around the limit. Example: A has a 3-crew ship, B a 5-crew ship, a shared ship has 10 → A counts 8, B 10.
Players of the career = connected players, owners of ships in this sector or mid-jump, and every player in another
sector's saved crew counts; leaving a sector saves the counts of offline players too.

## Install (every player)

1. Subscribe to / install **Yet Another Mod Loader** (workshop 3577650065) and follow its install steps.
2. Copy the `SharedShip` folder (`SharedShip.dll` + `mod.rules` + `strings`) into
   `%USERPROFILE%\Saved Games\Cosmoteer\<steamid>\Mods\SharedShip`.
3. Enable it in the in-game Mods list and **trust** its DLL there (the loader refuses untrusted libraries).
4. **All players must run the exact same SharedShip version.** The checks run inside lockstep inputs; a player
   without the mod (or with another version) will desync.

## How it works

- Flag = ship name contains `[S]`, synced through the vanilla `MPSetShipInfoInput`; nothing else marks a ship.
- Extra permission (`SharedRules.IsSharedWith`): Career game, owner and requesting player are both human slots
  (`CareerSimModeManager.IsHumanPlayerAllegiance`), name has the tag. AI factions, PvP and creative are untouched.
- Player-to-ship: prefixes on `CommandManager.IsCommandableBy / CanExecuteCommandsFrom / CanExecuteCrewCommandsFrom`
  and the three `...LocalPlayer` getters; postfix on `SimShipsManager.LocalPlayerShips`; prefix/postfix on
  `MPSetShipInfoInput.Execute` cancels teammates' direct control when the tag is removed; `MPTransferShipControlInput`
  accepts shared senders and strips the tag.
- Ship-to-ship: postfix on `Ship.IsSamePlayerAs(Ship)` adds `SharedRules.IsSharedPair` (drives construction/repair
  assist, resource sourcing, hauling, buyable-ship exclusion, and now crew moves and salvage). Postfixes on
  `SimModeManager.CanSalvage` and `CrewSoul.IsLocalPlayerCrew`; homeless crew adopt via `SharedRules.IsOwnFor`.
- Acting player (`Acting`): the game's input dispatch (`BaseMPManager` / SP managers) is routed through one wrapper
  that knows the sender of the input being executed.
- Payers (`Payers`): every trade job records the acting player at creation; the job's money code
  (`ResourceTransferJob`) reads it; jobs of different payers are never merged (`ResourceManager.SinkInfo`).
- Pre-purchase ledger (`PrePurchases`): lots of (payer, units) follow pre-purchased units on stations and in crew jobs;
  vanilla decides which units go, the ledger only says who paid them.
- Crew (`SharedCrew`): postfix on `SimCrewManager.CountCrewForPlayer` (the one crew count vanilla uses) and on
  `CareerGameModeManager.OnSwitchingSimulation`.
- Saves: payer data is written as extra optional entries, which the vanilla loader skips; all of it is part of the
  multiplayer integrity hash.
- Cosmoteer.dll is ReadyToRun, so some precompiled methods contain inlined copies of patched checks. The mod reads the
  exact list from the image's R2R inlining table (CrossModuleInlineInfo) and patches those methods too (21 in 0.30.5).
  Every patched inliner is logged to `SharedShip.log` next to the DLL.
- All-or-nothing: if any patch, IL pattern or the inlining-table read fails, all of the mod's patches are removed, the
  game runs vanilla and the log says `SharedShip DISABLED` (teammates still running the mod would then desync).

## Known limits

- Removing the tag cancels teammates' direct control only; other queued orders they gave stay until replaced.
- Two players building on the same shared ship at the same moment can override each other's pending blueprint edits
  (each build input is applied in order).
- Crew split: a player with zero ships who was never connected when the fleet left a sector is not counted.
- Sectors saved before 0.5.0 hold owner-only crew counts until the next visit (they update when you leave them again).
- Saves made with the mod load without it, but **finish or cancel pending trades before removing the mod**: when two
  players have pending trades with the same source, resource and price, vanilla keeps one job per such key and drops
  the other on load, together with the money held for it.
- Sell-back after a pre-purchase: one crew carry that holds units of several payers is indexed under the payer with
  the most units; the sale itself is split exactly.
- Trades and pre-purchases made before 0.5.0 have no payer record and settle with the ship owner (vanilla).
- `GetShipsForPlayer` is not changed (sim code counts ships per owner): fleet limits and owner-only counters still see
  the shared ship under its owner.
- Hotkey Ctrl+Shift+H is fixed; it is ignored while a dialog is open.

## 2-player test checklist

Host A and guest B, both with the same SharedShip build, a Career game; A shares ship X.

1. B commands X, builds on X and activates a blueprint A placed: B's money pays, A's does not change.
2. A places a blueprint on X that pre-buys from a station, B removes it before it is built: A is refunded.
3. B buys resources at a station into X, then cancels: B is refunded; B sells from X: B is paid.
4. A and B each buy the same resource from the same station into X at once: two separate jobs, each pays their own.
5. Crew: A 3-crew ship, B 5-crew ship, X 10 crew: the hire screen shows A 8, B 10; move crew between B's ship and X
   freely; destroy a crew member's home ship near X: the crew settles on X.
6. X salvages a part of B's private ship; B's private ship cannot be salvaged by A's private ship.
7. B leaves: A still commands X (also when B owns X); B's private ships stay B's and do not move. Jump to another
   sector and back, B rejoins from the save: B's private ships are B's again, the crew split is unchanged.
8. Save/load round trip in vanilla: with pending trades and pre-purchases, save; disable the mod (Mods list), restart,
   load the save: it loads, ships and money are intact; re-enable the mod, load again: payers still apply (step 2/3).
9. Watch for desync messages during all of the above; `SharedShip.log` must say `Patched ... methods`.

## Build

`dotnet build -c Release` (needs `assembly-publicizer` global tool; lib\ is generated from the installed game).
References the loader's own `0Harmony.dll` without copying it.
