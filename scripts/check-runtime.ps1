$ErrorActionPreference = 'Stop'
if (![Environment]::Is64BitOperatingSystem) { throw 'TagEverything requires 64-bit Windows.' }
$dotnetPath = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetPath)) { throw 'Install the .NET 9 Desktop Runtime (x64) first: https://dotnet.microsoft.com/download/dotnet/9.0' }
$runtimes = & $dotnetPath --list-runtimes
if ($LASTEXITCODE -ne 0 -or !($runtimes -match '^Microsoft.WindowsDesktop.App 9\.')) { throw '.NET 9 Desktop Runtime (x64) was not found.' }
Write-Output '.NET 9 Desktop Runtime (x64): OK'
