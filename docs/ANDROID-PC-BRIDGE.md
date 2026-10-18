# Android–Windows bağlantısı (MynAssistant)

MynAssistant Windows 0.1.2 paketi Android MynAssistant'ın Firebase tabanlı Windows köprüsünü ve .NET zamanlayıcı bileşeni MynAgent'ı ayrı ayrı kurar. Mevcut .NET zamanlayıcı Agent'ı değiştirilmez; bileşenler ayrı süreçlerde çalışır.

## Zorunlu iletişim mimarisi: sunucu üzerinden

Android telefon ve Windows bilgisayar birbirine IP adresi, yerel HTTP uç noktası veya aynı Wi-Fi ağı üzerinden doğrudan bağlanmaz. İkisi de Firebase'e ayrı istemci olarak bağlanır. Telefon komutu sahibi ve hedef cihaz kimliğiyle Firestore `commands` kuyruğuna yazar; Windows Desktop Bridge Agent sunucudan kendi cihazına ait bekleyen komutları alır, komutu yerel bilgisayarda yürütür ve sonucu yine sunucuya `completed`/`failed` olarak yazar. Telefon sonucu aynı komut belgesinin sunucu tarafındaki durumundan okur. Cihaz keşfi `devices` koleksiyonundaki kullanıcıya ait kayıtlardan yapılır. Android tarafındaki yerel PC URL/token ayarları artık kullanılmaz; doğrudan taşıma yolu CI tarafından yasaklanır.

## Geliştirme durumu

- Windows köprüsü: `MynAssistantDesktopBridge.exe`
- Mevcut .NET yardımcı süreç: `Agent/MynAgent.exe` ve kendi bağımlılıkları
- WPF uygulama: `MynAssistant.exe`
- Oturum ve cihaz kaydı Firebase Authentication / Firestore üzerinden yapılır.
- Komut kuyruğu kullanıcı UID'si, hedef Windows cihazı ve `pending` durumu ile filtrelenir. Komut `updateTime` önkoşuluyla `running` durumuna alınmadan çalıştırılmaz.
- Gerçek Android cihaz eşleştirmesi henüz fiziksel cihazla doğrulanmadı.

## Üretim kurulum paketi nasıl oluşturulur?

1. Google Cloud Console'da `myncloud-24` projesindeki **Desktop app / installed** OAuth istemcisinin JSON dosyasını indir.
2. GitHub'da `MynAI-DotNet` deposunun Settings → Secrets and variables → Actions bölümünde `MYNAI_GOOGLE_OAUTH_JSON` adlı repository secret oluştur. Secret'ın değeri tam OAuth JSON içeriği olmalı. Bu dosyayı kaynak koda, ZIP'e veya sohbete ekleme.
3. Workflow artık `main` dalında kullanılabilir: `main` dalındaki ilgili kaynak değişiklikleri otomatik build başlatır. İstersen Actions → **Build MynAssistant Windows Installer** → **Run workflow** ile manuel build de başlatabilirsin. Gerçek dağıtım için `MYNAI_GOOGLE_OAUTH_JSON` secret'ı gerekir.
4. Workflow Windows uygulamasını, .NET Agent'ı ve köprü EXE'sini derler; `MynAssistant-Setup-0.1.2.exe` üretip `MynAssistant-Windows-Installer-0.1.2` adıyla Actions artifact'ı olarak yükler. Pull request doğrulaması sahte OAuth yapılandırması kullanır ve dağıtılabilir artifact yüklemez. Artifact saklama süresi 7 gündür.

Actions artifact depolama kotası dolarsa GitHub Release yedeği için `GH_RELEASE_TOKEN` adlı repository Actions secret gerekir. Değer, yalnızca `MynAI-DotNet` deposuna erişen fine-grained token olmalı ve **Contents: Read and write** yetkisine sahip olmalıdır. Normal Actions artifact yüklemesi için bu token kullanılmaz.

Pull request doğrulamasında gerçek OAuth dosyası yerine yalnızca test amaçlı sahte yapılandırma kullanılır; PR artifact'ı yayımlanmaz.

## Kurulum doğrulamaları ve imzalama

