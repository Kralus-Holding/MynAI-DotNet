# MynAssistant 0.1.4 — Telefon, Windows ve Web Paneli bağlantısı

## Mimari

- Android uygulaması ve Windows Desktop Bridge, aynı Firebase Authentication hesabıyla oturum açar ve `myncloud-24` projesinin Cloud Firestore `devices` ve `commands` koleksiyonlarını kullanır.
- Windows Agent cihazı `connectionMode = firebase-agent`, `ownerId`, `status` ve `lastSeenAt` alanlarıyla kaydeder. Android, aynı UID'ye ait cihazları dinler; komutları `commands` kuyruğuna ekler.
- Render üzerindeki `MynAssistant-Server` yönetici paneli, `FIREBASE_SERVICE_ACCOUNT_JSON` ile aynı Firestore koleksiyonlarını okur. Web paneli, çevrimiçi ve son üç dakika içinde heartbeat göndermiş `firebase-agent` cihazlarına izin listesinde yer alan komutları gönderir.
- Önizleme görüntüsü varsayılan olarak sürekli yüklenmez. Android veya web panelinde kullanıcı önizlemeyi başlattığında Agent'a süreli `screenRequested` isteği yazılır; istek yenilenmediğinde görüntü gönderimi durur.

## İlk eşleştirme

1. Windows'ta MynAssistant 0.1.4 kurulumunu yapıp uygulamayı aç.
2. Uygulama, bileşenler hazır olduğunda Telefon-PC bağlantısını başlatmak için açık bir onay ister. Onay verirsen bağlantı bu oturumda başlar ve Windows açılışına kaydedilir. Reddetsen bile Ayarlar → Telefon-PC bağlantısı bölümünden elle başlatabilirsin.
3. İlk bağlantıda tarayıcıda Google girişi açılır. Android'de kullandığın **aynı Google/Firebase hesabını** seç. Farklı hesap farklı Firebase UID'si oluşturacağından bilgisayar telefonda görünmez.
4. Ayarlar → Telefon-PC bağlantısı bölümünde yalnızca işlemin çalıştığını değil, yerel `status.json` dosyasından okunan son Firestore heartbeat durumunu da kontrol et. Tanılama dosyaları `%LOCALAPPDATA%\MynAssistant\DesktopAgent\bridge.log` ve `status.json` konumlarındadır.
5. Android'de PC Kumandası / Cihazlarım bölümünü aç. Windows cihazı `firebase-agent` olarak ve son üç dakika içinde heartbeat göndermiş durumda görünmeli.
6. Web panelinde `/admin/` → Cihazlar sayfasını aç. Aynı kayıt burada görünmeli. Komut göndermek için cihazın çevrimiçi olması, `connectionMode` alanının `firebase-agent` olması ve Render servis hesabının Firestore erişiminin çalışması gerekir.

## Web paneli için sunucu koşulu

Render ortamında `FIREBASE_PROJECT_ID=myncloud-24` ve doğru `FIREBASE_SERVICE_ACCOUNT_JSON` bulunmalı. Panelin Genel Bakış sayfasındaki Firebase veri bağlantısı doğrulanmadıysa Windows uygulaması doğru çalışsa bile web paneli cihazları ve komut geçmişini listeleyemez. Servis hesabı gizli anahtarını GitHub'a, APK'ya ya da sohbete koyma.

## Legacy yerel bağlantı

`mynai-local-bridge` kayıtları eski, aynı yerel ağ/IP tabanlı bağlantıyı temsil eder. Web yönetici panelinin komut kuyruğu bu kayıt türüne komut göndermez; bulut tabanlı Android ve web kontrolü için `firebase-agent` bağlantısı gerekir. Android'de çevrimiçi Firebase Agent, eski çevrimdışı yerel kayda tercih edilir.

## Güvenlik ve durum

- İlk bağlantı otomatik açılmaz; kullanıcıdan onay alınır.
- Agent oturum yenileme anahtarını Windows DPAPI ile korur; Google OAuth JSON'u veya gizli anahtarlar kaynak depoya eklenmez.
- Web paneli ham PowerShell/shell komutları çalıştırmaz; yalnızca sunucudaki sabit allowlist komutları kabul edilir.
- Windows ayarlarında Bağlantıyı durdur seçeneği artık yalnızca süreci sonlandırmakla kalmaz, Windows açılışındaki otomatik başlatma kaydını da kaldırır.
