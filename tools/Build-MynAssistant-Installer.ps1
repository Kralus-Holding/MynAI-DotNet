#requires -Version 5.1
# Official local builder for MynAssistant Windows 0.1.5.
# Run this script after the PR containing it is merged to main.
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 treats UTF-8 without a BOM as the active ANSI code page.
# Force UTF-8 output as well so Turkish characters are not garbled in the console/log.
try { chcp 65001 | Out-Null } catch {}
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

$version = '0.1.5'
$repoUrl = 'https://github.com/MynKralus/MynAI-DotNet.git'
$downloads = Join-Path $env:USERPROFILE 'Downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$workRoot = Join-Path $env:LOCALAPPDATA "MynAssistant\Builds\MynAI-DotNet-$version-$stamp"
$logPath = Join-Path $downloads "MynAssistant-build-$version-$stamp.log"
$transcriptStarted = $false

function Stop-WithMessage([string]$Message) {
    throw $Message
}

function Invoke-Checked([string]$Label, [scriptblock]$Action) {
    Write-Host ""
    Write-Host "=== $Label ===" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed (exit code $LASTEXITCODE)."
    }
}

try {
    Start-Transcript -LiteralPath $logPath -Force | Out-Null
    $transcriptStarted = $true

    Write-Host "MynAssistant $version installer build"
    Write-Host "Temporary source: $workRoot"
    Write-Host "Log: $logPath"
    Write-Host "Existing installer files will not be overwritten."

    foreach ($tool in @('git.exe', 'dotnet.exe', 'py.exe')) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            Stop-WithMessage "Required tool not found: $tool"
        }
    }

    $pythonTag = $null
    foreach ($tag in @('3.12', '3.13')) {
        try {
            $versionOutput = @(& py "-$tag" --version 2>&1)
            if ($LASTEXITCODE -eq 0 -and (($versionOutput -join ' ') -match '^Python 3\.(12|13)\.\d+')) {
                $pythonTag = $tag
                Write-Host "Python runtime: $($versionOutput -join ' ')" -ForegroundColor Green
                break
            }
        } catch {}
    }
    if (-not $pythonTag) {
        Stop-WithMessage 'Python 3.12 or 3.13 x64 is required. py -3.13 --version should work before running this builder.'
    }

    $dotnetVersion = (& dotnet --version 2>&1 | Select-Object -Last 1).ToString().Trim()
    if ($LASTEXITCODE -ne 0 -or $dotnetVersion -notmatch '^10\.') {
        Stop-WithMessage "The .NET 10 SDK is required. Detected: $dotnetVersion"
    }
    Write-Host " .NET SDK: $dotnetVersion" -ForegroundColor Green

    $compilerCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $compiler = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $compiler) {
        $registryKeys = @(
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
        )
        foreach ($key in $registryKeys) {
            try {
                $location = (Get-ItemProperty -LiteralPath $key -ErrorAction Stop).InstallLocation
                if ($location -and (Test-Path (Join-Path $location 'ISCC.exe'))) {
                    $compiler = Join-Path $location 'ISCC.exe'
                    break
                }
            } catch {}
        }
    }
    if (-not $compiler) {
        Stop-WithMessage 'Inno Setup 6 compiler (ISCC.exe) not found. Install JRSoftware.InnoSetup, then rerun this script.'
    }
    Write-Host "Inno Setup compiler: $compiler" -ForegroundColor Green

    # Reuse the configured desktop OAuth JSON locally; never commit it to the repository.
    $oauthPath = Join-Path $env:LOCALAPPDATA 'MynAssistant\Google\client_secret.json'
    if (-not (Test-Path -LiteralPath $oauthPath)) {
        Write-Host 'Select the Google Desktop OAuth client_secret.json file.' -ForegroundColor Yellow
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        $dialog.Title = 'Select Google Desktop OAuth client_secret.json'
        $dialog.Filter = 'Google OAuth JSON (client_secret.json)|client_secret.json|JSON files (*.json)|*.json|All files (*.*)|*.*'
        $dialog.Multiselect = $false
        if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            Stop-WithMessage 'No Google OAuth JSON selected. A distributable installer cannot be built without it.'
        }
        $oauthPath = $dialog.FileName
    }
    $oauth = Get-Content -LiteralPath $oauthPath -Raw | ConvertFrom-Json
    if (-not $oauth.installed) {
        Stop-WithMessage 'The selected JSON is not a Google Desktop OAuth client (installed root is missing).'
    }

    Invoke-Checked 'Clone latest main into a clean temporary folder' {
        git clone --depth 1 --branch main --single-branch $repoUrl $workRoot
    }
    Set-Location $workRoot

    $project = Get-Content -LiteralPath 'src/MynAI.Windows/MynAI.Windows.csproj' -Raw
    $installerScript = Get-Content -LiteralPath 'installer/MynAI.iss' -Raw
    if ($project -notmatch '<Version>0\.1\.5</Version>' -or $installerScript -notmatch '#define MyAppVersion "0\.1\.5"') {
        Stop-WithMessage 'GitHub main does not contain the expected 0.1.5 source yet. Merge the branding/layout PR, then rerun this script.'
    }
    foreach ($required in @('installer/LICENSE-AGREEMENT.rtf','assets/branding/mynassistant-icon-source.png','tools/prepare-branding.py')) {
        if (-not (Test-Path -LiteralPath $required)) {
            Stop-WithMessage "Required source asset is missing: $required"
        }
    }

    New-Item -ItemType Directory -Force -Path 'build/config','build/installer','build/bridge-dist','build/pyinstaller-work','build/pyinstaller-spec' | Out-Null
    Copy-Item -LiteralPath $oauthPath -Destination 'build/config/client_secret.json' -Force

    Invoke-Checked 'Create project Python virtual environment' {
        & py "-$pythonTag" -m venv .venv
    }
    $python = Join-Path $workRoot '.venv\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $python)) {
        Stop-WithMessage 'The project Python virtual environment was not created.'
    }

    Invoke-Checked 'Install pinned Python dependencies' {
        & $python -m pip install --disable-pip-version-check --timeout 60 --retries 2 -r tools/MynAssistantDesktopBridge/requirements.txt
    }

    Invoke-Checked 'Prepare transparent app, taskbar, and installer logos' {
        & $python tools/prepare-branding.py
    }

    Invoke-Checked 'Build the Desktop Bridge executable' {
        & $python -m PyInstaller --noconfirm --clean --onefile --console --name MynAssistantDesktopBridge --distpath build/bridge-dist --workpath build/pyinstaller-work --specpath build/pyinstaller-spec --collect-all google_auth_oauthlib --collect-all google.auth --hidden-import win32crypt tools/MynAssistantDesktopBridge/agent.py
    }
    if (-not (Test-Path -LiteralPath 'build/bridge-dist/MynAssistantDesktopBridge.exe')) {
        Stop-WithMessage 'MynAssistantDesktopBridge.exe was not produced.'
    }

    Invoke-Checked 'Restore the .NET solution' {
        dotnet restore MynAI.slnx
    }
    Invoke-Checked 'Build Release configuration' {
        dotnet build MynAI.slnx --configuration Release --no-restore
    }
    Invoke-Checked 'Publish self-contained Windows app' {
        dotnet publish src/MynAI.Windows/MynAI.Windows.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false --output build/publish
    }
    Invoke-Checked 'Publish self-contained background Agent' {
        dotnet publish src/MynAI.Agent/MynAI.Agent.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:WindowsAppSDKSelfContained=true --output build/agent-publish
    }

    Copy-Item -LiteralPath 'build/bridge-dist/MynAssistantDesktopBridge.exe' -Destination 'build/publish/MynAssistantDesktopBridge.exe' -Force
    foreach ($required in @('build/publish/MynAssistant.exe','build/publish/Resources/mynassistant-logo.png','build/publish/MynAssistantDesktopBridge.exe','build/agent-publish/MynAgent.exe','build/installer/MynAssistantWizardSmall.bmp','src/MynAI.Windows/Resources/myn.ico')) {
        if (-not (Test-Path -LiteralPath $required)) {
            Stop-WithMessage "Build output is missing: $required"
        }
    }

    Invoke-Checked 'Compile the Inno Setup installer' {
        & $compiler 'installer/MynAI.iss'
    }
    $installer = Join-Path $workRoot 'build/installer/MynAssistant-Setup.exe'
    if (-not (Test-Path -LiteralPath $installer)) {
        Stop-WithMessage 'MynAssistant-Setup.exe was not created by Inno Setup.'
    }

    $downloadPath = Join-Path $downloads "MynAssistant-Setup-$version-$stamp.exe"
    Copy-Item -LiteralPath $installer -Destination $downloadPath
    $hash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
    Write-Host ''
    Write-Host 'BUILD SUCCEEDED' -ForegroundColor Green
    Write-Host "File: $downloadPath"
    Write-Host "Size: $([Math]::Round((Get-Item -LiteralPath $downloadPath).Length / 1MB, 2)) MB"
    Write-Host "SHA-256: $hash"
    Write-Host "Log: $logPath"
    Write-Host "Temporary sources retained at: $workRoot"
}
catch {
    Write-Host ''
    Write-Host ("BUILD FAILED: " + $_.Exception.Message) -ForegroundColor Red
    Write-Host "Log: $logPath" -ForegroundColor Yellow
    exit 1
}
finally {
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch {}
    }
}
