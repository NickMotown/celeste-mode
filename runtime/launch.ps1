# Celeste mode launcher : EmulationStation's
# command for the celeste system. Installed as <RetroBat>\system\celeste\
# launch.ps1, beside Celeste.cs; everything else is found from there, so the
# folder works in any RetroBat install.
#
# usage (from es_systems_celeste.cfg):
#   launch.ps1 <rule file> [emulatorLauncher arguments: -gameinfo %GAMEINFOXML%, %CONTROLLERSCONFIG%]
#
# 1. reads the rule file (Celeste.cs: the one parser);
# 2. starts the ROM through RetroBat's emulatorLauncher with RetroArch, so the
#    game gets the player's pads, decoration and settings like any NES game.
#    Arguments override settings in emulatorLauncher (Program.cs imports them
#    last), so these hold for this launch only:
#    -incrementalsavestates 0: one file per slot, or every snapshot would add
#      a numbered save state (savestate_auto_index, no limit kept);
#    -game_specific_options 1: RetroArch reads a game override, which this
#      script writes for the launch (config\<core>\<rom>.cfg) to hide the
#      "state saved" notice a snapshot a second would show; removed after;
#    RetroArch's network commands, if off, are turned on for this launch
#    and put back after it (retroarch.cfg's network_cmd_enable);
# 3. once RetroArch plays, runs the watcher (Celeste.Watcher) over RetroArch's
#    network commands until RetroArch exits;
# 4. puts the game's own save states back as they were (Celeste.Snapshots);
# 5. waits for emulatorLauncher, and exits with its code; on a failure of
#    its own (RetroAchievements hardcore on included) exits 250 with the
#    reason in %TEMP%\launch_error.log, which EmulationStation shows.
# No param block: EmulationStation's controller arguments (-p1index …) must
# reach $args as text, not be taken for this script's parameters.
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$here = $PSScriptRoot
$logDir = Join-Path $here 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir 'launch.jsonl'

function Log([string]$event, [hashtable]$data = @{}) {
  $data['ts'] = (Get-Date).ToString('o')
  $data['event'] = $event
  [IO.File]::AppendAllText($logFile, (($data | ConvertTo-Json -Compress -Depth 4) + "`n"), $utf8)
}

function Quote([string]$a) { if ($a -match '[\s"]') { '"' + $a.Replace('"', '\"') + '"' } else { $a } }

# A key of retroarch.cfg, or $null.
function RaSetting([string]$cfg, [string]$key) {
  $m = Select-String -LiteralPath $cfg -Pattern ('^' + [regex]::Escape($key) + '\s*=\s*"(.*)"') | Select-Object -First 1
  if ($m) { $m.Matches[0].Groups[1].Value } else { $null }
}

# Sets one key of retroarch.cfg, as text: its one line replaced (or added),
# the rest of the file and its line endings as they were.
function SetRaSetting([string]$cfg, [string]$key, [string]$value) {
  $text = [IO.File]::ReadAllText($cfg, $utf8)
  $eol = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
  $line = $key + ' = "' + $value + '"'
  $found = [regex]::Matches($text, '(?m)^' + [regex]::Escape($key) + '[ \t]*=[^\r\n]*')
  if ($found.Count -gt 1) { throw "$cfg has $($found.Count) lines for $key" }
  if ($found.Count -eq 1) { $text = $text.Substring(0, $found[0].Index) + $line + $text.Substring($found[0].Index + $found[0].Length) }
  else { if (-not $text.EndsWith($eol)) { $text += $eol }; $text += $line + $eol }
  [IO.File]::WriteAllText($cfg, $text, $utf8)
}

