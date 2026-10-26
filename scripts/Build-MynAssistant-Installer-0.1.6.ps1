#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Keep Windows PowerShell 5.1 output predictable.
try {
    chcp 65001 *> $null
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
    $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
} catch {}

$repoUrl = 'https://github.com/MynKralus/MynAI-DotNet.git'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$downloads = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads'
$workRoot = Join-Path $env:LOCALAPPDATA "MynAssistant\Builds\MynAI-DotNet-0.1.6-$stamp"
$logPath = Join-Path $downloads "MynAssistant-build-0.1.6-$stamp.log"
$transcriptStarted = $false

function Invoke-Checked([string]$Label, [scriptblock]$Action) {
    Write-Host ''
    Write-Host "=== $Label ===" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed (exit code $LASTEXITCODE). Log: $logPath"
    }
}

try {
    New-Item -ItemType Directory -Force -Path $downloads, (Split-Path $workRoot -Parent) | Out-Null
    Start-Transcript -Path $logPath -Force | Out-Null
    $transcriptStarted = $true

    Write-Host 'MynAssistant 0.1.6 Windows installer build'
    Write-Host "Temporary source: $workRoot"
    Write-Host "Build log: $logPath"
    Write-Host 'Existing installers will not be overwritten.'

    if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) {
        throw 'Git for Windows is required: https://git-scm.com/download/win'
    }
    if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
        throw '.NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0'
    }
    $sdkLines = @(& dotnet --list-sdks)
    if (-not ($sdkLines | Where-Object { $_ -match '^10\.0\.' })) {
        throw '.NET 10 SDK was not found.'
    }

    $pythonTag = $null
    foreach ($tag in @('3.13','3.12')) {
        try {
            $versionLines = @(& py "-$tag" --version 2>&1)
            $versionExit = $LASTEXITCODE
            $versionText = (($versionLines | ForEach-Object { [string]$_ }) -join ' ').Trim()
            if ($versionExit -eq 0 -and $versionText -match "^Python $([regex]::Escape($tag))\.\d+") {
                $pythonTag = $tag
                Write-Host "Python runtime selected: py -$tag ($versionText)"
                break
            }
        } catch {}
    }
    if (-not $pythonTag) {
        throw 'Python 3.13 or 3.12 could not be started by the Python launcher.'
    }

    $compilerCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $compiler = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
    if (-not $compiler) {
        foreach ($item in @(Get-Command ISCC.exe -All -ErrorAction SilentlyContinue)) {
            if ($item.Source -and (Test-Path -LiteralPath $item.Source -PathType Leaf)) {
                $compiler = $item.Source
                break
            }
        }
    }
    if (-not $compiler) {
        throw 'Inno Setup 6 (ISCC.exe) is required: https://jrsoftware.org/isdl.php'
    }
    Write-Host "Inno Setup compiler: $compiler"

    Invoke-Checked 'Clone clean 0.1.6 source' {
        git clone --depth 1 --branch main --single-branch $repoUrl $workRoot
    }
    Set-Location $workRoot

    $versionSource = Get-Content 'src/MynAI.Windows/MynAI.Windows.csproj' -Raw
    if ($versionSource -notmatch '<Version>0\.1\.6</Version>') {
        throw 'GitHub main is not the expected 0.1.6 source yet. Merge the 0.1.6 pull request first.'
    }
    if (-not (Test-Path 'assets/branding/mynassistant-icon-source.png')) {
        throw 'The new MynAssistant branding source image is missing.'
    }

    New-Item -ItemType Directory -Force -Path 'build/installer','build/bridge-dist','build/pyinstaller-work','build/pyinstaller-spec' | Out-Null

    Invoke-Checked 'Create project-local Python environment' {
        & py "-$pythonTag" -m venv .venv
    }
    $python = Join-Path $workRoot '.venv\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
        throw 'The project-local Python environment was not created.'
    }

    Invoke-Checked 'Install bridge requirements and Pillow' {
        & $python -m pip install --disable-pip-version-check --timeout 60 --retries 2 -r tools/MynAssistantDesktopBridge/requirements.txt Pillow
    }
    Invoke-Checked 'Run desktop bridge tests' {
        & $python -m unittest discover -s tools/MynAssistantDesktopBridge/tests -v
    }
    Invoke-Checked 'Generate new app, tray, settings, and installer logos' {
        & $python .github/scripts/generate_branding.py
    }
    Invoke-Checked 'Build hidden background PC bridge' {
        & $python -m PyInstaller --noconfirm --clean --onefile --noconsole --name MynAssistantDesktopBridge --distpath build/bridge-dist --workpath build/pyinstaller-work --specpath build/pyinstaller-spec --collect-all google_auth_oauthlib --collect-all google.auth --hidden-import win32crypt tools/MynAssistantDesktopBridge/agent.py
    }
    if (-not (Test-Path 'build/bridge-dist/MynAssistantDesktopBridge.exe')) {
        throw 'MynAssistantDesktopBridge.exe was not created.'
    }

    Invoke-Checked 'Restore solution' {
        dotnet restore MynAI.slnx
    }
    Invoke-Checked 'Build Release configuration' {
        dotnet build MynAI.slnx --configuration Release --no-restore
    }
    Invoke-Checked 'Publish self-contained Windows app' {
        dotnet publish src/MynAI.Windows/MynAI.Windows.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false --output build/publish
    }
    Invoke-Checked 'Publish self-contained Agent' {
        dotnet publish src/MynAI.Agent/MynAI.Agent.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:WindowsAppSDKSelfContained=true --output build/agent-publish
    }

    Copy-Item 'build/bridge-dist/MynAssistantDesktopBridge.exe' 'build/publish/MynAssistantDesktopBridge.exe' -Force
    foreach ($required in @('build/publish/MynAssistant.exe','build/publish/MynAssistantDesktopBridge.exe','build/agent-publish/MynAgent.exe','build/installer/MynAssistantWizardSmall.bmp')) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Required build output is missing: $required"
        }
    }

    Invoke-Checked 'Compile installer with Inno Setup' {
        & $compiler 'installer/MynAI.iss'
    }
    $installer = Join-Path $workRoot 'build/installer/MynAssistant-Setup.exe'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        throw 'The Inno Setup compiler did not produce MynAssistant-Setup.exe.'
    }
    if ((Get-Item -LiteralPath $installer).Length -lt 50MB) {
        throw 'Installer is unexpectedly small; refusing to report success.'
    }

    $downloadPath = Join-Path $downloads "MynAssistant-Setup-0.1.6-$stamp.exe"
    Copy-Item -LiteralPath $installer -Destination $downloadPath
    $sha = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
    Write-Host ''
    Write-Host 'BUILD SUCCESSFUL: MynAssistant 0.1.6 installer'
    Write-Host "Installer: $downloadPath"
    Write-Host "Size MB: $([Math]::Round((Get-Item -LiteralPath $downloadPath).Length / 1MB, 2))"
    Write-Host "SHA-256: $sha"
    Write-Host "Log: $logPath"
    Write-Host "Temporary source retained at: $workRoot"
}
catch {
    Write-Host ''
    Write-Host "BUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Log: $logPath" -ForegroundColor Yellow
    exit 1
}
finally {
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch {}
    }
}
