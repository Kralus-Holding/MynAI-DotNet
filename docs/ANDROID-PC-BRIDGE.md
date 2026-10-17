# Android–Windows bağlantısı (MynAssistant + MynAI)

MynAI Windows installer'ı Android MynAssistant'ın Firebase tabanlı Windows köprüsünü ayrıca kurar. Mevcut .NET zamanlayıcı Agent'ı değiştirilmez; bileşenler ayrı süreçlerde çalışır.

## Geliştirme durumu

- Windows köprüsü: `MynAssistantDesktopBridge.exe`
- Mevcut .NET yardımcı süreç: `Agent/MynAI.Agent.exe` ve kendi bağımlılıkları
- WPF uygulama: `MynAI.Windows.exe`
- Oturum ve cihaz kaydı Firebase Authentication / Firestore üzerinden yapılır.
- Komut kuyruğu kullanıcı UID'si, hedef Windows cihazı ve `pending` durumu ile filtrelenir. Komut `updateTime` önkoşuluyla `running` durumuna alınmadan çalıştırılmaz.
- Gerçek Android cihaz eşleştirmesi henüz fiziksel cihazla doğrulanmadı.

## Üretim kurulum paketi nasıl oluşturulur?

1. Google Cloud Console'da `myncloud-24` projesindeki **Desktop app / installed** OAuth istemcisinin JSON dosyasını indir.
2. GitHub'da `MynAI-DotNet` deposunun Settings → Secrets and variables → Actions bölümünde `MYNAI_GOOGLE_OAUTH_JSON` adlı repository secret oluştur. Secret'ın değeri tam OAuth JSON içeriği olmalı. Bu dosyayı kaynak koda, ZIP'e veya sohbete ekleme.
3. İnceleme ve test tamamlandıktan sonra paketleme değişikliklerini `main` dalına al. `.github/workflows/windows-installer.yml` Windows uygulamasını, .NET Agent'ı ve köprü EXE'sini derler; ardından `MynAI-Setup.exe` üretip `MynAI-Windows-Installer` adlı Actions artifact'ı olarak yayımlar.
4. Artifact yalnızca GitHub Actions'ta başarılı paketleme çalışması ve mevcut artifact saklama süresi boyunca indirilebilir. Bu workflow için retention 7 gündür.

Pull request doğrulamasında gerçek OAuth dosyası yerine yalnızca test amaçlı sahte yapılandırma kullanılır; PR artifact'ı yayımlanmaz.

## Son kullanıcı için ilk bağlantı

1. `MynAI-Setup.exe` kurulumunu çalıştır.
2. MynAI'yi aç ve Ayarlar → Agent → Telefon-PC bağlantısı bölümüne git.
3. Bağlantıyı başlat. İlk kullanımda Google tarayıcı girişi açılır; Android MynAssistant uygulamasında kullanılan aynı Google hesabını seç.
4. Agent Firestore'a Windows cihazı kaydettiğinde Android uygulamasının cihaz listesinde görünmelidir.
5. Bağlantı durumunu MynAI Ayarları'ndan kontrol et. Köprü günlükleri `%LOCALAPPDATA%\MynAI\DesktopAgent\bridge.log` konumundadır.

Son kullanıcıların Firebase CLI kurması veya Firestore indekslerini elle dağıtması gerekmez. İndeks dağıtımı geliştirici/operasyon adımıdır.

## Yerel test

Windows üzerinde, depo kökünden:

```powershell
python -m pip install -r tools/MynAssistantDesktopBridge/requirements.txt
python -m unittest discover -s tools/MynAssistantDesktopBridge/tests -v
dotnet restore MynAI.slnx
dotnet build MynAI.slnx --configuration Release
```

Installer workflow'u ayrıca Agent publish ve Inno Setup derlemesini doğrular. Gerçek Google OAuth girişi, Firestore güvenlik kuralları ve telefon-PC komutları fiziksel ortamda ayrıca test edilmelidir.

## Güvenlik notları

- OAuth istemci JSON'unu veya token'ları commit etme.
- Desktop OAuth istemcisi uygulama için dağıtılan yapılandırmadır; Firebase service-account anahtarı değildir. Firebase service-account anahtarını hiçbir istemci paketine koyma.
- Cihaz erişimi Firebase kullanıcı UID'si ve Firestore güvenlik kuralları ile sınırlandırılmalıdır.
- İndeksi dağıtmak Firestore güvenlik kurallarını dağıtmakla aynı şey değildir; canlı kuralların ayrıca gözden geçirilip yönetilmesi gerekir.
