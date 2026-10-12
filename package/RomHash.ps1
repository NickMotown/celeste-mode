# The checksum a Celeste rule file is matched on : the MD5 of the
# ROM without its header (the one RetroAchievements uses), so a header
# rewritten by a ROM tool does not hide a matching dump: NES, the 16-byte
# iNES header; SNES, a 512-byte copier header (a file 512 bytes over
# a multiple of 1 KB). A .zip: its first ROM (.nes, .sfc, .smc).
# Dot-sourced by Install-CelesteMode.ps1 and build.ps1: the one
# implementation both sides of the compatibility sheet use.
function RomMd5([string]$file) {
  $entry = $null
  $bytes = $null
  if ($file.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($file)
    try {
      $entry = @($zip.Entries | Where-Object { $_.Name -match '\.(nes|sfc|smc)$' }) | Select-Object -First 1
      if (-not $entry) { return $null }
      $ms = New-Object IO.MemoryStream
      $st = $entry.Open(); try { $st.CopyTo($ms) } finally { $st.Dispose() }
      $bytes = $ms.ToArray()
    } finally { $zip.Dispose() }
  } else { $bytes = [IO.File]::ReadAllBytes($file) }
  $skip = if ($bytes.Length -ge 16 -and $bytes[0] -eq 0x4E -and $bytes[1] -eq 0x45 -and $bytes[2] -eq 0x53 -and $bytes[3] -eq 0x1A) { 16 } elseif ($bytes.Length % 1024 -eq 512 -and $bytes.Length -gt 512 -and ($file -match '\.(sfc|smc)$' -or ($entry -and $entry.Name -match '\.(sfc|smc)$'))) { 512 } else { 0 }
  $md5 = [Security.Cryptography.MD5]::Create()
  ([BitConverter]::ToString($md5.ComputeHash($bytes, $skip, $bytes.Length - $skip))).Replace('-', '').ToLowerInvariant()
}
