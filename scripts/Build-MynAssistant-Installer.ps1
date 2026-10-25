[CmdletBinding()]
param(
    [string]$Ref = "main"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
try {
    chcp 65001 *> $null
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    [Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
    $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
} catch {}

$repoUrl = "https://github.com/MynKralus/MynAI-DotNet.git"
$version = "0.1.4"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$downloads = Join-Path ([Environment]::GetFolderPath("UserProfile")) "Downloads"
$workRoot = Join-Path $env:LOCALAPPDATA "MynAssistant\Builds\MynAI-DotNet-$version-$stamp"
$logPath = Join-Path $downloads "MynAssistant-build-$version-$stamp.log"
$transcriptStarted = $false
$originalLocation = Get-Location

function Stop-WithMessage([string]$Message) {
    throw $Message
}

function Invoke-Checked([string]$Label, [scriptblock]$Action) {
    Write-Host ([Environment]::NewLine + "=== $Label ===") -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Label başarısız oldu (exit code: $LASTEXITCODE). Günlük: $logPath"
    }
}

try {
    New-Item -ItemType Directory -Force -Path $downloads, (Split-Path $workRoot -Parent) | Out-Null
    Start-Transcript -Path $logPath -Force | Out-Null
    $transcriptStarted = $true

    Write-Host "MynAssistant $version kurulum paketi hazırlanacak." -ForegroundColor Green
    Write-Host "Kaynak dalı: $Ref"
    Write-Host "Geçici kaynak dizini: $workRoot"
    Write-Host "Günlük: $logPath"
    Write-Host "Mevcut kurulum dosyalarının üzerine yazılmayacak."

    if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) {
        Stop-WithMessage "Git bulunamadı. Git for Windows yükle: https://git-scm.com/download/win"
    }
    if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
        Stop-WithMessage ".NET SDK bulunamadı. .NET 10 SDK yükle: https://dotnet.microsoft.com/download/dotnet/10.0"
    }
    $sdkLines = & dotnet --list-sdks
    if (-not ($sdkLines | Where-Object { $_ -match '^10\.0\.' })) {
        Stop-WithMessage ".NET 10 SDK bulunamadı. .NET 10 SDK yükleyip terminali yeniden aç."
    }

    $pythonTag = $null
    foreach ($tag in @("3.13", "3.12")) {
        try {
            $output = @(& py "-$tag" --version 2>&1)
            $exitCode = $LASTEXITCODE
            $versionText = (($output | ForEach-Object { [string]$_ }) -join " ").Trim()
            if ($exitCode -eq 0 -and $versionText -match '^Python 3\.(12|13)\.\d+') {
                $pythonTag = $tag
                Write-Host "Python yorumlayıcısı: py -$tag ($versionText)" -ForegroundColor Green
                break
            }
        } catch {}
    }
    if (-not $pythonTag) {
        Stop-WithMessage "Python 3.12 veya 3.13 çalıştırılamadı. py -3.13 --version komutunu doğrula."
    }

    $compilerCandidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path ([Environment]::GetEnvironmentVariable("ProgramFiles(x86)")) "Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $compiler = $compilerCandidates | Select-Object -First 1
    if (-not $compiler) {
        foreach ($command in @(Get-Command "ISCC.exe" -All -ErrorAction SilentlyContinue)) {
            $candidate = $command.Source
            if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                $compiler = $candidate
                break
            }
        }
    }
    if (-not $compiler) {
        Stop-WithMessage "Inno Setup 6 yüklenmiş görünmüyor. 'winget install --id JRSoftware.InnoSetup --exact' komutunu çalıştır."
    }
    Write-Host "Inno Setup derleyicisi: $compiler" -ForegroundColor Green

    # Keep OAuth credentials local. Never commit or print their contents.
    $oauthPath = Join-Path $env:LOCALAPPDATA "MynAssistant\Google\client_secret.json"
    if (-not (Test-Path -LiteralPath $oauthPath)) {
        Write-Host "Mevcut kurulumdaki Google OAuth dosyası bulunamadı; dosya seçme penceresi açılacak." -ForegroundColor Yellow
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = New-Object System.Windows.Forms.OpenFileDialog
        $dialog.Title = "Google Desktop OAuth client_secret.json dosyasını seç"
        $dialog.Filter = "Google OAuth JSON (client_secret.json)|client_secret.json|JSON dosyaları (*.json)|*.json"
        $dialog.Multiselect = $false
        if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            Stop-WithMessage "Google OAuth JSON dosyası seçilmedi."
        }
        $oauthPath = $dialog.FileName
    }
    $oauth = Get-Content -LiteralPath $oauthPath -Raw | ConvertFrom-Json
    if (-not $oauth.installed) {
        Stop-WithMessage "Seçilen JSON, Google Desktop OAuth istemcisi değil. 'installed' kök alanı bulunamadı."
    }

    Invoke-Checked "Kaynak kodunu GitHub'dan temiz dizine indir" {
        git clone --depth 1 --branch $Ref --single-branch $repoUrl $workRoot
    }
    Set-Location $workRoot

    $project = Get-Content "src/MynAI.Windows/MynAI.Windows.csproj" -Raw
    if ($project -notmatch '<Version>0\.1\.4</Version>') {
        Stop-WithMessage "Seçilen '$Ref' dalında MynAssistant $version kaynak kodu yok. Doğru dalı seç; mevcut paket dosyaları değiştirilmedi."
    }
    foreach ($required in @(
        "src/MynAI.Windows/Resources/mynassistant.png",
        "src/MynAI.Windows/Resources/mynassistant.ico",
        "installer/LICENSE-AGREEMENT.rtf"
    )) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            Stop-WithMessage "Gerekli dosya bulunamadı: $required"
        }
    }

    New-Item -ItemType Directory -Force -Path "build/config", "build/installer", "build/bridge-dist", "build/pyinstaller-work", "build/pyinstaller-spec" | Out-Null
    Copy-Item -LiteralPath $oauthPath -Destination "build/config/client_secret.json" -Force

    Invoke-Checked "Projeye özel Python ortamını oluştur" {
        & py "-$pythonTag" -m venv .venv
    }
    $python = Join-Path $workRoot ".venv\Scripts\python.exe"
    if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
        Stop-WithMessage "Python sanal ortamı oluşturulamadı."
    }

    Invoke-Checked "Python bağımlılıklarını yükle" {
        & $python -m pip install --disable-pip-version-check --timeout 60 --retries 2 -r tools/MynAssistantDesktopBridge/requirements.txt Pillow
    }
    Invoke-Checked "Desktop Bridge birim testlerini çalıştır" {
        & $python -m unittest discover -s tools/MynAssistantDesktopBridge/tests -v
    }
    Invoke-Checked "Windows Desktop Bridge'i konsolsuz EXE olarak üret" {
        & $python -m PyInstaller --noconfirm --clean --onefile --noconsole --name MynAssistantDesktopBridge --distpath build/bridge-dist --workpath build/pyinstaller-work --specpath build/pyinstaller-spec --collect-all google_auth_oauthlib --collect-all google.auth --hidden-import win32crypt tools/MynAssistantDesktopBridge/agent.py
    }
    $bridgeExe = "build/bridge-dist/MynAssistantDesktopBridge.exe"
    if (-not (Test-Path -LiteralPath $bridgeExe -PathType Leaf)) {
        Stop-WithMessage "MynAssistantDesktopBridge.exe üretilemedi."
    }

    Invoke-Checked "Çözümü geri yükle" {
        dotnet restore MynAI.slnx
    }
    Invoke-Checked "Release derlemesini yap" {
        dotnet build MynAI.slnx --configuration Release --no-restore
    }
    Invoke-Checked "Windows uygulamasını yayımla" {
        dotnet publish src/MynAI.Windows/MynAI.Windows.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false --output build/publish
    }
    Invoke-Checked "Arka plan Agent uygulamasını yayımla" {
        dotnet publish src/MynAI.Agent/MynAI.Agent.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:WindowsAppSDKSelfContained=true --output build/agent-publish
    }
    Copy-Item -LiteralPath $bridgeExe -Destination "build/publish/MynAssistantDesktopBridge.exe" -Force
    foreach ($required in @("build/publish/MynAssistant.exe", "build/publish/MynAssistantDesktopBridge.exe", "build/agent-publish/MynAgent.exe")) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { Stop-WithMessage "Beklenen yayımlama dosyası bulunamadı: $required" }
    }

    Write-Host ([Environment]::NewLine + "=== Kurulum sihirbazı görselini yeni PNG logosundan üret ===") -ForegroundColor Cyan
    $normalizerPath = Join-Path $workRoot "build/NormalizeInstallerAssets.py"
    $normalizer = @'
