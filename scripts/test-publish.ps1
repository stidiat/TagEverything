param([string]$PublishDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'publish'))
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'check-runtime.ps1')
$exe = Join-Path (Resolve-Path -LiteralPath $PublishDirectory).Path 'TagEverything.exe'
$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('TagEverything-publish-smoke-' + [guid]::NewGuid().ToString('N'))
$library = Join-Path $smokeRoot 'library'
New-Item -ItemType Directory -Path $library -Force | Out-Null
$settings = Join-Path $env:LOCALAPPDATA 'TagEverything\settings.json'
$existed = Test-Path -LiteralPath $settings
if ($existed) { Copy-Item -LiteralPath $settings -Destination (Join-Path $smokeRoot 'settings.backup') }
$process = $null
try {
    $process = Start-Process -FilePath $exe -ArgumentList ('"' + $library + '"') -PassThru -WindowStyle Hidden
    $ready = $false
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw 'Published application exited before startup.' }
        if ($process.MainWindowTitle -eq 'TagEverything · 文件库' -and (Test-Path -LiteralPath (Join-Path $library 'TagEverythingData\library.db'))) { $ready = $true; break }
    }
    if (!$ready) { throw 'Published application did not initialize.' }
    $process.CloseMainWindow() | Out-Null
    if (!$process.WaitForExit(10000) -or $process.ExitCode -ne 0) { throw 'Published application did not exit normally.' }
    Write-Output 'PASS published executable: startup, database migrations/recovery, main window and normal exit.'
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if ($existed) { Copy-Item -LiteralPath (Join-Path $smokeRoot 'settings.backup') -Destination $settings -Force }
    elseif (Test-Path -LiteralPath $settings) { Remove-Item -LiteralPath $settings }
    Write-Output "Isolated startup data: $smokeRoot"
}
