$ErrorActionPreference = "Stop"

$projectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$publishDir = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "publish"))
$publishParent = [System.IO.Path]::GetDirectoryName($publishDir)

if (![string]::Equals($publishParent, $projectRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    [System.IO.Path]::GetFileName($publishDir) -ne "publish") {
    throw "Unexpected publish directory: $publishDir"
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

dotnet publish (Join-Path $projectRoot "TagEverything.UI\TagEverything.UI.csproj") -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE. Packaging stopped."
}

# Create bin directory
$binDir = Join-Path $publishDir "bin"
if (!(Test-Path -LiteralPath $binDir)) {
    New-Item -ItemType Directory -Path $binDir | Out-Null
}

# Move all other DLLs and files to bin, except the main executable.
# User settings live under LocalAppData and must never be packaged.
Get-ChildItem -LiteralPath $publishDir -File |
    Where-Object { $_.Name -ne "TagEverything.exe" } |
    Move-Item -Destination $binDir -Force

# Remove any stray PDB files that might still be generated
Get-ChildItem -LiteralPath $publishDir -Recurse -Filter "*.pdb" |
    Remove-Item -Force -ErrorAction SilentlyContinue

[xml]$versionProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$revision = git -C $projectRoot rev-parse HEAD
$dirty = @(git -C $projectRoot status --porcelain).Count -gt 0
$releaseInfo = [ordered]@{
    version = [string]$versionProperties.Project.PropertyGroup.Version
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
    revision = [string]$revision
    includesUncommittedChanges = $dirty
    runtime = 'Microsoft.WindowsDesktop.App 9.x x64'
    sha256 = (Get-FileHash -LiteralPath (Join-Path $publishDir 'TagEverything.exe') -Algorithm SHA256).Hash
}
$releaseInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishDir 'release-info.json') -Encoding utf8