from pathlib import Path
from PIL import Image, ImageOps

source = Path("src/MynAI.Windows/Resources/mynassistant.png")
wizard_path = Path("build/installer/MynAssistantWizardSmall.bmp")
if not source.is_file():
    raise FileNotFoundError(f"Current MynAssistant logo is missing: {source}")
with Image.open(source) as opened:
    image = opened.convert("RGB")
    content = ImageOps.contain(image, (48, 50), method=Image.Resampling.LANCZOS)
    canvas = Image.new("RGB", (55, 58), "white")
    canvas.paste(content, ((55-content.width)//2, (58-content.height)//2))
    canvas.save(wizard_path, format="BMP")
if not wizard_path.is_file() or wizard_path.stat().st_size < 100:
    raise RuntimeError("The Inno Setup wizard logo could not be generated.")
print(f"Generated wizard logo: {wizard_path} ({wizard_path.stat().st_size} bytes)")
'@
    [System.IO.File]::WriteAllText($normalizerPath, $normalizer, [System.Text.UTF8Encoding]::new($false))
    Invoke-Checked "Kurulum sihirbazı görselini oluştur" {
        & $python $normalizerPath
    }

    Invoke-Checked "Inno Setup ile kurulum dosyasını üret" {
        & $compiler "installer/MynAI.iss"
    }
    $installerPath = Join-Path $workRoot "build\installer\MynAssistant-Setup.exe"
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        Stop-WithMessage "Inno Setup bitirdi ama MynAssistant-Setup.exe bulunamadı."
    }

    $downloadPath = Join-Path $downloads "MynAssistant-Setup-$version-$stamp.exe"
    Copy-Item -LiteralPath $installerPath -Destination $downloadPath
    $hash = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash
    Write-Host ([Environment]::NewLine + "BAŞARILI: MynAssistant $version installer üretildi.") -ForegroundColor Green
    Write-Host "Dosya: $downloadPath"
    Write-Host "Boyut: $([Math]::Round((Get-Item -LiteralPath $downloadPath).Length / 1MB, 2)) MB"
    Write-Host "SHA-256: $hash"
    Write-Host "Günlük: $logPath"
    Write-Host "Geçici kaynak dizini korunuyor: $workRoot"
}
catch {
    Write-Host ([Environment]::NewLine + "BAŞARISIZ: $($_.Exception.Message)") -ForegroundColor Red
    Write-Host "Günlük dosyası: $logPath" -ForegroundColor Yellow
    exit 1
}
finally {
    try { Set-Location $originalLocation } catch {}
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch {}
    }
}
