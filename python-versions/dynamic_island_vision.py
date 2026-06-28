"""
dynamic_island_vision.py  –  Dynamic Island with optional camera person-detection.

SETUP
─────
  python -m venv .venv
  .venv\\Scripts\\activate
  pip install -r requirements-vision.txt
  python dynamic_island_vision.py

The original DynamicIsland_Premium.py is left completely untouched.

VISION TUNING (top of file)
─────────────────────────────
  PERSON_CONFIDENCE  – Detection sensitivity (default 0.45, range 0.1–1.0).
                       Maps to Haar cascade minNeighbors (× 6, min 2).
                       Raise to reduce false positives; lower to catch more.
  DETECT_INTERVAL_S  – Seconds between inference runs (default 0.40 ≈ 2.5/sec).
  DETECT_CONFIRM     – Consecutive detections required to switch dot → red (default 2).
  CLEAR_CONFIRM      – Consecutive non-detections required to switch dot → green (default 3).
  PREVIEW_REFRESH_HZ – Live-preview FPS cap in expanded view (default 15).
  SHOW_BBOXES        – True = overlay HOG detection rectangles on preview (debug only).
"""
from __future__ import annotations
import asyncio, ctypes, ctypes.wintypes as wt, os, shutil, subprocess, sys, threading, time
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import Optional, Tuple

import tkinter as tk
import tkinter.font as tkfont
from PIL import Image, ImageDraw, ImageTk

# ── Optional heavy imports ─────────────────────────────────────────────────────
try:
    import cv2
    import numpy as np
    HAS_CV2 = True
except ImportError:
    HAS_CV2 = False

try:
    import os as _os
    _os.environ.setdefault("TF_CPP_MIN_LOG_LEVEL", "3")   # silence TF/oneDNN spam
    _os.environ.setdefault("TF_ENABLE_ONEDNN_OPTS", "0")
    import mediapipe as mp
    _mp_face_detection = mp.solutions.face_detection
    _mp_face_mesh      = mp.solutions.face_mesh
    HAS_MEDIAPIPE = True
except ImportError:
    HAS_MEDIAPIPE = False

try:
    import psutil
    HAS_PSUTIL = True
except Exception:
    HAS_PSUTIL = False

try:
    from winsdk.windows.media.control import (
        GlobalSystemMediaTransportControlsSessionManager as MediaManager,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus as PlaybackStatus,
    )
    HAS_SMTC = True
except Exception:
    HAS_SMTC = False

HAS_PYCAW = False
try:
    from comtypes import CLSCTX_ALL  # type: ignore
    from pycaw.pycaw import AudioUtilities, IAudioEndpointVolume  # type: ignore
    HAS_PYCAW = True
except Exception:
    pass

# ── Window / layout constants ──────────────────────────────────────────────────
TRANSPARENT_KEY        = "#010203"
WINDOW_OPACITY         = 0.97
SHOW_SECONDS           = True
USE_24H                = False
TOP_MARGIN             = 10         # px distance from top of screen
COLLAPSED_H            = 42         # window height when collapsed
EXPANDED_H             = 272        # camera + privacy + timer rows
COLLAPSED_W_MIN        = 220
COLLAPSED_W_MAX        = 520
EXPANDED_W             = 760
RADIUS                 = 22
INNER_PAD_X            = 14
INNER_PAD_Y            = 10
HOVER_EXPAND_DELAY_S   = 0.14
HOVER_COLLAPSE_DELAY_S = 0.30
INTERACTION_HOLD_S     = 1.20
FRAME_MS               = 16
MEDIA_POLL_MS          = 240
BATTERY_POLL_S         = 5.0
THEME_POLL_S           = 2.5
META_HOLD_S            = 8.0
VOL_STEP               = 2

# ── Vision constants ───────────────────────────────────────────────────────────
CAMERA_INDEX              = 0      # cv2.VideoCapture device index
PERSON_CONFIDENCE         = 0.50   # MediaPipe min_detection_confidence (0–1)
                                   # Haar: maps to minNeighbors (×6, min 2)
DETECT_INTERVAL_S         = 0.15   # Haar polling interval (~6/sec); MediaPipe runs faster
DETECT_CONFIRM            = 1      # consecutive hits to flip dot → red/alert
CLEAR_CONFIRM             = 2      # consecutive misses to flip dot → green/safe
PREVIEW_REFRESH_HZ        = 15     # max FPS for in-island live preview
PREVIEW_W_PX              = 128    # preview canvas width  (logical px at 96 DPI)
PREVIEW_H_PX              = 72     # preview canvas height (logical px at 96 DPI)
SHOW_BBOXES               = True   # draw white boxes around detected faces

# ── Privacy ("not me") mode ───────────────────────────────────────────────────
# With MediaPipe: cosine similarity of normalised face-mesh landmark vectors.
# Landmarks capture actual face geometry (eye spacing, jaw shape, nose bridge)
# so lighting and distance don't affect it the way pixel histograms do.
PRIVACY_LANDMARK_THRESH   = 0.950  # cosine sim ≥ this → same person (tune ±0.005)
PRIVACY_UNKNOWN_FRAMES    = 3      # consecutive "unknown" frames before alerting
# Haar / no-MediaPipe fallback: histogram correlation (much less reliable)
PRIVACY_HIST_THRESH       = 0.55   # lower = more permissive
ENROLLMENT_FRAMES         = 100    # total landmark samples (5 poses × 20)

# ── Face ID guided enrollment ─────────────────────────────────────────────────
# Each pose: (key, instruction_label, yaw_target, pitch_target)
ENROLLMENT_POSES = [
    ("center", "Look straight",   0.00,  0.00),
    ("right",  "Turn right  →",   0.17,  0.00),
    ("left",   "←  Turn left",   -0.17,  0.00),
    ("up",     "Tilt up",          0.00, -0.12),
    ("down",   "Tilt down",        0.00,  0.12),
]
SAMPLES_PER_POSE  = ENROLLMENT_FRAMES // len(ENROLLMENT_POSES)  # 20

# ── Status-dot colours ────────────────────────────────────────────────────────
DOT_RED   = "#FF3B30"   # person detected / unknown face in privacy mode
DOT_GREEN = "#30D158"   # no person / owner detected (safe)
DOT_GRAY  = "#636366"   # camera off / initialising / unavailable / error
DOT_SZ    = 7           # dot diameter in logical px (smaller, left-side)


# ── Utility helpers ────────────────────────────────────────────────────────────

def clamp(v: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, v))


def dpi_scale(hwnd: int) -> float:
    try:
        u = ctypes.windll.user32
        try:
            u.GetDpiForWindow.restype = ctypes.c_uint
            dpi = u.GetDpiForWindow(hwnd)
        except Exception:
            u.GetDpiForSystem.restype = ctypes.c_uint
            dpi = u.GetDpiForSystem()
        return max(0.8, float(dpi) / 96.0)
    except Exception:
        return 1.0


def disable_corner_rounding(hwnd: int) -> None:
    try:
        dwm = ctypes.windll.dwmapi
        attr = wt.DWORD(33)
        val = wt.DWORD(1)
        dwm.DwmSetWindowAttribute(wt.HWND(hwnd), attr, ctypes.byref(val), ctypes.sizeof(val))
    except Exception:
        pass


def system_theme() -> str:
    if sys.platform != "win32":
        return "dark"
    try:
        import winreg
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
        ) as key:
            value, _ = winreg.QueryValueEx(key, "AppsUseLightTheme")
        return "light" if int(value) == 1 else "dark"
    except Exception:
        return "dark"


def time_text(now: datetime, show_seconds: bool, use_24h: bool) -> str:
    if use_24h:
        return now.strftime("%H:%M:%S" if show_seconds else "%H:%M")
    s = now.strftime("%I:%M:%S %p" if show_seconds else "%I:%M %p")
    return s[1:] if s.startswith("0") else s


def date_text(now: datetime) -> str:
    return now.strftime("%a, %b %#d" if sys.platform == "win32" else "%a, %b %-d")


def to_secs(v) -> float:
    try:
        if isinstance(v, timedelta):
            return max(0.0, v.total_seconds())
        return float(v) if v is not None else 0.0
    except Exception:
        return 0.0


def pick_font(root: tk.Tk) -> str:
    try:
        fams = set(tkfont.families(root))
    except Exception:
        return "Segoe UI"
    for n in ("SF Pro Text", "SF Pro Display", "Segoe UI Variable Text",
              "Segoe UI Variable", "Segoe UI"):
        if n in fams:
            return n
    return "Segoe UI"


def ellipsize(text: str, font: tkfont.Font, max_px: int) -> str:
    t = (text or "").strip()
    if not t or max_px <= 0:
        return ""
    if font.measure(t) <= max_px:
        return t
    suffix = "..."
    if font.measure(suffix) >= max_px:
        return ""
    lo, hi = 0, len(t)
    while lo < hi:
        mid = (lo + hi + 1) // 2
        c = t[:mid].rstrip() + suffix
        if font.measure(c) <= max_px:
            lo = mid
        else:
            hi = mid - 1
    return t[:lo].rstrip() + suffix


# ── Palette / colours ──────────────────────────────────────────────────────────

@dataclass(frozen=True)
class Palette:
    name: str
    panel: str
    panel_border: str
    panel_highlight: str
    text: str
    text_secondary: str
    text_muted: str
    chip: str
    chip_hover: str
    chip_press: str
    progress_track: str
    progress_fill: str


DARK = Palette("dark",  "#111215", "#282C34", "#22262E",
               "#F5F7FA", "#D1D5DE", "#8D93A0",
               "#1A1E25", "#242A34", "#303745",
               "#2A2F38", "#F5F7FA")

LIGHT = Palette("light", "#F7F8FB", "#D8DCE4", "#FFFFFF",
                "#14161A", "#4D5562", "#7A8391",
                "#ECEFF4", "#E1E6EE", "#D5DCE7",
                "#D6DBE3", "#1B2027")


# ── Media data ────────────────────────────────────────────────────────────────

@dataclass
class MediaSnapshot:
    title: str    = ""
    artist: str   = ""
    app: str      = ""
    paused: bool  = False
    can_prev: bool = False
    can_next: bool = False
    can_pp: bool  = True
    position: float = 0.0
    duration: float = 0.0
    rate: float   = 1.0
    available: bool = False


# ── Services ──────────────────────────────────────────────────────────────────