- Kurulum sihirbazı her seferinde hedef klasör sayfasını gösterir; varsa önceki kurulum klasörü varsayılan olarak önerilir ama değiştirilebilir. İlk kurulumda varsayılan klasör `%LOCALAPPDATA%\\Programs\\MynAssistant` olur.
- CI, kurulum paketini geçici bir klasöre sessizce yükleyip `MynAssistant.exe`, `MynAssistantDesktopBridge.exe`, `Agent\MynAgent.exe` ve Agent bağımlılıklarını kontrol eder. Ardından `MynAgent.exe` sürecini başlatır ve `MynAI.Agent` named-pipe IPC uç noktasının hazır olduğunu sınar.
- MynAssistant açıldığında Agent'ı kendisi başlatır; hata/çıkış günlükleri `%LOCALAPPDATA%\\MynAssistant\\Logs\\mynagent.log` dosyasına yazılır.
- Üretim sürümü yayınlanmadan önce güvenilir kod imzalama sertifikasıyla imza doğrulaması zorunludur. Repository Actions secrets: `MYNASSISTANT_SIGNING_PFX_BASE64` (PFX dosyasının Base64 metni) ve `MYNASSISTANT_SIGNING_PFX_PASSWORD` (PFX parolası). Sertifikanın doğrulanabilir konusu `MynKralus` içermelidir. Sertifika yoksa CI testleri çalışabilir ama üretim yayınlaması engellenir.

PowerShell'de PFX dosyasını Base64'e çevirmek için (dosya yolunu kendine göre değiştir):
```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\\path\\MynKralus-CodeSigning.pfx")) | Set-Clipboard
```
Bu çıktıyı yalnızca `MYNASSISTANT_SIGNING_PFX_BASE64` secret'ına koy; PFX dosyasını depoya commit etme.

## Son kullanıcı için ilk bağlantı

1. `MynAssistant-Setup-0.1.2.exe` kurulumunu çalıştır.
2. MynAssistant'ı aç ve Ayarlar → Agent → Telefon-PC bağlantısı bölümüne git.
3. Bağlantıyı başlat. İlk kullanımda Google tarayıcı girişi açılır; Android MynAssistant uygulamasında kullanılan aynı Google hesabını seç.
4. Agent Firestore'a Windows cihazı kaydettiğinde Android uygulamasının cihaz listesinde görünmelidir.
5. Bağlantı durumunu MynAssistant Ayarları'ndan kontrol et. Köprü günlükleri `%LOCALAPPDATA%\MynAssistant\DesktopAgent\bridge.log` konumundadır.

Son kullanıcıların Firebase CLI kurması veya Firestore indekslerini elle dağıtması gerekmez. İndeks dağıtımı geliştirici/operasyon adımıdır.

## Yerel test

Windows üzerinde, depo kökünden:

```powershell
python -m pip install -r tools/MynAssistantDesktopBridge/requirements.txt
python -m unittest discover -s tools/MynAssistantDesktopBridge/tests -v
dotnet restore MynAI.slnx
dotnet build MynAI.slnx --configuration Release
```

Installer, `MynAssistant.exe` ve `Agent\MynAgent.exe` dosyalarını ayrı ayrı pakete alır; kurulum sonunda `MynAgent` arka plan bileşenini başlatır. Eski sürümle uyumluluk için zamanlayıcı verileri `%LOCALAPPDATA%\MynAI` altında korunur. Workflow ayrıca Agent publish ve Inno Setup derlemesini doğrular. Gerçek Google OAuth girişi, Firestore güvenlik kuralları ve telefon-PC komutları fiziksel ortamda ayrıca test edilmelidir.

## Yayımcı / SmartScreen notu

Inno Setup'taki `AppPublisher` ve PE `CompanyName` metadata'sı tek başına SmartScreen yayıncısını doğrulamaz. Yayıncı adının `MynKralus` olarak görünmesi için indirilen kurulumun güvenilir bir sertifikayla Authenticode imzalı olması gerekir. Bu nedenle üretim yayınlaması geçerli imza yoksa bloke edilir.

## Güvenlik notları

- OAuth istemci JSON'unu veya token'ları commit etme.
- Desktop OAuth istemcisi uygulama için dağıtılan yapılandırmadır; Firebase service-account anahtarı değildir. Firebase service-account anahtarını hiçbir istemci paketine koyma.
- Cihaz erişimi Firebase kullanıcı UID'si ve Firestore güvenlik kuralları ile sınırlandırılmalıdır.
- İndeksi dağıtmak Firestore güvenlik kurallarını dağıtmakla aynı şey değildir; canlı kuralların ayrıca gözden geçirilip yönetilmesi gerekir.
