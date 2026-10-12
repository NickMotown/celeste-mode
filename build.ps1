# Builds the Celeste mode release zip, from this folder's layout (the
# release repository):
#   VERSION, README.md (the repo's), build.ps1
#   package\   Install-CelesteMode.ps1, Uninstall-CelesteMode.ps1, RomHash.ps1,
#              the two .bat files, README.txt (the zip's)
#   runtime\   launch.ps1, Celeste.cs, PadAudio.cs, install.ps1, Art.ps1,
#              es_features_celeste.cfg, es_systems_celeste.cfg
#   art\       celeste.png (logo), celeste.jpg (background): into the zip's
#              runtime\, install.ps1 puts them in the themes
#   roms\celeste\   the rule files (.yaml)
# Writes dist\celeste-mode-<version>.zip:
#   Install Celeste Mode.bat, Uninstall Celeste Mode.bat, README.txt
#   files\   the installer scripts, compatibility.csv, runtime\, roms\celeste\
# compatibility.csv (rule,name,md5) is made here: each rule file's ROM, read
# from -RetroBat (the builder's own, the ROM the rule file was made on), hashed
# by RomHash.ps1, the same code the installer matches with. A rule file whose
# ROM is missing stops the build: a sheet entry nobody checked would install
# a game on the wrong dump.
param([string]$RetroBat = 'C:\RetroBat')
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$root = $PSScriptRoot
$version = ([IO.File]::ReadAllText((Join-Path $root 'VERSION'))).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION must read like 1.2.3, not '$version'" }
. (Join-Path $root 'package\RomHash.ps1')

$rules = @(Get-ChildItem -LiteralPath (Join-Path $root 'roms\celeste') -Filter *.yaml | Sort-Object Name)
if (-not $rules.Count) { throw 'no rule files in roms\celeste' }
$rows = New-Object System.Collections.Generic.List[object]
foreach ($r in $rules) {
  $text = [IO.File]::ReadAllText($r.FullName, $utf8)
  $name = [regex]::Matches($text, '(?m)^name:[ \t]*"?([^"#\r\n]*?)"?[ \t]*(#.*)?\r?$')
  $rom = [regex]::Matches($text, '(?m)^rom:[ \t]*"?([^"#\r\n]*?)"?[ \t]*(#.*)?\r?$')
  if ($name.Count -ne 1 -or $rom.Count -ne 1) { throw "$($r.Name): expected one 'name:' and one 'rom:' line" }
  $file = Join-Path (Join-Path $RetroBat 'roms') ($rom[0].Groups[1].Value -replace '/', '\')
  if (-not (Test-Path -LiteralPath $file)) { throw "$($r.Name): its ROM is missing ($file): the checksum cannot be taken" }
  $rows.Add([pscustomobject]@{ rule = $r.Name; name = $name[0].Groups[1].Value; md5 = (RomMd5 $file) })
}
"checksums: $($rows.Count) rule files"

$dist = Join-Path $root 'dist'
$top = Join-Path $dist "celeste-mode-$version"
if (Test-Path -LiteralPath $top) { Remove-Item -LiteralPath $top -Recurse -Force }   # this build's own staging folder
$files = Join-Path $top 'files'
New-Item -ItemType Directory -Force -Path (Join-Path $files 'runtime'), (Join-Path $files 'roms\celeste') | Out-Null
foreach ($f in 'Install Celeste Mode.bat', 'Uninstall Celeste Mode.bat') { Copy-Item -LiteralPath (Join-Path $root "package\$f") -Destination $top }
# the zip's README: its version and the supported games filled in, CRLF for Notepad
$readme = [IO.File]::ReadAllText((Join-Path $root 'package\README.txt'), $utf8)
$list = ($rows | ForEach-Object { '- ' + $_.name }) -join "`n"
$readme = $readme.Replace('{{VERSION}}', $version).Replace('{{GAMES}}', $list) -replace "`r?`n", "`r`n"
[IO.File]::WriteAllText((Join-Path $top 'README.txt'), $readme, $utf8)
if (Test-Path -LiteralPath (Join-Path $root 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $top }
foreach ($f in 'Install-CelesteMode.ps1', 'Uninstall-CelesteMode.ps1', 'RomHash.ps1') { Copy-Item -LiteralPath (Join-Path $root "package\$f") -Destination $files }
Copy-Item -Path (Join-Path $root 'runtime\*') -Destination (Join-Path $files 'runtime')
foreach ($f in 'celeste.png', 'celeste.jpg') { Copy-Item -LiteralPath (Join-Path $root "art\$f") -Destination (Join-Path $files 'runtime') }
foreach ($r in $rules) { Copy-Item -LiteralPath $r.FullName -Destination (Join-Path $files 'roms\celeste') }
$rows | Export-Csv -LiteralPath (Join-Path $files 'compatibility.csv') -NoTypeInformation -Encoding UTF8

$zip = Join-Path $dist "celeste-mode-$version.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path $top -DestinationPath $zip
$n = (Get-ChildItem -LiteralPath $top -Recurse -File).Count
"zip: $zip ($n files, $([math]::Round((Get-Item -LiteralPath $zip).Length / 1KB)) KB)"
