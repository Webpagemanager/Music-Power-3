# Test-Exe.ps1 - Compile and Place Standalone EXE in Root Folder for Testing
$ProjectName = "MusicPower3"
# ADDED \x64\ TO MATCH THE NEW 64-BIT ARCHITECTURE BUILD PATH
$PublishDir  = "$PSScriptRoot\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish"
$OutExe      = "$PSScriptRoot\$ProjectName.exe"

Write-Host "0. Cleaning up previous test executables..." -ForegroundColor Yellow
if (Test-Path $OutExe) { Remove-Item $OutExe -Force }

Write-Host "1. Publishing Single-File Standalone App for Testing..." -ForegroundColor Cyan
# Suppress the missing pubxml warning with -p:PublishProfile= (empty string)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishProfile=

if ($LASTEXITCODE -ne 0) {
    Write-Host "BUILD FAILED! Please check the compile errors above." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "2. Copying standalone executable to project root..." -ForegroundColor Cyan
$PublishedExe = "$PublishDir\$ProjectName.exe"

if (Test-Path $PublishedExe) {
    # Copy the single-file executable to your main directory
    Copy-Item -Path $PublishedExe -Destination $OutExe -Force
    
    # Ensure localized assets (like thumb.ico) accompany the EXE so ms-appx:/// URIs resolve seamlessly
    if (Test-Path "$PublishDir\Assets") {
        Copy-Item -Path "$PublishDir\Assets" -Destination "$PSScriptRoot\Assets" -Recurse -Force
    }
    
    # Copy resources.pri if the Windows App SDK generated it alongside the executable
    if (Test-Path "$PublishDir\resources.pri") {
        Copy-Item -Path "$PublishDir\resources.pri" -Destination "$PSScriptRoot\resources.pri" -Force
    }

    Write-Host "SUCCESS: Ready for testing! You can now launch: $OutExe" -ForegroundColor Green
} else {
    Write-Host "ERROR: Could not find published executable at $PublishedExe" -ForegroundColor Red
}