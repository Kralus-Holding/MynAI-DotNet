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


PROJECT_ID = "myncloud-24"
FIREBASE_API_KEY = "AIzaSyB0z1b5OOMh8Pqn2GPd1ZuWoHEhRoKSrRM"
IDENTITY_TOOLKIT = "https://identitytoolkit.googleapis.com/v1"
SECURE_TOKEN = "https://securetoken.googleapis.com/v1/token"
FIRESTORE = f"https://firestore.googleapis.com/v1/projects/{PROJECT_ID}/databases/(default)/documents"
AGENT_VERSION = "0.2.2"
POLL_SECONDS = 2.0
COMMAND_POLL_SECONDS = 2.0
SCREEN_CHECK_SECONDS = 8.0
SCREEN_FRAME_SECONDS = 1.5
SCREEN_REQUEST_MAX_AGE_SECONDS = 25.0
HEARTBEAT_SECONDS = 25.0
DPAPI_ENTROPY = b"MynAssistantDesktopAgent-v1"
APP_DATA = Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "MynAssistant" / "DesktopAgent"
SESSION_FILE = APP_DATA / "session.bin"
DEVICE_FILE = APP_DATA / "devices.json"
STATUS_FILE = APP_DATA / "status.json"

VK_VOLUME_MUTE = 0xAD
VK_VOLUME_DOWN = 0xAE
VK_VOLUME_UP = 0xAF
KEYEVENTF_KEYUP = 0x0002


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")

def write_bridge_status(
    state: str,
    message: str,
    email: str = "",
    device_id: str = "",
) -> None:
    """Write a secret-free connection snapshot consumed by the Windows Settings page."""
    try:
        APP_DATA.mkdir(parents=True, exist_ok=True)
        payload = {
            "state": state,
            "message": message[:1000],
            "email": email[:320],
            "deviceId": device_id[:256],
            "machineName": platform.node()[:256],
            "updatedAt": utc_now(),
        }
        temporary = STATUS_FILE.with_suffix(".json.tmp")
        temporary.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
        temporary.replace(STATUS_FILE)
    except Exception as exc:
        # A local diagnostic file must never prevent the Firebase connection.
        print(f"[Status] Durum dosyası güncellenemedi: {exc}")




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


class FirebaseSessionExpiredError(RuntimeError):
    """A saved Firebase refresh token was explicitly rejected."""


class FirebaseSession:
    def __init__(self, state: dict):
        self.uid = state["uid"]
        self.email = state.get("email", "")
        self.display_name = state.get("displayName", "")
        self.refresh_token = state["refreshToken"]
        self.id_token = state.get("idToken", "")
        self.expires_at = float(state.get("expiresAt", 0))
        self.ensure_fresh(force=not self.id_token)

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
            message = ""
            try:
                message = str(response.json().get("error", {}).get("message", "")).upper()
            except Exception:
                pass
            if message in {"INVALID_REFRESH_TOKEN", "TOKEN_EXPIRED", "USER_DISABLED", "USER_NOT_FOUND"}:
                raise FirebaseSessionExpiredError("Firebase oturumu geçersiz veya süresi dolmuş.")
            raise RuntimeError(f"Firebase oturumu geçici olarak yenilenemedi (HTTP {response.status_code}).")
        data = response.json()
        self.id_token = data["id_token"]
        self.refresh_token = data.get("refresh_token", self.refresh_token)
        self.expires_at = time.time() + int(data.get("expires_in", "3600")) - 90
        self._store()

    def headers(self) -> dict:
        self.ensure_fresh()
        return {"Authorization": f"Bearer {self.id_token}", "Content-Type": "application/json"}


def firestore_patch(
    session: FirebaseSession,
    document_path: str,
    fields: dict,
    update_mask: list[str] | None = None,
    current_document_update_time: str | None = None,
):
    params = []
    if update_mask:
        params.extend(("updateMask.fieldPaths", field) for field in update_mask)
    if current_document_update_time:
        # Firestore's updateTime precondition provides compare-and-swap semantics.
        params.append(("currentDocument.updateTime", current_document_update_time))
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


