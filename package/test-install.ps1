# Installer test : a scratch RetroBat folder under dist\test-rb
# with four NES entries, then Install-CelesteMode.ps1 and
# Uninstall-CelesteMode.ps1 from the built zip's files\. Cases:
#   Contra (USA).nes          its name and file: installed
#   "Mega Man II.nes"         renamed, gamelist name "Mega Man 2": installed
#   "Castlevania (Europe).nes" Super C's bytes under Castlevania's name: not
#                             installed (no Castlevania rule file since the
#                             rewind files were archived, 2026-10-12)
#   "Double Dragon.zip"       the ROM inside a zip: installed (Super C until
#                             it left the release for a while, 2026-10-13)
#   snes\"Mega Man X.smc"     Mega Man X behind a 512-byte copier header: no
#                             rule file since 2026-10-12, not installed
#   nes\more\"Mega Man 3 (USA).nes"  on disk, in no gamelist: found by its
#                             file name, installed under the rule's name
# The scratch folder has a NES core (es_systems.cfg + a core file: the
# installer checks it) and a theme with art folders (the background and logo
# go in, and come out with the uninstaller, as do the Celeste options).
# Run from C:\celeste-mode after build.ps1. Exit 0 = all as expected.
param([string]$RetroBat = 'C:\RetroBat')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot | Split-Path
$version = ([IO.File]::ReadAllText((Join-Path $root 'VERSION'))).Trim()
$files = Join-Path $root "dist\celeste-mode-$version\files"
$rb = Join-Path $root 'dist\test-rb'
if (Test-Path -LiteralPath $rb) { Remove-Item -LiteralPath $rb -Recurse -Force }   # this test's own folder
$nes = Join-Path $rb 'roms\nes'
New-Item -ItemType Directory -Force -Path (Join-Path $rb 'emulationstation\.emulationstation'), (Join-Path $rb 'emulators\retroarch'), $nes | Out-Null
'' | Set-Content (Join-Path $rb 'emulationstation\emulationstation.exe'); '' | Set-Content (Join-Path $rb 'emulators\retroarch\retroarch.exe')
New-Item -ItemType Directory -Force -Path (Join-Path $rb 'emulators\retroarch\cores'), (Join-Path $rb 'emulationstation\.emulationstation\themes\T\art\logos'), (Join-Path $rb 'emulationstation\.emulationstation\themes\T\art\background') | Out-Null
'' | Set-Content (Join-Path $rb 'emulators\retroarch\cores\fceumm_libretro.dll')
'<?xml version="1.0"?><systemList><system><name>nes</name><emulators><emulator name="libretro"><cores><core default="true">fceumm</core></cores></emulator></emulators></system></systemList>' | Set-Content (Join-Path $rb 'emulationstation\.emulationstation\es_systems.cfg')
$src = Join-Path $RetroBat 'roms\nes'
Copy-Item -LiteralPath (Join-Path $src 'Contra (USA).nes') -Destination $nes
Copy-Item -LiteralPath (Join-Path $src 'Mega Man 2 (USA).nes') -Destination (Join-Path $nes 'Mega Man II.nes')
Copy-Item -LiteralPath (Join-Path $src 'Super C (USA).nes') -Destination (Join-Path $nes 'Castlevania (Europe).nes')
New-Item -ItemType Directory -Force -Path (Join-Path $nes 'more') | Out-Null
Copy-Item -LiteralPath (Join-Path $src 'Mega Man 3 (USA).nes') -Destination (Join-Path $nes 'more\Mega Man 3 (USA).nes')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::Open((Join-Path $nes 'Double Dragon.zip'), 'Create')
$null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, (Join-Path $src 'Double Dragon (USA).nes'), 'Double Dragon (USA).nes'); $z.Dispose()
@'
<?xml version="1.0"?>
<gameList>
	<game><path>./Contra (USA).nes</path><name>Contra</name><desc>test</desc></game>
	<game><path>./Mega Man II.nes</path><name>Mega Man 2</name></game>
	<game><path>./Castlevania (Europe).nes</path><name>Castlevania</name></game>
	<game><path>./Double Dragon.zip</path><name>Double Dragon</name></game>
</gameList>
'@ | Set-Content (Join-Path $nes 'gamelist.xml') -Encoding UTF8

# an es_settings.cfg with one Celeste option the player set (kept: a
# reinstall leaves a player's choice alone) and one other setting of his own
@(
  '<?xml version="1.0"?>', '<config>',
  '  <string name="celeste[&quot;Contra (USA).yaml&quot;].rewind_to_screen" value="2" />',
  '  <string name="nes.ratio" value="4/3" />', '</config>'
) -join "`r`n" | Set-Content -LiteralPath (Join-Path $rb 'emulationstation\.emulationstation\es_settings.cfg') -Encoding ASCII -NoNewline

$snes = Join-Path $rb 'roms\snes'
New-Item -ItemType Directory -Force -Path $snes | Out-Null
$mmx = [IO.File]::ReadAllBytes((Join-Path $RetroBat 'roms\snes\Mega Man X (USA) (Rev 1).sfc'))
[IO.File]::WriteAllBytes((Join-Path $snes 'Mega Man X.smc'), [byte[]]((New-Object byte[] 512) + $mmx))
@'
<?xml version="1.0"?>
<gameList>
	<game><path>./Mega Man X.smc</path><name>Mega Man X</name></game>
