from __future__ import annotations

import base64
import ctypes
import io
import json
import os
import platform
import subprocess
import sys
import unicodedata
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlencode

import pyautogui
import requests
import win32crypt
from google_auth_oauthlib.flow import InstalledAppFlow


PROJECT_ID = "myncloud-24"
FIREBASE_API_KEY = "AIzaSyB0z1b5OOMh8Pqn2GPd1ZuWoHEhRoKSrRM"
IDENTITY_TOOLKIT = "https://identitytoolkit.googleapis.com/v1"
SECURE_TOKEN = "https://securetoken.googleapis.com/v1/token"
FIRESTORE = f"https://firestore.googleapis.com/v1/projects/{PROJECT_ID}/databases/(default)/documents"
SCOPES = ["openid", "https://www.googleapis.com/auth/userinfo.email", "https://www.googleapis.com/auth/userinfo.profile"]
AGENT_VERSION = "0.2.1"
POLL_SECONDS = 2.0
COMMAND_POLL_SECONDS = 2.0
SCREEN_CHECK_SECONDS = 8.0
SCREEN_FRAME_SECONDS = 1.5
SCREEN_REQUEST_MAX_AGE_SECONDS = 25.0
HEARTBEAT_SECONDS = 25.0
DEVICE_STALE_AFTER_SECONDS = 90.0
DPAPI_ENTROPY = b"MynAssistantDesktopAgent-v1"
APP_DATA = Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "MynAssistant" / "DesktopAgent"
OAUTH_FILE_NAME = "client_secret.json"
SESSION_FILE = APP_DATA / "session.bin"
DEVICE_FILE = APP_DATA / "devices.json"
PHONE_DISCOVERY_SECONDS = 8.0
PHONE_DEVICE_CACHE_NAME = "android_devices.json"

VK_VOLUME_MUTE = 0xAD
VK_VOLUME_DOWN = 0xAE
VK_VOLUME_UP = 0xAF
KEYEVENTF_KEYUP = 0x0002


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def is_device_heartbeat_fresh(last_seen_at: str | None) -> bool:
    if not isinstance(last_seen_at, str) or not last_seen_at.strip():
        return False
    try:
        timestamp = datetime.fromisoformat(last_seen_at.strip().replace("Z", "+00:00"))
        if timestamp.tzinfo is None:
            timestamp = timestamp.replace(tzinfo=timezone.utc)
        age_seconds = (datetime.now(timezone.utc) - timestamp.astimezone(timezone.utc)).total_seconds()
        return -120.0 <= age_seconds <= DEVICE_STALE_AFTER_SECONDS
    except (OverflowError, ValueError):
        return False


def running_directory() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


def encode_string(value: str) -> dict:
    return {"stringValue": value}


def encode_timestamp(value: str | None = None) -> dict:
    return {"timestampValue": value or utc_now()}


def decode_field(value: dict | None):
    if not value:
        return None
    if "stringValue" in value:
        return value["stringValue"]
    if "integerValue" in value:
        return int(value["integerValue"])
    if "doubleValue" in value:
        return float(value["doubleValue"])
    if "booleanValue" in value:
        return bool(value["booleanValue"])
    if "timestampValue" in value:
        return value["timestampValue"]
    if "nullValue" in value:
        return None
    return None


def decode_document(document: dict) -> dict:
    return {key: decode_field(value) for key, value in document.get("fields", {}).items()}


def protect_session(payload: dict) -> None:
    APP_DATA.mkdir(parents=True, exist_ok=True)
    raw = json.dumps(payload).encode("utf-8")
    _description, encrypted = win32crypt.CryptProtectData(
        raw, "MynAssistant Desktop Agent", DPAPI_ENTROPY, None, None, 0
    )
    SESSION_FILE.write_bytes(encrypted)


