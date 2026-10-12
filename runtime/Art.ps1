# Celeste mode's own art in RetroBat's themes (the background picture and
# the wordmark): celeste.jpg and celeste.png copied into every installed theme's art
# folders that exist, so the Celeste system shows them in the carousel and
# behind its list. Ours by name; the uninstaller removes the same list.
# Dot-sourced by install.ps1 and Uninstall-CelesteMode.ps1: one list.
$CelesteArtPlaces = @(
  @('art\background', 'celeste.jpg'), @('_inc\backgrounds', 'celeste.jpg'),
  @('art\logos', 'celeste.png'), @('_inc\logos', 'celeste.png'),
  @('art\consoles', 'celeste.png'), @('_inc\consoles', 'celeste.png'))

function CelesteThemeArt([string]$RetroBat) {
  $themes = Join-Path $RetroBat 'emulationstation\.emulationstation\themes'
  if (-not (Test-Path -LiteralPath $themes)) { return @() }
  foreach ($t in Get-ChildItem -LiteralPath $themes -Directory) {
    foreach ($pair in $CelesteArtPlaces) {
      $d = Join-Path $t.FullName $pair[0]
      if (Test-Path -LiteralPath $d) { [pscustomobject]@{ Dir = $d; File = $pair[1]; Path = (Join-Path $d $pair[1]) } }
    }
  }
}

function Install-CelesteArt([string]$RetroBat, [string]$From) {
  $n = 0
  foreach ($a in CelesteThemeArt $RetroBat) {
    $src = Join-Path $From $a.File
    if (-not (Test-Path -LiteralPath $src)) { throw "art: $src is missing" }
    Copy-Item -LiteralPath $src -Destination $a.Path -Force
    $n++
  }
  $n
}

function Remove-CelesteArt([string]$RetroBat) {
  $n = 0
  foreach ($a in CelesteThemeArt $RetroBat) {
    if (Test-Path -LiteralPath $a.Path) { Remove-Item -LiteralPath $a.Path -Force; $n++ }
  }
  $n
}
