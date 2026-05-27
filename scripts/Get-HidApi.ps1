# Get-HidApi.ps1
# Downloads hidapi.dll (Windows x64) for local development.
# Run once from the repo root before building:  .\scripts\Get-HidApi.ps1

$version = "hidapi-0.14.0"
$url     = "https://github.com/libusb/hidapi/releases/download/$version/hidapi-win.zip"
$zip     = "$env:TEMP\hidapi-win.zip"
$extract = "$env:TEMP\hidapi-win"
$dest    = "$PSScriptRoot\..\PSVRPlayer\hidapi.dll"

Write-Host "Downloading hidapi $version..."
Invoke-WebRequest -Uri $url -OutFile $zip

Write-Host "Extracting..."
Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -Path $zip -DestinationPath $extract

$dll = Get-ChildItem -Path $extract -Recurse -Filter hidapi.dll |
         Where-Object { $_.FullName -match 'x64' } |
         Select-Object -First 1

if (-not $dll) {
    Write-Error "hidapi.dll (x64) not found in archive."
    exit 1
}

Copy-Item $dll.FullName -Destination $dest -Force
Write-Host "Installed: $dest"