class MediaService:
    def __init__(self, poll_ms: int = MEDIA_POLL_MS):
        self.poll_ms = max(120, int(poll_ms))
        self._state = MediaSnapshot()
        self._lock = threading.Lock()
        self._running = True
        self._session_cache = None
        self._last_meta = ("", "", "")
        self._last_meta_at = 0.0
        self._loop = None
        if HAS_SMTC:
            self._loop = asyncio.new_event_loop()
            threading.Thread(target=self._run_loop, daemon=True).start()
            asyncio.run_coroutine_threadsafe(self._poll(), self._loop)

    def _run_loop(self):
        asyncio.set_event_loop(self._loop)
        self._loop.run_forever()

    def shutdown(self):
        self._running = False
        if self._loop:
            try:
                self._loop.call_soon_threadsafe(self._loop.stop)
            except Exception:
                pass

    def snapshot(self) -> MediaSnapshot:
        with self._lock:
            return MediaSnapshot(**self._state.__dict__)

    def _set(self, s: MediaSnapshot):
        with self._lock:
            self._state = s

    async def _manager(self):
        try:
            return await MediaManager.request_async()
        except Exception:
            return None

    async def _props(self, session) -> Tuple[str, str]:
        for i in range(4):
            try:
                p = await session.try_get_media_properties_async()
                title      = (getattr(p, "title",        "") or "").strip()
                subtitle   = (getattr(p, "subtitle",     "") or "").strip()
                album      = (getattr(p, "album_title",  "") or "").strip()
                artist     = (getattr(p, "artist",       "") or "").strip()
                album_art  = (getattr(p, "album_artist", "") or "").strip()
                if not title:
                    title = subtitle or album
                if not artist:
                    artist = album_art
                if title or artist:
                    return title, artist
            except Exception:
                pass
            if i < 3:
                await asyncio.sleep(0.05)
        return "", ""

    def _map_app(self, aumid: str) -> str:
        if not aumid:
            return ""
        s = aumid.lower()
        if "spotify" in s: return "Spotify"
        if "vlc"     in s: return "VLC"
        if "zune" in s or "music" in s: return "Media"
        if "chrome"  in s: return "Chrome"
        if "edge"    in s: return "Edge"
        base = aumid.split("_")[0]
        return base.split(".")[-1].title()

    async def _pick_session(self, mgr):
        try:
            current = mgr.get_current_session()
        except Exception:
            current = None
        try:
            sessions = list(mgr.get_sessions())
        except Exception:
            sessions = []
        best, rank_best = None, None
        for idx, ses in enumerate([current] + sessions):
            if ses is None:
                continue
            try:
                info   = ses.get_playback_info()
                status = getattr(info, "playback_status", None)
                score  = 0
                if status == PlaybackStatus.PLAYING: score += 40
                elif status == PlaybackStatus.PAUSED:  score += 25
                elif status == PlaybackStatus.OPENED:  score += 18
                elif status == PlaybackStatus.STOPPED: score += 6
                t, a = await self._props(ses)
                if t: score += 24
                if a: score += 12
                if idx == 0: score += 4
                has  = bool(t or a)
                rank = (1 if has else 0, score, -idx)
            except Exception:
                continue
            if rank_best is None or rank > rank_best:
                best, rank_best = ses, rank
        return best

    async def _poll(self):
        while self._running:
            snap = MediaSnapshot()
            try:
                mgr = await self._manager()
                ses = await self._pick_session(mgr) if mgr else None
                self._session_cache = ses
                if ses:
                    title, artist = await self._props(ses)
                    app = ""
                    paused = False
                    can_prev = can_next = False
                    can_pp = True
                    pos = dur = 0.0
                    rate = 1.0
                    try:
                        info   = ses.get_playback_info()
                        st     = info.playback_status
                        paused = st in (PlaybackStatus.PAUSED, PlaybackStatus.STOPPED)
                        ctrls  = info.controls
                        can_prev = bool(getattr(ctrls, "is_previous_enabled",         False))
                        can_next = bool(getattr(ctrls, "is_next_enabled",              False))
                        can_pp   = bool(getattr(ctrls, "is_play_pause_toggle_enabled", True)
                                        or getattr(ctrls, "is_play_enabled",           True))
                        rate = float(getattr(info, "playback_rate", 1.0) or 1.0)
                    except Exception:
                        pass
                    try:
                        tl  = ses.get_timeline_properties()
                        pos = to_secs(getattr(tl, "position",  0.0))
                        dur = to_secs(getattr(tl, "end_time",  0.0))
                        if dur <= 0:
                            dur = to_secs(getattr(tl, "max_seek_time", 0.0))
                    except Exception:
                        pass
                    try:
                        app = self._map_app(str(ses.source_app_user_model_id))
                    except Exception:
                        app = ""
                    now = time.monotonic()
                    if title or artist:
                        self._last_meta    = (title, artist, app)
                        self._last_meta_at = now
                    else:
                        if (now - self._last_meta_at) <= META_HOLD_S and (
                                self._last_meta[0] or self._last_meta[1]):
                            title, artist = self._last_meta[0], self._last_meta[1]
                            if not app:
                                app = self._last_meta[2]
                    snap = MediaSnapshot(title, artist, app, paused,
                                         can_prev, can_next, can_pp,
                                         pos, dur, rate, True)
            except Exception:
                snap = MediaSnapshot()
            self._set(snap)
            await asyncio.sleep(max(0.12, self.poll_ms / 1000.0))

    def play_pause(self):
        if HAS_SMTC and self._loop:
            asyncio.run_coroutine_threadsafe(self._control("pp"), self._loop)

    def next(self):
        if HAS_SMTC and self._loop:
            asyncio.run_coroutine_threadsafe(self._control("next"), self._loop)

    def prev(self):
        if HAS_SMTC and self._loop:
            asyncio.run_coroutine_threadsafe(self._control("prev"), self._loop)

    async def _control(self, which: str):
        try:
            mgr = await self._manager()
            ses = self._session_cache or (mgr.get_current_session() if mgr else None)
        except Exception:
            ses = None
        if not ses:
            return
        try:
            if which == "pp":
                try:
                    await ses.try_toggle_play_pause_async()
                except Exception:
                    if self.snapshot().paused:
                        await ses.try_play_async()
                    else:
                        await ses.try_pause_async()
            elif which == "next":
                await ses.try_skip_next_async()
            elif which == "prev":
                await ses.try_skip_previous_async()
        except Exception:
            pass


class PowerService:
    def __init__(self, poll_s: float = BATTERY_POLL_S):
        self.poll_s   = max(2.0, float(poll_s))
        self._running = True
        self._percent = None
        self._charging = False
        self._lock = threading.Lock()
        threading.Thread(target=self._loop, daemon=True).start()

    def shutdown(self):
        self._running = False

    def snapshot(self) -> Tuple[Optional[int], bool]:
        with self._lock:
            return self._percent, self._charging

    def _loop(self):
        while self._running:
            p, c = None, False
            if HAS_PSUTIL:
                try:
                    b = psutil.sensors_battery()
                    if b is not None:
                        p = int(round(float(b.percent)))
                        c = bool(b.power_plugged)
                except Exception:
                    pass
            with self._lock:
                self._percent, self._charging = p, c
            time.sleep(self.poll_s)


