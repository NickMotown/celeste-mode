# Install Celeste mode on the PC , from the
# files `celeste.sh install` copied to $Stage:
#   launch.ps1, Celeste.cs, PadAudio.cs -> <RetroBat>\system\celeste\
#   games\*.yaml (rule files)    -> <RetroBat>\roms\celeste\
#   each game's entry in its own system's gamelist (text, art, video) ->
#     roms\celeste\media\<kind>\ and a <game> in roms\celeste\gamelist.xml
# Never deletes. A <game> already in the celeste gamelist is kept as it is
# (EmulationStation writes play counts and favourites into it); its media
# files are refreshed only when missing.
param(
  [string]$Stage = $PSScriptRoot,
  [string]$RetroBat = 'C:\RetroBat',
  # the release's test-install.ps1 only: a scratch RetroBat folder while the
  # builder's own EmulationStation may be open
  [switch]$SkipRunningCheck
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)

$sys = Join-Path $RetroBat 'system\celeste'
$roms = Join-Path $RetroBat 'roms'
$dest = Join-Path $roms 'celeste'
New-Item -ItemType Directory -Force -Path $sys, $dest | Out-Null
foreach ($f in 'launch.ps1', 'Celeste.cs', 'PadAudio.cs') { Copy-Item -LiteralPath (Join-Path $Stage $f) -Destination $sys -Force }
# poke.ps1: a development aid (celeste.sh try), not in the release zip
if (Test-Path -LiteralPath (Join-Path $Stage 'poke.ps1')) { Copy-Item -LiteralPath (Join-Path $Stage 'poke.ps1') -Destination $sys -Force }
# The Mesen engine's files, installed by earlier versions of this script (
#): ours, named one by one, moved into old\ rather than deleted.
foreach ($f in 'runtime.lua', 'pads.lua') {
  $old = Join-Path $sys $f
  if (Test-Path -LiteralPath $old) { New-Item -ItemType Directory -Force -Path (Join-Path $sys 'old') | Out-Null; Move-Item -LiteralPath $old -Destination (Join-Path $sys "old\$f") -Force; "moved aside: $f" }
}
"launcher: $sys"
# The player's options: beside EmulationStation's own features, read
# at its next start.
$features = Join-Path $RetroBat 'emulationstation\.emulationstation\es_features_celeste.cfg'
Copy-Item -LiteralPath (Join-Path $Stage 'es_features_celeste.cfg') -Destination $features -Force
"options: $features"
# Celeste mode's background and logo in the themes (Art.ps1)
. (Join-Path $Stage 'Art.ps1')
"art: $(Install-CelesteArt $RetroBat $Stage) files in the themes"

# Fields copied from the original entry; play history (playcount, lastplayed,
# gametime, favorite) and the gamedb link (work) belong to the original.
$textFields = 'name', 'desc', 'rating', 'releasedate', 'developer', 'publisher', 'genre', 'genreid', 'players', 'region', 'family'

$gl = Join-Path $dest 'gamelist.xml'
if (Test-Path -LiteralPath $gl) {
  $src = [IO.File]::ReadAllText($gl, $utf8)
} else {
  $src = "<?xml version=`"1.0`"?>`n<gameList>`n</gameList>`n"
}
$nl = if ($src.Contains("`r`n")) { "`r`n" } else { "`n" }