$snaps = $null
$override = $null
$netWas = $null
try {
  if ($args.Count -lt 1) { throw 'usage: launch.ps1 <rule file> [controller arguments]' }
  $rule = [string]$args[0]
  $rest = @($args | Select-Object -Skip 1 | ForEach-Object { [string]$_ })
  # RetroBat is two levels up (system\celeste); CELESTE_RETROBAT overrides it.
  $rb = if ($env:CELESTE_RETROBAT) { $env:CELESTE_RETROBAT } else { (Resolve-Path (Join-Path $here '..\..')).Path }
  Log 'start' @{ rule = $rule; retrobat = $rb; extraArgs = $rest.Count }

  # PadAudio.cs: the DualSense discovery the ○/△ switch reads the pads with
  Add-Type -Path (Join-Path $here 'Celeste.cs'), (Join-Path $here 'PadAudio.cs') -ReferencedAssemblies System.Core
  $base = [IO.Path]::GetFileNameWithoutExtension($rule)
  $work = Join-Path $here ('run\' + $base)
  New-Item -ItemType Directory -Force -Path $work | Out-Null
  [Celeste.Log]::File = Join-Path $logDir 'celeste.jsonl'

  $game = [Celeste.Game]::FromText([IO.File]::ReadAllText($rule, $utf8))
  if (-not $game.Rom) { throw "rule file: no 'rom:'" }
  $romPath = Join-Path (Join-Path $rb 'roms') ($game.Rom -replace '/', '\')
  if (-not (Test-Path -LiteralPath $romPath)) { throw "rom not found: $romPath ('rom: $($game.Rom)', relative to $rb\roms)" }
  $system = ($game.Rom -split '[\\/]')[0]

  # The player's options: es_settings.cfg, the game's own over the
  # system's; each settled value and its source go to celeste.jsonl.
  $esSettingsFile = Join-Path $rb 'emulationstation\.emulationstation\es_settings.cfg'
  $es = if (Test-Path -LiteralPath $esSettingsFile) { [Celeste.Options]::ReadSettings($esSettingsFile) } else { New-Object 'System.Collections.Generic.Dictionary[string,string]' }
  $options = [Celeste.Options]::Resolve($es, [IO.Path]::GetFileName($rule), $game)
  $options.Apply($game)
  $options.LogAll()

  $raDir = Join-Path $rb 'emulators\retroarch'
  $raCfg = Join-Path $raDir 'retroarch.cfg'
  # The watcher talks to RetroArch through its network commands. Off (the
  # RetroArch default): on for this launch only, put back once RetroArch has
  # exited (it saves its config on exit), so a player's own games never run
  # with the port open. emulatorLauncher does not manage this key.
  $netNow = RaSetting $raCfg 'network_cmd_enable'
  if ($netNow -ne 'true') {
    $netWas = if ($netNow) { $netNow } else { '' }
    SetRaSetting $raCfg 'network_cmd_enable' 'true'
    Log 'network_cmd_on' @{ was = $netWas; cfg = $raCfg }
  }
  $port = [int](RaSetting $raCfg 'network_cmd_port')
  if (-not $port) { $port = 55355 }
  $running = @(Get-Process retroarch -ErrorAction SilentlyContinue)
  if ($running.Count -gt 0) { throw "RetroArch is already running (pid $($running[0].Id)): the watcher would talk to that one" }

  # The core: the player's choice for the system, else its default (the first
  # <core> of es_systems.cfg); RetroArch files game overrides under the
  # core's own name (corename in its info file).
  $core = $null
  $esSettings = Join-Path $rb 'emulationstation\.emulationstation\es_settings.cfg'
  $m = Select-String -LiteralPath $esSettings -Pattern ('name="' + [regex]::Escape($system) + '\.core" value="([^"]+)"') | Select-Object -First 1
  if ($m) { $core = $m.Matches[0].Groups[1].Value }
  if (-not $core) {
    $sys = ([xml][IO.File]::ReadAllText((Join-Path $rb 'emulationstation\.emulationstation\es_systems.cfg'), $utf8)).systemList.system | Where-Object { $_.name -eq $system } | Select-Object -First 1
    $lr = $sys.emulators.emulator | Where-Object { $_.name -eq 'libretro' } | Select-Object -First 1
    if ($lr) { $core = @($lr.cores.core)[0]; if ($core -isnot [string]) { $core = $core.'#text' } }
  }
  if (-not $core) { throw "no libretro core known for system '$system'" }
  $info = Join-Path $raDir "info\${core}_libretro.info"
  $corename = $null
  if (Test-Path -LiteralPath $info) { $m = Select-String -LiteralPath $info -Pattern '^corename\s*=\s*"(.+)"' | Select-Object -First 1; if ($m) { $corename = $m.Matches[0].Groups[1].Value } }
  $romBase = [IO.Path]::GetFileNameWithoutExtension($romPath)
  $overrideText = "notification_show_save_state = `"false`"`r`n"
  if ($corename) {
    $path = Join-Path $raDir "config\$corename\$romBase.cfg"
    if (Test-Path -LiteralPath $path) { Log 'override_kept' @{ path = $path; note = 'an override of the player exists: left as is, the save notices will show' } }
    else { New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null; [IO.File]::WriteAllText($path, $overrideText, $utf8); $override = $path; Log 'override_written' @{ path = $path } }
  } else { Log 'override_skipped' @{ core = $core; note = "no corename in $info" } }

  $elArgs = @('-system', $system, '-emulator', 'libretro', '-core', $core, '-rom', $romPath, '-incrementalsavestates', '0', '-game_specific_options', '1') + $rest
  $started = Get-Date
  $launcher = Start-Process -FilePath (Join-Path $rb 'emulationstation\emulatorLauncher.exe') -ArgumentList (($elArgs | ForEach-Object { Quote $_ }) -join ' ') -PassThru
  $null = $launcher.Handle
  Log 'emulatorLauncher' @{ pid = $launcher.Id; args = $elArgs; port = $port }

  $retroarch = $null
  for ($i = 0; $i -lt 60 -and -not $retroarch; $i++) {
    Start-Sleep -Milliseconds 500
    if ($launcher.HasExited) { throw "emulatorLauncher exited with $($launcher.ExitCode) before RetroArch started" }
    $retroarch = Get-Process retroarch -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -ge $started.AddSeconds(-1) } | Select-Object -First 1
  }
  if (-not $retroarch) { throw 'RetroArch did not start within 30 s' }
  $ra = New-Object Celeste.RetroArch($port)
  $status = $null
  # PAUSED counts too: RetroArch pauses itself without the focus
  # (pause_nonactive), and the watcher waits out a pause (2026-10-09).
  for ($i = 0; $i -lt 60 -and @('PLAYING', 'PAUSED') -notcontains $status -and -not $retroarch.HasExited; $i++) { Start-Sleep -Milliseconds 500; $status = $ra.Status() }
  if (@('PLAYING', 'PAUSED') -notcontains $status) { throw "RetroArch never reported a game on port $port (last answer: $status)" }

  # RetroAchievements hardcore forbids loading a state, and every rewind is
  # one: with it on, the game would play with no rewind and say nothing.
  # emulatorLauncher has written this launch's settings by now, so this is
  # the effective value. RetroArch is closed and the launch fails with the
  # reason on EmulationStation's error box (below).
  if ((RaSetting $raCfg 'cheevos_enable') -eq 'true' -and (RaSetting $raCfg 'cheevos_hardcore_mode_enable') -eq 'true') {
    Log 'hardcore_refused' @{ retroarch = $retroarch.Id }
    $ra.Send('QUIT')
    if (-not $retroarch.WaitForExit(10000)) { Log 'hardcore_quit_timeout' @{ retroarch = $retroarch.Id } }
    throw 'Celeste mode needs save states: turn RetroAchievements hardcore mode off.'
  }

  # emulatorLauncher wrote the save state folder into retroarch.cfg before
  # starting RetroArch; the snapshots go through it.
  $states = RaSetting $raCfg 'savestate_directory'
  if (-not $states) { throw "no savestate_directory in $raCfg" }
  # the ring holds 6 times the rewind; a rewind without a limit (to the
  # starting point) keeps its starting points pinned, and 10 s for the rest
  $watcherStep = if ([double]::IsInfinity($game.Rewind)) { 10 } else { $game.Rewind }
  $snaps = New-Object Celeste.Snapshots($states, $romBase, $work, [int][math]::Max(60, [math]::Ceiling($watcherStep * 6)))
  Log 'watching' @{ retroarch = $retroarch.Id; states = $states; port = $port }
  $watcher = New-Object Celeste.Watcher($game, $ra, $snaps)
  # hits count from the player's first press (title demos play by themselves)
  $watcher.Armed = $false
  # every pad that is no DualSense, through RetroArch's own SDL2.dll
  $pads = $watcher.StartPadSwitch((Join-Path $rb 'emulators\retroarch\SDL2.dll'))
  try { $watcher.RunUntilExit($retroarch) } finally { $pads.Dispose() }

  $launcher.WaitForExit()
  Log 'end' @{ exitCode = $launcher.ExitCode }
  $code = $launcher.ExitCode
} catch {
  Log 'error' @{ message = $_.Exception.Message }
  [Console]::Error.WriteLine("celeste: " + $_.Exception.Message)
  # EmulationStation shows %TEMP%\launch_error.log in its "AN ERROR
  # OCCURRED" box for exit code 250 (FileData::getMessageFromExitCode); any
  # other non-zero code is dropped without a word.
  try { [IO.File]::WriteAllText((Join-Path $env:TEMP 'launch_error.log'), $_.Exception.Message, $utf8) } catch { Log 'error' @{ message = 'writing launch_error.log: ' + $_.Exception.Message } }
  $code = 250
} finally {
  if ($snaps) {
    try { $snaps.Restore() } catch { Log 'error' @{ message = 'restoring the save states: ' + $_.Exception.Message } }
  }
  # The override did not exist before this launch: moving it away puts things
  # back as they were. Moved into run\, never deleted. emulatorLauncher
  # rewrites it at launch (same key, no quotes, 2026-10-09), so its content is
  # not a test of ownership; its absence before the launch is.
  if ($null -ne $netWas) {
    # after RetroArch's own save on exit, or that save puts "true" back
    if ($retroarch -and -not $retroarch.HasExited) { $null = $retroarch.WaitForExit(30000) }
    # an empty $netWas: the key was absent, "false" is RetroArch's default
    try { SetRaSetting $raCfg 'network_cmd_enable' $(if ($netWas) { $netWas } else { 'false' }); Log 'network_cmd_restored' @{ value = $netWas } }
    catch { Log 'error' @{ message = 'restoring network_cmd_enable: ' + $_.Exception.Message } }
  }
  if ($override -and (Test-Path -LiteralPath $override)) {
    $kept = Join-Path $here ('run\override-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.cfg')
    Move-Item -LiteralPath $override -Destination $kept
    Log 'override_moved' @{ path = $override; to = $kept }
  }
}
exit $code
