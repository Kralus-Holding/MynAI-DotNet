# MynAssistant 0.1.5 — uzaktan PC bağlantısı

## Ortak veri akışı

Windows Desktop Bridge, Android masaüstü Agent'ı ve Render yönetici paneli aynı Firebase projesinin `devices` ve `commands` belgelerini kullanır. Windows Bridge bu sürümde Render ASP.NET API'sine doğrudan bağlanmaz; Firebase Authentication kullanıcı oturumunu alır ve Cloud Firestore REST API'sini kullanır.

### Cihaz durumu

- `ownerId`: Firebase kullanıcı UID'si.
- `deviceId`: kullanıcı için oluşturulan `win-...` kimliği.
- `connectionMode`: `firebase-agent`.
- `status`, `lastSeenAt`, `agentVersion`: çevrimiçi durum / heartbeat / Bridge sürümü.
- `screenRequested`, `screenRequestedAt`, `screenJpegBase64`, `screenFrameAt`: süreli düşük kare hızlı ekran önizlemesi.

### Komut döngüsü

1. Android veya yönetici paneli `ownerId`, `deviceId`, `command`, `status=pending` alanlarıyla komut ekler.
2. Windows Bridge yalnızca kendi UID'si ve cihaz kimliğiyle eşleşen bekleyen komutları sorgular.
3. Komut Firestore `updateTime` önkoşuluyla `running` durumuna alınır. Çakışma olursa komut ikinci kez sahiplenilmez.
4. İşlem sonrası `completed` veya `failed` sonucu yazılır. Panel ham PowerShell/komut satırı almaz; temel komutlar ve sabit kısayollarla sınırlıdır.
5. Bridge 0.2.1, Android ile uyumlu `type_text`, `key_backspace` ve `key_enter` komutlarını destekler. Metin 1000 karakterle sınırlıdır; Unicode metin Windows SendInput ile panoyu değiştirmeden yazılır.

## Yeni bilgisayarda eşleştirme

- Kurulumda **Telefon ve web panelinden PC kontrolünü etkinleştir** seçiliyse Bridge başlatma kaydı etkin olur. Sonradan Ayarlar → Agent bölümünden de yönetilebilir.
- İlk açılışta tarayıcı Google OAuth penceresi açılabilir. Android uygulamasındaki **aynı Google hesabını** seç.
- Android Cihazlarım ekranında Windows cihazı `online` görünmeli.
- Render panelinde `/api/v1/admin/data-status` için `connected=true` gerekir. `/health` tek başına Firestore izinlerini doğrulamaz.
- Ekran önizlemesi süreli bir istektir; yalnızca uzaktan kontrol sayfası açıkken istek yenilenirse etkin kalır.

## Sorun giderme

- Cihaz görünmüyorsa Windows Ayarlar → Agent bölümünde Bridge durumunu ve `%LOCALAPPDATA%\MynAssistant\DesktopAgent\bridge.log` günlüğünü kontrol et.
- `client_secret.json` bulunamadıysa Google Desktop OAuth JSON dosyasını `%LOCALAPPDATA%\MynAssistant\Google\client_secret.json` konumuna yerleştir. Dosyayı GitHub'a yükleme.
- Cihaz görünüyor ancak web paneli boşsa Render `FIREBASE_SERVICE_ACCOUNT_JSON`, proje kimliği ve Firestore yetkilerini doğrula.
- Installer derlemesinin başarılı olması Google OAuth, Firestore veya canlı komutların gerçek cihazlarda uçtan uca doğrulandığı anlamına gelmez.