$rules = @(Get-ChildItem -LiteralPath (Join-Path $Stage 'games') -Filter *.yaml)
if ($rules.Count -lt 1) { throw "no rule files in $Stage\games" }
$added = 0
foreach ($ruleFile in $rules) {
  Copy-Item -LiteralPath $ruleFile.FullName -Destination $dest -Force
  $text = [IO.File]::ReadAllText($ruleFile.FullName, $utf8)
  $m = [regex]::Matches($text, '(?m)^rom:[ \t]*("([^"]*)"|''([^'']*)''|([^#\r\n]*?))[ \t]*(#.*)?\r?$')
  if ($m.Count -ne 1) { throw "$($ruleFile.Name): expected one 'rom:' line, found $($m.Count)" }
  $rom = @($m[0].Groups[2].Value, $m[0].Groups[3].Value, $m[0].Groups[4].Value) | Where-Object { $_ } | Select-Object -First 1
  $parts = $rom -split '[\\/]'
  $origDir = Join-Path $roms $parts[0]
  $origPath = './' + (($parts | Select-Object -Skip 1) -join '/')

  # the ROM's own entry, for its text and art; a ROM the release installer
  # found on disk may have none (never scanned): then the rule file's
  # `name:` and no art
  $origList = Join-Path $origDir 'gamelist.xml'
  $entries = @()
  if (Test-Path -LiteralPath $origList) { $entries = @(([xml][IO.File]::ReadAllText($origList, $utf8)).gameList.game | Where-Object { $_.path -eq $origPath }) }
  if ($entries.Count -gt 1) { throw "$($ruleFile.Name): $($entries.Count) <game> entries with path $origPath in $origList" }
  if ($entries.Count -eq 1) { $entry = $entries[0] } else {
    $nm = [regex]::Match($text, '(?m)^name:[ \t]*"?([^"#\r\n]*?)"?[ \t]*(#.*)?\r?$')
    $entry = ([xml]("<game><name>" + [Security.SecurityElement]::Escape($nm.Groups[1].Value) + "</name></game>")).game
  }

  $base = [IO.Path]::GetFileNameWithoutExtension($ruleFile.Name)
  $romBase = [IO.Path]::GetFileNameWithoutExtension($parts[-1])
  $lines = New-Object System.Collections.Generic.List[string]
  $lines.Add("`t`t<path>./" + [Security.SecurityElement]::Escape($ruleFile.Name) + "</path>")
  $copied = 0
  foreach ($node in $entry.ChildNodes) {
    if ($node.NodeType -ne 'Element') { continue }
    $v = $node.InnerText
    if ($textFields -contains $node.Name) {
      $lines.Add("`t`t<$($node.Name)>" + [Security.SecurityElement]::Escape($v) + "</$($node.Name)>")
    } elseif ($v.StartsWith('./media/')) {
      $rel = $v.Substring(2) -replace '/', '\'
      $from = Join-Path $origDir $rel
      if (-not (Test-Path -LiteralPath $from)) { "  $base`: $($node.Name) missing in the original ($from), skipped"; continue }
      $kindDir = Split-Path $rel
      # the file is named after the rule file, keeping what the original adds
      # after the ROM's name ("-bezel.png", "_(us).pdf")
      $leaf = Split-Path $from -Leaf
      $name = if ($leaf.StartsWith($romBase)) { $base + $leaf.Substring($romBase.Length) } else { $base + [IO.Path]::GetExtension($from) }
      $to = Join-Path (Join-Path $dest $kindDir) $name
      New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
      if (-not (Test-Path -LiteralPath $to)) { Copy-Item -LiteralPath $from -Destination $to; $copied++ }
      $lines.Add("`t`t<$($node.Name)>./" + [Security.SecurityElement]::Escape(($kindDir -replace '\\', '/') + '/' + $name) + "</$($node.Name)>")
    }
  }
  "  $base`: media copied $copied"

  $pathTag = "<path>./" + [Security.SecurityElement]::Escape($ruleFile.Name) + "</path>"
  if ($src.Contains($pathTag)) {
    # kept as it is (EmulationStation's play counts, favourites), but for
    # the per-game profile <core> an install of 2026-10-09 wrote (
    # dropped the same evening: one core for all): taken out, as text, in
    # that one <game> block. A core of any other name is left alone.
    $at = $src.IndexOf($pathTag)
    $start = $src.LastIndexOf('<game', $at)   # ES may write <game id="…">; <gameList> is never nearer
    $end = $src.IndexOf('</game>', $at)
    if ($start -lt 0 -or $end -lt 0 -or $src.IndexOf($pathTag, $at + 1) -ge 0) { throw "celeste gamelist: $pathTag is not in exactly one <game>" }
    $block = $src.Substring($start, $end - $start)
    $old = [regex]::Matches($block, '\r?\n[ \t]*<core>(lives-music-screen|lives-music|lives-screen|music-screen|lives|music|screen|basic)</core>')
    if ($old.Count -gt 1) { throw "celeste gamelist: $base has $($old.Count) profile <core> tags" }
    if ($old.Count -eq 0) { "  $base`: already in the gamelist, kept"; continue }
    $src = $src.Substring(0, $start) + $block.Substring(0, $old[0].Index) + $block.Substring($old[0].Index + $old[0].Length) + $src.Substring($end)
    "  $base`: already in the gamelist, profile $($old[0].Groups[1].Value) taken out"
    continue
  }
  $close = [regex]::Matches($src, '</gameList>')
  if ($close.Count -ne 1) { throw "celeste gamelist: expected one </gameList>, found $($close.Count)" }
  $game = "`t<game>" + $nl + ($lines -join $nl) + $nl + "`t</game>" + $nl
  $src = $src.Substring(0, $close[0].Index) + $game + $src.Substring($close[0].Index)
  $added++
}
[xml]$check = $src
$count = @($check.gameList.game).Count
[IO.File]::WriteAllText($gl, $src, $utf8)
"gamelist: $count games ($added added) in $gl"

