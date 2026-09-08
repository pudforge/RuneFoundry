RuneFoundry
Mod tools for Warcraft II Remastered


WHAT IS IN THIS FOLDER

RuneFoundry Launcher.exe    Installs mods and starts the game with one applied.
RuneFoundry Editor.exe      Makes and edits mods.

Both programs share the files around them. Keep the folder together.
Neither program needs installing. Run either one directly.

RuneFoundry is not made by Blizzard Entertainment and is not endorsed by them.
Warcraft II belongs to Blizzard Entertainment.


PLAY A MOD

1. Run RuneFoundry Launcher.
2. Point it at your Warcraft II Remastered folder if it asks.
3. Add a mod file. Mods end in .w2mod.
4. Pick the mod and press Play.

One mod applies at a time. Choosing another puts the first one's files back.


MAKE A MOD

1. Run RuneFoundry Editor.
2. Create a project, or open one you already have.
3. Pick a game file and press Copy and edit. Your copy goes in the project folder.
4. Press Test to build the mod, apply it and start the game.

The project folder is an ordinary directory. You can add files to it with Explorer.


YOUR GAME FILES ARE SAFE

RuneFoundry copies the original of every file a mod replaces. The copies live in:

    C:\ProgramData\RuneFoundry\originals

Removing a mod puts those files back. If a program ever fails partway, copy that
folder over your game's x86\Data folder by hand. The layout matches.

RuneFoundry never changes the game's program file. The game refuses to start if
that file is edited.


ONE FEATURE TOUCHES THE RUNNING GAME

Changing a mission's victory condition writes two numbers into the running game.
Nothing else does. Warcraft II Remastered includes Blizzard's Warden anti-cheat.
The editor explains this and asks before it writes. You can decline and everything
else in the mod still applies.


IF SOMETHING GOES WRONG

The game will not start after applying a mod.
    Open the launcher, pick Vanilla in the list and press Play. Vanilla is the
    game as Blizzard shipped it, so choosing it puts every original file back.

The launcher cannot find the game.
    Open Settings and choose the folder holding x86\Data.

A mod applied, but the game looks unchanged.
    Check that the mod is the one marked active in the launcher list. Use
    Verify files on the Game menu to confirm every replaced file matches.