def load_protected_session() -> dict | None:
    if not SESSION_FILE.exists():
        return None
    try:
        encrypted = SESSION_FILE.read_bytes()
        _description, raw = win32crypt.CryptUnprotectData(encrypted, DPAPI_ENTROPY, None, None, 0)
        payload = json.loads(raw.decode("utf-8"))
        if payload.get("refreshToken") and payload.get("uid"):
            return payload
    except Exception as exc:
        print(f"[Auth] Kaydedilmiş oturum açılamadı: {exc}")
    return None


def delete_protected_session() -> None:
    try:
        SESSION_FILE.unlink(missing_ok=True)
    except OSError:
        pass


def load_device_id(uid: str) -> str:
    APP_DATA.mkdir(parents=True, exist_ok=True)
    try:
        data = json.loads(DEVICE_FILE.read_text("utf-8"))
    except Exception:
        data = {}
    device_id = data.get(uid)
    if not isinstance(device_id, str) or not device_id:
        device_id = "win-" + uuid.uuid4().hex
        data[uid] = device_id
        DEVICE_FILE.write_text(json.dumps(data, indent=2), encoding="utf-8")
    return device_id


def oauth_credentials_candidates() -> tuple[Path, ...]:
    # Prefer a side-by-side config for standalone use; packaged MynAI installs
    # share the per-user Google OAuth configuration used by the main app.
    return (
        running_directory() / OAUTH_FILE_NAME,
        APP_DATA.parent / "Google" / OAUTH_FILE_NAME,
    )


def find_oauth_credentials_path() -> Path | None:
    return next(
        (path for path in oauth_credentials_candidates() if path.is_file()),
        None,
    )


def google_oauth_credentials():
    credentials_path = find_oauth_credentials_path()
    if credentials_path is None:
        searched = "\n".join(f"- {path}" for path in oauth_credentials_candidates())
        raise FileNotFoundError(
            "Google OAuth dosyası bulunamadı. MynAI Google ayarlarında "
            f"'{OAUTH_FILE_NAME}' dosyasını yapılandır. Aranan konumlar:\n{searched}"
        )
    flow = InstalledAppFlow.from_client_secrets_file(str(credentials_path), SCOPES)
    return flow.run_local_server(
        host="127.0.0.1",
        port=0,
        open_browser=True,
        authorization_prompt_message="Google hesabıyla giriş için tarayıcı açılıyor...",
        success_message="Google girişi tamamlandı. Bu sekmeyi kapatıp MynAssistant Agent penceresine dön.",
        access_type="offline",
        prompt="select_account",
    )


def firebase_exchange_google_access_token(access_token: str) -> dict:
    post_body = urlencode({"access_token": access_token, "providerId": "google.com"})
    response = requests.post(
        f"{IDENTITY_TOOLKIT}/accounts:signInWithIdp",
        params={"key": FIREBASE_API_KEY},
        json={
            "postBody": post_body,
            "requestUri": "http://localhost",
            "returnSecureToken": True,
            "returnIdpCredential": False,
        },
        timeout=25,
    )
    if response.status_code >= 400:
        raise RuntimeError("Firebase Google girişi başarısız: " + response.text[:700])
    data = response.json()
    payload = {
        "uid": data["localId"],
        "email": data.get("email", ""),
        "displayName": data.get("displayName", ""),
        "refreshToken": data["refreshToken"],
    }
    protect_session(payload)
    return {
        **payload,
        "idToken": data["idToken"],
        "expiresAt": time.time() + int(data.get("expiresIn", "3600")) - 90,
    }


