param (
    [Parameter(Mandatory=$false)]
    [string]$NewVersion
)

# 1. Prompt for version if not provided as an argument
if ([string]::IsNullOrWhiteSpace($NewVersion)) {
    $NewVersion = Read-Host "Enter the new version number (e.g., 2.1.0.1)"
}

# WinUI 3 and Windows Registry require a strict 4-part version number (Major.Minor.Build.Revision)
if ($NewVersion -notmatch "^\d+\.\d+\.\d+\.\d+$") {
    Write-Warning "Warning: Windows requires a strict 4-part version format (e.g., 2.1.0.0). Your build might fail."
}

# --- FILE PATHS ---
$mainCsproj = ".\MusicPower3.csproj"
$manifestPath = ".\Package.appxmanifest"
$setupCsproj = ".\SetupWizard\SetupWizard.csproj"
$setupMainForm = ".\SetupWizard\MainForm.cs"

# Helper function to update .csproj files
function Update-CsprojVersion($path) {
    if (Test-Path $path) {
        $content = Get-Content $path -Raw
        $tags = @("Version", "AssemblyVersion", "FileVersion")
        $updated = $false

        foreach ($tag in $tags) {
            if ($content -match "<$tag>.*?</$tag>") {
                $content = $content -replace "<$tag>.*?</$tag>", "<$tag>$NewVersion</$tag>"
                $updated = $true
            } else {
                # Inject right under the first <PropertyGroup> if missing
                $content = $content -replace "<PropertyGroup>", "<PropertyGroup>`n    <$tag>$NewVersion</$tag>"
                $updated = $true
            }
        }

        if ($updated) {
            Set-Content -Path $path -Value $content -NoNewline
            Write-Host " [OK] v$NewVersion applied to $path" -ForegroundColor Green
        }
    } else {
        Write-Host " [SKIP] Could not find $path" -ForegroundColor DarkGray
    }
}

Write-Host "`n--- Updating Main Application ---" -ForegroundColor Cyan
Update-CsprojVersion $mainCsproj

if (Test-Path $manifestPath) {
    $content = Get-Content $manifestPath -Raw
    if ($content -match 'Version="\d+\.\d+\.\d+\.\d+"') {
        $content = $content -replace 'Version="\d+\.\d+\.\d+\.\d+"', "Version=""$NewVersion"""
        Set-Content -Path $manifestPath -Value $content -NoNewline
        Write-Host " [OK] v$NewVersion applied to $manifestPath" -ForegroundColor Green
    }
} else {
    Write-Host " [SKIP] Could not find $manifestPath" -ForegroundColor DarkGray
}

Write-Host "`n--- Updating Setup Wizard ---" -ForegroundColor Cyan
Update-CsprojVersion $setupCsproj

if (Test-Path $setupMainForm) {
    $content = Get-Content $setupMainForm -Raw
    
    # Matches: key.SetValue("DisplayVersion", "2.1.0.0");
    if ($content -match '(?mi)(key\.SetValue\("DisplayVersion",\s*")\d+\.\d+\.\d+\.\d+("\);)') {
        $content = $content -replace '(?mi)(key\.SetValue\("DisplayVersion",\s*")\d+\.\d+\.\d+\.\d+("\);)', "`$1$NewVersion`$2"
        Set-Content -Path $setupMainForm -Value $content -NoNewline
        Write-Host " [OK] Registry DisplayVersion updated in $setupMainForm" -ForegroundColor Green
    } else {
        Write-Host " [WARN] Could not find DisplayVersion registry key string in $setupMainForm" -ForegroundColor Yellow
    }
} else {
    Write-Host " [SKIP] Could not find $setupMainForm" -ForegroundColor DarkGray
}

Write-Host "`nMaster version update complete! Ready to build.`n" -ForegroundColor Green