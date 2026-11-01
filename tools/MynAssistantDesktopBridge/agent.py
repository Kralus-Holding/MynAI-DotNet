from __future__ import annotations

import base64
import binascii
import hashlib
import ctypes
import io
import json
import os
import platform
import re
import zipfile
import subprocess
import sys
import unicodedata
import time
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import urlencode

import pyautogui
import requests
import win32crypt


FIREBASE_API_KEY = "AIzaSyB0z1b5OOMh8Pqn2GPd1ZuWoHEhRoKSrRM"
SECURE_TOKEN = "https://securetoken.googleapis.com/v1/token"
SERVER_BASE_URL = "https://mynassistant-server.onrender.com"
AGENT_VERSION = "0.2.2"
POLL_SECONDS = 0.5
COMMAND_POLL_SECONDS = 1.0
SCREEN_CHECK_SECONDS = 8.0
SCREEN_FRAME_SECONDS = 1.5
SCREEN_REQUEST_MAX_AGE_SECONDS = 25.0
HEARTBEAT_SECONDS = 25.0
COMMAND_MAX_AGE_SECONDS = 60.0
COMMAND_FUTURE_SKEW_SECONDS = 60.0
DPAPI_ENTROPY = b"MynAssistantDesktopAgent-v1"
APP_DATA = Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "MynAssistant" / "DesktopAgent"
SESSION_FILE = APP_DATA / "session.bin"
DEVICE_FILE = APP_DATA / "devices.json"
STATUS_FILE = APP_DATA / "status.json"

VK_VOLUME_MUTE = 0xAD
VK_VOLUME_DOWN = 0xAE
VK_VOLUME_UP = 0xAF
WM_CLOSE = 0x0010
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
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
    if value is None:
        return None
    # Server relay responses contain already-decoded JSON fields.
    if not isinstance(value, dict):
        return value
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

def server_request(session: FirebaseSession, method: str, path: str, json_body: dict | None = None, timeout: int = 20) -> dict:
    """Authenticated Server API request. Windows never calls Firestore directly."""
    url = SERVER_BASE_URL + path
    response = requests.request(method, url, headers=session.headers(), json=json_body, timeout=timeout)
    if response.status_code == 401:
        session.ensure_fresh(force=True)
        response = requests.request(method, url, headers=session.headers(), json=json_body, timeout=timeout)
    if response.status_code >= 400:
        detail = ""
        try:
            payload = response.json()
            detail = str(payload.get("detail") or payload.get("message") or payload.get("title") or "")
        except Exception:
            pass
        raise RuntimeError(detail[:500] or f"MynAssistant Server isteği başarısız (HTTP {response.status_code}).")
    if not response.content:
        return {}
    payload = response.json()
    if not isinstance(payload, dict):
        raise RuntimeError("MynAssistant Server beklenmeyen yanıt biçimi döndürdü.")
    return payload


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