class FirebaseSession:
    def __init__(self, state: dict):
        self.uid = state["uid"]
        self.email = state.get("email", "")
        self.display_name = state.get("displayName", "")
        self.refresh_token = state["refreshToken"]
        self.id_token = state.get("idToken", "")
        self.expires_at = float(state.get("expiresAt", 0))
        self.ensure_fresh(force=not self.id_token)

    @classmethod
    def sign_in(cls):
        credentials = google_oauth_credentials()
        if not credentials.token:
            raise RuntimeError("Google OAuth sonucunda erişim anahtarı alınamadı.")
        return cls(firebase_exchange_google_access_token(credentials.token))

    def _store(self) -> None:
        protect_session({
            "uid": self.uid,
            "email": self.email,
            "displayName": self.display_name,
            "refreshToken": self.refresh_token,
        })

    def ensure_fresh(self, force: bool = False) -> None:
        if self.id_token and not force and time.time() < self.expires_at:
            return
        response = requests.post(
            SECURE_TOKEN,
            params={"key": FIREBASE_API_KEY},
            data={"grant_type": "refresh_token", "refresh_token": self.refresh_token},
            timeout=20,
        )
        if response.status_code >= 400:
            raise RuntimeError("Firebase oturumu yenilenemedi: " + response.text[:500])
        data = response.json()
        self.id_token = data["id_token"]
        self.refresh_token = data.get("refresh_token", self.refresh_token)
        self.expires_at = time.time() + int(data.get("expires_in", "3600")) - 90
        self._store()

    def headers(self) -> dict:
        self.ensure_fresh()
        return {"Authorization": f"Bearer {self.id_token}", "Content-Type": "application/json"}


def firestore_patch(session: FirebaseSession, document_path: str, fields: dict, update_mask: list[str] | None = None):
    params = []
    if update_mask:
        params.extend(("updateMask.fieldPaths", field) for field in update_mask)
    response = requests.patch(
        f"{FIRESTORE}/{document_path}",
        params=params,
        headers=session.headers(),
        json={"fields": fields},
        timeout=20,
    )
    if response.status_code >= 400:
        raise RuntimeError(
            f"Firestore PATCH {document_path} HTTP {response.status_code}: {response.text[:500]}"
        )
    return response.json()


def register_or_heartbeat(session: FirebaseSession, device_id: str, initial: bool = False) -> None:
    if initial:
        computer_name = platform.node().strip() or "Windows bilgisayarım"
        fields = {
            "ownerId": encode_string(session.uid),
            "name": encode_string(computer_name),
            "platform": encode_string("Windows"),
            "type": encode_string("Windows"),
            "status": encode_string("online"),
            "connectionMode": encode_string("firebase-agent"),
            "agentVersion": encode_string(AGENT_VERSION),
            "lastSeenAt": encode_timestamp(),
            "screenRequested": {"booleanValue": False},
        }
        response = requests.patch(
            f"{FIRESTORE}/devices/{device_id}",
            headers=session.headers(),
            json={"fields": fields},
            timeout=20,
        )
        if response.status_code >= 400:
            raise RuntimeError("Windows cihaz kaydı başarısız: " + response.text[:700])
    else:
        firestore_patch(
            session,
            f"devices/{device_id}",
            {
                "status": encode_string("online"),
                "lastSeenAt": encode_timestamp(),
            },
            update_mask=["status", "lastSeenAt"],
        )


def read_device_state(session: FirebaseSession, device_id: str) -> dict:
    response = requests.get(
        f"{FIRESTORE}/devices/{device_id}",
        headers=session.headers(),
        timeout=15,
    )
    if response.status_code >= 400:
        raise RuntimeError("Windows cihaz durumu okunamadı: " + response.text[:500])
    return decode_document(response.json())


def fetch_android_phone_devices(session: FirebaseSession) -> list[dict]:
    """Fetch this account's Android device records for the Windows pairing list."""
    response = requests.post(
        f"{FIRESTORE}:runQuery",
        headers=session.headers(),
        json={
            "structuredQuery": {
                "from": [{"collectionId": "devices"}],
                "where": {
                    "fieldFilter": {
                        "field": {"fieldPath": "ownerId"},
                        "op": "EQUAL",
                        "value": {"stringValue": session.uid},
                    }
                },
                "limit": 100,
            }
        },
        timeout=20,
    )
    if response.status_code >= 400:
        raise RuntimeError("Telefon cihazları okunamadı: " + response.text[:600])

    devices = []
    for result in response.json():
        document = result.get("document")
        if not document:
            continue
        fields = decode_document(document)
        platform_name = str(fields.get("platform") or fields.get("type") or "").strip()
        if fields.get("ownerId") != session.uid or platform_name.casefold() != "android":
            continue
        full_name = str(document.get("name") or "")
        last_seen_at = fields.get("lastSeenAt") if isinstance(fields.get("lastSeenAt"), str) else ""
        status = str(fields.get("status") or "offline")
        if status.casefold() == "online" and not is_device_heartbeat_fresh(last_seen_at):
            status = "offline"

        devices.append({
            "id": full_name.rsplit("/", 1)[-1],
            "name": str(fields.get("name") or fields.get("deviceName") or "Android telefon"),
            "platform": platform_name,
            "status": status,
            "lastSeenAt": last_seen_at,
        })

    devices.sort(key=lambda item: (item["name"].casefold(), item["id"]))
    return devices


