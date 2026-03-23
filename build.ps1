dotnet publish TagEverything.UI/TagEverything.UI.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o ./publish

# Create bin directory
$binDir = "publish\bin"
if (!(Test-Path $binDir)) {
    New-Item -ItemType Directory -Path $binDir | Out-Null
}

# Ensure ffmpeg binaries are copied to the bin directory
Copy-Item -Path "TagEverything.UI\ffmpeg.exe" -Destination "$binDir\ffmpeg.exe" -Force -ErrorAction SilentlyContinue
Copy-Item -Path "TagEverything.UI\ffprobe.exe" -Destination "$binDir\ffprobe.exe" -Force -ErrorAction SilentlyContinue

# Move all other DLLs and files to bin, except the main exe and settings.json
Get-ChildItem -Path "publish" -File | Where-Object { $_.Name -ne "TagEverything.exe" -and $_.Name -ne "settings.json" } | Move-Item -Destination $binDir -Force

# Remove any stray PDB files that might still be generated
Get-ChildItem -Path "publish" -Recurse -Filter "*.pdb" | Remove-Item -Force -ErrorAction SilentlyContinue