CELESTE MODE for RetroBat - version {{VERSION}}
=================================================

Play NES games the Celeste way: any hit is a death, and a death sends you
back to the last checkpoint instead of costing a life. EASY has the most
checkpoints and no rewind on a mere hit in a boss room; MEDIUM and HARD
have fewer. The lives counter shows how many times you died.

REQUIREMENTS
- Windows 10 or 11 (nothing else to install: PowerShell and .NET come with
  Windows).
- RetroBat (made with 8.2.1), with your NES games in roms\nes, scanned by
  RetroBat (the games must appear in its NES list).
- The exact game versions listed below. The installer checks each file.
- RetroAchievements "hardcore" mode off: it forbids the save states Celeste
  mode rewinds with.

INSTALL
1. Close RetroBat.
2. Double-click "Install Celeste Mode.bat".
3. Choose your RetroBat folder (the one holding "emulationstation" and
   "emulators").
4. Read the report: the games installed, the ones found in another version,
   the ones not found.
5. Start RetroBat: the games are in the new "Celeste mode" system.

Run the installer again after adding games: it adds the new matches and
keeps your play counts and favourites.

PLAYING
- Hold Circle for 1 second: a hit no longer sends you back (losing a life
  still does); hold Triangle for 1 second: back on. L1: back to the
  checkpoint before the last one, your deaths kept. (On another pad than a
  DualSense: the buttons in the same places, Circle the right face button,
  Triangle the top one.)
- Mega Man games: Select on the robot master selection screen gives every
  weapon and opens the late stages; in Mega Man 3 and 6 a second press
  goes further (Wily's castle).
- Options per system or per game (in RetroBat, on a game: ADVANCED GAME
  OPTIONS, CELESTE MODE): CHECKPOINTS (EASY, MEDIUM, HARD), CONTINUOUS
  MUSIC, COUNT LIVES. AUTO uses the default named in each option's
  description.

WHAT IT CHANGES ON YOUR PC
- Adds system\celeste, roms\celeste and two files in
  emulationstation\.emulationstation (es_systems_celeste.cfg,
  es_features_celeste.cfg).
- During a Celeste game only: turns on RetroArch's network commands if they
  were off (the mode talks to RetroArch through them, on this PC only), and
  puts the setting back when the game ends. Windows may ask once whether
  RetroArch may use the network: "deny" is fine.
- Your own games, their save states and your settings are left as they are.

UNINSTALL
Close RetroBat, double-click "Uninstall Celeste Mode.bat", choose the folder.
It asks before removing roms\celeste (the Celeste games list and its play
counts).

SUPPORTED GAMES
{{GAMES}}
