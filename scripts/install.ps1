<#
.SYNOPSIS
  Builds DockGlass, copies the wallpaper into Lively's library and starts
  DockGlass at sign-in. Run it again to update; -Uninstall undoes it.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    # Where DockGlass.exe goes.
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'GlassDesktop'),
    # Lively's wallpaper library (default: the Microsoft Store version's).
    [string]$LivelyLibrary,
    # Don't copy the wallpaper into Lively.
    [switch]$NoWallpaper,
    # Don't add DockGlass to startup and don't start it now.
    [switch]$NoStartup,
    # Stop DockGlass, remove it from startup and delete $InstallDir.
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $InstallDir 'DockGlass.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'DockGlass.lnk'

function Stop-DockGlass {
    Get-Process DockGlass -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe } |
        ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
}

if ($Uninstall) {
    Stop-DockGlass
    if (Test-Path $shortcut) { Remove-Item $shortcut }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse }
    Write-Host 'DockGlass removed. The wallpaper stays in Lively; delete it there if you like.'
    return
}

# 1. Build DockGlass with the C# compiler that ships with Windows (.NET Framework 4).
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }
$gac = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL'
$refs = 'UIAutomationClient', 'UIAutomationTypes', 'WindowsBase' |
    ForEach-Object { '/r:' + (Join-Path $gac "$_\v4.0_4.0.0.0__31bf3856ad364e35\$_.dll") }

New-Item -ItemType Directory -Force $InstallDir | Out-Null
Stop-DockGlass
& $csc /nologo /optimize /target:winexe "/out:$exe" @refs (Join-Path $root 'dockglass\DockGlass.cs')
if ($LASTEXITCODE -ne 0) { throw 'DockGlass failed to build.' }
Write-Host "Built $exe"

# 2. Copy the wallpaper to <library>\prothreads.live, the folder DockGlass writes to by default.
$geometry = $null
if (-not $NoWallpaper) {
    if (-not $LivelyLibrary) {
        $pkg = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter '12030rocksdanister.LivelyWallpaper_*' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $pkg) { throw 'Lively Wallpaper (Microsoft Store) not found. Install it, or pass -LivelyLibrary or -NoWallpaper.' }
        $LivelyLibrary = Join-Path $pkg.FullName 'LocalCache\Local\Lively Wallpaper\Library\wallpapers'
    }
    $target = Join-Path $LivelyLibrary 'prothreads.live'
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $root 'wallpaper\pro-threads-glass\*') $target -Force
    $geometry = Join-Path $target 'geometry.js'
    Write-Host "Copied the wallpaper to $target"
    Write-Host 'Restart Lively (tray icon > Exit, then open it again) and pick "PRO Threads Glass".'
}

# 3. Start DockGlass at sign-in, and now.
if (-not $NoStartup) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $InstallDir
    if ($geometry) { $link.Arguments = '"' + $geometry + '"' }
    $link.Save()
    if ($geometry) { Start-Process $exe -ArgumentList ('"' + $geometry + '"') } else { Start-Process $exe }
    Write-Host "DockGlass is running and starts at sign-in ($shortcut)."
}