def remote_control_enabled() -> bool:
    """Read the user's local permission; legacy settings keep existing behavior."""
    settings_path = (
        Path(os.environ.get("LOCALAPPDATA", str(Path.home())))
        / "MynAI"
        / "settings.json"
    )
    if not settings_path.exists():
        return True

    try:
        settings = json.loads(settings_path.read_text(encoding="utf-8-sig"))
    except FileNotFoundError:
        # The settings file may be replaced while the app is saving it.
        return True
    except Exception as exc:
        print(f"[Permissions] Could not read remote-control settings; disabled for safety: {exc}")
        return False

    if not isinstance(settings, dict):
        return False
    # Settings written before this option existed remain enabled for compatibility.
    return settings.get("RemoteControlEnabled", True) is True


def remote_control_reenable_cutoff() -> datetime | None:
    """Return the latest opt-in timestamp used to reject commands queued before re-enabling."""
    settings_path = (
        Path(os.environ.get("LOCALAPPDATA", str(Path.home())))
        / "MynAI"
        / "settings.json"
    )
    if not settings_path.exists():
        return None

    try:
        settings = json.loads(settings_path.read_text(encoding="utf-8-sig"))
    except FileNotFoundError:
        return None
    except Exception as exc:
        print(f"[Permissions] Could not read permission timestamp; stale commands will be blocked: {exc}")
        return datetime.max.replace(tzinfo=timezone.utc)

    if not isinstance(settings, dict):
        return datetime.max.replace(tzinfo=timezone.utc)

    value = settings.get("RemoteControlEnabledAt")
    if value is None:
        return None

    cutoff = _parse_command_timestamp(value)
    # A present but invalid timestamp must fail closed rather than replay old work.
    return cutoff or datetime.max.replace(tzinfo=timezone.utc)


def command_predates_remote_control_reenable(
    fields: dict,
    cutoff: datetime | None,
) -> bool:
    if cutoff is None:
        return False

    created_at = _parse_command_timestamp(fields.get("createdAt"))
    return created_at is None or created_at < cutoff


def fail_pending_commands_when_remote_control_disabled(
    session: FirebaseSession,
    device_id: str,
) -> None:
    """Expire queued work while permission is off so it cannot run after re-enabling."""
    try:
        documents = fetch_owner_commands(session, device_id)
    except Exception as exc:
        print(f"[Permissions] Could not refresh pending commands; Agent continues: {exc}")
        return

    for document in documents:
        fields = decode_document(document)
        if fields.get("deviceId") != device_id or fields.get("status") != "pending":
            continue
        try:
            update_command(
                session,
                document,
                "failed",
                "Remote-control permission is disabled; command was not executed.",
                expected_update_time=document.get("updateTime"),
            )
        except Exception as exc:
            print(f"[Permissions] Could not fail queued command while permission is disabled: {exc}")


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


def capture_screen_base64() -> str:
    image = pyautogui.screenshot()
    image.thumbnail((1280, 720))
    output = io.BytesIO()
    image.save(output, format="JPEG", quality=45, optimize=True)
    payload = output.getvalue()
    # Firestore document size includes base64 expansion (~4/3) and field
    # overhead. Keep the raw JPEG below 700 KB so the encoded value stays
    # comfortably under the 1 MiB document limit.
    if len(payload) > 560_000:
        output = io.BytesIO()
        image.save(output, format="JPEG", quality=30, optimize=True)
        payload = output.getvalue()
    if len(payload) > 700_000:
        raise RuntimeError("Ekran görüntüsü Firestore belge boyutu sınırına yaklaşıyor; kare güvenli boyut sınırının altında üretilmedi.")
    return base64.b64encode(payload).decode("ascii")


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


def fetch_owner_commands(session: FirebaseSession) -> list[dict]:
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
                                    "field": {"fieldPath": "status"},
                                    "op": "EQUAL",
                                    "value": {"stringValue": "pending"},
                                }
                            },
                        ],
                    }
                },
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


def command_targets_device(fields: dict, owner_uid: str, device_id: str) -> bool:
    """Require an exact owner, exact target device, and pending state before any command mutation."""
    return (
        isinstance(owner_uid, str)
        and bool(owner_uid)
        and isinstance(device_id, str)
        and bool(device_id)
        and fields.get("ownerId") == owner_uid
        and fields.get("deviceId") == device_id
        and fields.get("status") == "pending"
    )


