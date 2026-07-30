# Clean-Build.ps1 - Deep Clean for Music Power 3 Build Artifacts
$ProjectName = "MusicPower3"
$OutExe      = "$PSScriptRoot\$ProjectName-Installer.exe"

Write-Host "1. Running dotnet clean on all projects..." -ForegroundColor Cyan
dotnet clean --verbosity quiet

Write-Host "2. Purging main app bin/ and obj/ directories..." -ForegroundColor Cyan
if (Test-Path "$PSScriptRoot\bin") { Remove-Item -Path "$PSScriptRoot\bin" -Recurse -Force }
if (Test-Path "$PSScriptRoot\obj") { Remove-Item -Path "$PSScriptRoot\obj" -Recurse -Force }

Write-Host "3. Purging SetupWizard bin/ and obj/ directories..." -ForegroundColor Cyan
if (Test-Path "$PSScriptRoot\SetupWizard\bin") { Remove-Item -Path "$PSScriptRoot\SetupWizard\bin" -Recurse -Force }
if (Test-Path "$PSScriptRoot\SetupWizard\obj") { Remove-Item -Path "$PSScriptRoot\SetupWizard\obj" -Recurse -Force }

Write-Host "4. Removing intermediate 'zipping' payload folder..." -ForegroundColor Cyan
if (Test-Path "$PSScriptRoot\zipping") { Remove-Item -Path "$PSScriptRoot\zipping" -Recurse -Force }

Write-Host "5. Removing previous standalone installer..." -ForegroundColor Cyan
if (Test-Path $OutExe) { Remove-Item -Path $OutExe -Force }

# Optional: Clean up any lingering IExpress .sed or temporary files if they exist
Get-ChildItem -Path $PSScriptRoot -Filter "*.sed" -File -Recurse | Remove-Item -Force

Write-Host "SUCCESS: Workspace is 100% clean and ready for a fresh build!" -ForegroundColor Green