def fetch_owner_commands(session: FirebaseSession, device_id: str) -> list[dict]:
    filters = [
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
        {
            "fieldFilter": {
                "field": {"fieldPath": "deviceId"},
                "op": "EQUAL",
                "value": {"stringValue": device_id},
            }
        },
    ]
    response = requests.post(
        f"{FIRESTORE}:runQuery",
        headers=session.headers(),
        json={
            "structuredQuery": {
                "from": [{"collectionId": "commands"}],
                "where": {
                    "compositeFilter": {
                        "op": "AND",
                        "filters": filters,
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


def _parse_command_timestamp(value: object) -> datetime | None:
    if not isinstance(value, str) or not value.strip():
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except (ValueError, TypeError):
        return None
    if parsed.tzinfo is None:
        return None
    return parsed.astimezone(timezone.utc)


def command_expiration_reason(fields: dict, now: datetime | None = None) -> str | None:
    """Fail closed for stale, missing, or implausibly future-dated commands."""
    current = now or datetime.now(timezone.utc)
    if current.tzinfo is None:
        current = current.replace(tzinfo=timezone.utc)
    else:
        current = current.astimezone(timezone.utc)

    expires_at_value = fields.get("expiresAt")
    if expires_at_value is not None:
        expires_at = _parse_command_timestamp(expires_at_value)
        if expires_at is None:
            return "Komutun sona erme zamanı geçersiz; güvenlik için çalıştırılmadı."
        seconds_until_expiry = (expires_at - current).total_seconds()
        if seconds_until_expiry <= 0:
            return "Komutun süresi doldu; lütfen yeniden gönder."
        if seconds_until_expiry > COMMAND_MAX_AGE_SECONDS + COMMAND_FUTURE_SKEW_SECONDS:
            return "Komutun sona erme zamanı beklenen sınırı aşıyor; güvenlik için çalıştırılmadı."
        return None

    created_at = _parse_command_timestamp(fields.get("createdAt"))
    if created_at is None:
        return "Komutun oluşturulma zamanı eksik veya geçersiz; güvenlik için çalıştırılmadı."
    age_seconds = (current - created_at).total_seconds()
    if age_seconds > COMMAND_MAX_AGE_SECONDS:
        return "Komutun süresi doldu; lütfen yeniden gönder."
    if age_seconds < -COMMAND_FUTURE_SKEW_SECONDS:
        return "Komutun oluşturulma zamanı gelecekte; güvenlik için çalıştırılmadı."
    return None

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
    command_name = decode_field(document.get("fields", {}).get("command"))
    if command_name == "list_files":
        result_limit = FILE_LIST_MAX_RESULT_LENGTH
    elif command_name == "download_file":
        result_limit = FILE_DOWNLOAD_MAX_RESULT_LENGTH
    else:
        result_limit = 800
    fields = {
        "status": encode_string(status),
        "result": encode_string(result[:result_limit]),
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


FILE_LIST_MAX_ENTRIES = 200
FILE_LIST_MAX_PATH_LENGTH = 1024
FILE_LIST_MAX_RESULT_LENGTH = 350_000
FILE_DOWNLOAD_MAX_BYTES = 500_000
FILE_DOWNLOAD_MAX_RESULT_LENGTH = 700_000
FILE_UPLOAD_MAX_BYTES = 500_000
ZIP_MAX_FILES = 2_000
ZIP_MAX_INPUT_BYTES = 100_000_000
FILE_LIST_ROOTS = (
    ("Masaüstü", ("Desktop", "Masaüstü")),
    ("Belgeler", ("Documents", "Belgeler")),
    ("Resimler", ("Pictures", "Resimler")),
    ("Müzik", ("Music", "Müzik")),
    ("İndirilenler", ("Downloads", "İndirilenler")),
    ("Videolar", ("Videos", "Videolar")),
)


def _is_reparse_point(path: Path) -> bool:
    """Reject symbolic links and Windows junctions at every file-browser boundary."""
    try:
        if path.is_symlink():
            return True
        is_junction = getattr(path, "is_junction", None)
        return bool(callable(is_junction) and is_junction())
    except (OSError, RuntimeError):
        # If Windows cannot determine the path type, fail closed.
        return True


def _allowed_file_roots() -> list[tuple[str, Path]]:
    """Return only the standard profile folders exposed to the remote file browser."""
    profile = Path(os.environ.get("USERPROFILE") or Path.home()).resolve()
    roots: list[tuple[str, Path]] = []
    seen: set[str] = set()
    for label, candidates in FILE_LIST_ROOTS:
        for folder_name in candidates:
            candidate = profile / folder_name
            try:
                # Do not let a symlink/junction replace an allowlisted profile root.
                if _is_reparse_point(candidate) or not candidate.is_dir():
                    continue
                resolved = candidate.resolve(strict=True)
            except (OSError, RuntimeError):
                continue
            identity = os.path.normcase(str(resolved))
            if identity in seen:
                continue
            seen.add(identity)
            roots.append((label, resolved))
            break
    return roots


def _serialize_file_listing(
    current_path: str,
    parent_path: str | None,
    entries: list[dict],
    truncated: bool,
    warnings: list[str],
) -> str:
    payload = {
        "schemaVersion": 1,
        "currentPath": current_path,
        "parentPath": parent_path,
        "entries": entries,
        "truncated": truncated,
        "warnings": sorted(set(warnings)),
    }
    encoded = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    # Keep the command result below Firestore's 1 MiB document limit and remove
    # whole entries rather than cutting a JSON document mid-string.
    while len(encoded.encode("utf-8")) > FILE_LIST_MAX_RESULT_LENGTH and entries:
        entries.pop()
        payload["truncated"] = True
        payload["warnings"] = sorted(set(warnings + ["Liste boyut sınırına ulaştı; bazı öğeler gösterilmiyor."]))
        encoded = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    if len(encoded.encode("utf-8")) > FILE_LIST_MAX_RESULT_LENGTH:
        raise ValueError("Dosya listesi güvenli yanıt boyutuna sığmadı.")
    return encoded


def execute_list_files(path_value: str = "") -> str:
    """List metadata inside allowlisted profile folders; never read file contents."""
    if not isinstance(path_value, str) or len(path_value) > FILE_LIST_MAX_PATH_LENGTH:
        raise ValueError("Klasör yolu geçersiz veya çok uzun.")

    roots = _allowed_file_roots()
    if not path_value.strip():
        entries = [
            {
                "name": label,
                "path": str(root),
                "isDirectory": True,
                "size": 0,
                "modifiedUtc": "",
            }
            for label, root in roots
        ]
        return _serialize_file_listing("", None, entries, False, [])

    try:
        current = Path(path_value).expanduser().resolve(strict=True)
    except (OSError, RuntimeError, ValueError) as exc:
        raise ValueError("Klasör bulunamadı veya yolu geçersiz.") from exc

    if not current.is_dir():
        raise ValueError("Seçilen yol bir klasör değil.")

    allowed_root = next(
        (root for _label, root in roots if current == root or root in current.parents),
        None,
    )
    if allowed_root is None:
        raise ValueError(
            "Bu klasöre erişim izni yok. Yalnızca Masaüstü, Belgeler, Resimler, "
            "Müzik, İndirilenler ve Videolar klasörleri açılabilir."
        )

    entries: list[dict] = []
    warnings: list[str] = []
    truncated = False
    try:
        with os.scandir(current) as iterator:
            for item in iterator:
                try:
                    item_path = Path(item.path)
                    if _is_reparse_point(item_path):
                        continue
                    resolved_item = item_path.resolve(strict=True)
                    if len(str(resolved_item)) > FILE_LIST_MAX_PATH_LENGTH:
                        warnings.append("Yolu çok uzun olan bazı öğeler listelenmedi.")
                        continue
                    if not (resolved_item == allowed_root or allowed_root in resolved_item.parents):
                        continue
                    is_directory = item.is_dir(follow_symlinks=False)
                    if not is_directory and not item.is_file(follow_symlinks=False):
                        continue
                    stat = item.stat(follow_symlinks=False)
                    entries.append(
                        {
                            "name": item.name,
                            "path": str(resolved_item),
                            "isDirectory": is_directory,
                            "size": 0 if is_directory else int(stat.st_size),
                            "modifiedUtc": datetime.fromtimestamp(
                                stat.st_mtime, timezone.utc
                            ).isoformat(timespec="seconds").replace("+00:00", "Z"),
                        }
                    )
                except (OSError, RuntimeError, ValueError):
                    warnings.append("Bazı öğelere erişilemedi ve listeden çıkarıldı.")
                if len(entries) > FILE_LIST_MAX_ENTRIES:
                    truncated = True
                    warnings.append(
                        f"Klasör {FILE_LIST_MAX_ENTRIES} öğe sınırını aştı; bazı öğeler gösterilmiyor."
                    )
                    break
    except PermissionError as exc:
        raise ValueError("Windows bu klasörün içeriğini okumaya izin vermedi.") from exc
    except OSError as exc:
        raise ValueError(
            "Klasör içeriği okunamadı. Diskin ve erişim izinlerinin durumunu kontrol et."
        ) from exc

    entries.sort(key=lambda entry: (not entry["isDirectory"], entry["name"].casefold()))
    entries = entries[:FILE_LIST_MAX_ENTRIES]
    parent_path = "" if current == allowed_root else str(current.parent)
    return _serialize_file_listing(str(current), parent_path, entries, truncated, warnings)



def execute_download_file(path_value: str = "") -> str:
    """Return one bounded file payload only when it resolves inside an allowlisted profile folder."""
    if not isinstance(path_value, str) or not path_value.strip() or len(path_value) > FILE_LIST_MAX_PATH_LENGTH:
        raise ValueError("Dosya yolu geçersiz veya çok uzun.")

    try:
        requested = Path(path_value).expanduser()
        target = requested.resolve(strict=True)
    except (OSError, RuntimeError, ValueError) as exc:
        raise ValueError("İndirilecek dosya bulunamadı veya yolu geçersiz.") from exc

    if requested.is_symlink() or getattr(requested, "is_junction", lambda: False)():
        raise ValueError("Sembolik bağlantı veya junction üzerinden dosya indirme engellendi.")
    if not target.is_file():
        raise ValueError("Seçilen öğe bir dosya değil.")

    roots = _allowed_file_roots()
    allowed_root = next(
        (root for _label, root in roots if target == root or root in target.parents),
        None,
    )
    if allowed_root is None:
        raise ValueError(
            "Bu dosyaya erişim izni yok. Yalnızca izin verilen kullanıcı klasörlerindeki dosyalar indirilebilir."
        )

    try:
        before = target.stat()
        if before.st_size > FILE_DOWNLOAD_MAX_BYTES:
            raise ValueError(
                f"Dosya {before.st_size} bayt; bu sürüm tek yanıtla en fazla {FILE_DOWNLOAD_MAX_BYTES} bayt indirir. "
                "Büyük dosyalar için parçalı aktarım henüz etkin değil."
            )
        with target.open("rb") as stream:
            content = stream.read(FILE_DOWNLOAD_MAX_BYTES + 1)
        after = target.stat()
    except PermissionError as exc:
        raise ValueError("Windows bu dosyayı okumaya izin vermedi.") from exc
    except OSError as exc:
        raise ValueError("Dosya okunamadı; erişim iznini ve diskin durumunu kontrol et.") from exc

    if len(content) > FILE_DOWNLOAD_MAX_BYTES:
        raise ValueError(
            f"Dosya {FILE_DOWNLOAD_MAX_BYTES} bayt sınırını aştı. Büyük dosyalar için parçalı aktarım henüz etkin değil."
        )
    if before.st_size != after.st_size or len(content) != after.st_size:
        raise ValueError("Dosya indirme sırasında değişti; bütünlük için işlem iptal edildi.")

    payload = {
        "schemaVersion": 1,
        "name": target.name,
        "path": str(target),
        "size": len(content),
        "sha256": hashlib.sha256(content).hexdigest(),
        "contentBase64": base64.b64encode(content).decode("ascii"),
    }
    encoded = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    if len(encoded.encode("utf-8")) > FILE_DOWNLOAD_MAX_RESULT_LENGTH:
        raise ValueError("Dosya yanıtı güvenli aktarım boyutunu aştı.")
    return encoded


def lock_workstation() -> str:
    """Lock the interactive Windows session without logging the user out."""
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    user32.LockWorkStation.argtypes = []
    user32.LockWorkStation.restype = ctypes.c_bool
    if not user32.LockWorkStation():
        error = ctypes.get_last_error()
        raise RuntimeError(f"Windows oturumu kilitlenemedi (Win32={error}).")
    return "Windows oturumu kilitlendi."


def close_active_app() -> str:
    """Request a graceful WM_CLOSE for the foreground app; never force-kill a process."""
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    user32.GetForegroundWindow.argtypes = []
    user32.GetForegroundWindow.restype = ctypes.c_void_p
    user32.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
    user32.GetWindowThreadProcessId.restype = ctypes.c_ulong
    user32.PostMessageW.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_void_p, ctypes.c_void_p]
    user32.PostMessageW.restype = ctypes.c_bool

    kernel32.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_bool, ctypes.c_uint32]
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.QueryFullProcessImageNameW.argtypes = [
        ctypes.c_void_p, ctypes.c_uint32, ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_ulong)
    ]
    kernel32.QueryFullProcessImageNameW.restype = ctypes.c_bool
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    kernel32.CloseHandle.restype = ctypes.c_bool

    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return "Kapatılacak etkin uygulama penceresi bulunamadı."

    process_id = ctypes.c_ulong(0)
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(process_id))
    if not process_id.value:
        return "Etkin pencerenin işlemi belirlenemedi."

    process = kernel32.OpenProcess(
        PROCESS_QUERY_LIMITED_INFORMATION,
        False,
        process_id.value,
    )
    if not process:
        return "Etkin uygulama güvenli biçimde doğrulanamadı; kapatma isteği gönderilmedi."

    try:
        image_buffer = ctypes.create_unicode_buffer(32768)
        image_size = ctypes.c_ulong(len(image_buffer))
        if not kernel32.QueryFullProcessImageNameW(
            process, 0, image_buffer, ctypes.byref(image_size)
        ):
            return "Etkin uygulamanın adı doğrulanamadı; kapatma isteği gönderilmedi."
        process_name = Path(image_buffer.value).name.casefold()
    finally:
        kernel32.CloseHandle(process)

    protected_processes = {
        "mynassistant.exe",
        "mynagent.exe",
        "mynai.agent.exe",
        "mynassistantdesktopbridge.exe",
        "explorer.exe",
        "winlogon.exe",
        "csrss.exe",
        "dwm.exe",
        "services.exe",
        "lsass.exe",
        "smss.exe",
        "system",
    }
    if process_name in protected_processes:
        return "MynAssistant, Agent veya Windows sistem pencereleri uzaktan kapatılamaz."

    if not user32.PostMessageW(hwnd, WM_CLOSE, 0, 0):
        error = ctypes.get_last_error()
        raise RuntimeError(f"Uygulamaya güvenli kapatma mesajı gönderilemedi (Win32={error}).")
    return f"{process_name} uygulamasına güvenli kapatma isteği gönderildi."


def execute_text_command(text: str) -> str:
    command = normalize_text_command(text)
    if not command:
        return "Önce bir komut yaz."
    if command in {"bilgisayari kilitle", "bilgisayarı kilitle", "ekrani kilitle", "oturumu kilitle", "kilitle", "lock pc"}:
        return lock_workstation()
    if command in {"uygulamayi kapat", "uygulamayı kapat", "aktif uygulamayi kapat", "aktif pencereyi kapat", "pencereyi kapat", "close app"}:
        return close_active_app()

    if command in {
        "bilgisayar dosyalarini listele",
        "bilgisayardaki dosyalari listele",
        "dosyalari listele",
        "dosya listesini iste",
        "bilgisayar dosya listesini iste",
    }:
        return execute_list_files("")

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


def _path_inside_allowed_root(path: Path, roots: list[tuple[str, Path]]) -> Path | None:
    return next((root for _label, root in roots if path == root or root in path.parents), None)


def _safe_transfer_name(raw: object) -> str:
    if not isinstance(raw, str) or not raw.strip() or len(raw) > 255:
        raise ValueError("Dosya adı geçersiz.")
    name = raw.strip()
    if name in {".", ".."} or re.search(r'[<>:"/\\|?*\x00-\x1f]', name):
        raise ValueError("Dosya adı yol veya Windows tarafından desteklenmeyen karakter içeriyor.")
    if name.endswith(".") or name.endswith(" "):
        raise ValueError("Dosya adı nokta veya boşlukla bitemez.")
    base = name.split(".", 1)[0].upper()
    if base in {"CON", "PRN", "AUX", "NUL"} or re.fullmatch(r"(COM|LPT)[1-9]", base):
        raise ValueError("Bu Windows dosya adı kullanılamaz.")
    return name


def execute_upload_file(fields: dict) -> str:
    raw_destination = fields.get("destinationPath")
    if not isinstance(raw_destination, str) or not raw_destination.strip() or len(raw_destination) > FILE_LIST_MAX_PATH_LENGTH:
        raise ValueError("Hedef klasör yolu geçersiz.")
    requested = Path(raw_destination).expanduser()
    if _is_reparse_point(requested):
        raise ValueError("Sembolik bağlantı veya junction üzerinden dosya yükleme engellendi.")
    try:
        destination = requested.resolve(strict=True)
    except (OSError, RuntimeError, ValueError) as exc:
        raise ValueError("Hedef klasör bulunamadı.") from exc
    if not destination.is_dir():
        raise ValueError("Hedef yol bir klasör olmalı.")
    roots = _allowed_file_roots()
    if _path_inside_allowed_root(destination, roots) is None:
        raise ValueError("Bu klasöre erişim izni yok. Yalnızca izin verilen kullanıcı klasörlerine dosya yüklenebilir.")

    name = _safe_transfer_name(fields.get("name"))
    size = fields.get("size")
    if isinstance(size, bool) or not isinstance(size, int) or not 0 <= size <= FILE_UPLOAD_MAX_BYTES:
        raise ValueError(f"Dosya boyutu geçersiz; en fazla {FILE_UPLOAD_MAX_BYTES} bayt yüklenebilir.")
    expected_hash = fields.get("sha256")
    if not isinstance(expected_hash, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", expected_hash):
        raise ValueError("Dosyanın SHA-256 bilgisi geçersiz.")
    encoded = fields.get("contentBase64")
    max_encoded_length = ((FILE_UPLOAD_MAX_BYTES + 2) // 3) * 4 + 8
    if not isinstance(encoded, str) or len(encoded) > max_encoded_length:
        raise ValueError("Dosya içeriği geçersiz veya boyut sınırını aşıyor.")
    try:
        content = base64.b64decode(encoded, validate=True)
    except (ValueError, binascii.Error) as exc:
        raise ValueError("Dosya içeriği geçerli Base64 değil.") from exc
    if len(content) != size:
        raise ValueError("Dosya boyutu bildirilen değerle uyuşmuyor.")
    actual_hash = hashlib.sha256(content).hexdigest()
    if actual_hash.lower() != expected_hash.lower():
        raise ValueError("Dosyanın SHA-256 doğrulaması başarısız.")

    target = destination / name
    if target.exists() or target.is_symlink() or _is_reparse_point(target):
        raise ValueError("Hedefte aynı isimli dosya zaten var; üzerine yazılmadı.")
    created = False
    try:
        with target.open("xb") as stream:
            created = True
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
    except FileExistsError as exc:
        raise ValueError("Hedefte aynı isimli dosya zaten var; üzerine yazılmadı.") from exc
    except OSError as exc:
        if created:
            try:
                target.unlink(missing_ok=True)
            except OSError:
                pass
        raise ValueError("Dosya hedef klasöre yazılamadı; disk ve izinleri kontrol et.") from exc
    return f"Dosya yüklendi ve SHA-256 doğrulandı: {name} ({len(content)} bayt)."


def execute_zip_folder(raw_path: str = "") -> str:
    if not isinstance(raw_path, str) or not raw_path.strip() or len(raw_path) > FILE_LIST_MAX_PATH_LENGTH:
        raise ValueError("ZIP yapılacak klasör yolu geçersiz.")
    requested = Path(raw_path).expanduser()
    if _is_reparse_point(requested):
        raise ValueError("Sembolik bağlantı veya junction içeren klasör ZIP yapılamaz.")
    try:
        folder = requested.resolve(strict=True)
    except (OSError, RuntimeError, ValueError) as exc:
        raise ValueError("ZIP yapılacak klasör bulunamadı.") from exc
    if not folder.is_dir():
        raise ValueError("ZIP işlemi için klasör seçmelisin.")
    roots = _allowed_file_roots()
    root = _path_inside_allowed_root(folder, roots)
    if root is None:
        raise ValueError("Bu klasöre erişim izni yok. Yalnızca izin verilen kullanıcı klasörleri ZIP yapılabilir.")

    files: list[Path] = []
    total_size = 0

    def collect(directory: Path) -> None:
        nonlocal total_size
        with os.scandir(directory) as entries:
            for item in entries:
                candidate = Path(item.path)
                if _is_reparse_point(candidate):
                    continue
                try:
                    resolved = candidate.resolve(strict=True)
                    if not (resolved == root or root in resolved.parents):
                        continue
                    if item.is_dir(follow_symlinks=False):
                        collect(resolved)
                    elif item.is_file(follow_symlinks=False):
                        total_size += int(item.stat(follow_symlinks=False).st_size)
                        if total_size > ZIP_MAX_INPUT_BYTES:
                            raise ValueError(f"Klasör ZIP için {ZIP_MAX_INPUT_BYTES} bayt sınırını aşıyor.")
                        files.append(resolved)
                        if len(files) > ZIP_MAX_FILES:
                            raise ValueError(f"Klasör ZIP için {ZIP_MAX_FILES} dosya sınırını aşıyor.")
                except (OSError, RuntimeError):
                    continue

    try:
        collect(folder)
    except ValueError:
        raise
    except OSError as exc:
        raise ValueError("Klasör içeriği okunamadı; izinleri kontrol et.") from exc

    output_directory = folder.parent if _path_inside_allowed_root(folder.parent, roots) is not None else folder
    target = output_directory / _safe_transfer_name(folder.name + ".zip")
    if target.exists() or target.is_symlink() or _is_reparse_point(target):
        raise ValueError("Aynı isimli ZIP dosyası zaten var; üzerine yazılmadı.")
    temporary = output_directory / f".mynassistant-zip-{uuid.uuid4().hex}.tmp"
    target_created = False
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for source in files:
                try:
                    resolved = source.resolve(strict=True)
                    if _is_reparse_point(source) or not (resolved == root or root in resolved.parents):
                        continue
                    archive.write(resolved, str(resolved.relative_to(folder)).replace("\\", "/"))
                except (OSError, RuntimeError, ValueError):
                    continue
        with temporary.open("rb") as source, target.open("xb") as output:
            target_created = True
            while True:
                chunk = source.read(1024 * 1024)
                if not chunk:
                    break
                output.write(chunk)
            output.flush()
            os.fsync(output.fileno())
    except FileExistsError as exc:
        if target_created:
            try:
                target.unlink(missing_ok=True)
            except OSError:
                pass
        raise ValueError("Aynı isimli ZIP dosyası zaten var; üzerine yazılmadı.") from exc
    except Exception as exc:
        if target_created:
            try:
                target.unlink(missing_ok=True)
            except OSError:
                pass
        if isinstance(exc, ValueError):
            raise
        raise ValueError("ZIP oluşturulamadı; disk alanını ve klasör izinlerini kontrol et.") from exc
    finally:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass
    return f"ZIP oluşturuldu: {target.name} ({len(files)} dosya, {target.stat().st_size} bayt)."


def execute_command(command: str, fields: dict) -> str:
    command = command.strip().lower()
    if command == "text_command":
        return execute_text_command(str(fields.get("text") or ""))
    if command == "list_files":
        return execute_list_files(str(fields.get("path") or ""))
    if command == "download_file":
        return execute_download_file(str(fields.get("path") or ""))
    if command == "upload_file":
        return execute_upload_file(fields)
    if command == "zip_folder":
        return execute_zip_folder(str(fields.get("path") or ""))
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
    if command == "lock_pc":
        return lock_workstation()
    if command in {"close_app", "close_active_app"}:
        return close_active_app()
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
    return f"Desteklenmeyen komut: {command}"


def command_is_supported(command: str) -> bool:
    return command in {
        "mute", "volume_up", "volume_down", "home",
        "mouse_move", "mouse_click", "mouse_right_click", "lock_pc", "close_app", "text_command",
        "type_text", "key_backspace", "key_enter", "list_files", "download_file", "upload_file", "zip_folder"
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
                for document in fetch_owner_commands(session, device_id):
                    fields = decode_document(document)
                    if fields.get("deviceId") != device_id or fields.get("status") != "pending":
                        continue
                    expiration_reason = command_expiration_reason(fields)
                    if expiration_reason is not None:
                        try:
                            update_command(
                                session,
                                document,
                                "failed",
                                expiration_reason,
                                expected_update_time=document.get("updateTime"),
                            )
                        except Exception as expiry_error:
                            print(f"[Komut] Süresi dolmuş komutun durumu güncellenemedi; çalıştırılmadı: {expiry_error}")
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