def update_command(
    session: FirebaseSession,
    document: dict,
    status: str,
    result: str,
    expected_update_time: str | None = None,
    extra_fields: dict | None = None,
) -> None:
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
    if extra_fields:
        fields.update(extra_fields)
    update_mask = ["status", "result", "processedAt"]
    if extra_fields:
        update_mask.extend(extra_fields.keys())
    firestore_patch(
        session,
        document_path,
        fields,
        update_mask=update_mask,
        current_document_update_time=expected_update_time,
    )


def claim_pending_command(session: FirebaseSession, document: dict, device_id: str) -> bool:
    # Never run a command if its Firestore version cannot be checked. This prevents
    # a stale "pending" snapshot from overriding a cancellation written by the panel.
    update_time = document.get("updateTime")
    full_name = document.get("name", "")
    marker = "/documents/"
    if not isinstance(update_time, str) or not update_time or marker not in full_name:
        print("[Command] Missing update time; command was not executed.")
        return False

    document_path = full_name.split(marker, 1)[1]
    claim_token = uuid.uuid4().hex
    try:
        update_command(
            session,
            document,
            "running",
            "Windows Agent komutu güvenli şekilde sahiplendi.",
            expected_update_time=update_time,
            extra_fields={
                "claimToken": encode_string(claim_token),
                "claimedByDeviceId": encode_string(device_id),
            },
        )
        return True
    except Exception as claim_error:
        # A network timeout can happen after Firestore accepted the write. Read back
        # the document and proceed only if this exact claim token won the race.
        print(f"[Command] Claim response uncertain; verifying Firestore state: {claim_error}")
        try:
            response = requests.get(
                f"{FIRESTORE}/{document_path}",
                headers=session.headers(),
                timeout=15,
            )
            if response.status_code < 400:
                current = decode_document(response.json())
                return (
                    current.get("status") == "running"
                    and current.get("claimToken") == claim_token
                    and current.get("claimedByDeviceId") == device_id
                )
        except Exception as read_error:
            print(f"[Command] Claim verification failed; command was not executed: {read_error}")
        return False


def press_virtual_key(vk: int, repeat: int = 1) -> None:
    user32 = ctypes.windll.user32
    for _ in range(max(1, min(repeat, 15))):
        user32.keybd_event(vk, 0, 0, 0)
        user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)


KEYEVENTF_UNICODE = 0x0004
INPUT_KEYBOARD = 1
MAX_REMOTE_TEXT_LENGTH = 1000


class _KeyboardInput(ctypes.Structure):
    _fields_ = [
        ("wVk", ctypes.c_ushort),
        ("wScan", ctypes.c_ushort),
        ("dwFlags", ctypes.c_uint32),
        ("time", ctypes.c_uint32),
        ("dwExtraInfo", ctypes.c_size_t),
    ]


class _MouseInput(ctypes.Structure):
    _fields_ = [
        ("dx", ctypes.c_int32),
        ("dy", ctypes.c_int32),
        ("mouseData", ctypes.c_uint32),
        ("dwFlags", ctypes.c_uint32),
        ("time", ctypes.c_uint32),
        ("dwExtraInfo", ctypes.c_size_t),
    ]


class _HardwareInput(ctypes.Structure):
    _fields_ = [
        ("uMsg", ctypes.c_uint32),
        ("wParamL", ctypes.c_ushort),
        ("wParamH", ctypes.c_ushort),
    ]


class _InputUnion(ctypes.Union):
    _fields_ = [
        ("mi", _MouseInput),
        ("ki", _KeyboardInput),
        ("hi", _HardwareInput),
    ]


class _Input(ctypes.Structure):
    _fields_ = [
        ("type", ctypes.c_uint32),
        ("union", _InputUnion),
    ]


