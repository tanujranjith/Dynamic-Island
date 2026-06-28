
from __future__ import annotations
import asyncio, ctypes, ctypes.wintypes as wt, os, shutil, subprocess, sys, threading, time
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import Optional, Tuple
import tkinter as tk
import tkinter.font as tkfont
from PIL import Image, ImageDraw, ImageTk

try:
    import psutil
    HAS_PSUTIL = True
except Exception:
    HAS_PSUTIL = False

try:
    from winsdk.windows.media.control import GlobalSystemMediaTransportControlsSessionManager as MediaManager
    from winsdk.windows.media.control import GlobalSystemMediaTransportControlsSessionPlaybackStatus as PlaybackStatus
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

TRANSPARENT_KEY = "#010203"
WINDOW_OPACITY = 0.97
SHOW_SECONDS = True
USE_24H = False
TOP_MARGIN = 10
COLLAPSED_H = 42
EXPANDED_H = 124
COLLAPSED_W_MIN = 220
COLLAPSED_W_MAX = 520
EXPANDED_W = 760
RADIUS = 22
INNER_PAD_X = 14
INNER_PAD_Y = 10
HOVER_EXPAND_DELAY_S = 0.14
HOVER_COLLAPSE_DELAY_S = 0.90
INTERACTION_HOLD_S = 1.20
FRAME_MS = 16
MEDIA_POLL_MS = 240
BATTERY_POLL_S = 5.0
THEME_POLL_S = 2.5
META_HOLD_S = 8.0
VOL_STEP = 2


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
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") as key:
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
    for n in ("SF Pro Text", "SF Pro Display", "Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI"):
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


DARK = Palette("dark", "#111215", "#282C34", "#22262E", "#F5F7FA", "#D1D5DE", "#8D93A0", "#1A1E25", "#242A34", "#303745", "#2A2F38", "#F5F7FA")
LIGHT = Palette("light", "#F7F8FB", "#D8DCE4", "#FFFFFF", "#14161A", "#4D5562", "#7A8391", "#ECEFF4", "#E1E6EE", "#D5DCE7", "#D6DBE3", "#1B2027")


@dataclass
class MediaSnapshot:
    title: str = ""
    artist: str = ""
    app: str = ""
    paused: bool = False
    can_prev: bool = False
    can_next: bool = False
    can_pp: bool = True
    position: float = 0.0
    duration: float = 0.0
    rate: float = 1.0
    available: bool = False


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
                title = (getattr(p, "title", "") or "").strip()
                subtitle = (getattr(p, "subtitle", "") or "").strip()
                album = (getattr(p, "album_title", "") or "").strip()
                artist = (getattr(p, "artist", "") or "").strip()
                album_artist = (getattr(p, "album_artist", "") or "").strip()
                if not title:
                    title = subtitle or album
                if not artist:
                    artist = album_artist
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
        if "spotify" in s:
            return "Spotify"
        if "vlc" in s:
            return "VLC"
        if "zune" in s or "music" in s:
            return "Media"
        if "chrome" in s:
            return "Chrome"
        if "edge" in s:
            return "Edge"
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
                info = ses.get_playback_info()
                status = getattr(info, "playback_status", None)
                score = 0
                if status == PlaybackStatus.PLAYING:
                    score += 40
                elif status == PlaybackStatus.PAUSED:
                    score += 25
                elif status == PlaybackStatus.OPENED:
                    score += 18
                elif status == PlaybackStatus.STOPPED:
                    score += 6
                t, a = await self._props(ses)
                if t:
                    score += 24
                if a:
                    score += 12
                if idx == 0:
                    score += 4
                has = bool(t or a)
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
                        info = ses.get_playback_info()
                        st = info.playback_status
                        paused = st in (PlaybackStatus.PAUSED, PlaybackStatus.STOPPED)
                        ctrls = info.controls
                        can_prev = bool(getattr(ctrls, "is_previous_enabled", False))
                        can_next = bool(getattr(ctrls, "is_next_enabled", False))
                        can_pp = bool(getattr(ctrls, "is_play_pause_toggle_enabled", True) or getattr(ctrls, "is_play_enabled", True))
                        rate = float(getattr(info, "playback_rate", 1.0) or 1.0)
                    except Exception:
                        pass
                    try:
                        tl = ses.get_timeline_properties()
                        pos = to_secs(getattr(tl, "position", 0.0))
                        dur = to_secs(getattr(tl, "end_time", 0.0))
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
                        self._last_meta = (title, artist, app)
                        self._last_meta_at = now
                    else:
                        if (now - self._last_meta_at) <= META_HOLD_S and (self._last_meta[0] or self._last_meta[1]):
                            title, artist = self._last_meta[0], self._last_meta[1]
                            if not app:
                                app = self._last_meta[2]
                    snap = MediaSnapshot(title, artist, app, paused, can_prev, can_next, can_pp, pos, dur, rate, True)
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
                    st = self.snapshot()
                    if st.paused:
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
        self.poll_s = max(2.0, float(poll_s))
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
            dev = AudioUtilities.GetSpeakers()
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
            pct = int(clamp(pct, 0, 100))
            self.endpoint.SetMasterVolumeLevelScalar(pct / 100.0, None)
        except Exception:
            pass

    def step(self, delta: int):
        cur = self.get()
        if cur is None:
            return
        self.set(cur + int(delta))


