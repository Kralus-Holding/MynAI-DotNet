# MynAssistant Desktop Bridge (paketlenmiş Windows bileşeni)

Bu klasör, Android MynAssistant ve Windows uygulaması arasında Firebase/Firestore üzerinden bağlantı kuran Windows Desktop Bridge ajanını içerir. Paket adı `MynAssistantDesktopBridge.exe`, sürüm 0.2.1'dir.

## Eşleştirme

- İlk çalıştırmada Google Desktop OAuth akışı tarayıcıda açılır. Android uygulamasında oturum açılan aynı Google hesabı kullanılmalıdır.
- OAuth JSON dosyası önce EXE yanındaki `client_secret.json`, sonra `%LOCALAPPDATA%\MynAssistant\Google\client_secret.json` konumunda aranır.
- Bridge kendi UID'siyle sınırlanmış `devices`/`commands` verisini Firestore REST API üzerinden yönetir; PC için IP/port açmak gerekmez.
- Gerçek OAuth JSON'u, servis hesabı anahtarı, refresh token veya kullanıcı oturumunu kaynak denetimine ekleme.

## Uzaktan komutlar

Bridge ses aç/kapat, ses yükseltme/azaltma, masaüstünü gösterme, fare hareketi/tıklama ve Unicode metin/Enter/Backspace komutlarını destekler. Metin girdisi 1000 karakterle sınırlıdır ve panoyu değiştirmez. Ekran karesi yalnızca süreli önizleme isteği etkinken gönderilir; bu akıcı video aktarımı değildir.

## Yerel test

```powershell
py -m pip install -r tools/MynAssistantDesktopBridge/requirements.txt
py -m unittest discover -s tools/MynAssistantDesktopBridge/tests -v
pyinstaller --noconfirm --clean --onefile --console --name MynAssistantDesktopBridge --collect-all google_auth_oauthlib --collect-all google.auth --hidden-import win32crypt tools/MynAssistantDesktopBridge/agent.py
```