def send_unicode_text(text: str) -> None:
    """Type Unicode text through Win32 SendInput without changing the clipboard."""
    if os.name != "nt":
        raise RuntimeError("Uzaktan metin yazma yalnızca Windows Agent üzerinde kullanılabilir.")
    if len(text) > MAX_REMOTE_TEXT_LENGTH:
        raise ValueError(f"Metin en fazla {MAX_REMOTE_TEXT_LENGTH} karakter olabilir.")

    safe_text = "".join(char for char in text if ord(char) >= 32 and ord(char) != 127)
    if not safe_text:
        raise ValueError("Yazılacak metin boş.")

    utf16 = safe_text.encode("utf-16-le", errors="strict")
    events = []
    for index in range(0, len(utf16), 2):
        unit = utf16[index] | (utf16[index + 1] << 8)
        key_down = _Input(
            type=INPUT_KEYBOARD,
            union=_InputUnion(ki=_KeyboardInput(0, unit, KEYEVENTF_UNICODE, 0, 0)),
        )
        key_up = _Input(
            type=INPUT_KEYBOARD,
            union=_InputUnion(ki=_KeyboardInput(0, unit, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, 0, 0)),
        )
        events.extend((key_down, key_up))

    inputs = (_Input * len(events))(*events)
    sent = ctypes.windll.user32.SendInput(len(inputs), inputs, ctypes.sizeof(_Input))
    if sent != len(inputs):
        raise ctypes.WinError()


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
    if command == "type_text":
        text = str(fields.get("text") or "")
        if not text.strip():
            return "Bilgisayara yazılacak metni gir."
        if len(text) > MAX_REMOTE_TEXT_LENGTH:
            return f"Metin en fazla {MAX_REMOTE_TEXT_LENGTH} karakter olabilir."
        send_unicode_text(text)
        if fields.get("pressEnter") is True:
            pyautogui.press("enter")
        return "Metin bilgisayara yazıldı." + (" Enter gönderildi." if fields.get("pressEnter") is True else "")
    if command == "key_backspace":
        pyautogui.press("backspace")
        return "Backspace tuşu gönderildi."
    if command == "key_enter":
        pyautogui.press("enter")
        return "Enter tuşu gönderildi."
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
        "mouse_move", "mouse_click", "mouse_right_click", "power", "text_command",
        "type_text", "key_backspace", "key_enter"
    }