def write_android_phone_snapshot(session: FirebaseSession) -> None:
    """Atomically publish a small local cache consumed by the WPF phone list."""
    APP_DATA.mkdir(parents=True, exist_ok=True)
    snapshot = {
        "ownerId": session.uid,
        "email": getattr(session, "email", ""),
        "updatedAt": utc_now(),
        "devices": fetch_android_phone_devices(session),
    }
    destination = APP_DATA / PHONE_DEVICE_CACHE_NAME
    temporary = destination.with_suffix(".tmp")
    temporary.write_text(json.dumps(snapshot, ensure_ascii=False), encoding="utf-8")
    temporary.replace(destination)


def screen_request_is_active(fields: dict) -> bool:
    if not fields.get("screenRequested"):
        return False
    value = fields.get("screenRequestedAt")
    if not isinstance(value, str) or not value:
        return False
    try:
        requested_at = datetime.fromisoformat(value.replace("Z", "+00:00"))
        age = (datetime.now(timezone.utc) - requested_at).total_seconds()
        return -5 <= age <= SCREEN_REQUEST_MAX_AGE_SECONDS
    except Exception:
        return False


MAX_SCREEN_FRAME_BASE64_CHARS = 650_000


def capture_screen_base64() -> str:
    image = pyautogui.screenshot()
    # The Firestore document limit applies after Base64 expansion (roughly +33%),
    # not to the compressed JPEG bytes. Keep ample room for the document's other
    # fields by enforcing a 650 KB Base64 string budget.
    attempts = (
        ((1280, 720), 40),
        ((960, 540), 35),
        ((800, 450), 30),
        ((640, 360), 25),
    )

    for dimensions, quality in attempts:
        frame_image = image.copy()
        frame_image.thumbnail(dimensions)
        output = io.BytesIO()
        frame_image.save(output, format="JPEG", quality=quality, optimize=True)
        encoded = base64.b64encode(output.getvalue()).decode("ascii")
        if len(encoded) <= MAX_SCREEN_FRAME_BASE64_CHARS:
            return encoded

    raise RuntimeError(
        "Ekran görüntüsü Base64 olarak Firestore belge bütçesine sığmıyor; "
        "çözünürlüğü düşürüp tekrar dene."
    )


def upload_screen_frame(session: FirebaseSession, device_id: str) -> None:
    frame = capture_screen_base64()
    firestore_patch(
        session,
        f"devices/{device_id}",
        {
            "screenJpegBase64": encode_string(frame),
            "screenFrameAt": encode_timestamp(),
        },
        update_mask=["screenJpegBase64", "screenFrameAt"],
    )


