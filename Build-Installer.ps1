# Build-Installer.ps1
$ProjectName = "MusicPower3"

$PublishDir  = "$PSScriptRoot\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish"
$PayloadZip  = "$PSScriptRoot\SetupWizard\zipping\Payload.zip"
$InstallerExe = "$PSScriptRoot\MusicPower3-Installer.exe"

# --- VERSION FILES ---
$mainCsproj = "$PSScriptRoot\MusicPower3.csproj"
$manifestPath = "$PSScriptRoot\Package.appxmanifest"
$setupCsproj = "$PSScriptRoot\SetupWizard\SetupWizard.csproj"
$setupMainForm = "$PSScriptRoot\SetupWizard\MainForm.cs"

Write-Host "0. Calculating Next Version & Backing up sources..." -ForegroundColor Cyan

# Backup original files in memory
$origMainCsproj = if(Test-Path $mainCsproj) { Get-Content $mainCsproj -Raw } else { $null }
$origManifest = if(Test-Path $manifestPath) { Get-Content $manifestPath -Raw } else { $null }
$origSetupCsproj = if(Test-Path $setupCsproj) { Get-Content $setupCsproj -Raw } else { $null }
$origSetupMainForm = if(Test-Path $setupMainForm) { Get-Content $setupMainForm -Raw } else { $null }

# Rollback function (Triggered only if build fails)
function Restore-Originals {
    Write-Host "Restoring original version numbers due to build failure..." -ForegroundColor Yellow
    if ($origMainCsproj) { Set-Content -Path $mainCsproj -Value $origMainCsproj -NoNewline }
    if ($origManifest) { Set-Content -Path $manifestPath -Value $origManifest -NoNewline }
    if ($origSetupCsproj) { Set-Content -Path $setupCsproj -Value $origSetupCsproj -NoNewline }
    if ($origSetupMainForm) { Set-Content -Path $setupMainForm -Value $origSetupMainForm -NoNewline }
}

$NewVersion = "Unknown"

if ($origMainCsproj -match '<Version>(.*?)</Version>') {
    $currentVersion = $Matches[1]
    $parts = $currentVersion.Split('.')
    
    [int]$major = $parts[0]
    [int]$minor = $parts[1]
    [int]$build = if ($parts.Length -gt 2) { $parts[2] } else { 0 }
    [int]$rev = if ($parts.Length -gt 3) { $parts[3] } else { 0 }

    # Version Increment Logic
    if ($minor -ge 9) {
        $major++
        $minor = 0
    } else {
        $minor++
    }
    
    $NewVersion = "$major.$minor.$build.$rev"
    Write-Host "Version Bump Initialized: $currentVersion -> $NewVersion" -ForegroundColor Green

    # Injector Function for .csproj formats
    function Update-CsprojVersion([string]$content) {
        $tags = @("Version", "AssemblyVersion", "FileVersion")
        foreach ($tag in $tags) {
            if ($content -match "<$tag>.*?</$tag>") {
                $content = $content -replace "<$tag>.*?</$tag>", "<$tag>$NewVersion</$tag>"
            } else {
                $content = $content -replace "<PropertyGroup>", "<PropertyGroup>`n    <$tag>$NewVersion</$tag>"
            }
        }
        return $content
    }

    # Apply new version to files
    if ($origMainCsproj) { Set-Content -Path $mainCsproj -Value (Update-CsprojVersion $origMainCsproj) -NoNewline }
    if ($origManifest) { Set-Content -Path $manifestPath -Value ($origManifest -replace 'Version="\d+\.\d+\.\d+\.\d+"', "Version=""$NewVersion""") -NoNewline }
    if ($origSetupCsproj) { Set-Content -Path $setupCsproj -Value (Update-CsprojVersion $origSetupCsproj) -NoNewline }
    
    # FIXED: Replaced capture groups with absolute string generation to prevent C# syntax corruption
    if ($origSetupMainForm) { Set-Content -Path $setupMainForm -Value ($origSetupMainForm -replace 'key\.SetValue\("DisplayVersion",\s*"\d+\.\d+\.\d+\.\d+"\);', "key.SetValue(`"DisplayVersion`", `"$NewVersion`");") -NoNewline }
} else {
    Write-Host "Could not find current <Version> tag in MusicPower3.csproj. Skipping auto-bump." -ForegroundColor Yellow
}

Write-Host "`n1. Cleaning up testing files to prevent NETSDK1152 conflicts..." -ForegroundColor Cyan
if (Test-Path "$PSScriptRoot\$ProjectName.exe") { Remove-Item "$PSScriptRoot\$ProjectName.exe" -Force }

Write-Host "`n2. Publishing Single-File WinUI 3 App..." -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:PublishProfile=

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed! Please check errors." -ForegroundColor Red
    Restore-Originals
    exit $LASTEXITCODE
}

Write-Host "`n3. Zipping Payload into new 'zipping' folder..." -ForegroundColor Cyan
if (!(Test-Path "$PSScriptRoot\SetupWizard\zipping")) {
    New-Item -ItemType Directory -Force -Path "$PSScriptRoot\SetupWizard\zipping" | Out-Null
}
if (Test-Path $PayloadZip) { Remove-Item $PayloadZip -Force }

Push-Location $PublishDir
Compress-Archive -Path * -DestinationPath $PayloadZip -Force
Pop-Location

Write-Host "`n4. Compiling Single-File Setup Wizard..." -ForegroundColor Cyan
Push-Location "$PSScriptRoot\SetupWizard"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishProfile=
$setupExitCode = $LASTEXITCODE
Pop-Location

if ($setupExitCode -ne 0) {
    Write-Host "Setup compile failed! Please check errors." -ForegroundColor Red
    Restore-Originals
    exit $setupExitCode
}

Write-Host "`n5. Moving final installer..." -ForegroundColor Cyan
$SetupPublishDir = "$PSScriptRoot\SetupWizard\bin\Release\net10.0-windows\win-x64\publish"
if (Test-Path "$SetupPublishDir\SetupWizard.exe") {
    Copy-Item -Path "$SetupPublishDir\SetupWizard.exe" -Destination $InstallerExe -Force
    Write-Host "SUCCESS: Standalone Installer generated at: $InstallerExe (v$NewVersion)" -ForegroundColor Green
} else {
    Write-Host "ERROR: Could not find compiled SetupWizard.exe" -ForegroundColor Red
    Restore-Originals
    exit 1
}