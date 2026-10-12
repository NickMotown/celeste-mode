# Celeste mode uninstaller . Run by "Uninstall Celeste Mode.bat".
# Removes what the installer added to the chosen RetroBat folder:
#   system\celeste\                                  (runtime, logs, snapshots)
#   emulationstation\.emulationstation\es_systems_celeste.cfg
#   emulationstation\.emulationstation\es_features_celeste.cfg
#   the background and logo in the themes (runtime\Art.ps1's list, ours by name)
#   the Celeste options in es_settings.cfg (keys celeste.* and celeste["…"].*)
# and, only when the player answers yes, roms\celeste\ (the rule files, the
# celeste gamelist with its play counts, the artwork copies). Nothing else:
# the NES games, their gamelist and RetroArch's settings stay as they are.
# Any failure ends in one red sentence, never a PowerShell error dump.
# -SkipRunningCheck: package\test-install.ps1 only (a scratch RetroBat folder
# while the builder's own EmulationStation may be open)
# -RemoveGames: remove roms\celeste without asking
param([string]$RetroBat = '', [switch]$SkipRunningCheck, [switch]$RemoveGames)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Good([string]$m) { Write-Host '  v ' -ForegroundColor Green -NoNewline; Write-Host $m }

try {
  Write-Host 'Celeste mode uninstaller'
  Write-Host ''
  if (-not $RetroBat) {
    Write-Host 'Choose your RetroBat folder in the window that opened (it may be behind this one).'
    try {
      Add-Type -AssemblyName System.Windows.Forms
      $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
      $dlg.Description = 'Choose the RetroBat folder to remove Celeste mode from'
      $dlg.ShowNewFolderButton = $false
      if (Test-Path -LiteralPath 'C:\RetroBat') { $dlg.SelectedPath = 'C:\RetroBat' }
      if ($dlg.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { Write-Host 'Cancelled: nothing was removed.'; exit 1 }
      $RetroBat = $dlg.SelectedPath
    } catch { $RetroBat = Read-Host 'Your RetroBat folder (for example C:\RetroBat)' }
  }
  $RetroBat = $RetroBat.Trim().Trim('"').TrimEnd('\')
  if (-not (Test-Path -LiteralPath (Join-Path $RetroBat 'emulationstation\emulationstation.exe'))) { throw "$RetroBat is not a RetroBat folder (no emulationstation\emulationstation.exe in it)." }
  if (-not $SkipRunningCheck) { foreach ($p in 'emulationstation', 'retroarch') {
    if (@(Get-Process $p -ErrorAction SilentlyContinue).Count) { throw "$p is running: close RetroBat (and any game), then run the uninstaller again." }
  } }
  Write-Host "RetroBat folder: $RetroBat"
  Write-Host ''
  $sys = Join-Path $RetroBat 'system\celeste'
  $es = Join-Path $RetroBat 'emulationstation\.emulationstation'
  $targets = @($sys, (Join-Path $es 'es_systems_celeste.cfg'), (Join-Path $es 'es_features_celeste.cfg')) | Where-Object { Test-Path -LiteralPath $_ }
  foreach ($t in $targets) { Remove-Item -LiteralPath $t -Recurse -Force; Good "removed: $t" }
  . (Join-Path $PSScriptRoot 'runtime\Art.ps1')
  $art = Remove-CelesteArt $RetroBat
  if ($art) { Good "removed: the background and logo from the themes ($art files)" }
  # the options, as text: one <string> line per key, the rest of the file as it is
  $settings = Join-Path $es 'es_settings.cfg'
  if (Test-Path -LiteralPath $settings) {
    $text = [IO.File]::ReadAllText($settings, $utf8)
    $lines = [regex]::Matches($text, '(?m)^[ \t]*<string name="celeste(\.|\[)[^\r\n]*\r?\n')
    if ($lines.Count) {
      for ($i = $lines.Count - 1; $i -ge 0; $i--) { $text = $text.Remove($lines[$i].Index, $lines[$i].Length) }
      [xml]$null = $text
      [IO.File]::WriteAllText($settings, $text, $utf8)
      Good "removed: $($lines.Count) Celeste options from $settings"
    }
  }
  $roms = Join-Path $RetroBat 'roms\celeste'
  if (Test-Path -LiteralPath $roms) {
    $answer = if ($RemoveGames) { 'y' } else { Read-Host "Also remove $roms (the Celeste games list, its play counts and artwork copies)? [y/N]" }
    if ($answer -match '^[yY]') { Remove-Item -LiteralPath $roms -Recurse -Force; Good "removed: $roms" }
    else { Write-Host "  kept: $roms" }
  }
  if (-not $targets.Count -and -not $art) { Write-Host "Celeste mode was not installed in $RetroBat." }
  Write-Host ''
  Write-Host 'Celeste mode is uninstalled.'
  exit 0
} catch {
  Write-Host ''
  Write-Host "Uninstall stopped: $($_.Exception.Message)" -ForegroundColor Red
  exit 1
}
