# Celeste mode installer . Run by "Install Celeste Mode.bat" from
# the release zip, beside this file:
#   files\runtime\   launch.ps1, Celeste.cs, PadAudio.cs, install.ps1, Art.ps1,
#                    celeste.jpg, celeste.png (the background and logo),
#                    es_features_celeste.cfg, es_systems_celeste.cfg
#   files\roms\celeste\   the rule files (.yaml): the celeste system's "roms"
#   files\compatibility.csv   rule,name,md5: the ROM each rule file was made on
#                    (MD5 of the ROM without its header, RomHash.ps1)
# Steps:
#   1. a folder picker for the RetroBat folder (or -RetroBat), then checks,
#      each printed: the folder is RetroBat's, RetroBat and RetroArch are
#      closed (EmulationStation rewrites its gamelists on exit), Windows
#      PowerShell 5+, the C# compiler PowerShell uses at each launch (the
#      .NET Framework), the NES core. A failed check stops with what to do;
#   2. the ROMs: every file in roms\<system> (.nes, .zip) and every entry of
#      its gamelist, matched to a rule file's game by name (case,
#      punctuation and (tags) ignored), then verified by checksum;
#   3. a match is installed: its rule file is copied with the player's ROM
#      path, and install.ps1 copies the runtime, the art and the entry's text
#      and artwork into roms\celeste;
#   4. the system file (es_systems_celeste.cfg) beside EmulationStation's own;
#   5. the list: every supported game on one line, a green v when installed,
#      a red x when not (why, after it). Nothing of the player's is changed
#      or deleted; RetroArch's settings are not touched (launch.ps1 handles
#      its network commands for each Celeste launch).
# Any failure ends in one red sentence, never a PowerShell error dump.
# -SkipRunningCheck: package\test-install.ps1 only (a scratch RetroBat folder
# while the builder's own EmulationStation may be open)
param([string]$RetroBat = '', [switch]$SkipRunningCheck)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$here = $PSScriptRoot
$runtime = Join-Path $here 'runtime'
$games = Join-Path $here 'roms\celeste'

function Say([string]$m) { Write-Host $m }
function Good([string]$m) { Write-Host '  v ' -ForegroundColor Green -NoNewline; Write-Host $m }
function Bad([string]$m) { Write-Host '  x ' -ForegroundColor Red -NoNewline; Write-Host $m }
# a check that fails: its reason and what to do, then the installer stops
function Stop-Install([string]$m) { throw [System.OperationCanceledException]::new($m) }

