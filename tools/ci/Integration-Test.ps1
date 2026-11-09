<#
Test d'intégration RÉEL sur Windows : clone, image et restauration entre vrais disques virtuels (VHDX),
via la version console (mêmes moteurs et même accès disque natif que l'application).
Chaque résultat est vérifié en remontant le disque obtenu et en comparant les fichiers par SHA-256.
Sortie : code 0 uniquement si TOUT est correct.
#>
param(
  [Parameter(Mandatory)][string]$CliPath,
  [string]$WorkDir = (Join-Path $env:RUNNER_TEMP 'ghost-int'),
  [string]$LogFile
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $WorkDir | Out-Null
if (-not $LogFile) { $LogFile = Join-Path $WorkDir 'integration.log' }
Set-Content $LogFile ''

function Log([string]$m) { $l = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $m; Write-Host $l; Add-Content $LogFile $l }
function Fail([string]$m) { Log "ECHEC : $m"; throw $m }
function Check([bool]$ok, [string]$what) { if ($ok) { Log "OK    : $what" } else { Fail $what } }

$mounted = New-Object System.Collections.ArrayList

function New-Vhd([string]$name, [int]$mb) {
  $p = Join-Path $WorkDir "$name.vhdx"
  if (Test-Path $p) { Remove-Item $p -Force }
  $s = Join-Path $WorkDir "$name.dp"
  Set-Content $s "create vdisk file=`"$p`" maximum=$mb type=expandable"
  $o = & diskpart /s $s 2>&1 | Out-String
  if (-not (Test-Path $p)) { Fail "création du VHDX $name impossible : $o" }
  return $p
}
function Mount-Vhd([string]$p) {
  Mount-DiskImage -ImagePath $p -StorageType VHDX | Out-Null
  [void]$mounted.Add($p)
  $d = Get-DiskImage -ImagePath $p | Get-Disk
  if ($d.IsOffline) { Set-Disk -Number $d.Number -IsOffline $false }
  if ($d.IsReadOnly) { Set-Disk -Number $d.Number -IsReadOnly $false }
  return (Get-DiskImage -ImagePath $p | Get-Disk).Number
}
function Dismount-Vhd([string]$p) {
  try { Dismount-DiskImage -ImagePath $p | Out-Null } catch { }
  $mounted.Remove($p)
}
function Data-Letter([int]$n) {
  $p = Get-Partition -DiskNumber $n | Where-Object { $_.Type -eq 'Basic' } | Select-Object -First 1
  if (-not $p) { Fail "aucune partition de données sur le disque $n" }
  if (-not $p.DriveLetter) {
    $p | Add-PartitionAccessPath -AssignDriveLetter
    $p = Get-Partition -DiskNumber $n -PartitionNumber $p.PartitionNumber
  }
  return $p.DriveLetter
}
function Run-Cli([string]$stdin, [string[]]$cliArgs) {
  $out = if ($stdin) { $stdin | & $CliPath @cliArgs 2>&1 | Out-String } else { '' | & $CliPath @cliArgs 2>&1 | Out-String }
  $code = $LASTEXITCODE
  Log ("CLI " + ($cliArgs -join ' ') + " -> code $code")
  Add-Content $LogFile (($out -replace "`r", "`n") -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 14 | ForEach-Object { "    | $_" })
  return @{ Code = $code; Out = $out }
}

try {
  Log "=== Préparation : disque source GPT/NTFS avec données ==="
  $srcVhd = New-Vhd 'src' 300
  $sn = Mount-Vhd $srcVhd
  Initialize-Disk -Number $sn -PartitionStyle GPT
  $part = New-Partition -DiskNumber $sn -UseMaximumSize -AssignDriveLetter
  Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel 'GHOSTSRC' -Confirm:$false | Out-Null
  $sl = Data-Letter $sn
  $bytes = New-Object byte[] (24MB); (New-Object Random 42).NextBytes($bytes)
  [IO.File]::WriteAllBytes("${sl}:\data.bin", $bytes)
  New-Item -ItemType Directory "${sl}:\dossier" | Out-Null
  Set-Content "${sl}:\dossier\note.txt" 'GHOST CLONAGE test'
  $h1 = (Get-FileHash "${sl}:\data.bin" -Algorithm SHA256).Hash
  $h2 = (Get-FileHash "${sl}:\dossier\note.txt" -Algorithm SHA256).Hash
  Write-VolumeCache -DriveLetter $sl
  Log "source = disque $sn, volume ${sl}:"

  Log "=== Garde-fous (aucune écriture possible) ==="
  $sysDisk = (Get-Partition -DriveLetter C | Get-Disk).Number
  $r = Run-Cli '' @('clone', '--src', $sn, '--dst', $sysDisk)
  Check ($r.Code -eq 2 -and $r.Out -match 'Windows en cours') "clonage vers le disque système ($sysDisk) refusé"
  $r = Run-Cli '' @('clone', '--src', $sn, '--dst', $sn)
  Check ($r.Code -eq 2 -and $r.Out -match 'identiques') "clonage disque -> lui-même refusé"

  Log "=== CLONAGE : source -> disque plus grand contenant d'anciennes données ==="
  $dstVhd = New-Vhd 'dst' 400
  $dn = Mount-Vhd $dstVhd
  Initialize-Disk -Number $dn -PartitionStyle GPT
  $op = New-Partition -DiskNumber $dn -UseMaximumSize -AssignDriveLetter
  Format-Volume -Partition $op -FileSystem NTFS -NewFileSystemLabel 'ANCIEN' -Confirm:$false | Out-Null
  $ol = Data-Letter $dn
  Set-Content "${ol}:\ancien.txt" 'doit disparaitre'
  Write-VolumeCache -DriveLetter $ol

  # sans confirmation : rien ne doit changer
  $r = Run-Cli 'mauvaise confirmation' @('clone', '--src', $sn, '--dst', $dn)
  Check ($r.Code -eq 1) "clonage sans confirmation correcte : abandonné (code 1)"
  Check (Test-Path "${ol}:\ancien.txt") "destination intacte après refus"

  $r = Run-Cli "EFFACER DISQUE $dn" @('clone', '--src', $sn, '--dst', $dn)
  Check ($r.Code -eq 0) "clonage terminé avec succès (code 0)"
  Check ($r.Out -match 'terminé et vérifié') "message de succès vérifié affiché"

  Dismount-Vhd $srcVhd          # évite la collision de signatures de disque
  Dismount-Vhd $dstVhd
  $dn2 = Mount-Vhd $dstVhd
  $disk = Get-Disk -Number $dn2
  Check ($disk.PartitionStyle -eq 'GPT') "destination en GPT"
  Check ($disk.Size -eq 400MB) "taille du disque destination conservée (400 Mo)"
  $dl = Data-Letter $dn2
  Check ((Get-Volume -DriveLetter $dl).FileSystemLabel -eq 'GHOSTSRC') "étiquette de volume clonée"
  Check (-not (Test-Path "${dl}:\ancien.txt")) "ancienne donnée effacée"
  Check ((Get-FileHash "${dl}:\data.bin" -Algorithm SHA256).Hash -eq $h1) "data.bin identique (SHA-256)"
  Check ((Get-FileHash "${dl}:\dossier\note.txt" -Algorithm SHA256).Hash -eq $h2) "note.txt identique (SHA-256)"
  $scan = Repair-Volume -DriveLetter $dl -Scan
  Check ($scan -eq 'NoErrorsFound') "système de fichiers cloné sain (Repair-Volume : $scan)"
  Dismount-Vhd $dstVhd

  Log "=== IMAGE puis RESTAURATION ==="
  $sn = Mount-Vhd $srcVhd
  $img = Join-Path $WorkDir 'test.gci'
  if (Test-Path $img) { Remove-Item $img -Force }
  $r = Run-Cli '' @('image', '--src', $sn, '--out', $img)
  Check ($r.Code -eq 0 -and (Test-Path $img)) "image créée"
  Check (-not (Test-Path "$img.partial")) "aucun fichier temporaire résiduel"
  $r = Run-Cli '' @('verify', '--image', $img)
  Check ($r.Code -eq 0) "image vérifiée intègre"
  Log ("taille de l'image : {0:N1} Mo" -f ((Get-Item $img).Length / 1MB))

  $bad = Join-Path $WorkDir 'corrompue.gci'
  Copy-Item $img $bad -Force
  $fs = [IO.File]::Open($bad, 'Open', 'ReadWrite'); $fs.Position = 5MB; $b = $fs.ReadByte(); $fs.Position = 5MB; $fs.WriteByte(($b -bxor 0xFF)); $fs.Close()
  $r = Run-Cli '' @('verify', '--image', $bad)
  Check ($r.Code -eq 2) "image corrompue détectée"

  Dismount-Vhd $srcVhd
  $d2Vhd = New-Vhd 'dst2' 400
  $d2n = Mount-Vhd $d2Vhd
  $r = Run-Cli "EFFACER DISQUE $d2n" @('restore', '--image', $bad, '--dst', $d2n)
  Check ($r.Code -ne 0) "restauration d'une image corrompue refusée"
  $r = Run-Cli "EFFACER DISQUE $d2n" @('restore', '--image', $img, '--dst', $d2n)
  Check ($r.Code -eq 0) "restauration terminée avec succès"
  Dismount-Vhd $d2Vhd
  $d2n = Mount-Vhd $d2Vhd
  Check ((Get-Disk -Number $d2n).PartitionStyle -eq 'GPT') "disque restauré en GPT"
  $rl = Data-Letter $d2n
  Check ((Get-FileHash "${rl}:\data.bin" -Algorithm SHA256).Hash -eq $h1) "data.bin restauré identique (SHA-256)"
  Check ((Get-FileHash "${rl}:\dossier\note.txt" -Algorithm SHA256).Hash -eq $h2) "note.txt restauré identique (SHA-256)"
  $scan = Repair-Volume -DriveLetter $rl -Scan
  Check ($scan -eq 'NoErrorsFound') "système de fichiers restauré sain (Repair-Volume : $scan)"

  Log "=== TOUS LES TESTS D'INTEGRATION REUSSIS ==="
  exit 0
}
catch {
  Log ("ECHEC FATAL : " + $_.Exception.Message)
  Log ($_.ScriptStackTrace)
  exit 1
}
finally {
  foreach ($p in @($mounted)) { try { Dismount-DiskImage -ImagePath $p | Out-Null } catch { } }
}