class VolumeService:
    def __init__(self):
        self.ok = HAS_PYCAW
        self.endpoint = None
        if not self.ok:
            return
        try:
            dev   = AudioUtilities.GetSpeakers()
            iface = dev.Activate(IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
            self.endpoint = ctypes.cast(iface, ctypes.POINTER(IAudioEndpointVolume))
        except Exception:
            self.ok = False

    def get(self) -> Optional[int]:
        if not self.ok or self.endpoint is None:
            return None
        try:
            return int(round(float(self.endpoint.GetMasterVolumeLevelScalar()) * 100))
        except Exception:
            return None

    def set(self, pct: int):
        if not self.ok or self.endpoint is None:
            return
        try:
            self.endpoint.SetMasterVolumeLevelScalar(int(clamp(pct, 0, 100)) / 100.0, None)
        except Exception:
            pass

    def step(self, delta: int):
        cur = self.get()
        if cur is None:
            return
        self.set(cur + int(delta))


class CameraService:
    """
    Background webcam worker: capture + face detection + optional "not me" mode.

    Detection backend (auto-selected at startup):
      • MediaPipe Face Detection  – when mediapipe is installed (~5 ms/frame,
        works at angles, highly recommended).
      • OpenCV Haar cascade       – built-in fallback (~20–60 ms/frame).

    Privacy / "not me" mode
    ───────────────────────
    1. User enables privacy mode and clicks "Register my face".
    2. Camera captures ENROLLMENT_FRAMES face samples → stores histograms.
    3. Every subsequent detection compares detected faces to the stored
       histograms.  If any face fails to match the owner → privacy_alert=True.

    Dot semantics in privacy mode
      RED   = unknown face detected
      GREEN = owner's face (or nobody) → safe
      GRAY  = off / not enrolled / error
    """

    def __init__(
        self,
        camera_index:   int   = CAMERA_INDEX,
        confidence:     float = PERSON_CONFIDENCE,
        detect_confirm: int   = DETECT_CONFIRM,
        clear_confirm:  int   = CLEAR_CONFIRM,
    ) -> None:
        self._index  = camera_index
        self._conf   = confidence
        self._det_k  = detect_confirm
        self._clr_k  = clear_confirm

        self._lock     = threading.Lock()
        self._stop_evt = threading.Event()

        # ── Shared state (camera thread → UI thread) ─────────────────────────
        self._status: str  = "off"
        self._stable: bool = False
        self._frame        = None
        self._last_rects: list = []

        # ── Privacy / "not me" state ─────────────────────────────────────────
        self._privacy_mode:        bool  = False
        self._enrolled:            bool  = False
        self._enrolling:           bool  = False
        # Phase-based (Face ID style) enrollment
        self._enroll_phase:        int   = 0   # index into ENROLLMENT_POSES
        self._enroll_phase_count:  int   = 0   # samples captured for current pose
        self._enroll_phases_done:  list  = []  # completed pose keys
        self._enroll_count:        int   = 0   # total samples so far
        self._enrolled_embeddings: list  = []  # normalised np arrays (MediaPipe)
        self._enrolled_hists:      list  = []  # fallback histograms (Haar)
        self._privacy_alert:       bool  = False
        self._privacy_unknown_streak: int = 0   # consecutive unknown-face frames

        # ── Stabilisation counters (camera thread only) ───────────────────────
        self._hits   = 0
        self._misses = 0

    # ── Public API (UI thread) ────────────────────────────────────────────────

    def enable(self) -> None:
        with self._lock:
            if self._status not in ("off", "error"):
                return
            self._status = "initialising"
            self._stable = False
            self._frame  = None
        self._stop_evt.clear()
        threading.Thread(target=self._loop, daemon=True, name="cam-capture").start()

    def disable(self) -> None:
        self._stop_evt.set()
        with self._lock:
            self._status       = "off"
            self._stable       = False
            self._frame        = None
            self._last_rects   = []
            self._privacy_alert = False

    def shutdown(self) -> None:
        self.disable()

    def get_state(self) -> Tuple[str, bool, Optional[object]]:
        """(status, person_detected_stable, frame_bgr)"""
        with self._lock:
            return self._status, self._stable, self._frame

    def get_last_rects(self) -> list:
        with self._lock:
            return list(self._last_rects)

    def get_privacy_state(self):
        """(privacy_mode, enrolled, privacy_alert, enrolling, enroll_count,
            enroll_phase, enroll_phase_count, enroll_phases_done)"""
        with self._lock:
            return (self._privacy_mode, self._enrolled,
                    self._privacy_alert, self._enrolling, self._enroll_count,
                    self._enroll_phase, self._enroll_phase_count,
                    list(self._enroll_phases_done))

    def set_privacy_mode(self, enabled: bool) -> None:
        with self._lock:
            self._privacy_mode = enabled
            if not enabled:
                self._privacy_alert = False

    def start_enrollment(self) -> bool:
        """Begin Face ID-style guided enrollment.  Returns False if camera not active."""
        with self._lock:
            if self._status != "active":
                return False
            self._enrolling           = True
            self._enroll_phase        = 0
            self._enroll_phase_count  = 0
            self._enroll_phases_done  = []
            self._enroll_count        = 0
            self._enrolled_embeddings = []
            self._enrolled_hists      = []
            self._enrolled            = False
            self._privacy_alert       = False
        return True

    def clear_enrollment(self) -> None:
        with self._lock:
            self._enrolled                = False
            self._enrolling               = False
            self._enroll_phase            = 0
            self._enroll_phase_count      = 0
            self._enroll_phases_done      = []
            self._enroll_count            = 0
            self._enrolled_embeddings     = []
            self._enrolled_hists          = []
            self._privacy_alert           = False
            self._privacy_unknown_streak  = 0

    # ── Camera thread ─────────────────────────────────────────────────────────

    def _loop(self) -> None:
        cap       = None
        mp_det    = None
        mp_mesh   = None    # Face Mesh: used for landmark-based recognition
        face_cas  = None
        upper_cas = None
        try:
            cap = cv2.VideoCapture(self._index, cv2.CAP_DSHOW)
            cap.set(cv2.CAP_PROP_FRAME_WIDTH,  640)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
            cap.set(cv2.CAP_PROP_FPS,          15)

            if not cap.isOpened():
                with self._lock:
                    self._status = "error"
                return

            with self._lock:
                self._status = "active"

            if HAS_MEDIAPIPE:
                mp_det  = _mp_face_detection.FaceDetection(
                    model_selection=0,
                    min_detection_confidence=max(0.3, self._conf),
                )
                mp_mesh = _mp_face_mesh.FaceMesh(
                    max_num_faces=4,
                    refine_landmarks=False,
                    min_detection_confidence=0.5,
                    min_tracking_confidence=0.5,
                )
            else:
                face_cas  = cv2.CascadeClassifier(
                    cv2.data.haarcascades + "haarcascade_frontalface_default.xml")
                upper_cas = cv2.CascadeClassifier(
                    cv2.data.haarcascades + "haarcascade_upperbody.xml")

            last_det  = 0.0
            interval  = 0.08 if HAS_MEDIAPIPE else DETECT_INTERVAL_S
            self._hits = self._misses = 0

            while not self._stop_evt.is_set():
                ret, frame = cap.read()
                if not ret:
                    time.sleep(0.04)
                    continue

                frame = cv2.flip(frame, 1)
                with self._lock:
                    self._frame = frame.copy()

                now = time.monotonic()
                if (now - last_det) >= interval:
                    last_det = now
                    self._run(frame, mp_det, mp_mesh, face_cas, upper_cas)

        except Exception:
            with self._lock:
                self._status = "error"
        finally:
            for obj in (mp_det, mp_mesh):
                if obj is not None:
                    try: obj.close()
                    except Exception: pass
            if cap is not None:
                try: cap.release()
                except Exception: pass
            with self._lock:
                if self._status == "active":
                    self._status = "off"

    # ── Detection helpers (camera thread only) ────────────────────────────────

    @staticmethod
    def _head_pose(mesh_landmarks) -> tuple:
        """
        Returns (yaw, pitch) in normalised units from Face Mesh landmarks.
          yaw   > 0 → looking right;  yaw   < 0 → looking left
          pitch < 0 → looking up;     pitch > 0 → looking down
        """
        try:
            nose     = mesh_landmarks[1]
            l_eye    = mesh_landmarks[33]
            r_eye    = mesh_landmarks[263]
            forehead = mesh_landmarks[10]
            chin     = mesh_landmarks[152]

            cx  = (l_eye.x + r_eye.x) / 2.0
            fw  = abs(r_eye.x - l_eye.x)
            fh  = abs(chin.y - forehead.y)
            mid_y = (forehead.y + chin.y) / 2.0

            yaw   = (nose.x - cx) / fw      if fw  > 0.01 else 0.0
            pitch = (nose.y - mid_y) / fh   if fh  > 0.01 else 0.0
            return float(yaw), float(pitch)
        except Exception:
            return 0.0, 0.0

    def _crop_16x9(self, frame):
        src_h, src_w = frame.shape[:2]
        tgt = 16 / 9;  src = src_w / src_h
        if src > tgt:
            cw = int(src_h * tgt);  x0 = (src_w - cw) // 2
            return frame[:, x0:x0 + cw]
        if src < tgt:
            ch = int(src_w / tgt);  y0 = (src_h - ch) // 2
            return frame[y0:y0 + ch, :]
        return frame

    def _find_faces(self, small_rgb, face_cas, upper_cas) -> list:
        """
        Returns [(x,y,w,h)…] in 320×180 canvas coords.
        small_rgb must already be a 320×180 RGB image (for MediaPipe path).
        """
        if HAS_MEDIAPIPE:
            # mp_det is called via the rgb image directly in _run
            return []   # filled in _run for MediaPipe path
        gray  = cv2.cvtColor(cv2.cvtColor(small_rgb, cv2.COLOR_RGB2BGR),
                              cv2.COLOR_BGR2GRAY)
        gray  = cv2.equalizeHist(gray)
        min_n = max(2, int(self._conf * 6))
        found = face_cas.detectMultiScale(
            gray, scaleFactor=1.1, minNeighbors=min_n,
            minSize=(24, 24), flags=cv2.CASCADE_SCALE_IMAGE)
        if len(found) > 0:
            return [tuple(r) for r in found]
        up = upper_cas.detectMultiScale(
            gray, scaleFactor=1.1, minNeighbors=max(2, min_n - 1),
            minSize=(40, 40), flags=cv2.CASCADE_SCALE_IMAGE)
        return [tuple(r) for r in up] if len(up) > 0 else []

    # ── Face landmark embedding (MediaPipe path) ──────────────────────────────

    @staticmethod
    def _landmark_embedding(mesh_landmarks) -> "np.ndarray | None":
        """
        Convert 468 Face Mesh landmarks to a normalised geometry vector.

        Normalisation: translate so the nose tip (landmark 1) is at origin,
        then scale so the inter-eye distance (landmarks 33↔263) = 1.
        The result is lighting- and distance-independent; only face shape matters.
        """
        try:
            pts = np.array([[lm.x, lm.y, lm.z]
                             for lm in mesh_landmarks], dtype=np.float32)  # (468, 3)
            # Centre on nose tip
            pts -= pts[1]
            # Scale by inter-outer-eye-corner distance
            scale = float(np.linalg.norm(pts[33] - pts[263]))
            if scale < 1e-6:
                return None
            pts /= scale
            return pts.flatten()           # 468×3 = 1404-dim vector
        except Exception:
            return None

    @staticmethod
    def _cosine_sim(a, b) -> float:
        na, nb = np.linalg.norm(a), np.linalg.norm(b)
        if na < 1e-9 or nb < 1e-9:
            return 0.0
        return float(np.dot(a, b) / (na * nb))

    def _is_owner_embedding(self, emb) -> bool:
        if not self._enrolled_embeddings or emb is None:
            return False
        return max(self._cosine_sim(e, emb)
                   for e in self._enrolled_embeddings) >= PRIVACY_LANDMARK_THRESH

    def _is_owner_hist(self, gray_320x180, x, y, w, h) -> bool:
        """Histogram fallback used when MediaPipe is not available."""
        x1, y1 = max(0, x), max(0, y)
        x2, y2 = min(319, x + w), min(179, y + h)
        if x2 <= x1 or y2 <= y1 or not self._enrolled_hists:
            return False
        roi = gray_320x180[y1:y2, x1:x2]
        if roi.size == 0:
            return False
        try:
            r    = cv2.resize(roi, (64, 64))
            hist = cv2.calcHist([r], [0], None, [64], [0, 256])
            cv2.normalize(hist, hist)
            return max(cv2.compareHist(h, hist, cv2.HISTCMP_CORREL)
                       for h in self._enrolled_hists) >= PRIVACY_HIST_THRESH
        except Exception:
            return False

    def _run(self, frame, mp_det, mp_mesh, face_cas, upper_cas) -> None:
        try:
            cropped   = self._crop_16x9(frame)
            small_bgr = cv2.resize(cropped, (320, 180))
            small_rgb = cv2.cvtColor(small_bgr, cv2.COLOR_BGR2RGB)

            # ── Face detection ────────────────────────────────────────────────
            faces: list = []
            if HAS_MEDIAPIPE and mp_det is not None:
                det_res = mp_det.process(small_rgb)
                if det_res.detections:
                    for d in det_res.detections:
                        bb = d.location_data.relative_bounding_box
                        faces.append((
                            max(0, int(bb.xmin   * 320)),
                            max(0, int(bb.ymin   * 180)),
                            max(1, int(bb.width  * 320)),
                            max(1, int(bb.height * 180)),
                        ))
            else:
                faces = self._find_faces(small_rgb, face_cas, upper_cas)

            person_now = len(faces) > 0

            # ── Face Mesh embeddings (MediaPipe only) ─────────────────────────
            face_embeddings: list = []  # one per detected face, in order
            if HAS_MEDIAPIPE and mp_mesh is not None and person_now:
                mesh_res = mp_mesh.process(small_rgb)
                if mesh_res.multi_face_landmarks:
                    for fl in mesh_res.multi_face_landmarks:
                        emb = self._landmark_embedding(fl.landmark)
                        if emb is not None:
                            face_embeddings.append(emb)

            # ── Read privacy flags ────────────────────────────────────────────
            with self._lock:
                do_enroll   = self._enrolling
                priv_on     = self._privacy_mode
                enrolled    = self._enrolled
                cur_phase   = self._enroll_phase

            # ── Enrollment capture (Face ID pose-guided) ──────────────────────
            if do_enroll and person_now:
                if HAS_MEDIAPIPE and face_embeddings and mesh_res.multi_face_landmarks:
                    landmarks = mesh_res.multi_face_landmarks[0].landmark
                    yaw, pitch = self._head_pose(landmarks)
                    pose_key, _, yaw_t, pitch_t = ENROLLMENT_POSES[cur_phase]

                    # Check whether the user is holding the required head angle
                    # Tolerance: ±0.09 yaw, ±0.09 pitch around the target
                    tol = 0.09
                    in_pose = (abs(yaw - yaw_t) < tol and abs(pitch - pitch_t) < tol)

                    if in_pose:
                        emb = face_embeddings[0]
                        with self._lock:
                            if self._enrolling:
                                self._enrolled_embeddings.append(emb)
                                self._enroll_phase_count += 1
                                self._enroll_count += 1
                                if self._enroll_phase_count >= SAMPLES_PER_POSE:
                                    self._enroll_phases_done.append(pose_key)
                                    self._enroll_phase      += 1
                                    self._enroll_phase_count = 0
                                    if self._enroll_phase >= len(ENROLLMENT_POSES):
                                        self._enrolling = False
                                        self._enrolled  = True
                else:
                    # Haar fallback: histogram (no pose guidance)
                    gray = cv2.cvtColor(small_bgr, cv2.COLOR_BGR2GRAY)
                    for face in faces[:1]:
                        x, y, w, h = face
                        x1, y1 = max(0,x), max(0,y)
                        x2, y2 = min(319,x+w), min(179,y+h)
                        roi = gray[y1:y2, x1:x2]
                        if roi.size == 0:
                            continue
                        r    = cv2.resize(roi, (64, 64))
                        hist = cv2.calcHist([r], [0], None, [64], [0, 256])
                        cv2.normalize(hist, hist)
                        with self._lock:
                            if self._enrolling:
                                self._enrolled_hists.append(hist)
                                self._enroll_count += 1
                                if self._enroll_count >= ENROLLMENT_FRAMES:
                                    self._enrolling = False
                                    self._enrolled  = True

            # ── Privacy comparison ────────────────────────────────────────────
            unknown_now = False
            if person_now and priv_on and enrolled:
                if HAS_MEDIAPIPE and face_embeddings:
                    for emb in face_embeddings:
                        if not self._is_owner_embedding(emb):
                            unknown_now = True
                            break
                else:
                    gray = cv2.cvtColor(small_bgr, cv2.COLOR_BGR2GRAY)
                    for face in faces:
                        if not self._is_owner_hist(gray, *face):
                            unknown_now = True
                            break

            # ── Stabilisation ─────────────────────────────────────────────────
            if person_now:
                self._hits += 1;  self._misses = 0
            else:
                self._misses += 1; self._hits  = 0

            with self._lock:
                self._last_rects = [tuple(f) for f in faces]
                if not self._stable and self._hits   >= self._det_k:
                    self._stable = True
                elif self._stable and self._misses >= self._clr_k:
                    self._stable = False
                if priv_on and enrolled and person_now:
                    if unknown_now:
                        self._privacy_unknown_streak += 1
                        # Only alert after N consecutive unknown frames —
                        # prevents a single tilted-head frame from triggering
                        if self._privacy_unknown_streak >= PRIVACY_UNKNOWN_FRAMES:
                            self._privacy_alert = True
                    else:
                        # Recognised immediately — clear alert right away
                        self._privacy_unknown_streak = 0
                        self._privacy_alert = False
                else:
                    self._privacy_unknown_streak = 0
                    self._privacy_alert = False
        except Exception:
            pass


# ── Timer service ─────────────────────────────────────────────────────────────

class TimerService:
    """Simple countdown timer.  All methods are thread-safe."""

    DEFAULT_S = 5 * 60   # 5-minute default

    def __init__(self):
        self._lock       = threading.Lock()
        self._total_s    = self.DEFAULT_S
        self._remaining  = float(self.DEFAULT_S)
        self._start_mono = 0.0
        self._running    = False
        self._done       = False

    def get(self) -> dict:
        with self._lock:
            rem = self._remaining
            if self._running:
                elapsed = time.monotonic() - self._start_mono
                rem = max(0.0, self._remaining - elapsed)
                if rem == 0.0:
                    self._running   = False
                    self._remaining = 0.0
                    self._done      = True
            ratio = 1.0 - rem / self._total_s if self._total_s > 0 else 0.0
            return {
                "running":   self._running,
                "done":      self._done,
                "remaining": rem,
                "total":     self._total_s,
                "ratio":     float(clamp(ratio, 0.0, 1.0)),
            }

    def start_pause(self):
        with self._lock:
            if self._done:
                # Restart
                self._remaining  = float(self._total_s)
                self._done       = False
                self._running    = True
                self._start_mono = time.monotonic()
            elif self._running:
                # Pause — freeze remaining
                elapsed          = time.monotonic() - self._start_mono
                self._remaining  = max(0.0, self._remaining - elapsed)
                self._running    = False
            elif self._remaining > 0:
                # Resume / start
                self._running    = True
                self._start_mono = time.monotonic()

    def reset(self):
        with self._lock:
            self._remaining  = float(self._total_s)
            self._running    = False
            self._done       = False

    def adjust(self, delta_s: int):
        """Add/subtract seconds from total (and reset remaining). No-op when running."""
        with self._lock:
            if self._running:
                return
            self._total_s   = max(10, self._total_s + int(delta_s))
            self._remaining = float(self._total_s)
            self._done      = False

    @staticmethod
    def fmt(seconds: float) -> str:
        s = int(seconds)
        return f"{s // 60}:{s % 60:02d}"


# ── Button widgets ────────────────────────────────────────────────────────────

class GlyphButton(tk.Label):
    """Styled label that behaves as a button.  Works for both glyph chars and text."""

    def __init__(self, app: "App", parent, text: str, command,
                 font: tkfont.Font, padx: int, pady: int):
        super().__init__(parent, text=text, font=font, bd=0,
                         highlightthickness=0, cursor="hand2",
                         padx=padx, pady=pady)
        self.app, self.command = app, command
        self.enabled = self.hover = self.pressed = True
        self.enabled = True
        self.hover   = False
        self.pressed = False
        self.bind("<Enter>",          self._enter)
        self.bind("<Leave>",          self._leave)
        self.bind("<ButtonPress-1>",  self._press)
        self.bind("<ButtonRelease-1>", self._release)
        self.refresh()

    def set_enabled(self, ok: bool):
        self.enabled = bool(ok)
        self.refresh()

    def set_text(self, text: str):
        self.configure(text=text)

    def refresh(self):
        p = self.app.palette
        if not self.enabled:
            fg, bg, cur = p.text_muted, p.chip, "arrow"
        elif self.pressed:
            fg, bg, cur = p.text, p.chip_press, "hand2"
        elif self.hover:
            fg, bg, cur = p.text, p.chip_hover, "hand2"
        else:
            fg, bg, cur = p.text_secondary, p.chip, "hand2"
        self.configure(fg=fg, bg=bg, cursor=cur)

    def _enter(self, _):
        self.hover = True
        self.refresh()

    def _leave(self, _):
        self.hover   = False
        self.pressed = False
        self.refresh()

    def _press(self, _):
        if not self.enabled:
            return
        self.pressed = True
        self.app.mark_interaction()
        self.refresh()

    def _release(self, e):
        if not self.enabled:
            self.pressed = False
            self.refresh()
            return
        was = self.pressed
        self.pressed = False
        self.refresh()
        inside = 0 <= e.x < self.winfo_width() and 0 <= e.y < self.winfo_height()
        if was and inside:
            try:
                self.command()
            except Exception:
                pass


# ── Main application ──────────────────────────────────────────────────────────

class App:
    def __init__(self, root: tk.Tk):
        self.root = root
        root.withdraw()
        root.overrideredirect(True)
        root.attributes("-topmost", True)
        root.attributes("-alpha", WINDOW_OPACITY)
        root.configure(bg=TRANSPARENT_KEY)
        try:
            root.attributes("-transparentcolor", TRANSPARENT_KEY)
        except Exception:
            pass
        disable_corner_rounding(root.winfo_id())

        self.scale         = dpi_scale(root.winfo_id())
        self.top_margin    = int(round(TOP_MARGIN    * self.scale))
        self.radius        = int(round(RADIUS        * self.scale))
        self.inner_pad_x   = int(round(INNER_PAD_X  * self.scale))
        self.inner_pad_y   = int(round(INNER_PAD_Y  * self.scale))
        self.collapsed_h   = int(round(COLLAPSED_H  * self.scale))
        self.expanded_h    = int(round(EXPANDED_H   * self.scale))
        self.collapsed_w_min = int(round(COLLAPSED_W_MIN * self.scale))
        self.collapsed_w_max = int(round(COLLAPSED_W_MAX * self.scale))
        self.expanded_w    = min(
            int(round(EXPANDED_W * self.scale)),
            int(root.winfo_screenwidth() * 0.9),
        )
        self.expanded_w = max(self.expanded_w, self.collapsed_w_min + int(180 * self.scale))

        # ── Services ────────────────────────────────────────────────────────
        self.media  = MediaService(MEDIA_POLL_MS)
        self.power  = PowerService(BATTERY_POLL_S)
        self.volume = VolumeService()
        self.camera = CameraService() if HAS_CV2 else None
        self.timer  = TimerService()

        # ── Theme ────────────────────────────────────────────────────────────
        self.theme_mode    = "auto"
        self.theme_name    = system_theme()
        self.palette       = DARK if self.theme_name == "dark" else LIGHT
        self.last_theme_poll = 0.0

        # ── Interaction / animation state ────────────────────────────────────
        self.mode          = "collapsed"
        self.inside_since  = None
        self.outside_since = time.monotonic()
        self.interaction_hold_until = 0.0

        self.collapsed_width_ema = float(max(self.collapsed_w_min,
                                              int(self.expanded_w * 0.42)))
        self.target_w  = self.collapsed_width_ema
        self.target_h  = float(self.collapsed_h)
        self.current_w = self.target_w
        self.current_h = self.target_h
        self.vel_w     = self.vel_h = 0.0
        self.progress_display = 0.0
        self.volume_cached    = None
        self.last_volume_poll = 0.0
        self.panel_cache      = {}
        self.expanded_visible = False

        # ── Vision UI state ──────────────────────────────────────────────────
        self._cam_enabled    = False
        self._privacy_mode   = False   # mirrors CameraService._privacy_mode
        self._last_preview_t = 0.0
        self._dot_color      = DOT_GRAY
        # Preview canvas pixel size (scaled with DPI)
        self._prev_w = int(round(PREVIEW_W_PX * self.scale))
        self._prev_h = int(round(PREVIEW_H_PX * self.scale))
        self._cam_photo_ref  = None  # keep ImageTk reference alive

        # ── Fonts ────────────────────────────────────────────────────────────
        fam = pick_font(root)
        self.f_lane  = tkfont.Font(family=fam, size=max(11, int(12 * self.scale)), weight="bold")
        self.f_title = tkfont.Font(family=fam, size=max(11, int(12 * self.scale)), weight="bold")
        self.f_sub   = tkfont.Font(family=fam, size=max(9,  int(10 * self.scale)))
        self.f_time  = tkfont.Font(family=fam, size=max(12, int(14 * self.scale)), weight="bold")
        self.f_meta  = tkfont.Font(family=fam, size=max(8,  int(9  * self.scale)))
        self.f_btn   = tkfont.Font(family=fam, size=max(10, int(11 * self.scale)), weight="bold")
        self.f_vol   = tkfont.Font(family=fam, size=max(9,  int(10 * self.scale)), weight="bold")

        # ── Root-level background label ──────────────────────────────────────
        self.bg = tk.Label(root, bg=TRANSPARENT_KEY, bd=0, highlightthickness=0)
        self.bg.place(x=0, y=0)

        # ── Content container (centred inside the pill) ───────────────────────
        self.content = tk.Frame(root, bd=0, highlightthickness=0)
        self.content.place(relx=0.5, rely=0.5, anchor="center")

        self._build_collapsed()
        self._build_expanded()

        # ── Keyboard shortcuts ────────────────────────────────────────────────
        root.bind("<Escape>",        lambda e: self.quit())
        root.bind("<space>",         lambda e: self._act_pp())
        root.bind("<Control-Right>", lambda e: self._act_next())
        root.bind("<Control-Left>",  lambda e: self._act_prev())
        root.bind("<Control-Up>",    lambda e: self._act_vol_up())
        root.bind("<Control-Down>",  lambda e: self._act_vol_down())

        self.apply_palette()
        self.show_expanded(False)
        self.apply_geometry(force=True)
        root.deiconify()
        root.after(FRAME_MS, self.tick)

    # ── Layout builders ───────────────────────────────────────────────────────

    def _build_collapsed(self):
        self.collapsed = tk.Frame(self.content, bd=0, highlightthickness=0)
        self.collapsed.place(relx=0.5, rely=0.5, anchor="center",
                              relwidth=1.0, relheight=1.0)
        self.v_collapsed = tk.StringVar(value="")
        self.lbl_collapsed = tk.Label(
            self.collapsed, textvariable=self.v_collapsed,
            font=self.f_lane, anchor="center", justify="center",
            bd=0, highlightthickness=0,
            padx=int(round(8 * self.scale)),
        )
        self.lbl_collapsed.pack(fill="both", expand=True)

    def _build_expanded(self):
        self.expanded = tk.Frame(self.content, bd=0, highlightthickness=0)
        self.expanded.columnconfigure(1, weight=1)
        px = int(round(10 * self.scale))
        py = int(round(6  * self.scale))

        # ── Row 0 col 0: transport + volume ──────────────────────────────────
        self.left = tk.Frame(self.expanded, bd=0, highlightthickness=0)
        self.left.grid(row=0, column=0, sticky="w",
                        padx=(px, int(round(6 * self.scale))), pady=py)

        self.transport = tk.Frame(self.left, bd=0, highlightthickness=0)
        self.transport.pack(anchor="w")
        bpx = int(round(8 * self.scale))
        bpy = int(round(4 * self.scale))
        self.btn_prev = GlyphButton(self, self.transport, "<<", self._act_prev, self.f_btn, bpx, bpy)
        self.btn_pp   = GlyphButton(self, self.transport, ">",  self._act_pp,   self.f_btn, bpx, bpy)
        self.btn_next = GlyphButton(self, self.transport, ">>", self._act_next, self.f_btn, bpx, bpy)
        self.btn_prev.pack(side="left")
        self.btn_pp.pack(  side="left", padx=(int(round(4 * self.scale)),) * 2)
        self.btn_next.pack(side="left")

        self.vol_chip = tk.Frame(self.left, bd=0, highlightthickness=0)
        self.vol_chip.pack(anchor="w", pady=(int(round(6 * self.scale)), 0))
        self.btn_vm = GlyphButton(self, self.vol_chip, "-", self._act_vol_down,
                                   self.f_btn, int(round(7 * self.scale)), bpy)
        self.v_vol  = tk.StringVar(value="--")
        self.lbl_vol = tk.Label(self.vol_chip, textvariable=self.v_vol, font=self.f_vol,
                                  bd=0, highlightthickness=0,
                                  padx=int(round(8 * self.scale)), pady=bpy)
        self.btn_vp = GlyphButton(self, self.vol_chip, "+", self._act_vol_up,
                                   self.f_btn, int(round(7 * self.scale)), bpy)
        self.btn_vm.pack(side="left")
        self.lbl_vol.pack(side="left")
        self.btn_vp.pack(side="left")

        # ── Row 0 col 1: title / subtitle / progress ──────────────────────────
        self.center = tk.Frame(self.expanded, bd=0, highlightthickness=0)
        self.center.grid(row=0, column=1, sticky="nsew",
                          padx=(int(round(8 * self.scale)),) * 2, pady=py)
        self.center.columnconfigure(0, weight=1)
        self.v_title = tk.StringVar(value="")
        self.v_sub   = tk.StringVar(value="")
        self.lbl_title = tk.Label(self.center, textvariable=self.v_title,
                                    font=self.f_title, anchor="w", justify="left",
                                    bd=0, highlightthickness=0)
        self.lbl_sub   = tk.Label(self.center, textvariable=self.v_sub,
                                    font=self.f_sub, anchor="w", justify="left",
                                    bd=0, highlightthickness=0)
        self.progress  = tk.Canvas(self.center, height=max(8, int(round(10 * self.scale))),
                                    bd=0, highlightthickness=0)
        self.lbl_title.grid(row=0, column=0, sticky="ew")
        self.lbl_sub.grid(  row=1, column=0, sticky="ew",
                             pady=(int(round(2 * self.scale)), int(round(6 * self.scale))))
        self.progress.grid( row=2, column=0, sticky="ew")

        # ── Row 0 col 2: clock / date / battery ───────────────────────────────
        self.right = tk.Frame(self.expanded, bd=0, highlightthickness=0)
        self.right.grid(row=0, column=2, sticky="e",
                         padx=(int(round(6 * self.scale)), px), pady=py)
        self.v_time = tk.StringVar(value="")
        self.v_date = tk.StringVar(value="")
        self.v_batt = tk.StringVar(value="")
        self.lbl_time = tk.Label(self.right, textvariable=self.v_time,
                                   font=self.f_time, anchor="e", justify="right",
                                   bd=0, highlightthickness=0)
        self.lbl_date = tk.Label(self.right, textvariable=self.v_date,
                                   font=self.f_meta, anchor="e", justify="right",
                                   bd=0, highlightthickness=0)
        self.lbl_batt = tk.Label(self.right, textvariable=self.v_batt,
                                   font=self.f_meta, anchor="e", justify="right",
                                   bd=0, highlightthickness=0)
        self.lbl_time.pack(anchor="e")
        self.lbl_date.pack(anchor="e", pady=(int(round(2 * self.scale)), 0))
        self.lbl_batt.pack(anchor="e", pady=(int(round(1 * self.scale)), 0))

        # ── Row 1: separator ───────────────────────────────────────────────────
        sep_gap = int(round(6 * self.scale))
        self._cam_sep = tk.Frame(self.expanded, height=1, bd=0, highlightthickness=0)
        self._cam_sep.grid(row=1, column=0, columnspan=3, sticky="ew",
                            padx=px, pady=(sep_gap, sep_gap // 2))

        # ── Row 2: camera section ──────────────────────────────────────────────
        self._cam_row = tk.Frame(self.expanded, bd=0, highlightthickness=0)
        self._cam_row.grid(row=2, column=0, columnspan=3, sticky="ew",
                            padx=px, pady=(0, int(round(2 * self.scale))))
        self._cam_row.columnconfigure(1, weight=1)

        # Camera toggle (left col, row 0)
        self._cam_toggle = GlyphButton(
            self, self._cam_row,
            "Enable camera",
            self._toggle_camera,
            self.f_meta,
            int(round(8 * self.scale)), int(round(3 * self.scale)),
        )
        self._cam_toggle.grid(row=0, column=0, sticky="w",
                               padx=(0, int(round(6 * self.scale))))

        # Preview canvas (centre col, rows 0-1, spans both camera rows)
        self._cam_preview_frame = tk.Frame(
            self._cam_row, bd=0, highlightthickness=1,
            highlightbackground=self.palette.panel_border,
            width=self._prev_w, height=self._prev_h,
        )
        self._cam_preview_frame.grid(row=0, column=1, rowspan=2,
                                      padx=int(round(4 * self.scale)))
        self._cam_preview_frame.grid_propagate(False)

        self._cam_preview = tk.Canvas(
            self._cam_preview_frame,
            width=self._prev_w, height=self._prev_h,
            bd=0, highlightthickness=0,
        )
        self._cam_preview.pack(fill="both", expand=True)

        # Detection status label (right col, row 0)
        self._v_cam_status = tk.StringVar(value="Camera off")
        self._lbl_cam_status = tk.Label(
            self._cam_row, textvariable=self._v_cam_status,
            font=self.f_meta, anchor="e", justify="right",
            bd=0, highlightthickness=0,
        )
        self._lbl_cam_status.grid(row=0, column=2, sticky="e",
                                   padx=(int(round(6 * self.scale)), 0))

        # ── Row 3: privacy ("not me") controls ─────────────────────────────
        # Privacy toggle (left col, row 1 inside cam_row)
        self._priv_toggle = GlyphButton(
            self, self._cam_row,
            "Privacy: off",
            self._toggle_privacy,
            self.f_meta,
            int(round(8 * self.scale)), int(round(3 * self.scale)),
        )
        self._priv_toggle.grid(row=1, column=0, sticky="w",
                                padx=(0, int(round(6 * self.scale))),
                                pady=(int(round(4 * self.scale)), 0))

        # Enroll / clear button (right col, row 1 inside cam_row)
        self._v_enroll_btn = tk.StringVar(value="Register face")
        self._enroll_btn = GlyphButton(
            self, self._cam_row,
            "Register face",
            self._action_enroll,
            self.f_meta,
            int(round(8 * self.scale)), int(round(3 * self.scale)),
        )
        self._enroll_btn.grid(row=1, column=2, sticky="e",
                               padx=(int(round(6 * self.scale)), 0),
                               pady=(int(round(4 * self.scale)), 0))
        self._enroll_btn.set_enabled(False)   # enabled only when cam + privacy on

        # Draw initial placeholder
        self._cam_preview.after(50, lambda: self._draw_placeholder("Camera monitoring is off"))

        # ── Row 3: timer separator ──────────────────────────────────────────────
        self._tmr_sep = tk.Frame(self.expanded, height=1, bd=0, highlightthickness=0)
        self._tmr_sep.grid(row=3, column=0, columnspan=3, sticky="ew",
                            padx=px, pady=(int(round(4 * self.scale)), int(round(4 * self.scale))))

        # ── Row 4: timer controls ───────────────────────────────────────────────
        self._tmr_row = tk.Frame(self.expanded, bd=0, highlightthickness=0)
        self._tmr_row.grid(row=4, column=0, columnspan=3, sticky="ew",
                            padx=px, pady=(0, py))

        # Timer display
        self._v_timer = tk.StringVar(value=TimerService.fmt(TimerService.DEFAULT_S))
        self._lbl_timer = tk.Label(self._tmr_row, textvariable=self._v_timer,
                                    font=self.f_time, bd=0, highlightthickness=0,
                                    padx=int(round(4 * self.scale)))
        self._lbl_timer.pack(side="left")

        # Controls chip
        self._tmr_chip = tk.Frame(self._tmr_row, bd=0, highlightthickness=0)
        self._tmr_chip.pack(side="left", padx=(int(round(6 * self.scale)), 0))

        sbpx = int(round(6 * self.scale))
        sbpy = int(round(3 * self.scale))
        self._tbtn_m1m  = GlyphButton(self, self._tmr_chip, "-1m",  lambda: self._t_adj(-60),  self.f_meta, sbpx, sbpy)
        self._tbtn_m10  = GlyphButton(self, self._tmr_chip, "-10s", lambda: self._t_adj(-10),  self.f_meta, sbpx, sbpy)
        self._tbtn_pp   = GlyphButton(self, self._tmr_chip, ">",    self._t_start_pause,       self.f_btn,  sbpx, sbpy)
        self._tbtn_p10  = GlyphButton(self, self._tmr_chip, "+10s", lambda: self._t_adj(+10),  self.f_meta, sbpx, sbpy)
        self._tbtn_p1m  = GlyphButton(self, self._tmr_chip, "+1m",  lambda: self._t_adj(+60),  self.f_meta, sbpx, sbpy)
        self._tbtn_rst  = GlyphButton(self, self._tmr_chip, "R",    self._t_reset,             self.f_meta, sbpx, sbpy)
        for b in (self._tbtn_m1m, self._tbtn_m10, self._tbtn_pp,
                  self._tbtn_p10, self._tbtn_p1m, self._tbtn_rst):
            b.pack(side="left", padx=(0, int(round(2 * self.scale))))

    # ── Vision methods ────────────────────────────────────────────────────────

    def _update_dot(self, color: str) -> None:
        """Change the status-dot colour.  Forces a background redraw."""
        if self._dot_color != color:
            self._dot_color = color
            self.panel_cache.clear()
            # apply_geometry early-exits when the window hasn't resized.
            # Force it so the new dot color actually renders this tick.
            self.apply_geometry(force=True)

    def _toggle_camera(self) -> None:
        self.mark_interaction()
        if self.camera is None:
            self._v_cam_status.set("Install opencv-python to use camera")
            return
        self._cam_enabled = not self._cam_enabled
        if self._cam_enabled:
            self.camera.enable()
            self._cam_toggle.set_text("Disable camera")
        else:
            self.camera.disable()
            self._cam_enabled = False
            self._cam_toggle.set_text("Enable camera")
            self._v_cam_status.set("Camera off")
            self._update_dot(DOT_GRAY)
            self._draw_placeholder("Camera monitoring is off")
            self._enroll_btn.set_enabled(False)

    def _toggle_privacy(self) -> None:
        """Toggle the 'not me' privacy mode."""
        self.mark_interaction()
        if self.camera is None:
            return
        self._privacy_mode = not self._privacy_mode
        self.camera.set_privacy_mode(self._privacy_mode)
        if self._privacy_mode:
            self._priv_toggle.set_text("Privacy: ON")
            self._enroll_btn.set_enabled(self._cam_enabled)
        else:
            self._priv_toggle.set_text("Privacy: off")
            self._enroll_btn.set_enabled(False)
            self._update_dot(DOT_GRAY)

    def _action_enroll(self) -> None:
        """Register face / clear enrollment toggle."""
        self.mark_interaction()
        if self.camera is None:
            return
        _, enrolled, _, enrolling, *_ = self.camera.get_privacy_state()
        if enrolled:
            # Clear and re-register
            self.camera.clear_enrollment()
            self._enroll_btn.set_text("Register face")
            self._v_cam_status.set("Enrollment cleared")
        elif enrolling:
            # Cancel
            self.camera.clear_enrollment()
            self._enroll_btn.set_text("Register face")
        else:
            ok = self.camera.start_enrollment()
            if ok:
                self._enroll_btn.set_text("Cancel enroll")
                self._v_cam_status.set("Look at camera...")

    # ── Timer actions ─────────────────────────────────────────────────────────

    def _t_start_pause(self):
        self.mark_interaction()
        self.timer.start_pause()

    def _t_reset(self):
        self.mark_interaction()
        self.timer.reset()

    def _t_adj(self, delta_s: int):
        self.mark_interaction()
        self.timer.adjust(delta_s)

    def _draw_faceid_overlay(self, img, phase_idx: int, phase_cnt: int, phases_done: list):
        """
        Face ID-style enrollment guide drawn on the PIL preview image in-place.
        Shows: 5 phase dots, an oval face guide, a directional arrow, and a
        progress arc that fills as samples are collected for the current pose.
        """
        try:
            draw = ImageDraw.Draw(img, "RGBA")
            tw, th = img.size
            n = len(ENROLLMENT_POSES)

            # ── Phase dots (top centre) ───────────────────────────────────────
            dot_r = 3
            spacing = 12
            start_x = tw // 2 - (n - 1) * spacing // 2
            for i in range(n):
                dx = start_x + i * spacing
                dy = 5
                if i < len(phases_done):
                    col = DOT_GREEN          # done
                elif i == phase_idx:
                    col = "#FFFFFF"          # current
                else:
                    col = "#444444"          # upcoming
                draw.ellipse([dx-dot_r, dy-dot_r, dx+dot_r, dy+dot_r], fill=col)

            # ── Face oval ────────────────────────────────────────────────────
            cx, cy  = tw // 2, th // 2 + 4
            rx, ry  = tw // 4, th // 3
            draw.ellipse([cx-rx, cy-ry, cx+rx, cy+ry],
                          outline="#FFFFFF", width=2)

            # ── Progress arc (fills as samples are captured) ──────────────────
            prog = phase_cnt / max(1, SAMPLES_PER_POSE)
            if prog > 0:
                # Draw arc segment from top (270°) clockwise
                span = int(360 * prog)
                draw.arc([cx-rx, cy-ry, cx+rx, cy+ry],
                          start=270, end=270+span,
                          fill=DOT_GREEN, width=3)

            # ── Direction arrow ───────────────────────────────────────────────
            _, _, yaw_t, pitch_t = ENROLLMENT_POSES[phase_idx]
            arrow_r = max(rx, ry) + 8
            ax = int(cx + yaw_t  * arrow_r * 3)
            ay = int(cy + pitch_t * arrow_r * 3)
            ax = max(4, min(tw - 5, ax))
            ay = max(12, min(th - 5, ay))
            ar = 5
            draw.ellipse([ax-ar, ay-ar, ax+ar, ay+ar],
                          fill="#FFFFFF")
            # Line from oval edge toward arrow dot
            edge_x = int(cx + yaw_t  * rx * 1.1)
            edge_y = int(cy + pitch_t * ry * 1.1)
            if abs(yaw_t) > 0.05 or abs(pitch_t) > 0.05:
                draw.line([edge_x, edge_y, ax, ay], fill="#FFFFFF", width=2)
        except Exception:
            pass

    def _draw_placeholder(self, text: str) -> None:
        """Draw a text placeholder on the camera preview canvas."""
        c = self._cam_preview
        c.delete("all")
        w, h = self._prev_w, self._prev_h
        c.create_rectangle(0, 0, w, h, fill=self.palette.chip, outline="")
        c.create_text(
            w // 2, h // 2,
            text=text,
            fill=self.palette.text_muted,
            font=self.f_meta,
            anchor="center",
            justify="center",
            width=w - 8,
        )

    def _update_preview(self) -> None:
        """Called each tick: dot colour, status label, enrollment progress, live preview."""
        if self.camera is None:
            return

        status, detected, frame = self.camera.get_state()
        priv_on, enrolled, priv_alert, enrolling, enroll_cnt, \
            enroll_phase, enroll_phase_cnt, phases_done = \
            self.camera.get_privacy_state()

        # Get current face count — used for multi-face check
        rects      = self.camera.get_last_rects()
        face_count = len(rects)
        multi_face = face_count > 1   # more than one person → always alert

        # ── Status dot ────────────────────────────────────────────────────────
        if status != "active":
            self._update_dot(DOT_GRAY)
        elif multi_face:
            # Multiple faces → red regardless of mode
            self._update_dot(DOT_RED)
        elif priv_on:
            if not enrolled:
                self._update_dot(DOT_GRAY)
            elif priv_alert:
                self._update_dot(DOT_RED)
            else:
                self._update_dot(DOT_GREEN)
        else:
            self._update_dot(DOT_RED if detected else DOT_GREEN)

        # ── Enroll button label sync ──────────────────────────────────────────
        if priv_on and self._cam_enabled:
            self._enroll_btn.set_enabled(True)
            if enrolling:
                self._enroll_btn.set_text("Cancel enroll")
            elif enrolled:
                self._enroll_btn.set_text("Clear face")
            else:
                self._enroll_btn.set_text("Register face")
        else:
            self._enroll_btn.set_enabled(False)

        # ── Status label ──────────────────────────────────────────────────────
        if status == "initialising":
            status_txt = "Initialising..."
        elif status == "error":
            status_txt = "Camera unavailable"
        elif status == "off":
            status_txt = "Camera off"
        elif multi_face:
            status_txt = f"⚠ {face_count} faces detected"
        elif priv_on:
            if not enrolled:
                if enrolling and enroll_phase < len(ENROLLMENT_POSES):
                    pose_label = ENROLLMENT_POSES[enroll_phase][1]
                    status_txt = f"{pose_label}  ({enroll_phase + 1}/{len(ENROLLMENT_POSES)})"
                else:
                    status_txt = "Register your face"
            elif priv_alert:
                status_txt = "⚠ Unknown face"
            elif detected:
                status_txt = "Just you"
            else:
                status_txt = "All clear"
        else:
            status_txt = "Person detected" if detected else "No person detected"
        self._v_cam_status.set(status_txt)

        # ── Live preview (only when expanded and within FPS cap) ───────────────
        if not self.expanded_visible:
            return
        now = time.monotonic()
        if (now - self._last_preview_t) < (1.0 / PREVIEW_REFRESH_HZ):
            return
        self._last_preview_t = now

        if frame is None or status != "active":
            self._draw_placeholder({
                "initialising": "Initialising camera...",
                "error":        "Camera unavailable",
                "off":          "Camera monitoring is off",
            }.get(status, "Camera off"))
            return

        try:
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            img = Image.fromarray(rgb)

            # Crop to 16:9 (same as detection did) then resize to canvas
            tw, th   = self._prev_w, self._prev_h
            sw, sh   = img.size
            sr, tr   = sw / sh, tw / th
            if sr > tr:
                cw = int(sh * tr);  x0 = (sw - cw) // 2
                img = img.crop((x0, 0, x0 + cw, sh))
            elif sr < tr:
                ch = int(sw / tr);  y0 = (sh - ch) // 2
                img = img.crop((0, y0, sw, y0 + ch))
            img = img.resize((tw, th), Image.BILINEAR)

            # ── Face ID enrollment overlay ────────────────────────────────────
            if enrolling and enroll_phase < len(ENROLLMENT_POSES):
                self._draw_faceid_overlay(img, enroll_phase, enroll_phase_cnt, phases_done)

            # ── Bounding boxes (not shown during enrollment to keep overlay clean) ──
            elif rects:
                ov  = ImageDraw.Draw(img)
                sx, sy = tw / 320.0, th / 180.0
                box_color = (DOT_RED if priv_alert else DOT_GREEN) \
                            if (priv_on and enrolled) else "#FFFFFF"
                for (rx, ry, rw_r, rh_r) in rects:
                    ov.rounded_rectangle(
                        [max(1, int(rx*sx)),      max(1, int(ry*sy)),
                         min(tw-2, int((rx+rw_r)*sx)), min(th-2, int((ry+rh_r)*sy))],
                        radius=4, outline=box_color, width=2,
                    )

            photo = ImageTk.PhotoImage(img)
            c = self._cam_preview
            c.delete("all")
            c.create_image(0, 0, anchor="nw", image=photo)
            self._cam_photo_ref = photo

        except Exception:
            self._draw_placeholder("Preview error")

    # ── Interaction ───────────────────────────────────────────────────────────

    def mark_interaction(self):
        now = time.monotonic()
        self.interaction_hold_until = max(self.interaction_hold_until,
                                           now + INTERACTION_HOLD_S)
        if self.mode != "expanded":
            self.set_mode("expanded")

    def _act_prev(self):
        self.mark_interaction()
        self.media.prev()

    def _act_pp(self):
        self.mark_interaction()
        self.media.play_pause()

    def _act_next(self):
        self.mark_interaction()
        self.media.next()

    def _act_vol_up(self):
        self.mark_interaction()
        self.volume.step(VOL_STEP)
        self.last_volume_poll = 0.0

    def _act_vol_down(self):
        self.mark_interaction()
        self.volume.step(-VOL_STEP)
        self.last_volume_poll = 0.0

    # ── Hover / mode logic ────────────────────────────────────────────────────

    def pointer_inside(self) -> bool:
        try:
            px, py = self.root.winfo_pointerx(), self.root.winfo_pointery()
            rx, ry = self.root.winfo_rootx(),    self.root.winfo_rooty()
            rw, rh = self.root.winfo_width(),    self.root.winfo_height()
            return (rx <= px < rx + rw) and (ry <= py < ry + rh)
        except Exception:
            return False

    def set_mode(self, mode: str):
        if mode == self.mode:
            return
        self.mode = mode
        self.show_expanded(mode == "expanded")

    def show_expanded(self, show: bool):
        if show == self.expanded_visible:
            return
        self.expanded_visible = show
        if show:
            self.collapsed.place_forget()
            self.expanded.place(relx=0.5, rely=0.5, anchor="center",
                                 relwidth=1.0, relheight=1.0)
        else:
            self.expanded.place_forget()
            self.collapsed.place(relx=0.5, rely=0.5, anchor="center",
                                  relwidth=1.0, relheight=1.0)

    def update_mode_from_pointer(self, now: float):
        inside = self.pointer_inside()
        if inside:
            if self.inside_since is None:
                self.inside_since = now
            self.outside_since = None
            if self.mode == "collapsed" and (now - self.inside_since) >= HOVER_EXPAND_DELAY_S:
                self.set_mode("expanded")
        else:
            if self.outside_since is None:
                self.outside_since = now
            self.inside_since = None
            if (self.mode == "expanded"
                    and self.outside_since is not None
                    and (now - self.outside_since) >= HOVER_COLLAPSE_DELAY_S
                    and now >= self.interaction_hold_until):
                self.set_mode("collapsed")

    # ── Data derivation ───────────────────────────────────────────────────────

    def battery_string(self) -> str:
        p, c = self.power.snapshot()
        if p is None:
            return "Battery --"
        return f"Charging {p}%" if c else f"Battery {p}%"

    def volume_string(self, now: float) -> str:
        if not self.volume.ok:
            return "Volume --"
        if (now - self.last_volume_poll) >= 0.25 or self.volume_cached is None:
            self.volume_cached    = self.volume.get()
            self.last_volume_poll = now
        if self.volume_cached is None:
            return "Volume --"
        return f"Volume {self.volume_cached}%"

    def derive(self, m: MediaSnapshot, now_mono: float):
        now = datetime.now()
        t_expanded  = time_text(now, SHOW_SECONDS, USE_24H)
        t_collapsed = time_text(now, False,         USE_24H)
        d = date_text(now)

        # ── Timer takes collapsed-pill priority when running ──────────────────
        tmr = self.timer.get()
        if tmr["running"]:
            collapsed = f"Timer  {TimerService.fmt(tmr['remaining'])}"
        elif tmr["done"]:
            collapsed = "Timer done!"
        else:
            has_media = bool((m.title or "").strip() or (m.artist or "").strip())
            if has_media:
                collapsed = (m.title or m.artist).strip()
            else:
                collapsed = t_collapsed

        has_media = bool((m.title or "").strip() or (m.artist or "").strip())
        if has_media:
            title     = (m.title or m.artist).strip()
            subtitle  = (m.artist.strip() if m.title.strip() and m.artist.strip()
                         else (m.app.strip() or "Now Playing"))
        elif m.available and m.app:
            title     = m.app.strip()
            subtitle  = "No track metadata available"
        else:
            title     = "Nothing playing"
            subtitle  = "Start media in any app"

        ratio = 0.0
        if m.duration > 0.5:
            ratio = clamp(m.position / m.duration, 0.0, 1.0)
        self.progress_display += (ratio - self.progress_display) * 0.20

        play = ">" if (not m.available or m.paused) else "||"

        return {
            "collapsed": collapsed,
            "title":     title,
            "subtitle":  subtitle,
            "time":      t_expanded,
            "date":      d,
            "battery":   self.battery_string(),
            "volume":    self.volume_string(now_mono),
            "progress":  float(clamp(self.progress_display, 0.0, 1.0)),
            "play":      play,
            "can_prev":  bool(m.available and m.can_prev),
            "can_next":  bool(m.available and m.can_next),
            "can_pp":    bool(m.available and m.can_pp),
            "has_media": has_media,
        }

    # ── Geometry / spring ────────────────────────────────────────────────────

    def collapsed_width_target(self, vm) -> int:
        desired = self.f_lane.measure(vm["collapsed"]) + int(round(40 * self.scale))
        if not vm["has_media"]:
            desired = max(desired, int(round(250 * self.scale)))
        desired = int(clamp(desired, self.collapsed_w_min, self.collapsed_w_max))
        desired = int(round(desired / 8.0) * 8)
        self.collapsed_width_ema = self.collapsed_width_ema * 0.84 + desired * 0.16
        return int(self.collapsed_width_ema)

    def update_target_geometry(self, vm):
        if self.mode == "expanded":
            self.target_w, self.target_h = float(self.expanded_w), float(self.expanded_h)
        else:
            self.target_w, self.target_h = float(self.collapsed_width_target(vm)), float(self.collapsed_h)

    def spring_step(self):
        dw = self.target_w - self.current_w
        dh = self.target_h - self.current_h
        self.vel_w = (self.vel_w + dw * 0.22) * 0.72
        self.vel_h = (self.vel_h + dh * 0.24) * 0.70
        self.current_w += self.vel_w
        self.current_h += self.vel_h
        if abs(dw) < 0.35 and abs(self.vel_w) < 0.20:
            self.current_w, self.vel_w = self.target_w, 0.0
        if abs(dh) < 0.35 and abs(self.vel_h) < 0.20:
            self.current_h, self.vel_h = self.target_h, 0.0
        self.current_w = clamp(self.current_w, self.collapsed_w_min,
                                max(self.expanded_w, self.collapsed_w_max))
        self.current_h = clamp(self.current_h, self.collapsed_h, self.expanded_h)

    # ── Rendering ────────────────────────────────────────────────────────────

    def panel_img(self, w: int, h: int) -> ImageTk.PhotoImage:
        """
        Create or retrieve (cached) the rounded-pill background image.
        The status dot is baked directly into the PIL image so it has no
        transparency artefacts.
        """
        dot_color = self._dot_color
        key = (self.palette.name, int(w), int(h), dot_color)
        if key in self.panel_cache:
            return self.panel_cache[key]
        if len(self.panel_cache) > 80:
            self.panel_cache.clear()

        img = Image.new("RGBA", (max(1, w), max(1, h)), (0, 0, 0, 0))
        d   = ImageDraw.Draw(img)
        r   = int(clamp(self.radius, 8, min(w, h) // 2))

        # Main pill body
        d.rounded_rectangle(
            (0, 0, w - 1, h - 1),
            radius=r, fill=self.palette.panel, outline=self.palette.panel_border, width=1,
        )
        # Subtle inner-top highlight — matches DynamicIsland_Premium.py exactly
        top_h = max(10, int(h * 0.45))
        d.rounded_rectangle(
            (1, 1, w - 2, top_h),
            radius=max(1, r - 1), outline=self.palette.panel_highlight, width=1,
        )

        # ── Status dot ────────────────────────────────────────────────────────
        # Sits in the LEFT margin so it is never hidden by the content frame.
        # Content frame left edge = inner_pad_x.
        # Dot centre = inner_pad_x / 2  (midpoint of the left margin strip).
        ds    = max(5, int(round(DOT_SZ * self.scale)))
        dot_x = int(clamp(self.inner_pad_x // 2,  ds, self.inner_pad_x - 2))
        dot_y = int(clamp(self.collapsed_h  // 2,  ds, h - ds - 1))
        d.ellipse(
            [(dot_x - ds // 2, dot_y - ds // 2),
             (dot_x + ds // 2, dot_y + ds // 2)],
            fill=dot_color,
        )

        out = ImageTk.PhotoImage(img)
        self.panel_cache[key] = out
        return out

    def apply_geometry(self, force=False):
        w, h = int(round(self.current_w)), int(round(self.current_h))
        if not force and abs(w - self.root.winfo_width()) < 1 and abs(h - self.root.winfo_height()) < 1:
            return
        x = max(0, int((self.root.winfo_screenwidth() - w) / 2))
        self.root.geometry(f"{w}x{h}+{x}+{self.top_margin}")
        img = self.panel_img(w, h)
        self.bg.configure(image=img)
        self.bg.image = img
        self.bg.place(x=0, y=0, width=w, height=h)
        cw = max(20, w - 2 * self.inner_pad_x)
        ch = max(20, h - 2 * self.inner_pad_y)
        self.content.place(relx=0.5, rely=0.5, anchor="center", width=cw, height=ch)

    def draw_progress(self, ratio: float):
        c = self.progress
        c.delete("all")
        w  = max(20, c.winfo_width())
        h  = max(6,  c.winfo_height())
        y  = h // 2
        pad = int(round(2 * self.scale))
        th  = max(2, int(round(3 * self.scale)))
        c.create_line(pad, y, w - pad, y,
                       fill=self.palette.progress_track, width=th, capstyle="round")
        if ratio > 0.001:
            c.create_line(pad, y, pad + (w - 2 * pad) * float(clamp(ratio, 0.0, 1.0)), y,
                           fill=self.palette.progress_fill, width=th, capstyle="round")

    def render(self, vm):
        lane_w = max(40, self.content.winfo_width() - int(round(22 * self.scale)))
        self.v_collapsed.set(ellipsize(vm["collapsed"], self.f_lane, lane_w))

        center_w = self.center.winfo_width()
        if center_w < 80:
            center_w = int(self.expanded_w * 0.40)
        self.v_title.set(ellipsize(vm["title"],    self.f_title, center_w))
        self.v_sub.set(  ellipsize(vm["subtitle"], self.f_sub,   center_w))

        self.v_time.set(vm["time"])
        self.v_date.set(vm["date"])
        self.v_batt.set(vm["battery"])
        self.v_vol.set( vm["volume"])

        self.btn_pp.set_text(vm["play"])
        self.btn_prev.set_enabled(vm["can_prev"])
        self.btn_next.set_enabled(vm["can_next"])
        self.btn_pp.set_enabled(  vm["can_pp"])

        vol_ok = bool(self.volume.ok)
        self.btn_vm.set_enabled(vol_ok)
        self.btn_vp.set_enabled(vol_ok)

        self.draw_progress(vm["progress"])

        # ── Timer display ──────────────────────────────────────────────────────
        tmr = self.timer.get()
        self._v_timer.set(TimerService.fmt(tmr["remaining"]))
        # Play/pause glyph on timer button
        self._tbtn_pp.set_text("||" if tmr["running"] else ">")
        # Disable adjust buttons while running
        idle = not tmr["running"]
        for b in (self._tbtn_m1m, self._tbtn_m10, self._tbtn_p10, self._tbtn_p1m):
            b.set_enabled(idle)

    def apply_palette(self):
        p = self.palette
        for f in (self.content, self.collapsed, self.expanded,
                   self.left, self.center, self.right):
            f.configure(bg=p.panel)
        self.transport.configure(bg=p.chip)
        self.vol_chip.configure( bg=p.chip)
        self.lbl_collapsed.configure(bg=p.panel, fg=p.text)
        self.lbl_title.configure(    bg=p.panel, fg=p.text)
        self.lbl_sub.configure(      bg=p.panel, fg=p.text_secondary)
        self.lbl_time.configure(     bg=p.panel, fg=p.text)
        self.lbl_date.configure(     bg=p.panel, fg=p.text_secondary)
        self.lbl_batt.configure(     bg=p.panel, fg=p.text_muted)
        self.lbl_vol.configure(      bg=p.chip,  fg=p.text_secondary)
        self.progress.configure(     bg=p.panel)
        for b in (self.btn_prev, self.btn_pp, self.btn_next, self.btn_vm, self.btn_vp):
            b.refresh()

        # Camera + privacy section
        self._cam_sep.configure(bg=p.panel_border)
        self._cam_row.configure(bg=p.panel)
        self._cam_preview_frame.configure(bg=p.chip, highlightbackground=p.panel_border)
        self._cam_preview.configure(bg=p.chip)
        self._lbl_cam_status.configure(bg=p.panel, fg=p.text_muted)
        self._cam_toggle.refresh()
        self._priv_toggle.refresh()
        self._enroll_btn.refresh()

        # Timer section
        self._tmr_sep.configure(bg=p.panel_border)
        self._tmr_row.configure(bg=p.panel)
        self._tmr_chip.configure(bg=p.chip)
        self._lbl_timer.configure(bg=p.panel, fg=p.text)
        for b in (self._tbtn_m1m, self._tbtn_m10, self._tbtn_pp,
                  self._tbtn_p10, self._tbtn_p1m, self._tbtn_rst):
            b.refresh()

        self.panel_cache.clear()

    def maybe_theme(self, now: float):
        if (now - self.last_theme_poll) < THEME_POLL_S:
            return
        self.last_theme_poll = now
        desired = ("dark"  if self.theme_mode == "dark"
                   else "light" if self.theme_mode == "light"
                   else system_theme())
        if desired != self.theme_name:
            self.theme_name = desired
            self.palette    = DARK if desired == "dark" else LIGHT
            self.apply_palette()
            self.apply_geometry(force=True)
            # Refresh placeholder with new palette colours
            if not self._cam_enabled or (self.camera and self.camera.get_state()[0] != "active"):
                self._draw_placeholder("Camera monitoring is off")

    # ── Main loop ─────────────────────────────────────────────────────────────

    def tick(self):
        now = time.monotonic()
        self.maybe_theme(now)
        self.update_mode_from_pointer(now)
        media = self.media.snapshot()
        vm    = self.derive(media, now)
        self.render(vm)
        self.update_target_geometry(vm)
        self.spring_step()
        self.apply_geometry(force=False)
        self._update_preview()
        self.root.after(FRAME_MS, self.tick)

    def quit(self):
        for svc in (self.media, self.power):
            try:
                svc.shutdown()
            except Exception:
                pass
        if self.camera is not None:
            try:
                self.camera.shutdown()
            except Exception:
                pass
        self.root.destroy()


# ── Startup helpers (identical to Premium) ───────────────────────────────────

def startup_path() -> str:
    appdata = os.environ.get("APPDATA", "")
    return os.path.join(appdata, "Microsoft", "Windows",
                         "Start Menu", "Programs", "Startup",
                         "DynamicIslandVision.lnk")


def install_shortcut(script_path: str):
    try:
        import win32com.client  # type: ignore
        shell = win32com.client.Dispatch("WScript.Shell")
        sc = shell.CreateShortCut(startup_path())
        sc.Targetpath       = sys.executable
        sc.Arguments        = f'"{script_path}"'
        sc.WorkingDirectory = os.path.dirname(script_path)
        sc.save()
        print("Startup shortcut created.")
    except Exception as exc:
        print(f"Startup shortcut failed: {exc}")


def maybe_relaunch_with_smtc(script_path: str) -> bool:
    if HAS_SMTC or os.environ.get("DI_RELAUNCHED") == "1" or "--install" in sys.argv[1:]:
        return False
    check = "from winsdk.windows.media.control import GlobalSystemMediaTransportControlsSessionManager"
    cands = []
    py = shutil.which("py")
    if py:
        cands.append([py, "-3.11"])
    py311 = os.path.join(os.path.expanduser("~"), "AppData", "Local",
                          "Programs", "Python", "Python311")
    for exe in ("pythonw.exe", "python.exe"):
        p = os.path.join(py311, exe)
        if os.path.exists(p):
            cands.append([p])
    seen = set()
    for base in cands:
        key = tuple(base)
        if key in seen:
            continue
        seen.add(key)
        try:
            probe = subprocess.run(base + ["-c", check],
                                    capture_output=True, text=True, timeout=6)
            if probe.returncode != 0:
                continue
            env = os.environ.copy()
            env["DI_RELAUNCHED"] = "1"
            subprocess.Popen(base + [script_path],
                              cwd=os.path.dirname(script_path), env=env)
            return True
        except Exception:
            continue
    return False


def main():
    if not HAS_CV2:
        print(
            "Warning: opencv-python is not installed.\n"
            "Camera monitoring will be unavailable.\n"
            "Run:  pip install opencv-python numpy\n"
        )

    script = os.path.abspath(__file__)
    if "--install" in sys.argv[1:]:
        install_shortcut(script)
    if maybe_relaunch_with_smtc(script):
        return

    root = tk.Tk()
    app  = App(root)
    root.protocol("WM_DELETE_WINDOW", app.quit)
    root.mainloop()


if __name__ == "__main__":
    main()
