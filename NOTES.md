# Internal notes (not exported)

## Game facts used by the bot

From the game's code (GameMaker 1.4, `data.win`):

- Shapes: `oSolid.blocktype` 1 triangle up, 2 triangle down, 3 square up, 4 square down; bound to
  `CONTROL_1..4` (`%APPDATA%\WAVESHAPER\config.ini`, `[KEYBOARD_CONTROLS] KEY1..KEY5`, KEY5 = restart).
- A shape is destroyed on first contact if `active_keypress` (held) or `last_keypress` (sticky)
  matches its type. Holding a different key during contact is a wrong key; passing untouched is a
  miss. Each costs 1 health; damage wipes every shape on screen and starts a 0.5 s grace period.
- Geometry (480x270 room): player at (80,136), 16 px wide mask; baseline y=136; contact starts when a
  shape's centre reaches x=103, ends below x=57.
- Health colours: 3 lime, 2 yellow, 1 orange (255,160,64), 0 red.
- Start speeds: Normal 250 px/s, Hard 350, Normal (Short) 250+160; +1 per shape destroyed.
- High score, streak and full heals are stored only when the player dies. Restart (KEY5) or Esc
  stores total score and shape counts. Attempts are stored as soon as the score leaves its start value.
- `stat_23` is the game's tamper flag: set when the score jumps by more than 3 in one frame; it stops
  all stat saving. Key presses can't trigger it.
- Menus: arrows or WASD move, Enter/Space/Shift select, Esc/Backspace go back. Selecting with a
  controller button switches the game to gamepad bindings (keyboard game keys are then ignored).
  Fresh main menu: cursor on PLAY GAME, mode NORMAL. Play submenu row 0 cycles NORMAL / NORMAL (SHORT)
  / HARD MODE with left/right (wraps); row 1 HOW TO PLAY (RTFM). Options: DOWN x4 = CUSTOM FILES;
  its row 0 toggles Custom Sprites (loads `Sprites/spr_triangle_up_1.png`, save folder first).

## Stats

Achievement progress stats (schema `UserGameStatsSchema_562260.bin`): `high_score`, `high_streak`,
`hm_high_score`, `hm_high_streak`, `full_heals`, `hm_full_heals`, `combined_total_score` (5,000),
`combined_total_attempts` (30), `shape_1..4_count` (400). Event achievements: RTFM, SCORE_0, SPRITE,
CONTROLS, SCREENSHOT, FRIENDS_HIGHSCORE.

## Restore leaderboards

The game's own steam_api.dll (old SDK) can't connect outside a Steam launch: current Steam clients leave
`HKCU\Software\Valve\Steam\ActiveProcess` pid/ActiveUser at 0, which that library checks. The helper
talks to `steamclient.dll` instead (SteamClient017, SteamUtils008, STEAMUSERSTATS_INTERFACE_VERSION011)
through vtable offsets, with `SteamAppId` set. Before writing it requires GetAppID() == 562260 and the
high_score read back to equal Steam's local cache. MSVC orders overloaded virtuals in reverse, so
GetStat(int32) is slot 2 and SetStat(int32) slot 4.

## Detection notes

- Capture uses `PrintWindow` (client area, full content), falling back to screen copy.
- Explosion particles (sprite mode) can form shape-sized blobs near the player; they sit over an
  unbroken baseline, which real shapes never do. Each phantom counted as a point and ended capped
  runs short (259/257 on Hard) before the baseline check and the new-target distance guard.

## Release

1. `./build.ps1`, then `bin\autoshaper-cli.exe --self-test` and `--render <folder>` for a visual check.
2. Bump `src\AssemblyInfo.cs`.
3. `./package.ps1 -OutputDirectory ..\..\release\outputs` (single exe + SHA-256).
4. `./export-public.ps1 -LicenseHolder "<name>"` into an empty `..\..\release\public`.

Research tools (bytecode disassembler, room dumper, stats readers) live outside this repository in
`..\research` and are never exported.
