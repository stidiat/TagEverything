param([Parameter(Mandatory)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'check-runtime.ps1')
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('TagEverything-install-smoke-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $smokeRoot 'app'
$library = Join-Path $smokeRoot 'library'
New-Item -ItemType Directory -Path $library -Force | Out-Null
$settings = Join-Path $env:LOCALAPPDATA 'TagEverything\settings.json'
$settingsExisted = Test-Path -LiteralPath $settings
if ($settingsExisted) { Copy-Item -LiteralPath $settings -Destination (Join-Path $smokeRoot 'settings.backup') }
$appProcess = $null
try {
    $installArgs = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /TASKS="" /DIR="' + $target + '" /LOG="' + (Join-Path $smokeRoot 'install.log') + '"'
    $installation = Start-Process -FilePath $installer -ArgumentList $installArgs -Wait -PassThru -WindowStyle Hidden
    if ($installation.ExitCode -ne 0) { throw "Installer failed: $($installation.ExitCode)" }
    $exe = Join-Path $target 'TagEverything.exe'
    if (!(Test-Path -LiteralPath $exe)) { throw 'Main executable missing after installation.' }
    $appProcess = Start-Process -FilePath $exe -ArgumentList ('"' + $library + '"') -PassThru -WindowStyle Hidden
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 250
        $appProcess.Refresh()
        if ($appProcess.HasExited) { throw 'Installed application exited before startup completed.' }
        if ($appProcess.MainWindowTitle -eq 'TagEverything · 文件库' -and (Test-Path -LiteralPath (Join-Path $library 'TagEverythingData\library.db'))) { $ready = $true; break }
    }
    if (!$ready) { throw 'Installed application did not initialize a library/window.' }
    $appProcess.CloseMainWindow() | Out-Null
    if (!$appProcess.WaitForExit(10000)) { throw 'Installed application did not exit normally.' }
    if ($appProcess.ExitCode -ne 0) { throw "Application failed: $($appProcess.ExitCode)" }
    $uninstaller = Join-Path $target 'unins000.exe'
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru -WindowStyle Hidden
    if ($uninstall.ExitCode -ne 0 -or (Test-Path -LiteralPath $exe)) { throw 'Uninstall smoke check failed.' }
    Write-Output 'PASS: installer, first library startup, normal shutdown, uninstall.'
} finally {
    if ($appProcess -and !$appProcess.HasExited) { $appProcess.Kill(); $appProcess.WaitForExit() }
    if ($settingsExisted) { Copy-Item -LiteralPath (Join-Path $smokeRoot 'settings.backup') -Destination $settings -Force }
    elseif (Test-Path -LiteralPath $settings) { Remove-Item -LiteralPath $settings }
    Write-Output "Installer logs retained: $smokeRoot"
}