def fetch_device_commands(session: FirebaseSession, device_id: str) -> list[dict]:
    """Fetch only pending commands for this Firebase user and this registered device."""
    response = requests.post(
        f"{FIRESTORE}:runQuery",
        headers=session.headers(),
        json={
            "structuredQuery": {
                "from": [{"collectionId": "commands"}],
                "where": {
                    "compositeFilter": {
                        "op": "AND",
                        "filters": [
                            {
                                "fieldFilter": {
                                    "field": {"fieldPath": "ownerId"},
                                    "op": "EQUAL",
                                    "value": {"stringValue": session.uid},
                                }
                            },
                            {
                                "fieldFilter": {
                                    "field": {"fieldPath": "deviceId"},
                                    "op": "EQUAL",
                                    "value": {"stringValue": device_id},
                                }
                            },
                            {
                                "fieldFilter": {
                                    "field": {"fieldPath": "status"},
                                    "op": "EQUAL",
                                    "value": {"stringValue": "pending"},
                                }
                            },
                        ],
                    }
                },
                "orderBy": [
                    {
                        "field": {"fieldPath": "createdAt"},
                        "direction": "ASCENDING",
                    }
                ],
                "limit": 100,
            }
        },
        timeout=25,
    )
    if response.status_code >= 400:
        raise RuntimeError("Komut kuyruğu okunamadı: " + response.text[:700])
    documents = []
    for result in response.json():
        document = result.get("document")
        if document:
            documents.append(document)
    return documents


def claim_command(session: FirebaseSession, document: dict) -> bool:
    """Atomically transition a pending command to running using Firestore updateTime."""
    full_name = document.get("name", "")
    marker = "/documents/"
    update_time = document.get("updateTime")
    if marker not in full_name or not update_time:
        raise RuntimeError("Komut belgesinin yolu veya updateTime bilgisi eksik.")

    document_path = full_name.split(marker, 1)[1]
    params = [
        ("updateMask.fieldPaths", "status"),
        ("updateMask.fieldPaths", "result"),
        ("updateMask.fieldPaths", "startedAt"),
        ("currentDocument.updateTime", update_time),
    ]
    response = requests.patch(
        f"{FIRESTORE}/{document_path}",
        params=params,
        headers=session.headers(),
        json={
            "fields": {
                "status": encode_string("running"),
                "result": encode_string("Windows Agent komutu sahipleniyor."),
                "startedAt": encode_timestamp(),
            }
        },
        timeout=20,
    )
    if response.status_code >= 400:
        try:
            error = response.json().get("error", {})
        except (ValueError, AttributeError):
            error = {}
        error_status = str(error.get("status", "")).upper()
        error_message = str(error.get("message", "")).lower()
        if (
            response.status_code == 409
            or error_status in {"ABORTED", "FAILED_PRECONDITION"}
            or "update time" in error_message
            or "precondition" in error_message
        ):
            # Another Agent changed the pending command first; let that Agent process it.
            return False
        raise RuntimeError(
            f"Komut sahiplenilemedi: HTTP {response.status_code}: {response.text[:500]}"
        )
    return True


def update_command(session: FirebaseSession, document: dict, status: str, result: str) -> None:
    full_name = document.get("name", "")
    marker = "/documents/"
    if marker not in full_name:
        raise RuntimeError("Komut belgesinin yolu geçersiz.")
    document_path = full_name.split(marker, 1)[1]
    fields = {
        "status": encode_string(status),
        "result": encode_string(result[:800]),
        "processedAt": encode_timestamp(),
    }
    firestore_patch(session, document_path, fields, update_mask=["status", "result", "processedAt"])


def press_virtual_key(vk: int, repeat: int = 1) -> None:
    user32 = ctypes.windll.user32
    for _ in range(max(1, min(repeat, 15))):
        user32.keybd_event(vk, 0, 0, 0)
        user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)


def normalize_text_command(text: str) -> str:
    normalized = unicodedata.normalize("NFKD", text.casefold())
    normalized = "".join(char for char in normalized if not unicodedata.combining(char))
    normalized = normalized.replace("ı", "i")
    return " ".join(normalized.split())


