# MynAI-DotNet

MynAssistant Windows uygulaması (.NET 10 WPF), yerel zamanlanmış görev Agent'ı ve telefon/web paneli için Firebase Desktop Bridge.

## Windows Installer 0.1.5

- WPF uygulaması ve yerel Agent `win-x64` için self-contained yayımlanır.
- `MynAssistantDesktopBridge.exe` PyInstaller ile tek EXE olarak paketlenir; hedef bilgisayarda Python veya .NET SDK kurulması gerekmez.
- Windows uygulama/tepsi simgesi, WPF pencere logosu ve Inno Setup görseli Android ile web panelindeki ortak MynAssistant logosundan üretilir.
- `tools/build_windows_icon.py` ortak PNG'den Windows çok boyutlu ICO ve 55×58 Inno Setup görseli üretir.

## Telefon ve web paneliyle PC bağlantısı

Bağlantı yolu aynı Firebase Authentication kullanıcı kimliği ve Cloud Firestore `devices`/`commands` koleksiyonlarını kullanır; PC için yerel IP/port açmak gerekmez.

1. Kurulumda **Telefon ve web panelinden PC kontrolünü etkinleştir** seçeneği açıksa Desktop Bridge Windows oturum açılışında başlatılacak şekilde ayarlanır.
2. İlk açılışta Google OAuth akışı tarayıcıda açılır. Android uygulamasında kullanılan aynı Google hesabıyla giriş yapılmalıdır.
3. Bridge cihazı `devices` koleksiyonuna kaydeder, yaklaşık 25 saniyede bir heartbeat gönderir ve ilgili `commands` kuyruğunu kontrol eder.
4. Android aynı kullanıcıya ait cihaz/komut belgelerini Firestore üzerinden görür. Render web paneli aynı veriyi servis hesabıyla okur ve izin verilen komutları kuyruğa yazar.
5. Ekran önizlemesi yalnızca Android veya yönetici paneli süreli ekran isteğini yenilerken gönderilir; tam zamanlı video değildir.

Bileşenlerin görevleri:
- `MynAssistant.exe`: kullanıcı arayüzü.
- `Agent/MynAgent.exe`: yerel zamanlanmış görevler ve IPC.
- `MynAssistantDesktopBridge.exe`: Firebase üzerinden telefon ve web paneliyle PC kontrolü.
- `MynAssistant-Server`: Render üzerindeki yönetici web paneli/API. Windows Bridge bu sürümde Render API'sine değil, doğrudan Firebase/Firestore'a bağlanır.

## Gerekenler ve doğrulama

Hedef sistem Windows 10 build 19041 (2004) veya daha yeni Windows x64 olmalıdır. Hedef bilgisayarda Python, .NET SDK, Git veya Inno Setup gerekmez; bunlar installer üretmek için geliştirme makinesinde kullanılır. İnternet, geçerli Google Desktop OAuth yapılandırması ve Firebase Authentication/Firestore erişimi gerekir. Web paneli için Render'da `FIREBASE_SERVICE_ACCOUNT_JSON` geçerli/yetkili olmalıdır.

Uçtan uca test için aynı Google hesabıyla PC ve Android'de oturum aç, Android'in Cihazlarım bölümünde Windows cihazının `online` görünmesini, Render `/api/v1/admin/data-status` sonucunun `connected=true` olmasını ve izinli bir komutun `completed` durumuna gelmesini doğrula. Başarılı installer derlemesi tek başına gerçek bağlantı testi değildir.