</gameList>
'@ | Set-Content (Join-Path $snes 'gamelist.xml') -Encoding UTF8
$ok = $true
function Fail([string]$m) { $script:ok = $false; "FAIL $m" }
$out = & (Join-Path $files 'Install-CelesteMode.ps1') -RetroBat $rb -SkipRunningCheck 6>&1 | Out-String
$out
$cel = Join-Path $rb 'roms\celeste'
# only the games on checkpoints are installed (2026-10-12: the rewind rule
# files are archived, Mega Man X and Castlevania among them, so the copier
# header and wrong-version cases have no rule file left to check)
foreach ($c in @(@('Contra (USA).yaml', 'nes/Contra (USA).nes'), @('Mega Man 2 (USA).yaml', 'nes/Mega Man II.nes'), @('Double Dragon (USA).yaml', 'nes/Double Dragon.zip'), @('Mega Man 3 (USA).yaml', 'nes/more/Mega Man 3 (USA).nes'))) {
  $f = Join-Path $cel $c[0]
  if (-not (Test-Path -LiteralPath $f)) { Fail "$($c[0]) not installed"; continue }
  if (-not (Select-String -LiteralPath $f -SimpleMatch ('rom: "' + $c[1] + '"') -Quiet)) { Fail "$($c[0]): rom is not $($c[1])" }
}
if (Test-Path -LiteralPath (Join-Path $cel 'Castlevania (USA) (Rev A).yaml')) { Fail 'Castlevania installed on the wrong ROM' }
$games = @(([xml](Get-Content (Join-Path $cel 'gamelist.xml') -Raw -Encoding UTF8)).gameList.game).Count
if ($games -ne 4) { Fail "celeste gamelist has $games games, expected 4" }
foreach ($f in 'art\logos\celeste.png', 'art\background\celeste.jpg') { if (-not (Test-Path -LiteralPath (Join-Path $rb "emulationstation\.emulationstation\themes\T\$f"))) { Fail "theme art $f not installed" } }
# (captured, the coloured mark and the name are two records: a line break between them)
if (-not ($out -match 'v\s+Mega Man 3 \(USA\)' -and $out -match 'x\s+Batman')) { Fail 'the list does not show v / x per game' }
$es = [IO.File]::ReadAllText((Join-Path $rb 'emulationstation\.emulationstation\es_settings.cfg'))
foreach ($w in @(
  'celeste[&quot;Contra (USA).yaml&quot;].rewind_to_screen" value="2"',
  'celeste[&quot;Contra (USA).yaml&quot;].continuous_music" value="1"',
  'celeste[&quot;Contra (USA).yaml&quot;].count_lives" value="1"',
  'celeste[&quot;Contra (USA).yaml&quot;].checkpoints" value="medium"',
  'name="nes.ratio" value="4/3"')) { if (-not $es.Contains($w)) { Fail "es_settings.cfg lacks $w" } }
if (([regex]::Matches($es, 'Contra \(USA\)\.yaml&quot;\]\.rewind_to_screen')).Count -ne 1) { Fail 'the player''s old option not kept exactly once' }
if ($es.Contains('rewind_time')) { Fail 'a rewind_time option was written' }
if (-not $es.Contains("`r`n") -or ([regex]::Matches($es, "(?<!`r)`n")).Count) { Fail 'es_settings.cfg line endings changed' }
foreach ($f in 'system\celeste\launch.ps1', 'system\celeste\Celeste.cs', 'system\celeste\PadAudio.cs', 'emulationstation\.emulationstation\es_systems_celeste.cfg', 'emulationstation\.emulationstation\es_features_celeste.cfg') {
  if (-not (Test-Path -LiteralPath (Join-Path $rb $f))) { Fail "$f missing" }
}
$un = & (Join-Path $files 'Uninstall-CelesteMode.ps1') -RetroBat $rb -SkipRunningCheck -RemoveGames 6>&1 | Out-String
$un
foreach ($f in 'system\celeste', 'roms\celeste', 'emulationstation\.emulationstation\es_systems_celeste.cfg') { if (Test-Path -LiteralPath (Join-Path $rb $f)) { Fail "$f left after uninstall" } }
if (-not (Test-Path -LiteralPath (Join-Path $nes 'Contra (USA).nes'))) { Fail 'the uninstaller touched the NES games' }
if (Test-Path -LiteralPath (Join-Path $rb 'emulationstation\.emulationstation\themes\T\art\logos\celeste.png')) { Fail 'theme art left after uninstall' }
$es2 = [IO.File]::ReadAllText((Join-Path $rb 'emulationstation\.emulationstation\es_settings.cfg'))
if ($es2.Contains('celeste[') -or -not $es2.Contains('name="nes.ratio" value="4/3"')) { Fail 'uninstall: Celeste options left, or the player''s own setting lost' }
# no ROM at all: the sentence on where ROMs are searched
$empty = Join-Path $root 'dist\test-rb-empty'
if (Test-Path -LiteralPath $empty) { Remove-Item -LiteralPath $empty -Recurse -Force }
Copy-Item -LiteralPath $rb -Destination $empty -Recurse
Remove-Item -LiteralPath (Join-Path $empty 'roms\nes') -Recurse -Force
$none = & (Join-Path $files 'Install-CelesteMode.ps1') -RetroBat $empty -SkipRunningCheck 6>&1 | Out-String
if (-not $none.Contains('looks for NES ROMs')) { Fail 'no-ROM case: the sentence on where ROMs are searched is missing' }
Remove-Item -LiteralPath $empty -Recurse -Force
if ($ok) { 'install test: PASS'; exit 0 } else { 'install test: FAILED'; exit 1 }
