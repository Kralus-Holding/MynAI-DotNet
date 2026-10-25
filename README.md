# MynAI-DotNet

## Bileşenler

- **MynAssistant.exe**: WPF arayüzü ve yerel komut girişi.
- **Agent\MynAgent.exe**: zamanlanmış görevler ve yerel IPC kanalı.
- **MynAssistantDesktopBridge.exe**: telefon ve web kontrol paneli için Firebase Windows Agent. PC'nin ekran karelerini istek süresince gönderir ve izin verilen komutları Firestore kuyruğundan alır.

Bu üç çalıştırılabilir dosya aynı şey değildir. Özellikle telefon/web kumandası için `MynAssistantDesktopBridge.exe` çalışıyor olmalıdır.

## Telefon / web paneli → PC bağlantısı

1. Kurulum sırasında **“Telefon ve web panelinden PC bağlantısını etkinleştir”** seçeneğini açık bırak. Bu seçenek varsayılan olarak seçilidir ve Windows oturum açılışında Bridge'i başlatır.
2. İlk çalıştırmada Google oturum açma penceresi açılır. Android MynAssistant'ta kullandığın Google hesabıyla giriş yap.
3. Bridge, `devices/{win-...}` belgesini `connectionMode = firebase-agent` ile kaydeder ve `lastSeenAt` alanını yaklaşık 25 saniyede bir yeniler.
4. Android uygulaması aynı kullanıcının Firestore `devices` koleksiyonunu dinler. PC çevrimiçi görününce cihazı seçip komut gönderilebilir. Komutlar `commands` koleksiyonuna yazılır; Windows Bridge sonucu `completed` veya `failed` durumuyla döndürür.
5. Web kontrol paneli Render API üzerinden aynı Firestore cihaz ve komut verilerini kullanır. Bunun için Render'da `FIREBASE_SERVICE_ACCOUNT_JSON` yapılandırılmış olmalı ve yönetici paneli oturumu açık olmalıdır. Ekran önizlemesi yalnızca açık istek süresince çalışır.

## Sorun giderme

- Windows ayarları → **Agent** → **Telefon-PC bağlantısı** alanında Bridge durumuna bak.
- Bridge günlük kaydı: `%LOCALAPPDATA%\MynAssistant\DesktopAgent\bridge.log`.
- Google Desktop OAuth yapılandırması: `%LOCALAPPDATA%\MynAssistant\Google\client_secret.json`.
- Android PC'yi göremiyorsa her iki tarafta aynı Google hesabını kullan, Windows Bridge'in çalıştığını ve `lastSeenAt` değerinin güncel olduğunu doğrula.
- Web panelinde cihazlar görünmüyorsa Render loglarını ve `FIREBASE_SERVICE_ACCOUNT_JSON` servise erişimini kontrol et. API `/health` yanıtı tek başına Firestore erişimini doğrulamaz.

## Derleme

Windows installer workflow .NET 10, Python 3.12, PyInstaller ve Inno Setup kullanır. Yerel paket de kendi Python sanal ortamında Desktop Bridge'i tek EXE olarak paketler. Son installer gerçek Windows cihazında açılış, OAuth girişi, telefon komutu ve web paneli komutu ile ayrıca test edilmelidir.
