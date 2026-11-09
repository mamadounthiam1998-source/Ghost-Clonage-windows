<#
.SYNOPSIS
  Crée une clé USB de démarrage WinPE contenant la version console de GHOST CLONAGE WINDOWS
  (clonage / image / restauration hors ligne, sans que le Windows source soit en cours d'exécution).

.DESCRIPTION
  Prérequis (à installer une fois, gratuits chez Microsoft) :
    - Windows ADK  (https://learn.microsoft.com/windows-hardware/get-started/adk-install)
    - Windows PE add-on for the ADK
  Exécuter dans « Environnement de déploiement et d'outils de création d'images » (PowerShell/CMD administrateur).

  ATTENTION : la clé USB indiquée est FORMATÉE (toutes ses données sont effacées).

  STATUT : script fourni mais NON validé sur du matériel réel dans le cadre de ce développement. Testez la clé
  produite (démarrage, détection des disques) AVANT de vous y fier pour une opération importante.
  Limite connue : l'interface graphique WPF ne fonctionne pas dans WinPE ; la version console est utilisée.

.PARAMETER UsbDrive
  Lettre du lecteur USB, par exemple E:

.PARAMETER CliPath
  Dossier de la version console publiée (par défaut ..\cli à côté de ce script, comme dans l'installateur).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z]:?$')][string]$UsbDrive,
  [string]$CliPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'cli'),
  [string]$WorkDir = (Join-Path $env:TEMP 'GhostWinPE')
)

$ErrorActionPreference = 'Stop'

function Assert-Admin {
  $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
  if (-not $p.IsInPrincipal([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Ce script doit être exécuté en tant qu'administrateur."
  }
}
Assert-Admin

$drive = ($UsbDrive.TrimEnd(':')).ToUpper() + ':'
$adkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Assessment and Deployment Kit\Windows Preinstallation Environment'
$copype = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Assessment and Deployment Kit\Windows Preinstallation Environment\copype.cmd'
$envBat = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\DandISetEnv.bat'
if (-not (Test-Path $copype) -or -not (Test-Path $envBat)) {
  throw "Windows ADK et son module complémentaire Windows PE sont introuvables. Installez-les d'abord (voir l'aide du script)."
}
if (-not (Test-Path (Join-Path $CliPath 'GhostClonageCli.exe'))) {
  throw "GhostClonageCli.exe introuvable dans '$CliPath'."
}

# Garde-fous : jamais le disque système, uniquement un disque amovible
$sysDrive = $env:SystemDrive.ToUpper()
if ($drive -eq $sysDrive) { throw "Refus : $drive est le lecteur système." }
$vol = Get-Volume -DriveLetter $drive[0] -ErrorAction Stop
$part = Get-Partition -DriveLetter $drive[0]
$disk = Get-Disk -Number $part.DiskNumber
if ($disk.BusType -ne 'USB') {
  throw "Refus : $drive n'est pas sur un disque USB (bus : $($disk.BusType))."
}

Write-Host ""
Write-Host "  Clé USB cible : $drive  —  $($disk.FriendlyName)  —  $([math]::Round($disk.Size/1GB,1)) Go" -ForegroundColor Yellow
Write-Host "  TOUTES LES DONNÉES de cette clé vont être EFFACÉES." -ForegroundColor Red
$answer = Read-Host "  Pour confirmer, tapez exactement : EFFACER USB $($disk.Number)"
if ($answer -ne "EFFACER USB $($disk.Number)") { Write-Host "Annulé : rien n'a été modifié."; exit 1 }

if (Test-Path $WorkDir) { Remove-Item $WorkDir -Recurse -Force }

# Toutes les étapes ADK dans un seul cmd pour conserver l'environnement DandISetEnv
$mount = Join-Path $WorkDir 'mount'
$script = @"
call "$envBat"
copype amd64 "$WorkDir" || exit /b 1
Dism /Mount-Image /ImageFile:"$WorkDir\media\sources\boot.wim" /Index:1 /MountDir:"$mount" || exit /b 1
xcopy /E /I /Y "$CliPath" "$mount\GhostClonage" || goto :fail
copy /Y "$PSScriptRoot\Ghost-Menu.cmd" "$mount\GhostClonage\Ghost-Menu.cmd" || goto :fail
> "$mount\Windows\System32\startnet.cmd" echo wpeinit
>> "$mount\Windows\System32\startnet.cmd" echo X:\GhostClonage\Ghost-Menu.cmd
Dism /Unmount-Image /MountDir:"$mount" /Commit || exit /b 1
MakeWinPEMedia /UFD /F "$WorkDir" $drive || exit /b 1
exit /b 0
:fail
Dism /Unmount-Image /MountDir:"$mount" /Discard
exit /b 1
"@
$cmdFile = Join-Path $env:TEMP 'ghost-winpe-build.cmd'
Set-Content -Path $cmdFile -Value $script -Encoding ASCII
& cmd.exe /c $cmdFile
if ($LASTEXITCODE -ne 0) { throw "La création du support WinPE a échoué (code $LASTEXITCODE). Aucune garantie sur l'état de la clé." }

Write-Host ""
Write-Host "Support de démarrage créé sur $drive." -ForegroundColor Green
Write-Host "Démarrez l'ordinateur depuis cette clé (menu de démarrage / BIOS-UEFI), puis suivez le menu."
Write-Host "Rappel : testez ce support avant d'en avoir besoin." -ForegroundColor Yellow