def execute_text_command(text: str) -> str:
    command = normalize_text_command(text)
    if not command:
        return "Önce bir komut yaz."

    if command in {"sesi kapa", "sesi kapat", "sessize al", "sesi sustur", "mute"}:
        press_virtual_key(VK_VOLUME_MUTE)
        return "Bilgisayarın sesi sessize alındı veya açıldı."
    if command in {"ses arttir", "sesi arttir", "ses artır", "sesi artır", "ses yuksel", "sesi yuksel", "ses ac", "sesi ac", "volume up"}:
        press_virtual_key(VK_VOLUME_UP, 3)
        return "Bilgisayarın sesi artırıldı."
    if command in {"ses azalt", "sesi azalt", "ses kis", "sesi kis", "volume down"}:
        press_virtual_key(VK_VOLUME_DOWN, 3)
        return "Bilgisayarın sesi azaltıldı."
    if command in {"masaustu goster", "masaustunu goster", "masaustune don", "masaustu", "show desktop"}:
        pyautogui.hotkey("win", "d")
        return "Masaüstü gösterildi."

    if command in {"not defteri ac", "not defterini ac", "notepad ac", "notepad"}:
        subprocess.Popen(["notepad.exe"])
        return "Not Defteri açıldı."
    if command in {"hesap makinesi ac", "hesap makinesini ac", "calculator ac", "calc ac"}:
        subprocess.Popen(["calc.exe"])
        return "Hesap makinesi açıldı."
    if command in {"dosya gezgini ac", "dosya yoneticisini ac", "explorer ac"}:
        subprocess.Popen(["explorer.exe"])
        return "Dosya Gezgini açıldı."
    if command in {"gorev yoneticisi ac", "task manager ac", "taskmgr ac"}:
        subprocess.Popen(["taskmgr.exe"])
        return "Görev Yöneticisi açıldı."
    if command in {"ayarlar ac", "windows ayarlari ac"}:
        os.startfile("ms-settings:")
        return "Windows Ayarları açıldı."
    if command in {"chrome ac", "google chrome ac"}:
        candidates = [
            Path(os.environ.get("PROGRAMFILES", r"C:\Program Files")) / "Google/Chrome/Application/chrome.exe",
            Path(os.environ.get("PROGRAMFILES(X86)", r"C:\Program Files (x86)")) / "Google/Chrome/Application/chrome.exe",
            Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "Google/Chrome/Application/chrome.exe",
        ]
        chrome = next((candidate for candidate in candidates if candidate.is_file()), None)
        if chrome:
            subprocess.Popen([str(chrome)])
            return "Google Chrome açıldı."
        os.startfile("https://www.google.com")
        return "Varsayılan tarayıcı açıldı."
    if command in {"youtube ac", "youtube'u ac", "youtubeu ac"}:
        os.startfile("https://www.youtube.com")
        return "YouTube açıldı."
    if command in {"google ac", "google'i ac", "googlei ac"}:
        os.startfile("https://www.google.com")
        return "Google açıldı."

    search_prefixes = ("internette ara ", "google'da ara ", "googleda ara ", "webde ara ")
    for prefix in search_prefixes:
        if command.startswith(prefix) and command[len(prefix):].strip():
            query = command[len(prefix):].strip()
            os.startfile("https://www.google.com/search?" + urlencode({"q": query}))
            return "İnternet araması açıldı."

    site_prefixes = ("site ac ", "web sitesi ac ", "adres ac ")
    for prefix in site_prefixes:
        if command.startswith(prefix):
            address = text.strip()[len(prefix):].strip()
            if not address:
                return "Açılacak site adresini yaz."
            if "://" not in address:
                address = "https://" + address
            from urllib.parse import urlsplit
            parts = urlsplit(address)
            if parts.scheme not in {"http", "https"} or not parts.hostname or parts.username or parts.password:
                return "Güvenli bir web adresi yazıp tekrar dene."
            os.startfile(address)
            return "Web sitesi açıldı."

    return "Desteklenmeyen komut. Şunları deneyebilirsin: Chrome aç, Not Defteri aç, Hesap makinesi aç, YouTube aç, sesi artır, sesi azalt, masaüstünü göster."