# Every game's Celeste options, explicit: written
# into es_settings.cfg as celeste["<rule>.yaml"].<option>, so no game inherits
# the system's value. What the rule file supports is on, what it cannot use
# is off (REWIND TIME and REWIND TO SCREEN went with the rewind rule files):
# CONTINUOUS MUSIC with a `keep`;
# COUNT LIVES with `lives`. A player's own choice is kept (2026-10-10: a
# reinstall put his REWIND TO SCREEN 2 on Mega Man 1-4 back to 1): the
# values written are recorded in options-written.txt beside the launcher,
# and an option is only updated while it still holds the value written last
# (nobody touched it) or when the rule file cannot use it (then off); one
# with no record yet is the player's. EmulationStation rewrites es_settings.cfg on exit, so
# never while it runs. Edited as text, one <string> line per key, the file's
# line endings kept, parsed again before it is written.
$esSettings = Join-Path $RetroBat 'emulationstation\.emulationstation\es_settings.cfg'
if (-not (Test-Path -LiteralPath $esSettings)) { "options: no $esSettings, per-game options not written" }
else {
  if (-not $SkipRunningCheck -and @(Get-Process emulationstation -ErrorAction SilentlyContinue).Count) { throw "EmulationStation is running: it would overwrite the per-game options on exit; close it and install again" }
  Add-Type -Path (Join-Path $Stage 'Celeste.cs'), (Join-Path $Stage 'PadAudio.cs') -ReferencedAssemblies System.Core
  $text = [IO.File]::ReadAllText($esSettings, $utf8)
  $eol = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
  $want = [ordered]@{}
  $forced = @{}   # off because the rule file cannot use it: written whatever the player chose
  $recordFile = Join-Path $sys 'options-written.txt'
  $written = @{}
  if (Test-Path -LiteralPath $recordFile) {
    foreach ($row in [IO.File]::ReadAllLines($recordFile, $utf8)) {
      $tab = $row.IndexOf("`t")
      if ($tab -gt 0) { $written[$row.Substring(0, $tab)] = $row.Substring($tab + 1) }
    }
  }
  foreach ($ruleFile in $rules) {
    $g = [Celeste.Game]::FromText([IO.File]::ReadAllText($ruleFile.FullName, $utf8))
    $prefix = 'celeste["' + $ruleFile.Name + '"].'
    $want[$prefix + 'continuous_music'] = $(if ($g.Keep.Count -gt 0) { '1' } else { '0' })
    $want[$prefix + 'count_lives'] = $(if ($g.Lives -ge 0) { '1' } else { '0' })
    if ($g.Keep.Count -eq 0) { $forced[$prefix + 'continuous_music'] = $true }
    if ($g.Lives -lt 0) { $forced[$prefix + 'count_lives'] = $true }
    # mode checkpoints: the difficulty, medium unless the player chose
    if ($g.UsesCheckpoints) { $want[$prefix + 'checkpoints'] = 'medium' }
  }
  $lines = New-Object System.Collections.Generic.List[string]
  $lines.AddRange([string[]]($text -split "`r?`n"))
  $seen = @{}
  $changed = 0
  $kept = 0
  for ($i = 0; $i -lt $lines.Count; $i++) {
    $m = [regex]::Match($lines[$i], '^(\s*)<string name="([^"]*)" value="([^"]*)" />\s*$')
    if (-not $m.Success) { continue }
    $name = [System.Net.WebUtility]::HtmlDecode($m.Groups[2].Value)
    if (-not $want.Contains($name)) { continue }
    if ($seen.ContainsKey($name)) { throw "es_settings.cfg: $name is there twice" }
    $seen[$name] = $true
    $current = [System.Net.WebUtility]::HtmlDecode($m.Groups[3].Value)
    if ($current -eq $want[$name]) { $written[$name] = $want[$name]; continue }
    if (-not $forced.ContainsKey($name) -and -not ($written.ContainsKey($name) -and $written[$name] -eq $current)) {
      # the player's choice: left as it is
      $kept++
      continue
    }
    $lines[$i] = $m.Groups[1].Value + '<string name="' + $m.Groups[2].Value + '" value="' + $want[$name] + '" />'
    $written[$name] = $want[$name]
    $changed++
  }
  $close = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -match '^\s*</config>\s*$' })
  if ($close.Count -ne 1) { throw "es_settings.cfg: expected one </config>, found $($close.Count)" }
  $added = 0
  $new = New-Object System.Collections.Generic.List[string]
  foreach ($name in $want.Keys) {
    if ($seen.ContainsKey($name)) { continue }
    # the escaping EmulationStation itself writes: &quot; for the quotes, & as &amp;
    $enc = $name.Replace('&', '&amp;').Replace('"', '&quot;').Replace('<', '&lt;').Replace('>', '&gt;')
    $new.Add('  <string name="' + $enc + '" value="' + $want[$name] + '" />')
    $written[$name] = $want[$name]
    $added++
  }
  $lines.InsertRange($close[0], $new)
  $out = ($lines -join $eol)
  $esCheck = [xml]$out   # parse before writing
  if (-not $esCheck.config) { throw 'es_settings.cfg: no <config> after the edit' }
  if ($changed -or $added) { [IO.File]::WriteAllText($esSettings, $out, $utf8) }
  $record = foreach ($key in ($written.Keys | Sort-Object)) { $key + "`t" + $written[$key] }
  [IO.File]::WriteAllLines($recordFile, [string[]]$record, $utf8)
  "options: $($rules.Count) games, $($want.Count) settings ($changed changed, $added added, $kept kept as the player set them) in $esSettings"
}