def run_agent() -> int:
    APP_DATA.mkdir(parents=True, exist_ok=True)
    write_bridge_status("starting", "Telefon-PC bağlantısı hazırlanıyor.")
    session_state = load_protected_session()
    session = None

    if session_state:
        try:
            session = FirebaseSession(session_state)
            print(f"Kaydedilmiş Firebase oturumu kullanılıyor: {session.email or session.uid}")
        except FirebaseSessionExpiredError:
            delete_protected_session()
            print("[Auth] Kaydedilmiş oturum geçersiz. Uygulamadan yeniden giriş gerekli.")
        except requests.RequestException:
            write_bridge_status("error", "Firebase oturumu doğrulanamadı: internet bağlantısını kontrol edip yeniden deneyin.")
            print("[Auth] Firebase doğrulama isteği başarısız; kayıtlı oturum korunuyor.")
            return 1
        except Exception as exc:
            write_bridge_status(
                "error",
                f"Firebase oturumu doğrulanamadı ({type(exc).__name__}). Hesap bölümünden yeniden giriş yapabilirsiniz.",
            )
            print(f"[Auth] Oturum doğrulaması tamamlanamadı: {type(exc).__name__}")
            return 1

    if session is None:
        write_bridge_status("auth_required", "Telefon işlemleri için lütfen önce Hesap bölümünden giriş yapın.")
        print("[Auth] Telefon-PC işlemleri için MynAssistant Hesap bölümünden giriş yapın.")
        return 2

    device_id = load_device_id(session.uid)
    write_bridge_status("connecting", "Firebase oturumu doğrulandı; Windows PC kaydediliyor.", email=session.email, device_id=device_id)
    register_or_heartbeat(session, device_id, initial=True)
    write_bridge_status("online", "PC Firestore'a kaydedildi; Android ve web paneli için hazır.", email=session.email, device_id=device_id)
    print(f"Cihaz çevrimiçi: {platform.node()} (ID: {device_id})")
    print("Android uygulamasında Cihazlarım bölümünde otomatik görünmesi gerekir.")
    print("PC Kumandası açıkken masaüstü önizlemesi yaklaşık saniyede bir güncellenir.")

    last_heartbeat = time.monotonic()
    last_screen_check = 0.0
    last_screen_frame = 0.0
    last_command_poll = 0.0
    screen_active = False
    remote_control_was_enabled: bool | None = None
    pyautogui.FAILSAFE = False
    try:
        while True:
            session.ensure_fresh()
            now = time.monotonic()

            if now - last_heartbeat >= HEARTBEAT_SECONDS:
                register_or_heartbeat(session, device_id)
                write_bridge_status(
                    "online",
                    "PC Firestore'a kaydedildi; kalp atışı başarılı.",
                    email=session.email,
                    device_id=device_id,
                )
                last_heartbeat = now

            remote_allowed = remote_control_enabled()
            if not remote_allowed:
                if remote_control_was_enabled is not False:
                    write_bridge_status(
                        "online",
                        "Uzaktan kontrol İzinler bölümünde kapalı; uzaktan komutlar ve ekran önizlemesi duraklatıldı.",
                        email=session.email,
                        device_id=device_id,
                    )
                    try:
                        firestore_patch(
                            session,
                            f"devices/{device_id}",
                            {
                                "screenRequested": {"booleanValue": False},
                                "screenJpegBase64": encode_string(""),
                                "screenFrameAt": encode_timestamp(),
                            },
                            update_mask=["screenRequested", "screenJpegBase64", "screenFrameAt"],
                        )
                    except Exception as exc:
                        print(f"[Permissions] Could not stop screen preview: {exc}")

                remote_control_was_enabled = False
                screen_active = False
                last_screen_check = now
                last_screen_frame = now
                if now - last_command_poll >= COMMAND_POLL_SECONDS:
                    fail_pending_commands_when_remote_control_disabled(session, device_id)
                    last_command_poll = now
                time.sleep(POLL_SECONDS)
                continue

            if remote_control_was_enabled is False:
                write_bridge_status(
                    "online",
                    "Uzaktan kontrol izni açık; PC telefon ve web paneli için hazır.",
                    email=session.email,
                    device_id=device_id,
                )
            remote_control_was_enabled = True

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
                permission_reenable_cutoff = remote_control_reenable_cutoff()
                for document in fetch_owner_commands(session):
                    fields = decode_document(document)
                    # Defense in depth: the query is owner-scoped, and we independently
                    # verify owner + exact device + pending state before any mutation.
                    if not command_targets_device(fields, session.uid, device_id):
                        continue
                    if command_predates_remote_control_reenable(fields, permission_reenable_cutoff):
                        try:
                            update_command(
                                session,
                                document,
                                "failed",
                                "Command was queued before remote-control permission was re-enabled; it was not executed.",
                                expected_update_time=document.get("updateTime"),
                            )
                        except Exception as permission_error:
                            print(f"[Permissions] Could not fail pre-reenable command: {permission_error}")
                        continue
                    command = str(fields.get("command") or "")
                    if not command_is_supported(command):
                        try:
                            update_command(
                                session,
                                document,
                                "failed",
                                "Desteklenmeyen komut.",
                                expected_update_time=document.get("updateTime"),
                            )
                        except Exception as claim_error:
                            print(f"[Komut] Geçersiz komutun durumu değişmiş olabilir; kayıt güncellenmedi: {claim_error}")
                        continue
                    if not claim_pending_command(session, document, device_id):
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
        write_bridge_status(
            "offline",
            "Windows bağlantı köprüsü kapatıldı.",
            email=session.email,
            device_id=device_id,
        )
    return 0


def configure_packaged_logging() -> None:
    """Keep the packaged background bridge silent and persist diagnostics to disk."""
    global _PACKAGED_LOG_HANDLE
    if not getattr(sys, "frozen", False):
        return
    try:
        APP_DATA.mkdir(parents=True, exist_ok=True)
        _PACKAGED_LOG_HANDLE = (APP_DATA / "bridge.log").open(
            "a", encoding="utf-8", buffering=1
        )
        sys.stdout = _PACKAGED_LOG_HANDLE
        sys.stderr = _PACKAGED_LOG_HANDLE
    except Exception:
        # Logging setup must never prevent the bridge from attempting to connect.
        pass


_PACKAGED_LOG_HANDLE = None


def main() -> int:
    configure_packaged_logging()
    if os.name != "nt":
        print("MynAssistant Desktop Agent yalnızca Windows üzerinde çalışır.")
        return 2
    try:
        return run_agent()
    except FileNotFoundError as exc:
        write_bridge_status("error", f"Kurulum: {exc}")
        print(f"\n[Kurulum] {exc}")
        return 2
    except Exception as exc:
        write_bridge_status("error", f"Bağlantı hatası: {exc}")
        print(f"\n[Hata] {exc}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