class GlyphButton(tk.Label):
    def __init__(self, app: "App", parent, text: str, command, font: tkfont.Font, padx: int, pady: int):
        super().__init__(parent, text=text, font=font, bd=0, highlightthickness=0, cursor="hand2", padx=padx, pady=pady)
        self.app, self.command = app, command
        self.enabled, self.hover, self.pressed = True, False, False
        self.bind("<Enter>", self._enter)
        self.bind("<Leave>", self._leave)
        self.bind("<ButtonPress-1>", self._press)
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
        self.hover = False
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

        self.scale = dpi_scale(root.winfo_id())
        self.top_margin = int(round(TOP_MARGIN * self.scale))
        self.radius = int(round(RADIUS * self.scale))
        self.inner_pad_x = int(round(INNER_PAD_X * self.scale))
        self.inner_pad_y = int(round(INNER_PAD_Y * self.scale))
        self.collapsed_h = int(round(COLLAPSED_H * self.scale))
        self.expanded_h = int(round(EXPANDED_H * self.scale))
        self.collapsed_w_min = int(round(COLLAPSED_W_MIN * self.scale))
        self.collapsed_w_max = int(round(COLLAPSED_W_MAX * self.scale))
        self.expanded_w = min(int(round(EXPANDED_W * self.scale)), int(root.winfo_screenwidth() * 0.9))
        self.expanded_w = max(self.expanded_w, self.collapsed_w_min + int(180 * self.scale))

        self.media, self.power, self.volume = MediaService(MEDIA_POLL_MS), PowerService(BATTERY_POLL_S), VolumeService()

        self.theme_mode = "auto"
        self.theme_name = system_theme()
        self.palette = DARK if self.theme_name == "dark" else LIGHT
        self.last_theme_poll = 0.0

        self.mode = "collapsed"
        self.inside_since, self.outside_since = None, time.monotonic()
        self.interaction_hold_until = 0.0

        self.collapsed_width_ema = float(max(self.collapsed_w_min, int(self.expanded_w * 0.42)))
        self.target_w, self.target_h = self.collapsed_width_ema, float(self.collapsed_h)
        self.current_w, self.current_h = self.target_w, self.target_h
        self.vel_w = self.vel_h = 0.0
        self.progress_display = 0.0
        self.volume_cached = None
        self.last_volume_poll = 0.0
        self.panel_cache = {}
        self.expanded_visible = False

        fam = pick_font(root)
        self.f_lane = tkfont.Font(family=fam, size=max(11, int(12 * self.scale)), weight="bold")
        self.f_title = tkfont.Font(family=fam, size=max(11, int(12 * self.scale)), weight="bold")
        self.f_sub = tkfont.Font(family=fam, size=max(9, int(10 * self.scale)))
        self.f_time = tkfont.Font(family=fam, size=max(12, int(14 * self.scale)), weight="bold")
        self.f_meta = tkfont.Font(family=fam, size=max(8, int(9 * self.scale)))
        self.f_btn = tkfont.Font(family=fam, size=max(10, int(11 * self.scale)), weight="bold")
        self.f_vol = tkfont.Font(family=fam, size=max(9, int(10 * self.scale)), weight="bold")

        self.bg = tk.Label(root, bg=TRANSPARENT_KEY, bd=0, highlightthickness=0)
        self.bg.place(x=0, y=0)
        self.content = tk.Frame(root, bd=0, highlightthickness=0)
        self.content.place(relx=0.5, rely=0.5, anchor="center")

        self._build_collapsed()
        self._build_expanded()

        root.bind("<Escape>", lambda e: self.quit())
        root.bind("<space>", lambda e: self._act_pp())
        root.bind("<Control-Right>", lambda e: self._act_next())
        root.bind("<Control-Left>", lambda e: self._act_prev())
        root.bind("<Control-Up>", lambda e: self._act_vol_up())
        root.bind("<Control-Down>", lambda e: self._act_vol_down())

        self.apply_palette()
        self.show_expanded(False)
        self.apply_geometry(force=True)
        root.deiconify()
        root.after(FRAME_MS, self.tick)

    def _build_collapsed(self):
        self.collapsed = tk.Frame(self.content, bd=0, highlightthickness=0)
        self.collapsed.place(relx=0.5, rely=0.5, anchor="center", relwidth=1.0, relheight=1.0)
        self.v_collapsed = tk.StringVar(value="")
        self.lbl_collapsed = tk.Label(self.collapsed, textvariable=self.v_collapsed, font=self.f_lane, anchor="center", justify="center", bd=0, highlightthickness=0, padx=int(round(8 * self.scale)))
        self.lbl_collapsed.pack(fill="both", expand=True)

    def _build_expanded(self):
        self.expanded = tk.Frame(self.content, bd=0, highlightthickness=0)
        self.expanded.columnconfigure(1, weight=1)
        px = int(round(10 * self.scale))
        py = int(round(6 * self.scale))

        self.left = tk.Frame(self.expanded, bd=0, highlightthickness=0)
        self.left.grid(row=0, column=0, sticky="w", padx=(px, int(round(6 * self.scale))), pady=py)
        self.transport = tk.Frame(self.left, bd=0, highlightthickness=0)
        self.transport.pack(anchor="w")
        bpx, bpy = int(round(8 * self.scale)), int(round(4 * self.scale))
        self.btn_prev = GlyphButton(self, self.transport, "<<", self._act_prev, self.f_btn, bpx, bpy)
        self.btn_pp = GlyphButton(self, self.transport, ">", self._act_pp, self.f_btn, bpx, bpy)
        self.btn_next = GlyphButton(self, self.transport, ">>", self._act_next, self.f_btn, bpx, bpy)
        self.btn_prev.pack(side="left")
        self.btn_pp.pack(side="left", padx=(int(round(4 * self.scale)), int(round(4 * self.scale))))
        self.btn_next.pack(side="left")

        self.vol_chip = tk.Frame(self.left, bd=0, highlightthickness=0)
        self.vol_chip.pack(anchor="w", pady=(int(round(6 * self.scale)), 0))
        self.btn_vm = GlyphButton(self, self.vol_chip, "-", self._act_vol_down, self.f_btn, int(round(7 * self.scale)), bpy)
        self.v_vol = tk.StringVar(value="--")
        self.lbl_vol = tk.Label(self.vol_chip, textvariable=self.v_vol, font=self.f_vol, bd=0, highlightthickness=0, padx=int(round(8 * self.scale)), pady=bpy)
        self.btn_vp = GlyphButton(self, self.vol_chip, "+", self._act_vol_up, self.f_btn, int(round(7 * self.scale)), bpy)
        self.btn_vm.pack(side="left")
        self.lbl_vol.pack(side="left")
        self.btn_vp.pack(side="left")

        self.center = tk.Frame(self.expanded, bd=0, highlightthickness=0)
        self.center.grid(row=0, column=1, sticky="nsew", padx=(int(round(8 * self.scale)), int(round(8 * self.scale))), pady=py)
        self.center.columnconfigure(0, weight=1)
        self.v_title, self.v_sub = tk.StringVar(value=""), tk.StringVar(value="")
        self.lbl_title = tk.Label(self.center, textvariable=self.v_title, font=self.f_title, anchor="w", justify="left", bd=0, highlightthickness=0)
        self.lbl_sub = tk.Label(self.center, textvariable=self.v_sub, font=self.f_sub, anchor="w", justify="left", bd=0, highlightthickness=0)
        self.progress = tk.Canvas(self.center, height=max(8, int(round(10 * self.scale))), bd=0, highlightthickness=0)
        self.lbl_title.grid(row=0, column=0, sticky="ew")
        self.lbl_sub.grid(row=1, column=0, sticky="ew", pady=(int(round(2 * self.scale)), int(round(6 * self.scale))))
        self.progress.grid(row=2, column=0, sticky="ew")

        self.right = tk.Frame(self.expanded, bd=0, highlightthickness=0)
        self.right.grid(row=0, column=2, sticky="e", padx=(int(round(6 * self.scale)), px), pady=py)
        self.v_time, self.v_date, self.v_batt = tk.StringVar(value=""), tk.StringVar(value=""), tk.StringVar(value="")
        self.lbl_time = tk.Label(self.right, textvariable=self.v_time, font=self.f_time, anchor="e", justify="right", bd=0, highlightthickness=0)
        self.lbl_date = tk.Label(self.right, textvariable=self.v_date, font=self.f_meta, anchor="e", justify="right", bd=0, highlightthickness=0)
        self.lbl_batt = tk.Label(self.right, textvariable=self.v_batt, font=self.f_meta, anchor="e", justify="right", bd=0, highlightthickness=0)
        self.lbl_time.pack(anchor="e")
        self.lbl_date.pack(anchor="e", pady=(int(round(2 * self.scale)), 0))
        self.lbl_batt.pack(anchor="e", pady=(int(round(1 * self.scale)), 0))

    def mark_interaction(self):
        now = time.monotonic()
        self.interaction_hold_until = max(self.interaction_hold_until, now + INTERACTION_HOLD_S)
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
    def pointer_inside(self) -> bool:
        try:
            px, py = self.root.winfo_pointerx(), self.root.winfo_pointery()
            rx, ry = self.root.winfo_rootx(), self.root.winfo_rooty()
            rw, rh = self.root.winfo_width(), self.root.winfo_height()
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
            self.expanded.place(relx=0.5, rely=0.5, anchor="center", relwidth=1.0, relheight=1.0)
        else:
            self.expanded.place_forget()
            self.collapsed.place(relx=0.5, rely=0.5, anchor="center", relwidth=1.0, relheight=1.0)

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
            if self.mode == "expanded" and self.outside_since is not None and (now - self.outside_since) >= HOVER_COLLAPSE_DELAY_S and now >= self.interaction_hold_until:
                self.set_mode("collapsed")

    def battery_string(self) -> str:
        p, c = self.power.snapshot()
        if p is None:
            return "Battery --"
        return f"Charging {p}%" if c else f"Battery {p}%"

    def volume_string(self, now: float) -> str:
        if not self.volume.ok:
            return "Volume --"
        if (now - self.last_volume_poll) >= 0.25 or self.volume_cached is None:
            self.volume_cached = self.volume.get()
            self.last_volume_poll = now
        if self.volume_cached is None:
            return "Volume --"
        return f"Volume {self.volume_cached}%"

    def derive(self, m: MediaSnapshot, now_mono: float):
        now = datetime.now()
        t_expanded = time_text(now, SHOW_SECONDS, USE_24H)
        t_collapsed = time_text(now, False, USE_24H)
        d = date_text(now)

        has_media = bool((m.title or "").strip() or (m.artist or "").strip())
        if has_media:
            collapsed = (m.title or m.artist).strip()
            title = (m.title or m.artist).strip()
            subtitle = m.artist.strip() if m.title.strip() and m.artist.strip() else (m.app.strip() or "Now Playing")
        elif m.available and m.app:
            collapsed = t_collapsed
            title = m.app.strip()
            subtitle = "No track metadata available"
        else:
            collapsed = t_collapsed
            title = "Nothing playing"
            subtitle = "Start media in any app"

        ratio = 0.0
        if m.duration > 0.5:
            ratio = clamp(m.position / m.duration, 0.0, 1.0)
        self.progress_display += (ratio - self.progress_display) * 0.20

        play = ">" if (not m.available or m.paused) else "||"

        return {
            "collapsed": collapsed,
            "title": title,
            "subtitle": subtitle,
            "time": t_expanded,
            "date": d,
            "battery": self.battery_string(),
            "volume": self.volume_string(now_mono),
            "progress": float(clamp(self.progress_display, 0.0, 1.0)),
            "play": play,
            "can_prev": bool(m.available and m.can_prev),
            "can_next": bool(m.available and m.can_next),
            "can_pp": bool(m.available and m.can_pp),
            "has_media": has_media,
        }

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
        dw, dh = self.target_w - self.current_w, self.target_h - self.current_h
        self.vel_w = (self.vel_w + dw * 0.22) * 0.72
        self.vel_h = (self.vel_h + dh * 0.24) * 0.70
        self.current_w += self.vel_w
        self.current_h += self.vel_h
        if abs(dw) < 0.35 and abs(self.vel_w) < 0.20:
            self.current_w, self.vel_w = self.target_w, 0.0
        if abs(dh) < 0.35 and abs(self.vel_h) < 0.20:
            self.current_h, self.vel_h = self.target_h, 0.0
        self.current_w = clamp(self.current_w, self.collapsed_w_min, max(self.expanded_w, self.collapsed_w_max))
        self.current_h = clamp(self.current_h, self.collapsed_h, self.expanded_h)

    def panel_img(self, w: int, h: int):
        key = (self.palette.name, int(w), int(h))
        if key in self.panel_cache:
            return self.panel_cache[key]
        if len(self.panel_cache) > 80:
            self.panel_cache.clear()
        img = Image.new("RGBA", (max(1, w), max(1, h)), (0, 0, 0, 0))
        d = ImageDraw.Draw(img)
        r = int(clamp(self.radius, 8, min(w, h) // 2))
        d.rounded_rectangle((0, 0, w - 1, h - 1), radius=r, fill=self.palette.panel, outline=self.palette.panel_border, width=1)
        top_h = max(10, int(h * 0.45))
        d.rounded_rectangle((1, 1, w - 2, top_h), radius=max(1, r - 1), outline=self.palette.panel_highlight, width=1)
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
        cw, ch = max(20, w - 2 * self.inner_pad_x), max(20, h - 2 * self.inner_pad_y)
        self.content.place(relx=0.5, rely=0.5, anchor="center", width=cw, height=ch)

    def draw_progress(self, ratio: float):
        c = self.progress
        c.delete("all")
        w, h = max(20, c.winfo_width()), max(6, c.winfo_height())
        y = h // 2
        pad = int(round(2 * self.scale))
        th = max(2, int(round(3 * self.scale)))
        c.create_line(pad, y, w - pad, y, fill=self.palette.progress_track, width=th, capstyle="round")
        if ratio > 0.001:
            c.create_line(pad, y, pad + (w - 2 * pad) * float(clamp(ratio, 0.0, 1.0)), y, fill=self.palette.progress_fill, width=th, capstyle="round")

    def render(self, vm):
        lane_w = max(40, self.content.winfo_width() - int(round(22 * self.scale)))
        self.v_collapsed.set(ellipsize(vm["collapsed"], self.f_lane, lane_w))

        center_w = self.center.winfo_width()
        if center_w < 80:
            center_w = int(self.expanded_w * 0.40)
        self.v_title.set(ellipsize(vm["title"], self.f_title, center_w))
        self.v_sub.set(ellipsize(vm["subtitle"], self.f_sub, center_w))

        self.v_time.set(vm["time"])
        self.v_date.set(vm["date"])
        self.v_batt.set(vm["battery"])
        self.v_vol.set(vm["volume"])

        self.btn_pp.set_text(vm["play"])
        self.btn_prev.set_enabled(vm["can_prev"])
        self.btn_next.set_enabled(vm["can_next"])
        self.btn_pp.set_enabled(vm["can_pp"])

        vol_ok = bool(self.volume.ok)
        self.btn_vm.set_enabled(vol_ok)
        self.btn_vp.set_enabled(vol_ok)

        self.draw_progress(vm["progress"])
    def apply_palette(self):
        p = self.palette
        for f in (self.content, self.collapsed, self.expanded, self.left, self.center, self.right, self.transport, self.vol_chip):
            f.configure(bg=p.panel)
        self.transport.configure(bg=p.chip)
        self.vol_chip.configure(bg=p.chip)
        self.lbl_collapsed.configure(bg=p.panel, fg=p.text)
        self.lbl_title.configure(bg=p.panel, fg=p.text)
        self.lbl_sub.configure(bg=p.panel, fg=p.text_secondary)
        self.lbl_time.configure(bg=p.panel, fg=p.text)
        self.lbl_date.configure(bg=p.panel, fg=p.text_secondary)
        self.lbl_batt.configure(bg=p.panel, fg=p.text_muted)
        self.lbl_vol.configure(bg=p.chip, fg=p.text_secondary)
        self.progress.configure(bg=p.panel)
        for b in (self.btn_prev, self.btn_pp, self.btn_next, self.btn_vm, self.btn_vp):
            b.refresh()
        self.panel_cache.clear()

    def maybe_theme(self, now: float):
        if (now - self.last_theme_poll) < THEME_POLL_S:
            return
        self.last_theme_poll = now
        desired = "dark" if self.theme_mode == "dark" else "light" if self.theme_mode == "light" else system_theme()
        if desired != self.theme_name:
            self.theme_name = desired
            self.palette = DARK if desired == "dark" else LIGHT
            self.apply_palette()
            self.apply_geometry(force=True)

    def tick(self):
        now = time.monotonic()
        self.maybe_theme(now)
        self.update_mode_from_pointer(now)
        media = self.media.snapshot()
        vm = self.derive(media, now)
        self.render(vm)
        self.update_target_geometry(vm)
        self.spring_step()
        self.apply_geometry(force=False)
        self.root.after(FRAME_MS, self.tick)

    def quit(self):
        try:
            self.media.shutdown()
        except Exception:
            pass
        try:
            self.power.shutdown()
        except Exception:
            pass
        self.root.destroy()


def startup_path() -> str:
    appdata = os.environ.get("APPDATA", "")
    return os.path.join(appdata, "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "DynamicIsland.lnk")


def install_shortcut(script_path: str):
    try:
        import win32com.client  # type: ignore
        shell = win32com.client.Dispatch("WScript.Shell")
        sc = shell.CreateShortCut(startup_path())
        sc.Targetpath = sys.executable
        sc.Arguments = f'"{script_path}"'
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
    py311 = os.path.join(os.path.expanduser("~"), "AppData", "Local", "Programs", "Python", "Python311")
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
            probe = subprocess.run(base + ["-c", check], capture_output=True, text=True, timeout=6)
            if probe.returncode != 0:
                continue
            env = os.environ.copy()
            env["DI_RELAUNCHED"] = "1"
            subprocess.Popen(base + [script_path], cwd=os.path.dirname(script_path), env=env)
            return True
        except Exception:
            continue
    return False


def main():
    script = os.path.abspath(__file__)
    if "--install" in sys.argv[1:]:
        install_shortcut(script)
    if maybe_relaunch_with_smtc(script):
        return
    root = tk.Tk()
    app = App(root)
    root.protocol("WM_DELETE_WINDOW", app.quit)
    root.mainloop()


if __name__ == "__main__":
    main()