def execute_command(command: str, fields: dict) -> str:
    command = command.strip().lower()
    if command == "text_command":
        return execute_text_command(str(fields.get("text") or ""))
    if command == "mute":
        press_virtual_key(VK_VOLUME_MUTE)
        return "Bilgisayarın sesi aç/kapat tuşuyla değiştirildi."
    if command == "volume_up":
        press_virtual_key(VK_VOLUME_UP, 3)
        return "Bilgisayarın sesi artırıldı."
    if command == "volume_down":
        press_virtual_key(VK_VOLUME_DOWN, 3)
        return "Bilgisayarın sesi azaltıldı."
    if command == "home":
        pyautogui.hotkey("win", "d")
        return "Windows masaüstü gösterildi."
    if command == "mouse_move":
        dx = int(fields.get("dx", 0) or 0)
        dy = int(fields.get("dy", 0) or 0)
        pyautogui.moveRel(dx, dy, duration=0)
        return "Fare hareketi uygulandı."
    if command == "mouse_click":
        pyautogui.click()
        return "Sol tıklama uygulandı."
    if command == "mouse_right_click":
        pyautogui.click(button="right")
        return "Sağ tıklama uygulandı."
    if command == "power":
        return "Güç kapatma bu sürümde etkin değil; yanlışlıkla kapanmayı önlemek için işlem uygulanmadı."
    return f"Desteklenmeyen komut: {command}"


def command_is_supported(command: str) -> bool:
    return command in {
        "mute", "volume_up", "volume_down", "home",
        "mouse_move", "mouse_click", "mouse_right_click", "power", "text_command"
    }


def ask_install_startup() -> None:
    try:
        answer = input("MynAssistant Agent Windows açılışında otomatik başlasın mı? [E/h]: ").strip().lower()
    except (EOFError, KeyboardInterrupt):
        return
    if answer in {"", "e", "evet", "y", "yes"}:
        if not getattr(sys, "frozen", False):
            print("Otomatik başlangıç yalnızca paketlenmiş EXE sürümünde desteklenir.")
            return
        try:
            import winreg
            key = winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Software\Microsoft\Windows\CurrentVersion\Run",
                0,
                winreg.KEY_SET_VALUE,
            )
            winreg.SetValueEx(key, "MynAssistantAgent", 0, winreg.REG_SZ, f'"{Path(sys.executable).resolve()}"')
            winreg.CloseKey(key)
            print("Otomatik başlangıç etkinleştirildi.")
        except Exception as exc:
            print(f"Otomatik başlangıç ayarlanamadı: {exc}")


