param([string]$CompilerPath, [switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$CompilerPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $CompilerPath = $command.Source }
    else { $CompilerPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
}
if (!(Test-Path -LiteralPath $CompilerPath)) { throw 'Inno Setup compiler missing. Supply -CompilerPath or install Inno Setup 6.' }
& $CompilerPath (Join-Path $projectRoot 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Production installer compilation failed.' }
if ($SmokeTest) {
    $smokeOutput = Join-Path $projectRoot 'artifacts\installer-smoke'
    New-Item -ItemType Directory -Path $smokeOutput -Force | Out-Null
    # Use the same payload/runtime check, without modifying shell associations or global uninstall registration.
    & $CompilerPath '/DSmokeTest' ("/O" + $smokeOutput) '/FTagEverything_Smoke' (Join-Path $projectRoot 'installer.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Smoke installer compilation failed.' }
    & (Join-Path $PSScriptRoot 'test-installer.ps1') -InstallerPath (Join-Path $smokeOutput 'TagEverything_Smoke.exe')
}
