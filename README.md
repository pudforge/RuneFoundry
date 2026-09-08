# RuneFoundry

RuneFoundry is a set of mod tools for Warcraft II Remastered. It has two programs.

- The **Launcher** installs mods and plays them.
- The **Editor** makes mods.

Both programs are in the same download. Keep the folder together. They share the files
around them, and neither one needs installing.

RuneFoundry is not made by Blizzard Entertainment and is not endorsed by them.

## Get RuneFoundry

Download the zip from [Releases](https://github.com/pudforge/RuneFoundry/releases). It holds
both programs. Unzip it anywhere and run either one. Nothing needs installing.

This is an alpha. It edits the game you have installed. Keep a backup of your saves.

Two videos:

- [Feature walkthrough](https://www.youtube.com/watch?v=2CbPMADp0ao) shows what the two
  programs do.
- [Example campaign](https://www.youtube.com/watch?v=jKroUVWjOF0) plays through a campaign
  made with them.

## Play a mod

Open the **Launcher**.

1. Choose **File → Install a mod…** and select a `.w2mod` file. You can also drag the file
   onto the window.
2. Select the mod in the list.
3. Press **Play**.

The Launcher applies one mod at a time. To go back to the original game, select **Vanilla**
and press **Play**.

**Apply** changes the game files but does not start the game.

**Game → Verify files** checks that each replaced file is still correct. Use it if a game
update changed something.

## Make a mod

Open the **Editor** and choose **File → New project…**.

| Tab | Contents |
|---|---|
| Files | Every game file |
| Mod details | Name, version, author, and the pictures, movies and music the whole game shows |
| Campaign | Missions, briefings, speech, objectives, scenario rules, acts |
| Units | Cost, damage, armour, speed |
| Upgrades | Cost and research time |
| AI scripts | The 84 computer-player scripts |
| Icons | Portraits and command buttons |
| Sprites | Unit art and building art |

Select a file, then press **Copy and edit**. Your copy goes in the project folder, and you
can change it there.

**File → Save & test** (F5) builds the mod, installs it, and starts the game.

**Ctrl+Z** takes back the last change.

**File → Build .w2mod…** makes the file you share with other people.

The project folder is an ordinary directory. Adding a file to it with Explorer works as well
as going through the Editor.

## Replace a unit's artwork

Open the **Sprites** tab and select a unit.

1. Press **Export art…** and choose a folder.
2. Paint on the grid the Editor writes. Keep the grid: same cell size, same number of cells.
   One row is one facing.
3. Press **Import art…** and choose the same folder.

The Editor packs your frames back into the game's sheet and rewrites its frame table. Use
straight alpha, not premultiplied.

## Edit an AI script

Open the **AI scripts** tab and select a script.

The list shows all 84. Each one has a fixed amount of room, which the tab reports as you
edit. **Add wave…** writes a whole attack wave. **Copy** puts the script on the clipboard as
text, and **Replace…** takes text back, which is how you move a script between slots or
share one.

A script that runs out of room needs the file laid out again. Press **Make room…**, or
accept the offer that appears when a save has no space left. Every script moves, so play
through any mission that runs an AI afterwards.

## Your files are safe

RuneFoundry copies each original game file before it replaces it. If you remove a mod, the
original files come back. Select **Vanilla** in the Launcher and press **Play** to go back to
the standard game at any time.

The copies are here:

```
C:\ProgramData\RuneFoundry\originals\
```

This folder uses the same layout as the game. To restore the game by hand, copy this folder
over:

```
C:\Program Files (x86)\Warcraft II Remastered\x86\Data\
```

RuneFoundry never changes the game's program file. The game refuses to start if that file is
edited.

Battle.net can also put the game back. Choose Warcraft II Remastered, then
**Settings → Scan and Repair**. This replaces every game file with Blizzard's own and leaves
your projects alone.

## Campaign objectives

The game does not keep victory conditions in a file. They are numbers in the running game.
RuneFoundry writes these numbers to the game memory while the game runs. It changes no file,
and the change ends when you close the game.

Warcraft II Remastered includes Blizzard's Warden anti-cheat, which starts with the game.
This function is optional. RuneFoundry writes nothing unless it recognises your game version
exactly, and the Editor explains the risks and asks first. If you decline, everything else in
the mod still applies.

## If something goes wrong

- **A mod does not appear in the game.** Press **Apply** or **Play** first. The Launcher does
  not write files until you do.
- **The game looks wrong after an update.** Choose **Game → Verify files**. A game update can
  replace a modded file.
- **You want the original game back.** Select **Vanilla** and press **Play**.

Both programs ask for administrator rights. The game is in Program Files, and applying a mod
writes files there.

## Notes

Consult Blizzard's end user licence agreement before you install a mod or distribute one.

RuneFoundry's own source is MIT licensed. See `LICENSE`. Warcraft II and all of its data,
artwork, music and recordings belong to Blizzard Entertainment, and RuneFoundry ships none of
them.

To build RuneFoundry from source, see `DEVELOPING.md`.