def run_agent() -> int:
    APP_DATA.mkdir(parents=True, exist_ok=True)
    session_state = load_protected_session()
    session = None
    if session_state:
        try:
            session = FirebaseSession(session_state)
            print(f"Kaydedilmiş Firebase oturumu kullanılıyor: {session.email or session.uid}")
        except Exception as exc:
            print(f"[Auth] Kayıtlı oturum yenilenemedi; Google girişi yeniden açılacak. Ayrıntı: {exc}")
            delete_protected_session()

    if session is None:
        session = FirebaseSession.sign_in()
        print(f"Firebase hesabı: {session.email or session.uid}")
        ask_install_startup()

    device_id = load_device_id(session.uid)
    register_or_heartbeat(session, device_id, initial=True)
    print(f"Cihaz çevrimiçi: {platform.node()} (ID: {device_id})")
    print("Android uygulamasında Cihazlarım bölümünde otomatik görünmesi gerekir.")
    print("PC Kumandası açıkken masaüstü önizlemesi yaklaşık saniyede bir güncellenir.")
    print("Durdurmak için Ctrl+C.")

    try:
        write_android_phone_snapshot(session)
    except Exception as exc:
        print(f"[Telefonlar] İlk telefon listesi alınamadı: {exc}")

    last_heartbeat = time.monotonic()
    last_phone_discovery = time.monotonic()
    last_screen_check = 0.0
    last_screen_frame = 0.0
    last_command_poll = 0.0
    screen_active = False
    pyautogui.FAILSAFE = False
    try:
        while True:
            session.ensure_fresh()
            now = time.monotonic()

            if now - last_heartbeat >= HEARTBEAT_SECONDS:
                register_or_heartbeat(session, device_id)
                last_heartbeat = now

            if now - last_phone_discovery >= PHONE_DISCOVERY_SECONDS:
                try:
                    write_android_phone_snapshot(session)
                except Exception as exc:
                    print(f"[Telefonlar] Telefon listesi yenilenemedi: {exc}")
                last_phone_discovery = now

            screen_check_interval = 3.0 if screen_active else SCREEN_CHECK_SECONDS
            if now - last_screen_check >= screen_check_interval:
                device_state = read_device_state(session, device_id)
                was_active = screen_active
                screen_active = screen_request_is_active(device_state)
                last_screen_check = now
                if was_active and not screen_active:
                    try:
                        firestore_patch(
                            session,
                            f"devices/{device_id}",
                            {"screenJpegBase64": encode_string(""), "screenFrameAt": encode_timestamp()},
                            update_mask=["screenJpegBase64", "screenFrameAt"],
                        )
                    except Exception as exc:
                        print(f"[Uyarı] Önizleme alanı temizlenemedi: {exc}")

            if screen_active and now - last_screen_frame >= SCREEN_FRAME_SECONDS:
                try:
                    upload_screen_frame(session, device_id)
                    last_screen_frame = now
                except Exception as exc:
                    print(f"[Önizleme] Ekran karesi gönderilemedi: {exc}")
                    last_screen_frame = now

            if now - last_command_poll >= COMMAND_POLL_SECONDS:
                for document in fetch_device_commands(session, device_id):
                    fields = decode_document(document)
                    if (
                        fields.get("ownerId") != session.uid
                        or fields.get("deviceId") != device_id
                        or fields.get("status") != "pending"
                    ):
                        continue

                    # Claim with the fetched updateTime so multiple Agent processes cannot
                    # successfully transition the same pending document to running.
                    try:
                        claimed = claim_command(session, document)
                    except Exception as exc:
                        print(f"[Hata] Komut sahiplenilemedi: {exc}")
                        continue
                    if not claimed:
                        continue

                    command = str(fields.get("command") or "")
                    if not command_is_supported(command):
                        try:
                            update_command(session, document, "failed", "Desteklenmeyen komut.")
                        except Exception as exc:
                            print(f"[Hata] Desteklenmeyen komutun durumu güncellenemedi: {exc}")
                        continue

                    try:
                        result = execute_command(command, fields)
                        status = "failed" if result.startswith("Desteklenmeyen komut") else "completed"
                        update_command(session, document, status, result)
                        print(f"[Komut] {command}: {result}")
                    except Exception as exc:
                        try:
                            update_command(session, document, "failed", str(exc))
                        except Exception as update_exc:
                            print(f"[Hata] Komut durumu güncellenemedi: {update_exc}")
                        print(f"[Hata] {command}: {exc}")
                last_command_poll = now

            time.sleep(POLL_SECONDS)
    except KeyboardInterrupt:
        print("\nAgent kapatılıyor...")
    finally:
        try:
            firestore_patch(
                session,
                f"devices/{device_id}",
                {
                    "status": encode_string("offline"),
                    "lastSeenAt": encode_timestamp(),
                    "screenRequested": {"booleanValue": False},
                    "screenJpegBase64": encode_string(""),
                },
                update_mask=["status", "lastSeenAt", "screenRequested", "screenJpegBase64"],
            )
        except Exception as exc:
            print(f"[Uyarı] Çevrimdışı durumu kaydedilemedi: {exc}")
    return 0


def main() -> int:
    if os.name != "nt":
        print("MynAssistant Desktop Agent yalnızca Windows üzerinde çalışır.")
        return 2
    try:
        return run_agent()
    except FileNotFoundError as exc:
        print(f"\n[Kurulum] {exc}")
        input("\nÇıkmak için Enter'a bas...")
        return 2
    except Exception as exc:
        print(f"\n[Hata] {exc}")
        input("\nÇıkmak için Enter'a bas...")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