try {
  Say 'Celeste mode installer'
  Say ''
  # Files from a downloaded zip carry the "from the internet" mark, and
  # Add-Type refuses a marked .cs on some setups: unmark ours.
  Get-ChildItem -LiteralPath $here -Recurse -File | Unblock-File

  # --- 1. RetroBat and the checks -------------------------------------------
  if (-not $RetroBat) {
    Say 'Choose your RetroBat folder in the window that opened (it may be behind this one).'
    try {
      Add-Type -AssemblyName System.Windows.Forms
      $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
      $dlg.Description = 'Choose your RetroBat folder (the one that holds emulationstation and emulators)'
      $dlg.ShowNewFolderButton = $false
      if (Test-Path -LiteralPath 'C:\RetroBat') { $dlg.SelectedPath = 'C:\RetroBat' }
      if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { Say 'Cancelled: nothing was installed.'; exit 1 }
      $RetroBat = $dlg.SelectedPath
    } catch {
      # no window possible: ask in the terminal instead
      $RetroBat = Read-Host 'Your RetroBat folder (for example C:\RetroBat)'
    }
  }
  $RetroBat = $RetroBat.Trim().Trim('"').TrimEnd('\')
  Say "RetroBat folder: $RetroBat"
  Say ''
  Say 'Checks:'
  if (-not (Test-Path -LiteralPath (Join-Path $RetroBat 'emulationstation\emulationstation.exe')) -or -not (Test-Path -LiteralPath (Join-Path $RetroBat 'emulators\retroarch\retroarch.exe'))) {
    Bad 'RetroBat folder'
    Stop-Install "$RetroBat is not a RetroBat folder: it should hold emulationstation\emulationstation.exe and emulators\retroarch\retroarch.exe. Run the installer again and pick the folder RetroBat is installed in."
  }
  Good 'RetroBat folder'
  if (-not $SkipRunningCheck) {
    foreach ($p in 'emulationstation', 'retroarch') {
      if (@(Get-Process $p -ErrorAction SilentlyContinue).Count) {
        Bad 'RetroBat closed'
        Stop-Install "$p is running: close RetroBat (and any game), then run the installer again."
      }
    }
    Good 'RetroBat closed'
  }
  if ($PSVersionTable.PSVersion.Major -lt 5) {
    Bad "Windows PowerShell 5 or newer (this one: $($PSVersionTable.PSVersion))"
    Stop-Install 'Windows PowerShell 5.1 comes with Windows 10 and 11; on an older Windows, install the Windows Management Framework 5.1 from Microsoft.'
  }
  Good "Windows PowerShell $($PSVersionTable.PSVersion.Major).$($PSVersionTable.PSVersion.Minor)"
  # the watcher is C# that PowerShell compiles at each game's launch
  try {
    Add-Type -TypeDefinition 'public static class CelesteInstallCheck { public static int Two() { return 2; } }'
    if ([CelesteInstallCheck]::Two() -ne 2) { throw 'wrong result' }
    Good 'C# compiler (.NET Framework)'
  } catch {
    Bad 'C# compiler (.NET Framework)'
    Stop-Install "PowerShell could not compile C# ($($_.Exception.Message)). Celeste mode needs the .NET Framework 4.8, part of Windows 10 and 11: install it from Microsoft (search for '.NET Framework 4.8 runtime')."
  }
  $esDir = Join-Path $RetroBat 'emulationstation\.emulationstation'
  foreach ($f in Get-ChildItem -LiteralPath $esDir -Filter 'es_systems_*.cfg' -ErrorAction SilentlyContinue) {
    if ($f.Name -ne 'es_systems_celeste.cfg' -and (Select-String -LiteralPath $f.FullName -Pattern '<name>celeste</name>' -Quiet)) {
      Bad 'no other "celeste" system'
      Stop-Install "$($f.FullName) already defines a 'celeste' system: remove it first, or the two would collide."
    }
  }
  # the NES core RetroArch will run, chosen as launch.ps1 chooses it: the
  # player's nes.core, else the first libretro core RetroBat lists for nes
  $core = $null
  $esSettings = Join-Path $esDir 'es_settings.cfg'
  if (Test-Path -LiteralPath $esSettings) {
    $m = Select-String -LiteralPath $esSettings -Pattern 'name="nes\.core" value="([^"]+)"' | Select-Object -First 1
    if ($m) { $core = $m.Matches[0].Groups[1].Value }
  }
  if (-not $core) {
    $esSystems = Join-Path $esDir 'es_systems.cfg'
    if (Test-Path -LiteralPath $esSystems) {
      $nes = ([xml][IO.File]::ReadAllText($esSystems, $utf8)).systemList.system | Where-Object { $_.name -eq 'nes' } | Select-Object -First 1
      $lr = $nes.emulators.emulator | Where-Object { $_.name -eq 'libretro' } | Select-Object -First 1
      if ($lr) { $core = @($lr.cores.core)[0]; if ($core -isnot [string]) { $core = $core.'#text' } }
    }
  }
  if (-not $core) {
    Bad 'a RetroArch core for NES games'
    Stop-Install "RetroBat lists no RetroArch (libretro) core for NES in $esDir\es_systems.cfg: Celeste mode runs NES games in RetroArch. Update or reinstall RetroBat, then run the installer again."
  }
  $coreFile = Join-Path $RetroBat "emulators\retroarch\cores\$($core)_libretro.dll"
  if (-not (Test-Path -LiteralPath $coreFile)) {
    Bad "RetroArch's $core core (for NES games)"
    Stop-Install "$coreFile is missing: in RetroBat, open the main menu, UPDATES & DOWNLOADS, and update RetroBat (or reinstall it), then run the installer again."
  }
  Good "RetroArch's $core core (for NES games)"
  Say ''

  # --- 2. the ROMs ---------------------------------------------------------------
  function Normalize([string]$s) {
    $s = $s.ToLowerInvariant() -replace '\([^)]*\)', '' -replace '\[[^\]]*\]', '' -replace '&', 'and'
    $s -replace '[^a-z0-9]', ''
  }
  . (Join-Path $here 'RomHash.ps1')
  # Each rule file's system: the folder its `rom:` line names (nes).
  function RuleSystem([string]$rule) {
    $m = [regex]::Matches([IO.File]::ReadAllText((Join-Path $games $rule), $utf8), '(?m)^rom:[ \t]*["'']?([^/\\"''\r\n]+)[/\\]')
    if ($m.Count -ne 1) { throw "$($rule): expected one 'rom:' line naming a system folder, found $($m.Count)" }
    $m[0].Groups[1].Value
  }
  # A system's ROMs, read once: every .nes/.zip file under roms\<system> and
  # every entry of its gamelist, each with the keys it can match on (the
  # file's name, and the gamelist's name for the game).
  $known = @{}
  function Candidates([string]$system) {
    if ($known.ContainsKey($system)) { return $known[$system] }
    $dir = Join-Path $RetroBat "roms\$system"
    $byPath = @{}
    if (Test-Path -LiteralPath $dir) {
      foreach ($f in Get-ChildItem -LiteralPath $dir -Recurse -File | Where-Object { $_.Extension -in '.nes', '.zip' }) {
        $rel = $f.FullName.Substring($dir.Length + 1) -replace '\\', '/'
        $byPath[$rel] = [pscustomobject]@{ path = $rel; keys = @(Normalize $f.BaseName) }
      }
      $list = Join-Path $dir 'gamelist.xml'
      if (Test-Path -LiteralPath $list) {
        foreach ($g in @(([xml][IO.File]::ReadAllText($list, $utf8)).gameList.game)) {
          if (-not $g) { continue }
          $rel = ([string]$g.path) -replace '^\./', ''
          if (-not $byPath.ContainsKey($rel)) { $byPath[$rel] = [pscustomobject]@{ path = $rel; keys = @(Normalize ([IO.Path]::GetFileNameWithoutExtension($rel))) } }
          $byPath[$rel].keys += Normalize ([string]$g.name)
        }
      }
    }
    Say "Your $system games: $($byPath.Count) files in $dir"
    $known[$system] = @($byPath.Values)
    return $known[$system]
  }
  $compat = @(Import-Csv -LiteralPath (Join-Path $here 'compatibility.csv'))

  $stage = Join-Path ([IO.Path]::GetTempPath()) ('celeste-install-' + [Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force -Path (Join-Path $stage 'games') | Out-Null
  foreach ($f in Get-ChildItem -LiteralPath $runtime -File) { Copy-Item -LiteralPath $f.FullName -Destination $stage }

  $hashed = @{}
  $result = New-Object System.Collections.Generic.List[object]
  foreach ($row in $compat) {
    $key = Normalize $row.name
    $system = RuleSystem $row.rule
    $sysDir = Join-Path $RetroBat "roms\$system"
    $cands = @(Candidates $system | Where-Object { $_.keys -contains $key })
    $hit = $null
    foreach ($c in $cands) {
      $file = Join-Path $sysDir ($c.path -replace '/', '\')
      if (-not (Test-Path -LiteralPath $file)) { continue }
      if (-not $hashed.ContainsKey($file)) { try { $hashed[$file] = RomMd5 $file } catch { $hashed[$file] = $null } }
      if ($hashed[$file] -eq $row.md5) { $hit = $c; break }
    }
    if ($hit) {
      $text = [IO.File]::ReadAllText((Join-Path $games $row.rule), $utf8)
      $romLine = [regex]::Matches($text, '(?m)^rom:[^\r\n]*')
      if ($romLine.Count -ne 1) { throw "$($row.rule): expected one 'rom:' line, found $($romLine.Count)" }
      $rel = "$system/" + $hit.path
      $text = $text.Substring(0, $romLine[0].Index) + 'rom: "' + $rel.Replace('"', '') + '"' + $text.Substring($romLine[0].Index + $romLine[0].Length)
      [IO.File]::WriteAllText((Join-Path $stage "games\$($row.rule)"), $text, $utf8)
      $result.Add([pscustomobject]@{ name = $row.name; ok = $true; why = '' })
    } elseif ($cands.Count) {
      $result.Add([pscustomobject]@{ name = $row.name; ok = $false; why = 'another version: ' + (($cands | ForEach-Object { $_.path }) -join ', ') })
    } else { $result.Add([pscustomobject]@{ name = $row.name; ok = $false; why = '' }) }
  }
  $installed = @($result | Where-Object { $_.ok }).Count
  Say "$($hashed.Count) ROMs checked"
  Say ''

  # --- 3, 4. install -------------------------------------------------------------
  if ($installed) {
    Say 'Installing:'
    & (Join-Path $stage 'install.ps1') -Stage $stage -RetroBat $RetroBat -SkipRunningCheck:$SkipRunningCheck | ForEach-Object { if ($_ -notmatch '^\s') { Good $_ } }
    Copy-Item -LiteralPath (Join-Path $runtime 'es_systems_celeste.cfg') -Destination (Join-Path $esDir 'es_systems_celeste.cfg') -Force
    Good "system: $(Join-Path $esDir 'es_systems_celeste.cfg')"
    Say ''
  }
  Remove-Item -LiteralPath $stage -Recurse -Force

  # --- 5. the list -----------------------------------------------------------------
  Say "Supported games ($installed of $($result.Count) installed):"
  foreach ($r in $result) { if ($r.ok) { Good $r.name } else { Bad ($r.name + $(if ($r.why) { "   ($($r.why))" } else { '' })) } }
  Say ''
  if ($installed) {
    Say 'Done. Start RetroBat: the games are in the "Celeste mode" system.'
  } else {
    Say 'None of your games matches a supported version: nothing was installed.'
    Say "The installer looks for NES ROMs (.nes, or a .zip holding one) in $RetroBat\roms\nes and its subfolders, and in that folder's gamelist.xml. A ROM is found by its name (the game's name as in the list above; case, punctuation and tags in parentheses do not matter), then checked by its checksum: only the exact version listed (MD5 without the iNES header, in the README's compatibility list) is installed. 'another version' means the name matched but the checksum did not."
  }
  exit 0
} catch {
  Say ''
  if ($_.Exception -is [System.OperationCanceledException]) {
    Write-Host "Installation stopped: $($_.Exception.Message)" -ForegroundColor Red
  } else {
    Write-Host "Installation failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "(at line $($_.InvocationInfo.ScriptLineNumber) of $([IO.Path]::GetFileName($_.InvocationInfo.ScriptName)); please report it with this text)"
  }
  Write-Host 'Nothing of yours was deleted. Fix the cause and run the installer again.'
  if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
  exit 1
}
