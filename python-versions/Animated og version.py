"""
DynamicIsland (Tkinter) — Apple-ish hover island (no adaptive bg) with a smooth ring.
Collapsed = centered title only. Expanded = controls + time/date + elegant orange progress ring.
Volume: animated Apple-like pill with -  XX%  + (no slider).

Install (Windows):
  pip install pillow psutil winsdk
Optional (audio-driven ring accuracy + volume control):
  pip install pycaw comtypes
"""

import sys, os, time, threading, asyncio, ctypes, math, subprocess, shutil, json
from ctypes import POINTER, cast
from dataclasses import dataclass, field, asdict
from datetime import datetime, timedelta
import tkinter as tk
from tkinter import ttk  # harmless if unused elsewhere
import tkinter.font as tkfont
from PIL import Image, ImageTk, ImageDraw, ImageFilter

try:
    import winsound  # stdlib on Windows
    HAS_WINSOUND = True
except Exception:
    HAS_WINSOUND = False

try:
    from winsdk.windows.ui.notifications import ToastNotificationManager, ToastNotification  # type: ignore
    from winsdk.windows.data.xml.dom import XmlDocument  # type: ignore
    HAS_TOAST = True
except Exception:
    try:
        from winrt.windows.ui.notifications import ToastNotificationManager, ToastNotification  # type: ignore
        from winrt.windows.data.xml.dom import XmlDocument  # type: ignore
        HAS_TOAST = True
    except Exception:
        HAS_TOAST = False

# ---------- optional imports ----------
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
    try:
        from winrt.windows.media.control import GlobalSystemMediaTransportControlsSessionManager as MediaManager
        from winrt.windows.media.control import GlobalSystemMediaTransportControlsSessionPlaybackStatus as PlaybackStatus
        HAS_SMTC = True
    except Exception:
        HAS_SMTC = False

HAS_PYCAW = False
try:
    from comtypes import CLSCTX_ALL  # type: ignore
    from pycaw.pycaw import AudioUtilities, IAudioEndpointVolume, IAudioMeterInformation  # type: ignore
    import comtypes  # type: ignore
    HAS_PYCAW = True
except Exception:
    pass

# Optional: Windows Storage Streams DataReader for SMTC thumbnail/artwork
_HAS_DATAREADER = False
_WinDataReader = None
try:
    from winsdk.windows.storage.streams import DataReader as _WinDataReader  # type: ignore
    _HAS_DATAREADER = True
except Exception:
    try:
        from winrt.windows.storage.streams import DataReader as _WinDataReader  # type: ignore
        _HAS_DATAREADER = True
    except Exception:
        pass

# ---------- config ----------
OPACITY = 0.82  # slightly transparent
BASE_FONT_SIZE = 11
SHOW_SECONDS = True
USE_24H = False
MEDIA_POLL_MS = 700  # keep background polling light
UI_TICK_MS    = 300  # avoid continuous layout churn on the Tk thread
RING_TICK_MS  = UI_TICK_MS
POWER_POLL_MS = 250   # reflect plug/unplug changes quickly

# layout
RADIUS = 24  # Slightly more rounded for Apple feel
PADDING_X = 18
PADDING_Y = 9
ALLOW_DRAG_BAND_PX = 100
SNAP_TOP_MARGIN_PX = 0  # Slightly reduced gap from top

MAX_WIDTH_RATIO_COLLAPSED = 0.13   # compact — title scrolls via marquee
MAX_WIDTH_RATIO_EXPANDED  = 0.52
MIN_WIDTH_COLLAPSED_PX    = 180
MIN_WIDTH_EXPANDED_PX     = 420
MEDIA_MIN_COLLAPSED_PX   = 140
MEDIA_MAX_COLLAPSED_PX   = 260    # compact; marquee handles overflow
MEDIA_MIN_EXPANDED_PX    = 300
MEDIA_MAX_EXPANDED_PX    = 520

# volume pill
VOL_PILL_WIDTH_PX  = 110
VOL_PILL_HEIGHT_PX = 26
VOL_STEP           = 2    # +/- increment

SPACE_PX       = 10
SHOW_DIVIDER   = True
SHOW_APP_BADGE = False

# Apple's actual Dynamic Island palette
DARK  = dict(bg="#1C1C1E", fg="#FFFFFF", divider="#3A3A3C", fg_dim="#98989F", track="#2C2C2E", fill="#FFFFFF", accent="#FF9F0A")
LIGHT = dict(bg="#F2F2F7", fg="#000000", divider="#C6C6C8", fg_dim="#8E8E93", track="#D1D1D6", fill="#000000", accent="#FF9F0A")

# collapse/expand
HOVER_COLLAPSE_DELAY_MS = 300  # Slightly longer for more deliberate feel
COLLAPSED_HEIGHT_PX     = 36
EXPANDED_HEIGHT_PX      = 58

# animations - Apple uses ~350-400ms with spring easing
ANIM_MS     = 220
ANIM_STEPS  = 20
BACKGROUND_CACHE_LIMIT = 80

# progress ring
RING_DIAM_PX  = 20
RING_THICK_PX = 2.5
RING_COLOR    = "#FF9F0A"  # iOS orange

FALLBACK_DURATION_SEC = 240
BUFFER_GRACE_SEC = 0.0

# audio peak detection
PEAK_POLL_MS    = 100
PEAK_RECENT_S   = 3.0
PEAK_THRESHOLD  = 0.010

# timer / alarm
TIMER_ALARM_STATE_FILE = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    ".timer_alarm_state.json"
)
TIMER_TICK_MS              = 200    # UI poll for timer/alarm transitions
DEFAULT_SNOOZE_MIN         = 5
EXTRA_SNOOZE_MIN           = 10
ALARM_RING_TIMEOUT_S       = 60     # auto-stop ringing after this many seconds
TIMER_DONE_DISPLAY_S       = 5      # auto-clear "Timer done" from compact island
ALARM_BEEP_INTERVAL_S      = 1.4    # gap between beep bursts while ringing
TIMER_PRESETS_MIN          = (5, 10, 15, 25, 30)
REDUCE_MOTION              = False  # respected for pulse animations
ALARM_PULSE_COLOR          = "#FF453A"   # iOS red for ringing
TIMER_DONE_COLOR           = "#30D158"   # iOS green for completion
TIMER_COLOR                = "#FF9F0A"   # iOS orange (matches ring)

# ---------- utils ----------
def _dpi_scale(hwnd: int) -> float:
    try:
        user32 = ctypes.windll.user32
        try:
            user32.GetDpiForWindow.restype = ctypes.c_uint
            return max(0.5, user32.GetDpiForWindow(hwnd) / 96.0)
        except Exception:
            user32.GetDpiForSystem.restype = ctypes.c_uint
            return max(0.5, user32.GetDpiForSystem() / 96.0)
    except Exception:
        return 1.0

def _rounded_with_shadow(w, h, r, fill):
    """Fast rounded pill renderer; single Image object to minimise allocations."""
    pad = 4
    im = Image.new("RGBA", (w + pad * 2, h + pad * 2), (0, 0, 0, 0))
    draw = ImageDraw.Draw(im)
    # Shadow drawn first (slightly offset down), pill on top covers the overlap
    draw.rounded_rectangle([pad, pad + 1, pad + w - 1, pad + h],     r,             fill=(0, 0, 0, 72))
    draw.rounded_rectangle([pad + 1, pad + 2, pad + w - 2, pad + h - 1], max(1, r - 1), fill=(0, 0, 0, 28))
    # Pill fill + inner highlight + hairline outline
    draw.rounded_rectangle([pad, pad, pad + w - 1, pad + h - 1],     r,             fill=fill)
    draw.rounded_rectangle([pad + 1, pad + 1, pad + w - 2, pad + h - 2], max(1, r - 1),
                           outline=(255, 255, 255, 42), width=1)
    draw.rounded_rectangle([pad, pad, pad + w - 1, pad + h - 1],     r,
                           outline=(0, 0, 0, 55), width=1)
    return ImageTk.PhotoImage(im)

def _choose_font():
    try:
        fams = set(tkfont.families())
        for name in ("SF Pro Text", "SF Pro Display", "San Francisco", "SF Pro", "SFUIText"):
            if name in fams: return name
    except Exception:
        pass
    return "Segoe UI"

def _ease_out_cubic(t: float) -> float:
    return 1 - (1 - t) ** 3

def _ease_out_expo(t: float) -> float:
    """Exponential ease-out - fast start, smooth stop"""
    return 1 if t == 1 else 1 - pow(2, -10 * t)

def _ease_out_back(t: float) -> float:
    """Slight overshoot for Apple-like spring feel"""
    c1 = 1.70158
    c3 = c1 + 1
    return 1 + c3 * pow(t - 1, 3) + c1 * pow(t - 1, 2)

def _ease_out_quart(t: float) -> float:
    """Smooth quartic ease-out"""
    return 1 - pow(1 - t, 4)

def _spring_ease(t: float) -> float:
    """Apple-style spring animation with subtle bounce"""
    if t == 0 or t == 1:
        return t
    # Damped spring approximation
    return 1 - math.cos(t * math.pi / 2) * math.exp(-t * 3)

class _Tooltip:
    """Apple-style tooltip with rounded corners and subtle shadow."""
    def __init__(self, widget, text: str):
        self.widget = widget
        self.text = text
        self.tip = None
        self._after_id = None
        widget.bind("<Enter>", self._schedule_show)
        widget.bind("<Leave>", self.hide)
    
    def _schedule_show(self, event=None):
        """Delay tooltip appearance like macOS"""
        self._after_id = self.widget.after(400, self.show)
    
    def show(self, event=None):
        if self.tip or not self.text: return
        x = self.widget.winfo_rootx() + self.widget.winfo_width() // 2 - 20
        y = self.widget.winfo_rooty() + self.widget.winfo_height() + 8
        self.tip = tk.Toplevel(self.widget)
        self.tip.wm_overrideredirect(True)
        self.tip.attributes("-topmost", True)
        try:
            self.tip.attributes("-alpha", 0.95)
        except: pass
        # Apple-style dark tooltip
        frame = tk.Frame(self.tip, bg="#1C1C1E", bd=0, highlightthickness=0)
        frame.pack(fill="both", expand=True, padx=1, pady=1)
        lbl = tk.Label(frame, text=self.text, bg="#1C1C1E", fg="#FFFFFF",
                       bd=0, padx=10, pady=6, font=(_choose_font(), 10))
        lbl.pack()
        self.tip.wm_geometry(f"+{x}+{y}")
    
    def hide(self, event=None):
        if self._after_id:
            try: self.widget.after_cancel(self._after_id)
            except: pass
            self._after_id = None
        if self.tip:
            try: self.tip.destroy()
            except Exception: pass
            self.tip = None

class SYSTEM_POWER_STATUS(ctypes.Structure):
    _fields_ = [
        ("ACLineStatus", ctypes.c_ubyte),
        ("BatteryFlag", ctypes.c_ubyte),
        ("BatteryLifePercent", ctypes.c_ubyte),
        ("SystemStatusFlag", ctypes.c_ubyte),
        ("BatteryLifeTime", ctypes.c_uint),
        ("BatteryFullLifeTime", ctypes.c_uint),
    ]

def _read_windows_battery():
    try:
        status = SYSTEM_POWER_STATUS()
        ok = ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(status))
        if not ok:
            return None, None
        pct = None if status.BatteryLifePercent == 255 else int(status.BatteryLifePercent)
        charging = None if status.ACLineStatus == 255 else (status.ACLineStatus == 1)
        return pct, charging
    except Exception:
        return None, None

# ---------- Volume pill (no slider) ----------
class VolumePill(tk.Canvas):
    """
    Apple-like animated volume control: [-]  XX%  [+]
    """
    __slots__ = (
        "_get_theme","_get","_set","_step",
        "_W_px","_H_px","_anim_after","_pulse_after","_pulse_t",
        "_value","_display","_side_w","font_pct","font_btn",
        "_get_muted","_set_muted","_muted",
    )

    def __init__(self, master, width=120, height=24, get_theme=lambda: DARK,
                 get_cb=lambda: 50, set_cb=lambda v: None, step=5,
                 get_muted_cb=None, set_muted_cb=None, **kw):
        super().__init__(master, width=width, height=height, highlightthickness=0, bd=0, **kw)
        self._get_theme = get_theme
        self._get = get_cb
        self._set = set_cb
        self._step = max(1, int(step))
        self._get_muted = get_muted_cb or (lambda: False)
        self._set_muted = set_muted_cb or (lambda _m: None)
        self._muted = False
        try:
            self._muted = bool(get_muted_cb()) if get_muted_cb else False
        except Exception:
            self._muted = False

        # IMPORTANT: do not shadow tkinter's internal self._w
        self._W_px = int(width)
        self._H_px = int(height)

        self._anim_after = None
        self._pulse_after = None
        self._pulse_t = 0.0
        self._value = max(0, min(100, int(self._get() or 50)))
        self._display = float(self._value)  # tweened

        # hit zones
        self._side_w = max(22, int(self._H_px*0.9))
        self.configure(bg=self._get_theme()["bg"])
        self._choose_fonts()

        # events
        self.bind("<Button-1>", self._on_click)
        self.bind("<Motion>",   self._on_motion)
        self.bind("<Leave>",    lambda e: self.config(cursor=""))

        self._draw_static()
        self._paint(force=True)

    # API
    @property
    def width_px(self): return self._W_px
    @property
    def height_px(self): return self._H_px

    def apply_theme(self):
        self.configure(bg=self._get_theme()["bg"])
        self._draw_static(); self._paint(force=True)

    def set_percent(self, pct: int, animate=True, pulse=False):
        pct = max(0, min(100, int(pct)))
        self._value = pct
        if not animate:
            self._display = float(pct)
            self._paint(force=True)
            return
        self._tween_to(float(pct))
        if pulse:
            self._start_pulse()

    # internals
    def _choose_fonts(self):
        fam = _choose_font()
        self.font_pct = tkfont.Font(family=fam, size=max(10, int(self._H_px*0.45)), weight="bold")
        self.font_btn = tkfont.Font(family=fam, size=max(10, int(self._H_px*0.52)), weight="bold")

    def _blend(self, c1, c2, t):
        def p(h): h = h.lstrip("#"); return tuple(int(h[i:i+2],16) for i in (0,2,4))
        r1,g1,b1 = p(c1); r2,g2,b2 = p(c2)
        r=int(r1*(1-t)+r2*t); g=int(g1*(1-t)+g2*t); b=int(b1*(1-t)+b2*t)
        return f"#{r:02x}{g:02x}{b:02x}"

    def _draw_static(self):
        self.delete("static")
        c = self._get_theme()
        x0,y0,x1,y1 = 0,0,self._W_px,self._H_px
        r = (self._H_px)//2
        track = c["track"]
        border = self._blend(track, "#000000", 0.25 if c["bg"]!=LIGHT["bg"] else 0.10)
        # track
        self.create_rounded_rect(x0, y0, x1, y1, r, fill=track, outline=border, width=1, tags=("static",))
        # highlight
        hi = self._blend(track, "#FFFFFF", 0.08 if c["bg"]!=LIGHT["bg"] else 0.10)
        self.create_rounded_rect(x0+1, y0+1, x1-1, y0+max(2, r//2), r-1, fill=hi, outline="", tags=("static",))

    def _paint(self, force=False):
        self.delete("dynamic")
        c = self._get_theme()
        x0,y0,x1,y1 = 0,0,self._W_px,self._H_px
        mid_y = self._H_px//2

        # +/- zones — dim when muted so the pill reads as inactive
        btn_fill = c["fg_dim"] if self._muted else c["fg"]
        lw = self._side_w; rw = self._side_w
        self.create_text((lw//2, mid_y), text="–", fill=btn_fill, font=self.font_btn, tags=("dynamic","btn_minus"))
        self.create_text((self._W_px - rw//2, mid_y), text="+", fill=btn_fill, font=self.font_btn, tags=("dynamic","btn_plus"))

        # Center: show mute indicator or animated percentage
        pct = int(round(self._display))
        if self._muted:
            # Speaker-slash drawn as unicode; fall back to plain cross if glyph absent
            pct_s = "✕"
            pct_fill = c["fg_dim"]
            dy = 0.0
        else:
            pct_s = f"{pct}%"
            pct_fill = c["fg"]
            dy = -2 * math.sin(min(1.0, abs(self._display - self._value) / 8.0) * math.pi)
        self.create_text((self._W_px//2, mid_y + dy), text=pct_s, fill=pct_fill, font=self.font_pct, tags=("dynamic","pct"))

        # pulse overlay on click (subtle)
        if self._pulse_t > 0:
            t = self._pulse_t  # 0..1
            ring_w = int(2 + 2*t)
            ring_color = self._blend("#FFFFFF", c["bg"], 0.45)
            r = self._H_px//2
            self.create_rounded_rect(3, 3, self._W_px-3, self._H_px-3, r-3,
                                     outline=ring_color, width=ring_w, tags=("dynamic",), fill="")

    def _tween_to(self, target):
        if self._anim_after:
            try: self.after_cancel(self._anim_after)
            except Exception: pass
        start = float(self._display); end = float(target)
        if abs(end - start) < 0.01:
            self._display = end; self._paint()
            return
        steps, dur = 10, 140
        interval = max(12, dur//steps)
        def step(i=1):
            if i>steps:
                self._display = end; self._paint()
                return
            self._display = start + (end-start)*_ease_out_cubic(i/steps)
            self._paint()
            self._anim_after = self.after(interval, step, i+1)
        step()

    def _start_pulse(self):
        if self._pulse_after:
            try: self.after_cancel(self._pulse_after)
            except Exception: pass
        self._pulse_t = 0.001
        def step():
            self._pulse_t += 0.12
            if self._pulse_t >= 1.0:
                self._pulse_t = 0.0
                self._paint()
                return
            self._paint()
            self._pulse_after = self.after(16, step)
        step()

    # mouse interaction
    def _on_click(self, e):
        lw = self._side_w; rw = self._side_w
        if e.x <= lw:
            self._bump(-self._step)
        elif e.x >= self._W_px - rw:
            self._bump(+self._step)
        else:
            self._toggle_mute()

    def _on_motion(self, _e):
        # Entire pill is interactive — always show hand cursor
        self.config(cursor="hand2")

    def _bump(self, delta):
        cur = max(0, min(100, int(self._get() or 0)))
        new = max(0, min(100, cur + delta))
        if new != cur:
            try: self._set(new)
            except Exception: pass
        self.set_percent(new, animate=True, pulse=True)

    def _toggle_mute(self):
        try:
            new_muted = not self._muted
            self._set_muted(new_muted)
            self._muted = new_muted
            self._paint(force=True)
        except Exception:
            pass

    def update_muted(self, muted: bool):
        """Sync pill display to current system mute state (call from UI thread)."""
        m = bool(muted)
        if m != self._muted:
            self._muted = m
            self._paint(force=True)

    # helper: rounded rect
    def create_rounded_rect(self, x1, y1, x2, y2, r, **kwargs):
        r = max(0, min(r, (y2-y1)//2, (x2-x1)//2))
        pts = [(x1+r,y1),(x2-r,y1),(x2,y1),(x2,y1+r),(x2,y2-r),(x2,y2),
               (x2-r,y2),(x1+r,y2),(x1,y2),(x1,y2-r),(x1,y1+r),(x1,y1)]
        return self.create_polygon(pts, smooth=True, splinesteps=36, **kwargs)

# ---------- marquee strip ----------
class MarqueeCanvas(tk.Canvas):
    """
    Smooth pixel-scrolling text strip for the collapsed island pill.
    Static when text fits; continuously scrolls when text overflows.
    Manages its own 60fps tick loop internally.
    """
    _SPEED_PPS  = 55.0   # pixels per second
    _PAUSE_MS   = 2400   # pause at start/wrap before scrolling
    _GAP_PX     = 24     # gap between end of text and looping copy
    _TICK_MS    = 15     # ~67 fps

    def __init__(self, master, get_theme, font, height=20, **kw):
        super().__init__(master, highlightthickness=0, bd=0,
                         height=height, **kw)
        self._get_theme = get_theme
        self._font      = font
        self._text        = ""
        self._text_w      = 0
        self._active      = False   # True while in collapsed mode
        self._px          = 0.0    # scroll offset in pixels
        self._phase       = "pause"
        self._pause_rem   = float(self._PAUSE_MS)
        self._last_t      = 0.0
        self._after       = None
        self._force_scroll = False  # when True, always scroll regardless of overflow
        self.bind("<Configure>", lambda _e: self._redraw())

    # ── public ──────────────────────────────────────────────────────────────
    def set_text(self, text: str):
        t = (text or "").strip()
        if t == self._text:
            self._redraw()
            return
        self._text   = t
        self._text_w = self._font.measure(t)
        self._reset()
        self._redraw()

    def set_font(self, font):
        self._font   = font
        self._text_w = self._font.measure(self._text)
        self._reset()
        self._redraw()

    def activate(self):
        """Call when the strip becomes visible (collapsed mode)."""
        self._active = True
        self._reset()
        self._start_loop()
        self._redraw()

    def deactivate(self):
        """Call when the strip is hidden (expanded mode)."""
        self._active = False
        self._stop_loop()

    def apply_theme(self, bg):
        self.configure(bg=bg)
        self._redraw()

    # ── internals ───────────────────────────────────────────────────────────
    def _vw(self):
        w = self.winfo_width()
        return w if w > 10 else max(80, int(self.cget("width") or 80))

    def _vh(self):
        h = self.winfo_height()
        return h if h > 4 else int(self.cget("height") or 20)

    def set_force_scroll(self, val: bool):
        """Always scroll the text, even when it fits — ticker-tape mode."""
        changed = bool(val) != self._force_scroll
        self._force_scroll = bool(val)
        if changed:
            self._reset()
            self._redraw()

    def _needs_scroll(self):
        if self._force_scroll and self._active and self._text:
            return True
        return self._active and (self._text_w > self._vw() - 12)

    def _reset(self):
        self._px        = 0.0
        self._phase     = "pause"
        self._pause_rem = float(self._PAUSE_MS)

    def _start_loop(self):
        self._stop_loop()
        self._last_t = time.monotonic()
        self._tick()

    def _stop_loop(self):
        if self._after:
            try: self.after_cancel(self._after)
            except Exception: pass
            self._after = None

    def _tick(self):
        if not self._active:
            self._after = None
            return
        now  = time.monotonic()
        dt_s = max(0.0, now - self._last_t)
        self._last_t = now

        if self._needs_scroll():
            if self._phase == "pause":
                self._pause_rem -= dt_s * 1000.0
                if self._pause_rem <= 0:
                    self._phase = "run"
            else:
                self._px += self._SPEED_PPS * dt_s
                cycle = self._text_w + self._GAP_PX
                if self._px >= cycle:
                    self._px = 0.0
                    self._phase = "pause"
                    self._pause_rem = float(self._PAUSE_MS)
            self._redraw()

        self._after = self.after(self._TICK_MS, self._tick)

    def _redraw(self):
        self.delete("all")
        c = self._get_theme()
        bg = c["bg"]
        fg = c["fg"]
        self.configure(bg=bg)
        text = self._text
        if not text:
            return
        vw = self._vw()
        vh = self._vh()
        mid_y = max(1, vh // 2)

        if not self._needs_scroll():
            self.create_text(vw // 2, mid_y, text=text,
                             fill=fg, font=self._font, anchor="center")
            return

        # Draw primary copy
        x0 = 8.0 - self._px
        self.create_text(x0, mid_y, text=text,
                         fill=fg, font=self._font, anchor="w")
        # Draw wrap-around copy
        x1 = x0 + self._text_w + self._GAP_PX
        if x1 < vw + self._text_w:
            self.create_text(x1, mid_y, text=text,
                             fill=fg, font=self._font, anchor="w")

    def destroy(self):
        self._stop_loop()
        super().destroy()


# ---------- sound wave ----------
class SoundWaveCanvas(tk.Canvas):
    """
    Animated equalizer bars — bounces when playing, dims/freezes when paused.
    Uses sinusoidal per-bar animation with varied phases/speeds.
    """
    _PHASES = (0.0, 1.1, 2.3, 0.7, 1.8)
    _SPEEDS = (5.5, 7.2, 4.8, 6.3, 5.0)
    _TICK_MS = 75   # ~13 fps — light enough for background

    def __init__(self, master, get_theme, num_bars=4, width=22, height=16, **kw):
        super().__init__(master, width=width, height=height,
                         highlightthickness=0, bd=0, **kw)
        self._get_theme = get_theme
        self._num_bars  = num_bars
        self._W, self._H = width, height
        self._playing   = False
        self._t         = 0.0
        self._after     = None
        self._heights   = [0.25] * num_bars
        self._phases    = list(self._PHASES[:num_bars])
        self._speeds    = list(self._SPEEDS[:num_bars])
        # fill up if more bars than presets
        while len(self._phases) < num_bars:
            self._phases.append(len(self._phases) * 1.3)
            self._speeds.append(5.0 + len(self._speeds) * 0.5)
        self.bind("<Configure>", lambda _e: self._redraw())

    # ── public ────────────────────────────────────────────────────────────────
    def set_playing(self, playing: bool):
        was = self._playing
        self._playing = bool(playing)
        if self._playing and not was:
            self._start_loop()
        elif not self._playing and was:
            self._stop_loop()
            for i in range(self._num_bars):
                self._heights[i] = 0.25
            self._redraw()

    def apply_theme(self):
        self.configure(bg=self._get_theme()["bg"])
        self._redraw()

    # ── internals ─────────────────────────────────────────────────────────────
    def _start_loop(self):
        self._stop_loop()
        self._tick()

    def _stop_loop(self):
        if self._after:
            try: self.after_cancel(self._after)
            except Exception: pass
            self._after = None

    def _tick(self):
        if not self._playing:
            self._after = None
            return
        self._t += self._TICK_MS / 1000.0
        for i in range(self._num_bars):
            raw = math.sin(self._t * self._speeds[i] + self._phases[i])
            self._heights[i] = max(0.12, min(1.0, 0.20 + 0.72 * (raw * 0.5 + 0.5)))
        self._redraw()
        self._after = self.after(self._TICK_MS, self._tick)

    def _redraw(self):
        self.delete("all")
        c = self._get_theme()
        self.configure(bg=c["bg"])
        W = self.winfo_width()
        H = self.winfo_height()
        if W <= 2 or H <= 2:
            W, H = self._W, self._H
        n   = self._num_bars
        gap = max(1, W // max(1, n * 3))
        bar_w = max(2, (W - gap * max(0, n - 1)) // n)
        for i in range(n):
            x0    = i * (bar_w + gap)
            x1    = x0 + bar_w - 1
            h_frac = self._heights[i] if self._playing else 0.25
            bar_h  = max(2, int(H * h_frac))
            y0     = H - bar_h
            color  = c["accent"] if self._playing else c["fg_dim"]
            self.create_rectangle(x0, y0, x1, H - 1, fill=color, outline="", width=0)

    def destroy(self):
        self._stop_loop()
        super().destroy()


# ---------- services ----------
@dataclass
class MediaState:
    title: str = ""
    artist: str = ""
    app: str = ""
    paused: bool = False
    can_prev: bool = False
    can_next: bool = False
    can_playpause: bool = True
    position: float = 0.0
    duration: float = 0.0
    rate: float = 1.0

import asyncio, threading

class MediaService:
    def __init__(self, poll_ms=1000):
        self.poll_ms = poll_ms
        self._state = MediaState()
        self._lock = threading.Lock()
        self._loop = None
        self._ready = threading.Event()
        threading.Thread(target=self._run_loop, daemon=True).start()
        self._ready.wait(timeout=1.0)
        self._manager_cache = None
        self._manager_last_try = 0.0
        self._manager_retry_s = 2.0
        self._session_cache = None
        self._session_cache_at = 0.0
        self._session_hold_s = 2.0
        self._last_meta = ("", "", "")
        # Artwork cache — guarded by _artwork_lock (written in asyncio loop, read on UI thread)
        self._artwork_bytes = None   # raw bytes of current thumbnail
        self._artwork_key   = ""     # "title|artist" identity key
        self._artwork_lock  = threading.Lock()
        self._ps_script = (
            "$ErrorActionPreference='Stop'; "
            "Add-Type -AssemblyName System.Runtime.WindowsRuntime; "
            "$null=[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager,Windows.Media.Control,ContentType=WindowsRuntime]; "
            "$mgr=[System.WindowsRuntimeSystemExtensions]::AsTask([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()).GetAwaiter().GetResult(); "
            "if($null -eq $mgr){return}; "
            "$s=$mgr.GetCurrentSession(); "
            "if($null -eq $s){$all=$mgr.GetSessions(); foreach($x in $all){$pi=$x.GetPlaybackInfo(); if([string]$pi.PlaybackStatus -eq 'Playing'){$s=$x; break}; if($null -eq $s){$s=$x}}}; "
            "if($null -eq $s){return}; "
            "$props=[System.WindowsRuntimeSystemExtensions]::AsTask($s.TryGetMediaPropertiesAsync()).GetAwaiter().GetResult(); "
            "$info=$s.GetPlaybackInfo(); "
            "$tl=$s.GetTimelineProperties(); "
            "[pscustomobject]@{title=[string]$props.Title;artist=[string]$props.Artist;app=[string]$s.SourceAppUserModelId;status=[string]$info.PlaybackStatus;position=[double]$tl.Position.TotalSeconds;duration=[double]$tl.EndTime.TotalSeconds} | ConvertTo-Json -Compress"
        )
        if self._loop is not None:
            asyncio.run_coroutine_threadsafe(self._poll(), self._loop)

    def _run_loop(self):
        try:
            ctypes.windll.ole32.CoInitializeEx(None, 0)
        except Exception:
            pass
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        self._loop = loop
        self._ready.set()
        loop.run_forever()

    def shutdown(self):
        try:
            if self._loop is not None:
                self._loop.call_soon_threadsafe(self._loop.stop)
        except Exception: pass

    def get(self) -> MediaState:
        with self._lock:
            return MediaState(**self._state.__dict__)

    # controls
    def play_pause(self):
        if not HAS_SMTC or self._loop is None: return
        asyncio.run_coroutine_threadsafe(self._toggle_pp(), self._loop)
    def next(self):
        if not HAS_SMTC or self._loop is None: return
        asyncio.run_coroutine_threadsafe(self._ctrl("next"), self._loop)
    def prev(self):
        if not HAS_SMTC or self._loop is None: return
        asyncio.run_coroutine_threadsafe(self._ctrl("prev"), self._loop)

    async def _manager(self):
        if self._manager_cache is not None:
            return self._manager_cache
        now = time.monotonic()
        if (now - self._manager_last_try) < self._manager_retry_s:
            return None
        self._manager_last_try = now
        try:
            self._manager_cache = await MediaManager.request_async()
        except Exception:
            self._manager_cache = None
        return self._manager_cache

    async def _get_props_safe(self, session):
        """SMTC can briefly return empty props; retry a few times before giving up."""
        for i in range(4):
            try:
                props = await session.try_get_media_properties_async()
                title = (getattr(props, "title", "") or "").strip()
                subtitle = (getattr(props, "subtitle", "") or "").strip()
                album_title = (getattr(props, "album_title", "") or "").strip()
                artist = (getattr(props, "artist", "") or "").strip()
                album_artist = (getattr(props, "album_artist", "") or "").strip()
                if not title:
                    title = subtitle or album_title
                if not artist and album_artist:
                    artist = album_artist
                if title or artist:
                    return title, artist
            except Exception:
                pass
            if i < 3:
                await asyncio.sleep(0.05)
        return "", ""

    def _map_app(self, aumid: str) -> str:
        if not aumid: return ""
        s = aumid.lower()
        if "spotify" in s: return "Spotify"
        if "vlc" in s: return "VLC"
        if "zune" in s or "music" in s: return "Media"
        if "chrome" in s: return "Chrome"
        if "msedge" in s or "edge" in s: return "Edge"
        base = aumid.split("_")[0]
        return base.split(".")[-1].title()

    async def _select_best_session(self, mgr):
        try:
            current = mgr.get_current_session()
        except Exception:
            current = None
        try:
            sessions = list(mgr.get_sessions())
        except Exception:
            sessions = []

        # Prefer Windows-selected "current" session first.
        ordered = []
        seen = set()
        for s in ([current] + sessions):
            if s is None:
                continue
            sid = id(s)
            if sid in seen:
                continue
            seen.add(sid)
            ordered.append(s)

        best, best_rank = None, None
        for idx, s in enumerate(ordered):
            try:
                info = s.get_playback_info()
                status = getattr(info, "playback_status", None)
                score = 0
                if idx == 0: score += 2  # keep Windows' current session preference meaningful
                if status == PlaybackStatus.PLAYING: score += 3
                elif status == PlaybackStatus.PAUSED: score += 2
                elif status == PlaybackStatus.STOPPED: score += 1
                title, artist = await self._get_props_safe(s)
                try:
                    aumid = str(s.source_app_user_model_id)
                except Exception:
                    aumid = ""
                has_app = bool(self._map_app(aumid))
                try:
                    tl = s.get_timeline_properties()
                    has_timeline = bool(
                        self._to_secs(getattr(tl, "position", 0.0)) > 0 or
                        self._to_secs(getattr(tl, "end_time", 0.0)) > 0 or
                        self._to_secs(getattr(tl, "max_seek_time", 0.0)) > 0
                    )
                except Exception:
                    has_timeline = False
                if title: score += 4
                if artist: score += 2
                has_meta = bool(title or artist)
                # Prefer active sessions first, then metadata/timeline/app hints.
                rank = (
                    1 if status == PlaybackStatus.PLAYING else 0,
                    1 if has_meta else 0,
                    1 if has_timeline else 0,
                    1 if has_app else 0,
                    score,
                    -idx,
                )
            except Exception:
                continue
            if best_rank is None or rank > best_rank:
                best, best_rank = s, rank
        return best

    def _to_secs(self, maybe_td):
        try:
            if isinstance(maybe_td, timedelta): return max(0.0, maybe_td.total_seconds())
            return float(maybe_td) if maybe_td is not None else 0.0
        except Exception:
            return 0.0

    def _fallback_apps(self) -> str:
        wanted = {
            "spotify.exe": "Spotify",
            "music.ui.exe": "Media",
            "vlc.exe": "VLC",
            "chrome.exe": "Chrome",
            "msedge.exe": "Edge",
        }
        # Try psutil first when available.
        if HAS_PSUTIL:
            try:
                names = {
                    (p.info.get("name") or "").lower()
                    for p in psutil.process_iter(["name"])
                }
                picks = [label for exe, label in wanted.items() if exe in names]
                if picks:
                    return ", ".join(sorted(set(picks)))
            except Exception:
                pass
        # Fallback without dependencies.
        try:
            out = subprocess.run(
                ["tasklist", "/fo", "csv", "/nh"],
                capture_output=True,
                text=True,
                timeout=1.2,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            ).stdout.lower()
            picks = [label for exe, label in wanted.items() if exe in out]
            return ", ".join(sorted(set(picks)))
        except Exception:
            return ""

    def _fallback_media_snapshot(self):
        try:
            proc = subprocess.run(
                [
                    "powershell",
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-Command",
                    self._ps_script,
                ],
                capture_output=True,
                text=True,
                timeout=1.8,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            raw = (proc.stdout or "").strip()
            if not raw:
                return None
            line = raw.splitlines()[-1].strip()
            data = json.loads(line)
            title = str(data.get("title") or "").strip()
            artist = str(data.get("artist") or "").strip()
            app = self._map_app(str(data.get("app") or ""))
            status = str(data.get("status") or "").strip().lower()
            paused = status != "playing"
            try:
                pos = float(data.get("position") or 0.0)
            except Exception:
                pos = 0.0
            try:
                dur = float(data.get("duration") or 0.0)
            except Exception:
                dur = 0.0
            return dict(
                title=title,
                artist=artist,
                app=app,
                paused=paused,
                position=pos,
                duration=dur,
            )
        except Exception:
            return None

    async def _poll(self):
        while True:
            try:
                if HAS_SMTC:
                    mgr = await self._manager()
                    session = await self._select_best_session(mgr) if mgr else None
                    if session is None and self._session_cache is not None and (
                        time.monotonic() - self._session_cache_at
                    ) <= self._session_hold_s:
                        session = self._session_cache
                    self._session_cache = session
                    if session is not None:
                        self._session_cache_at = time.monotonic()
                    if session:
                        title, artist = await self._get_props_safe(session)

                        paused, can_prev, can_next, can_pp = False, False, False, True
                        pos, dur, rate = 0.0, 0.0, 1.0
                        try:
                            info = session.get_playback_info()
                            status = info.playback_status
                            paused = status in (PlaybackStatus.PAUSED, PlaybackStatus.STOPPED)
                            ctrls  = info.controls
                            can_prev = getattr(ctrls, "is_previous_enabled", True)
                            can_next = getattr(ctrls, "is_next_enabled", True)
                            can_pp   = getattr(ctrls, "is_play_pause_toggle_enabled", True) or getattr(ctrls, "is_play_enabled", True)
                            rate     = float(getattr(info, "playback_rate", 1.0) or 1.0)
                        except Exception:
                            pass
                        try:
                            tl = session.get_timeline_properties()
                            pos = self._to_secs(getattr(tl, "position", 0.0))
                            dur = self._to_secs(getattr(tl, "end_time", 0.0)) or self._to_secs(getattr(tl, "max_seek_time", 0.0))
                        except Exception:
                            pass
                        aumid = ""
                        try: aumid = str(session.source_app_user_model_id)
                        except Exception: pass
                        appname = self._map_app(aumid)
                        if title or artist:
                            self._last_meta = (title, artist, appname)
                        else:
                            last_t, last_a, last_app = self._last_meta
                            if last_t or last_a:
                                # Keep last known metadata to avoid app-only flicker on empty SMTC polls.
                                title = last_t
                                artist = last_a
                                if not appname:
                                    appname = last_app
                        self._set(title, artist, appname, paused, can_prev, can_next, can_pp, pos, dur, rate)
                        # Fetch artwork when the track identity changes (title|artist key)
                        new_art_key = f"{title}|{artist}"
                        if new_art_key != self._artwork_key:
                            art = await self._fetch_artwork(session)
                            with self._artwork_lock:
                                self._artwork_key   = new_art_key
                                self._artwork_bytes = art
                    else:
                        snap = await asyncio.get_running_loop().run_in_executor(None, self._fallback_media_snapshot)
                        if snap:
                            title = snap.get("title", "")
                            artist = snap.get("artist", "")
                            appname = snap.get("app", "")
                            paused = bool(snap.get("paused", False))
                            pos = float(snap.get("position", 0.0) or 0.0)
                            dur = float(snap.get("duration", 0.0) or 0.0)
                            if title or artist:
                                self._last_meta = (title, artist, appname)
                            else:
                                last_t, last_a, last_app = self._last_meta
                                if last_t or last_a:
                                    title, artist = last_t, last_a
                                    if not appname:
                                        appname = last_app
                            self._set(title, artist, appname, paused, False, False, False, pos, dur, 1.0)
                        else:
                            last_t, last_a, last_app = self._last_meta
                            appname = self._fallback_apps()
                            if last_t or last_a or last_app or appname:
                                self._set(last_t, last_a, appname or last_app, True, False, False, False, 0.0, 0.0, 1.0)
                            else:
                                self._set("", "", "", False, False, False, False, 0.0, 0.0, 1.0)
                else:
                    snap = await asyncio.get_running_loop().run_in_executor(None, self._fallback_media_snapshot)
                    if snap:
                        title = snap.get("title", "")
                        artist = snap.get("artist", "")
                        appname = snap.get("app", "")
                        paused = bool(snap.get("paused", False))
                        pos = float(snap.get("position", 0.0) or 0.0)
                        dur = float(snap.get("duration", 0.0) or 0.0)
                        if title or artist:
                            self._last_meta = (title, artist, appname)
                        else:
                            last_t, last_a, last_app = self._last_meta
                            if last_t or last_a:
                                title, artist = last_t, last_a
                                if not appname:
                                    appname = last_app
                        self._set(title, artist, appname, paused, False, False, False, pos, dur, 1.0)
                    else:
                        appname = self._fallback_apps()
                        self._set("", "", appname, False, False, False, False, 0.0, 0.0, 1.0)
            except Exception:
                pass
            sleep_s = max(0.2, self.poll_ms/1000.0)
            if not HAS_SMTC:
                sleep_s = max(1.2, sleep_s)
            await asyncio.sleep(sleep_s)

    def get_artwork(self):
        """Return (bytes_or_None, key_str) — safe to call from any thread."""
        with self._artwork_lock:
            return (self._artwork_bytes, self._artwork_key)

    async def _fetch_artwork(self, session) -> "bytes | None":
        """Fetch SMTC thumbnail bytes from current session. Returns None on any failure."""
        if not (_HAS_DATAREADER and _WinDataReader is not None):
            return None
        try:
            props = await session.try_get_media_properties_async()
            thumb_ref = getattr(props, "thumbnail", None)
            if thumb_ref is None:
                return None
            stream = await thumb_ref.open_read_async()
            size = int(getattr(stream, "size", 0))
            if not (100 < size < 5_000_000):
                return None
            reader = _WinDataReader(stream)
            loaded = await reader.load_async(size)
            raw = reader.read_bytes(int(loaded))
            return bytes(raw) if raw else None
        except Exception:
            return None

    def _set(self, title, artist, app, paused, prv, nxt, pp, position, duration, rate):
        with self._lock:
            self._state.title = title or ""
            self._state.artist = artist or ""
            self._state.app = app or ""
            self._state.paused = bool(paused)
            self._state.can_prev = bool(prv)
            self._state.can_next = bool(nxt)
            self._state.can_playpause = bool(pp)
            self._state.position = float(position or 0.0)
            self._state.duration = float(duration or 0.0)
            self._state.rate = float(rate or 1.0)

    async def _toggle_pp(self):
        try:
            mgr = await self._manager()
            s = self._session_cache or (mgr.get_current_session() if mgr else None)
            if not s: return
            try:
                await s.try_toggle_play_pause_async(); return
            except Exception:
                pass
            st = self.get()
            if st.paused: await s.try_play_async()
            else:         await s.try_pause_async()
        except Exception:
            pass

    async def _ctrl(self, which):
        try:
            mgr = await self._manager()
            s = self._session_cache or (mgr.get_current_session() if mgr else None)
        except Exception:
            s = None
        if not s: return
        try:
            if which=="next": await s.try_skip_next_async()
            elif which=="prev": await s.try_skip_previous_async()
        except Exception:
            pass

# ---------- optional audio peak service ----------
class AudioPeakService:
    def __init__(self, poll_ms=PEAK_POLL_MS):
        self.ok = HAS_PYCAW
        self.poll_ms = poll_ms
        self._hist = []
        self._lock = threading.Lock()
        if not self.ok:
            return
        try:
            dev = AudioUtilities.GetSpeakers()
            iface = dev.Activate(IAudioMeterInformation._iid_, CLSCTX_ALL, None)
            self.meter = cast(iface, POINTER(IAudioMeterInformation))
            threading.Thread(target=self._loop, daemon=True).start()
        except Exception:
            self.ok = False

    def _loop(self):
        max_len = max(1, int((PEAK_RECENT_S*1000)//self.poll_ms))
        while True:
            try:
                v = float(self.meter.GetPeakValue())  # 0..1
            except Exception:
                v = 0.0
            with self._lock:
                self._hist.append(v)
                if len(self._hist) > max_len:
                    self._hist = self._hist[-max_len:]
            time.sleep(max(0.02, self.poll_ms/1000.0))

    def is_playing(self) -> bool:
        if not self.ok:
            return False
        with self._lock:
            return any(v >= PEAK_THRESHOLD for v in self._hist[-5:])

# ---------- Timer / Alarm models & service ----------
@dataclass
class TimerState:
    phase: str = "idle"            # idle | running | paused | completed | canceled
    label: str = ""
    total_s: float = 0.0
    start_ts: float = 0.0          # epoch when current run started
    accumulated_s: float = 0.0     # elapsed before current run (for pause/resume)
    paused_remaining_s: float = 0.0
    completed_at: float = 0.0
    completion_acked: bool = False

    def remaining_s(self) -> float:
        if self.phase == "running" and self.start_ts > 0:
            elapsed = (time.time() - self.start_ts) + self.accumulated_s
            return max(0.0, self.total_s - elapsed)
        if self.phase == "paused":
            return max(0.0, self.paused_remaining_s)
        if self.phase == "completed":
            return 0.0
        return max(0.0, self.total_s)

    def progress(self) -> float:
        if self.total_s <= 0: return 0.0
        return max(0.0, min(1.0, 1.0 - self.remaining_s() / self.total_s))


@dataclass
class AlarmState:
    phase: str = "none"            # none | scheduled | ringing | snoozed | dismissed | canceled
    hour: int = 7
    minute: int = 0
    use_24h: bool = False
    label: str = ""
    target_ts: float = 0.0         # epoch target time
    ring_started_ts: float = 0.0
    snooze_until_ts: float = 0.0
    snooze_count: int = 0
    last_ring_acked: bool = True

    def time_until_s(self) -> float:
        ref = self.snooze_until_ts if self.phase == "snoozed" else self.target_ts
        if ref <= 0: return 0.0
        return max(0.0, ref - time.time())

    def time_str(self, force_24h: bool = None) -> str:
        use_24 = self.use_24h if force_24h is None else force_24h
        h, m = int(self.hour), int(self.minute)
        if use_24:
            return f"{h:02d}:{m:02d}"
        suffix = "AM" if h < 12 else "PM"
        h12 = h % 12
        if h12 == 0: h12 = 12
        return f"{h12}:{m:02d} {suffix}"


def _format_mmss(seconds: float) -> str:
    s = int(math.ceil(max(0.0, seconds)))
    if s >= 3600:
        h = s // 3600
        m = (s % 3600) // 60
        return f"{h}:{m:02d}:{s%60:02d}"
    return f"{s//60:02d}:{s%60:02d}"


def _format_human_delta(seconds: float) -> str:
    s = int(max(0, seconds))
    if s < 60: return f"{s}s"
    m = s // 60
    if m < 60: return f"{m}m"
    h, rem = divmod(m, 60)
    if rem == 0: return f"{h}h"
    return f"{h}h {rem}m"


class TimerAlarmService:
    """
    State-machine + persistence for a single timer and a single alarm.
    Uses real epoch timestamps so behaviour is correct across refresh,
    sleep, or delayed UI ticks. Sound + (best-effort) toast notifications.
    """

    def __init__(self, state_path: str = TIMER_ALARM_STATE_FILE,
                 on_event=None):
        self.state_path = state_path
        self.timer = TimerState()
        self.alarm = AlarmState()
        self._lock = threading.RLock()
        self._on_event = on_event  # callback(event_name, payload)
        self._sound_thread = None
        self._sound_stop = threading.Event()
        self._notif_thread = None
        self._load()

    # ---- persistence ----
    def _load(self):
        try:
            with open(self.state_path, "r", encoding="utf-8") as f:
                data = json.load(f) or {}
            t = data.get("timer") or {}
            a = data.get("alarm") or {}
            for k, v in t.items():
                if hasattr(self.timer, k):
                    setattr(self.timer, k, v)
            for k, v in a.items():
                if hasattr(self.alarm, k):
                    setattr(self.alarm, k, v)
            # If a timer was running when the app last closed, recompute state
            # using real timestamps. If it should already be completed, mark so.
            now = time.time()
            if self.timer.phase == "running":
                elapsed = (now - (self.timer.start_ts or now)) + (self.timer.accumulated_s or 0)
                if elapsed >= self.timer.total_s and self.timer.total_s > 0:
                    self.timer.phase = "completed"
                    self.timer.completed_at = now
                    self.timer.completion_acked = True  # don't loudly fire on cold start
            # Alarm: if scheduled time long passed and not snoozed, mark dismissed
            if self.alarm.phase == "scheduled" and self.alarm.target_ts > 0:
                if now > self.alarm.target_ts + ALARM_RING_TIMEOUT_S:
                    self.alarm.phase = "dismissed"
                    self.alarm.last_ring_acked = True
            if self.alarm.phase == "ringing":
                # If the app died mid-ring, treat as dismissed on cold start.
                self.alarm.phase = "dismissed"
                self.alarm.last_ring_acked = True
            self._save()
        except FileNotFoundError:
            pass
        except Exception:
            # Corrupt file — start fresh.
            self.timer = TimerState()
            self.alarm = AlarmState()

    def _save(self):
        try:
            payload = {"timer": asdict(self.timer), "alarm": asdict(self.alarm)}
            tmp = self.state_path + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(payload, f, indent=2)
            os.replace(tmp, self.state_path)
        except Exception:
            pass

    def _emit(self, name: str, payload=None):
        cb = self._on_event
        if cb is None: return
        try: cb(name, payload)
        except Exception: pass

    # ---- timer api ----
    def start_timer(self, total_s: float, label: str = ""):
        with self._lock:
            self._stop_sound()
            self.timer = TimerState(
                phase="running",
                label=(label or "").strip(),
                total_s=max(1.0, float(total_s)),
                start_ts=time.time(),
                accumulated_s=0.0,
                completion_acked=False,
            )
            self._save()
        self._emit("timer_started")

    def pause_timer(self):
        with self._lock:
            if self.timer.phase != "running": return
            elapsed = (time.time() - self.timer.start_ts) + self.timer.accumulated_s
            remaining = max(0.0, self.timer.total_s - elapsed)
            self.timer.accumulated_s = elapsed
            self.timer.paused_remaining_s = remaining
            self.timer.phase = "paused"
            self._save()
        self._emit("timer_paused")

    def resume_timer(self):
        with self._lock:
            if self.timer.phase != "paused": return
            self.timer.start_ts = time.time()
            self.timer.phase = "running"
            self._save()
        self._emit("timer_resumed")

    def reset_timer(self):
        with self._lock:
            self._stop_sound()
            label = self.timer.label
            total = self.timer.total_s
            self.timer = TimerState(label=label, total_s=total)
            self._save()
        self._emit("timer_reset")

    def cancel_timer(self):
        with self._lock:
            self._stop_sound()
            self.timer = TimerState(phase="canceled", label=self.timer.label)
            self._save()
        self._emit("timer_canceled")
        # Idle a moment later so UI can show "canceled" briefly if desired
        with self._lock:
            self.timer = TimerState()
            self._save()

    def ack_timer_completion(self):
        with self._lock:
            self.timer.completion_acked = True
            self._stop_sound()
            self._save()

    # ---- alarm api ----
    def set_alarm(self, hour: int, minute: int, use_24h: bool, label: str = ""):
        with self._lock:
            self._stop_sound()
            now = datetime.now()
            target = now.replace(hour=int(hour) % 24, minute=int(minute) % 60,
                                 second=0, microsecond=0)
            if target <= now:
                target = target + timedelta(days=1)
            self.alarm = AlarmState(
                phase="scheduled",
                hour=int(hour) % 24, minute=int(minute) % 60,
                use_24h=bool(use_24h),
                label=(label or "").strip(),
                target_ts=target.timestamp(),
                last_ring_acked=True,
            )
            self._save()
        self._emit("alarm_set")

    def delete_alarm(self):
        with self._lock:
            self._stop_sound()
            self.alarm = AlarmState()
            self._save()
        self._emit("alarm_deleted")

    def dismiss_alarm(self):
        with self._lock:
            self._stop_sound()
            if self.alarm.phase in ("ringing",):
                self.alarm.phase = "dismissed"
                self.alarm.last_ring_acked = True
                self._save()
        self._emit("alarm_dismissed")

    def snooze_alarm(self, minutes: int = DEFAULT_SNOOZE_MIN):
        with self._lock:
            self._stop_sound()
            if self.alarm.phase not in ("ringing", "snoozed", "scheduled"):
                return
            snooze_ts = time.time() + max(1, int(minutes)) * 60
            self.alarm.phase = "snoozed"
            self.alarm.snooze_until_ts = snooze_ts
            self.alarm.snooze_count += 1
            self.alarm.last_ring_acked = True
            self._save()
        self._emit("alarm_snoozed")

    def ack_alarm_ring(self):
        with self._lock:
            self.alarm.last_ring_acked = True

    # ---- ticking (called from UI thread) ----
    def tick(self):
        """
        Advance state based on real time.
        Returns a dict of newly-fired events for the UI to react to.
        """
        events = []
        now = time.time()
        with self._lock:
            # Timer completion
            if self.timer.phase == "running":
                elapsed = (now - self.timer.start_ts) + self.timer.accumulated_s
                if elapsed >= self.timer.total_s:
                    self.timer.phase = "completed"
                    self.timer.completed_at = now
                    self.timer.completion_acked = False
                    self._save()
                    events.append("timer_completed")
            # Auto-clear the "Timer done" compact-island banner after a short
            # display window so it doesn't linger forever once the user has
            # already heard / seen the completion.
            if (self.timer.phase == "completed"
                    and not self.timer.completion_acked
                    and self.timer.completed_at > 0
                    and now - self.timer.completed_at >= TIMER_DONE_DISPLAY_S):
                self.timer.completion_acked = True
                self._stop_sound()
                self._save()
                events.append("timer_done_auto_cleared")
            # Alarm scheduled -> ringing
            if self.alarm.phase == "scheduled" and self.alarm.target_ts > 0:
                if now >= self.alarm.target_ts:
                    self.alarm.phase = "ringing"
                    self.alarm.ring_started_ts = now
                    self.alarm.last_ring_acked = False
                    self._save()
                    events.append("alarm_ringing")
            # Snoozed -> ringing
            elif self.alarm.phase == "snoozed" and self.alarm.snooze_until_ts > 0:
                if now >= self.alarm.snooze_until_ts:
                    self.alarm.phase = "ringing"
                    self.alarm.ring_started_ts = now
                    self.alarm.last_ring_acked = False
                    self._save()
                    events.append("alarm_ringing")
            # Auto-stop endless ringing
            if self.alarm.phase == "ringing" and self.alarm.ring_started_ts > 0:
                if now - self.alarm.ring_started_ts >= ALARM_RING_TIMEOUT_S:
                    self.alarm.phase = "dismissed"
                    self.alarm.last_ring_acked = True
                    self._save()
                    events.append("alarm_auto_dismissed")
        for ev in events:
            self._emit(ev)
        return events

    # ---- priority for compact island ----
    def primary_state(self) -> str:
        """
        Returns one of:
          'alarm_ringing', 'timer_completed', 'timer_running', 'timer_paused',
          'alarm_snoozed', 'alarm_scheduled', 'idle'
        """
        a = self.alarm.phase
        t = self.timer.phase
        if a == "ringing": return "alarm_ringing"
        if t == "completed" and not self.timer.completion_acked: return "timer_completed"
        if t == "running":   return "timer_running"
        if t == "paused":    return "timer_paused"
        if a == "snoozed":   return "alarm_snoozed"
        if a == "scheduled": return "alarm_scheduled"
        return "idle"

    # ---- sound + notification ----
    def play_alert(self, kind: str = "timer"):
        """
        kind: 'timer' (single short chime) | 'alarm' (looping bursts).
        Loop is bounded by ALARM_RING_TIMEOUT_S; tick() will dismiss after that.
        """
        self._stop_sound()
        self._sound_stop = threading.Event()
        stop = self._sound_stop

        def run():
            if not HAS_WINSOUND:
                return
            try:
                if kind == "timer":
                    # Two short tones, once.
                    for f, d in ((880, 180), (1175, 220)):
                        if stop.is_set(): break
                        try: winsound.Beep(f, d)
                        except Exception: break
                else:
                    # Alarm: bounded loop until stop or timeout
                    started = time.time()
                    while not stop.is_set():
                        if time.time() - started > ALARM_RING_TIMEOUT_S:
                            break
                        try:
                            winsound.Beep(880, 200)
                            if stop.is_set(): break
                            winsound.Beep(660, 200)
                            if stop.is_set(): break
                            winsound.Beep(880, 200)
                        except Exception:
                            break
                        # Wait between bursts; respect early stop
                        stop.wait(ALARM_BEEP_INTERVAL_S)
            except Exception:
                pass

        self._sound_thread = threading.Thread(target=run, daemon=True)
        self._sound_thread.start()

    def _stop_sound(self):
        try:
            if self._sound_stop is not None:
                self._sound_stop.set()
        except Exception:
            pass

    def show_notification(self, title: str, body: str = ""):
        """Best-effort toast; silent failure if unavailable."""
        if not HAS_TOAST:
            return False
        def run():
            try:
                xml = (
                    "<toast><visual><binding template='ToastGeneric'>"
                    f"<text>{title}</text><text>{body}</text>"
                    "</binding></visual></toast>"
                )
                doc = XmlDocument()
                doc.load_xml(xml)
                notifier = ToastNotificationManager.create_toast_notifier(
                    "DynamicIsland.Timer"
                )
                notifier.show(ToastNotification(doc))
            except Exception:
                pass
        self._notif_thread = threading.Thread(target=run, daemon=True)
        self._notif_thread.start()
        return True

    def shutdown(self):
        self._stop_sound()
        with self._lock:
            self._save()


# ---------- Timer / Alarm Panel (Toplevel) ----------
class TimerAlarmPanel(tk.Toplevel):
    """
    Expanded controls for Timer + Alarm. Opens below the island when the
    user clicks the timer icon. Re-uses the app's theme. Closes on focus
    loss or Escape so it feels lightweight (similar to a popover).
    """

    PAD_X = 18
    PAD_Y = 16
    PANEL_WIDTH = 420

    def __init__(self, app, service: "TimerAlarmService"):
        super().__init__(app.root)
        self.app = app
        self.svc = service
        self._tick_after = None
        self._destroyed = False
        self._closing = False

        self.overrideredirect(True)
        try: self.attributes("-topmost", True)
        except Exception: pass
        try: self.attributes("-alpha", 0.985)
        except Exception: pass

        c = app.colors
        # Slightly elevated surface so it reads as a popover above the island.
        self._surface = self._tint(c["bg"], 0.06)
        self._chip_bg = c["track"]
        self._chip_bg_hover = self._tint(c["track"], 0.14)

        self.configure(bg=self._surface, bd=0, highlightthickness=1,
                       highlightbackground=c["divider"])

        fam = _choose_font()
        self.font_h     = tkfont.Font(family=fam, size=app.font_sz + 1, weight="bold")
        self.font_b     = tkfont.Font(family=fam, size=app.font_sz)
        self.font_btn   = tkfont.Font(family=fam, size=app.font_sz, weight="bold")
        self.font_l     = tkfont.Font(family=fam, size=max(9, app.font_sz - 2))
        self.font_caps  = tkfont.Font(family=fam, size=max(8, app.font_sz - 3),
                                      weight="bold")
        # Tabular monospace for clocks so digits don't jitter.
        clock_family = "Consolas" if "Consolas" in set(tkfont.families()) else fam
        self.font_clock = tkfont.Font(family=clock_family,
                                      size=max(28, app.font_sz + 22),
                                      weight="bold")
        self.font_total = tkfont.Font(family=clock_family,
                                      size=max(11, app.font_sz - 1))

        outer = tk.Frame(self, bg=self._surface)
        outer.pack(fill="both", expand=True, padx=self.PAD_X, pady=self.PAD_Y)
        outer.configure(width=self.PANEL_WIDTH)
        outer.pack_propagate(True)

        # ---------- TIMER section ----------
        timer_frame = tk.Frame(outer, bg=self._surface)
        timer_frame.pack(fill="x")
        self._build_timer_section(timer_frame)

        # divider
        tk.Frame(outer, bg=c["divider"], height=1).pack(fill="x", pady=(16, 14))

        # ---------- ALARM section ----------
        alarm_frame = tk.Frame(outer, bg=self._surface)
        alarm_frame.pack(fill="x")
        self._build_alarm_section(alarm_frame)

        # ---------- close hint ----------
        hint = tk.Label(outer, text="Esc to close", bg=self._surface,
                        fg=c["fg_dim"], font=self.font_l, anchor="e")
        hint.pack(fill="x", pady=(14, 0))

        self.bind("<Escape>", lambda _e: self.close())
        self.protocol("WM_DELETE_WINDOW", self.close)

        # Treat the popover as part of the island's hover region so the
        # island doesn't collapse the moment the cursor enters the panel.
        def _enter(_e=None):
            try:
                if self.app._collapse_after:
                    self.app.root.after_cancel(self.app._collapse_after)
                    self.app._collapse_after = None
            except Exception: pass
        def _leave(_e=None):
            try: self.app._schedule_collapse_check(160)
            except Exception: pass
        self.bind("<Enter>", _enter)
        self.bind("<Leave>", _leave)
        # Also bind to all child widgets so traversing inputs doesn't fire <Leave>
        # on the popover (Tk fires <Leave> on parent when entering a child only
        # in some configs; the explicit child binds avoid that risk).
        def _bind_children(w):
            try:
                w.bind("<Enter>", _enter, add="+")
                w.bind("<Leave>", _leave, add="+")
                for ch in w.winfo_children():
                    _bind_children(ch)
            except Exception: pass
        self.update_idletasks()
        _bind_children(self)

        self._position_under_island()
        self.deiconify()
        # Don't steal focus — that triggers FocusOut on the island root in
        # some setups and was the original cause of the collapse race.
        self._refresh()
        self._tick_after = self.after(250, self._auto_refresh)

    # ---------- layout helpers ----------
    def _btn(self, parent, text, cmd, kind="ghost", size="md", width=None):
        """
        Pill-style button.
          kind: 'accent' (orange filled) | 'ghost' (subtle) | 'danger' (red outline) |
                'success' (green filled)
          size: 'sm' | 'md' | 'lg'
        """
        c = self.app.colors
        if kind == "accent":
            fg = "#000000" if c is LIGHT else "#FFFFFF"
            bg = TIMER_COLOR
            hover = self._tint(bg, 0.12)
        elif kind == "danger":
            fg, bg = "#FFFFFF", "#FF453A"
            hover = self._tint(bg, 0.10)
        elif kind == "success":
            fg, bg = "#000000", TIMER_DONE_COLOR
            hover = self._tint(bg, 0.10)
        else:  # ghost
            fg, bg = c["fg"], self._chip_bg
            hover = self._chip_bg_hover

        if size == "sm":
            padx, pady, font = 10, 4, self.font_b
        elif size == "lg":
            padx, pady, font = 16, 9, self.font_btn
        else:
            padx, pady, font = 13, 7, self.font_btn

        b = tk.Label(parent, text=text, bg=bg, fg=fg,
                     font=font, padx=padx, pady=pady,
                     bd=0, cursor="hand2", anchor="center")
        if width is not None:
            b.configure(width=width)
        b.bind("<Button-1>", lambda _e: cmd())
        b.bind("<Enter>", lambda _e: b.configure(bg=hover))
        b.bind("<Leave>", lambda _e: b.configure(bg=bg))
        # remember base color for state-driven re-styling later
        b._base_bg = bg
        b._hover_bg = hover
        return b

    def _restyle_btn(self, btn, kind):
        """Swap a button's color kind in-place."""
        c = self.app.colors
        if kind == "accent":
            fg = "#000000" if c is LIGHT else "#FFFFFF"
            bg = TIMER_COLOR
        elif kind == "danger":
            fg, bg = "#FFFFFF", "#FF453A"
        elif kind == "success":
            fg, bg = "#000000", TIMER_DONE_COLOR
        else:
            fg, bg = c["fg"], self._chip_bg
        hover = self._tint(bg, 0.12)
        btn._base_bg = bg
        btn._hover_bg = hover
        btn.configure(bg=bg, fg=fg)
        # rebind hover to new colors
        btn.bind("<Enter>", lambda _e: btn.configure(bg=hover))
        btn.bind("<Leave>", lambda _e: btn.configure(bg=bg))

    def _tint(self, hexc, amt):
        """Lighten (amt > 0) or darken (amt < 0) a hex color."""
        try:
            h = hexc.lstrip("#")
            r, g, b = (int(h[i:i+2], 16) for i in (0, 2, 4))
            if amt >= 0:
                r = int(r + (255 - r) * amt)
                g = int(g + (255 - g) * amt)
                b = int(b + (255 - b) * amt)
            else:
                k = 1 + amt
                r = max(0, int(r * k))
                g = max(0, int(g * k))
                b = max(0, int(b * k))
            return f"#{r:02x}{g:02x}{b:02x}"
        except Exception:
            return hexc

    # backward-compat alias
    def _lighten(self, hexc, amt):
        return self._tint(hexc, amt)

    # ---------- timer ----------
    def _build_timer_section(self, parent):
        c = self.app.colors
        bg = self._surface

        # ---- header: TIMER caps + state pill ----
        head = tk.Frame(parent, bg=bg)
        head.pack(fill="x")
        tk.Label(head, text="TIMER", bg=bg, fg=c["fg"],
                 font=self.font_caps, anchor="w").pack(side="left")
        self.t_state_pill = tk.Label(head, text="", bg=self._chip_bg,
                                     fg=c["fg_dim"], font=self.font_l,
                                     padx=8, pady=2)
        self.t_state_pill.pack(side="right")

        # ---- clock row: big mm:ss + small "of total" + label on right ----
        clock_row = tk.Frame(parent, bg=bg)
        clock_row.pack(fill="x", pady=(10, 6))

        # Stack the clock and the "of XX:XX" subtitle vertically on the left.
        clock_left = tk.Frame(clock_row, bg=bg)
        clock_left.pack(side="left")
        self.t_clock = tk.Label(clock_left, text="00:00", bg=bg, fg=c["fg"],
                                font=self.font_clock, anchor="w")
        self.t_clock.pack(anchor="w")
        self.t_total_lbl = tk.Label(clock_left, text="", bg=bg, fg=c["fg_dim"],
                                    font=self.font_total, anchor="w")
        self.t_total_lbl.pack(anchor="w")

        self.t_label_lbl = tk.Label(clock_row, text="", bg=bg, fg=c["fg_dim"],
                                    font=self.font_b, anchor="e",
                                    wraplength=180, justify="right")
        self.t_label_lbl.pack(side="right", anchor="s", pady=(0, 6))

        # ---- progress bar (thicker, rounded ends) ----
        self.t_progress = tk.Canvas(parent, height=10, bg=bg,
                                    highlightthickness=0, bd=0)
        self.t_progress.pack(fill="x", pady=(2, 14))
        self.t_progress.bind("<Configure>", lambda _e: self._draw_progress())

        # ---- primary controls ----
        ctrls = tk.Frame(parent, bg=bg)
        ctrls.pack(fill="x", pady=(0, 12))
        # width=8 (chars) is wide enough for the longest label ("Resume") to prevent
        # layout jumps when switching between Pause / Resume / Start / Done.
        self.t_btn_primary = self._btn(ctrls, "Start", self._on_primary,
                                       kind="accent", size="lg", width=8)
        self.t_btn_primary.pack(side="left", fill="x", expand=True, padx=(0, 6))
        self.t_btn_reset = self._btn(ctrls, "Reset", self._on_reset, kind="ghost")
        self.t_btn_reset.pack(side="left", padx=(0, 6))
        self.t_btn_cancel = self._btn(ctrls, "Cancel", self._on_cancel, kind="ghost")
        self.t_btn_cancel.pack(side="left")

        # ---- presets ----
        tk.Label(parent, text="Quick start", bg=bg, fg=c["fg_dim"],
                 font=self.font_l, anchor="w").pack(fill="x", pady=(0, 4))
        prow = tk.Frame(parent, bg=bg)
        prow.pack(fill="x", pady=(0, 14))
        for i, m in enumerate(TIMER_PRESETS_MIN):
            b = self._btn(prow, f"{m}m", lambda mm=m: self._start_preset(mm),
                          kind="ghost", size="sm")
            b.pack(side="left", fill="x", expand=True,
                   padx=(0 if i == 0 else 5, 0))

        # ---- custom timer ----
        tk.Label(parent, text="Custom", bg=bg, fg=c["fg_dim"],
                 font=self.font_l, anchor="w").pack(fill="x", pady=(0, 6))

        # spinbox row: hh : mm : ss
        spin_row = tk.Frame(parent, bg=bg)
        spin_row.pack(fill="x", pady=(0, 6))
        self.var_hr  = tk.StringVar(value="0")
        self.var_min = tk.StringVar(value="10")
        self.var_sec = tk.StringVar(value="0")
        self._spin(spin_row, self.var_hr, 0, 23, "hours").pack(side="left")
        tk.Label(spin_row, text=":", bg=bg, fg=c["fg"],
                 font=self.font_btn).pack(side="left", padx=6, pady=(0, 14))
        self._spin(spin_row, self.var_min, 0, 59, "minutes").pack(side="left")
        tk.Label(spin_row, text=":", bg=bg, fg=c["fg"],
                 font=self.font_btn).pack(side="left", padx=6, pady=(0, 14))
        self._spin(spin_row, self.var_sec, 0, 59, "seconds").pack(side="left")

        # label + start button row
        lbl_row = tk.Frame(parent, bg=bg)
        lbl_row.pack(fill="x", pady=(2, 0))
        self.var_lbl = tk.StringVar(value="")
        e_wrap = tk.Frame(lbl_row, bg=self._chip_bg)
        e_wrap.pack(side="left", fill="x", expand=True, padx=(0, 8), ipady=2)
        e = tk.Entry(e_wrap, textvariable=self.var_lbl,
                     bg=self._chip_bg, fg=c["fg"],
                     insertbackground=c["fg"], bd=0, relief="flat",
                     font=self.font_b)
        e.pack(fill="x", expand=True, padx=10, pady=6)
        self._setup_placeholder(e, self.var_lbl, "Label (Study, Break…)")
        self._btn(lbl_row, "Start", self._start_custom,
                  kind="accent", size="md").pack(side="right")

    def _spin(self, parent, var, lo, hi, label):
        """Compact spinbox with label below; native Tk spinbox arrows."""
        c = self.app.colors
        bg = self._surface
        wrap = tk.Frame(parent, bg=bg)
        # Fixed-width wrapper around the spinbox so all three line up evenly.
        box = tk.Frame(wrap, bg=self._chip_bg)
        box.pack()
        sp = tk.Spinbox(box, from_=lo, to=hi, textvariable=var, width=3,
                        font=self.font_btn,
                        bg=self._chip_bg, fg=c["fg"],
                        insertbackground=c["fg"],
                        buttonbackground=self._chip_bg,
                        readonlybackground=self._chip_bg,
                        bd=0, relief="flat", justify="center",
                        highlightthickness=0)
        sp.pack(padx=8, pady=6)
        tk.Label(wrap, text=label, bg=bg, fg=c["fg_dim"],
                 font=self.font_l).pack(pady=(2, 0))
        return wrap

    def _setup_placeholder(self, entry, var, placeholder):
        c = self.app.colors
        if not hasattr(self, "_placeholders"):
            self._placeholders = {}
        self._placeholders[id(var)] = placeholder
        def on_focus_in(_e):
            if var.get() == placeholder:
                var.set("")
                entry.configure(fg=c["fg"])
        def on_focus_out(_e):
            if not var.get():
                var.set(placeholder)
                entry.configure(fg=c["fg_dim"])
        if not var.get():
            var.set(placeholder)
            entry.configure(fg=c["fg_dim"])
        entry.bind("<FocusIn>", on_focus_in)
        entry.bind("<FocusOut>", on_focus_out)

    def _real_value(self, var):
        v = var.get()
        ph = getattr(self, "_placeholders", {}).get(id(var))
        if ph is not None and v == ph:
            return ""
        return v

    def _draw_progress(self):
        c = self.app.colors
        canvas = self.t_progress
        try:
            canvas.configure(bg=self._surface)
            canvas.delete("all")
            w = canvas.winfo_width()
            h = canvas.winfo_height()
            if w < 4 or h < 2: return
            # track
            self._rounded_rect(canvas, 0, 0, w, h, h//2,
                               fill=self._tint(c["track"], -0.10), outline="")
            ratio = self.svc.timer.progress()
            ph = self.svc.timer.phase
            colour = TIMER_COLOR
            if ph == "completed": colour = TIMER_DONE_COLOR
            elif ph == "paused":  colour = c["fg_dim"]
            elif ph == "idle":    colour = c["fg_dim"]
            fill_w = max(0, min(w, int(w * ratio)))
            if fill_w > 1:
                self._rounded_rect(canvas, 0, 0, fill_w, h, h//2,
                                   fill=colour, outline="")
        except Exception:
            pass

    @staticmethod
    def _rounded_rect(canvas, x0, y0, x1, y1, r, **kw):
        r = max(0, min(r, (x1 - x0)//2, (y1 - y0)//2))
        if r <= 0:
            return canvas.create_rectangle(x0, y0, x1, y1, **kw)
        pts = [x0+r, y0, x1-r, y0, x1, y0,
               x1, y0+r, x1, y1-r, x1, y1,
               x1-r, y1, x0+r, y1, x0, y1,
               x0, y1-r, x0, y0+r, x0, y0]
        return canvas.create_polygon(pts, smooth=True, splinesteps=24, **kw)

    # ---------- alarm ----------
    def _build_alarm_section(self, parent):
        c = self.app.colors
        bg = self._surface

        # ---- header ----
        head = tk.Frame(parent, bg=bg)
        head.pack(fill="x")
        tk.Label(head, text="ALARM", bg=bg, fg=c["fg"],
                 font=self.font_caps, anchor="w").pack(side="left")
        self.a_state_pill = tk.Label(head, text="", bg=self._chip_bg,
                                     fg=c["fg_dim"], font=self.font_l,
                                     padx=8, pady=2)
        self.a_state_pill.pack(side="right")

        # ---- alarm display: time + countdown + label ----
        body = tk.Frame(parent, bg=bg)
        body.pack(fill="x", pady=(10, 6))
        body_left = tk.Frame(body, bg=bg)
        body_left.pack(side="left")
        self.a_clock = tk.Label(body_left, text="--:--", bg=bg, fg=c["fg"],
                                font=self.font_clock, anchor="w")
        self.a_clock.pack(anchor="w")
        self.a_until_lbl = tk.Label(body_left, text="No alarm set",
                                    bg=bg, fg=c["fg_dim"],
                                    font=self.font_total, anchor="w")
        self.a_until_lbl.pack(anchor="w")

        self.a_label_lbl = tk.Label(body, text="", bg=bg, fg=c["fg_dim"],
                                    font=self.font_b, anchor="e",
                                    wraplength=180, justify="right")
        self.a_label_lbl.pack(side="right", anchor="s", pady=(0, 6))

        # ---- input row ----
        tk.Label(parent, text="New alarm", bg=bg, fg=c["fg_dim"],
                 font=self.font_l, anchor="w").pack(fill="x", pady=(8, 6))

        inp = tk.Frame(parent, bg=bg)
        inp.pack(fill="x", pady=(0, 6))

        self.var_alarm_h = tk.StringVar(value="7")
        self.var_alarm_m = tk.StringVar(value="00")
        self.var_alarm_ampm = tk.StringVar(value="AM")
        self.var_alarm_lbl = tk.StringVar(value="")

        h_max = 23 if self.app.use_24h else 12
        self._spin(inp, self.var_alarm_h, 0 if self.app.use_24h else 1,
                   h_max, "hour").pack(side="left")
        tk.Label(inp, text=":", bg=bg, fg=c["fg"],
                 font=self.font_btn).pack(side="left", padx=6, pady=(0, 14))
        self._spin(inp, self.var_alarm_m, 0, 59, "minute").pack(side="left")

        if not self.app.use_24h:
            ampm_wrap = tk.Frame(inp, bg=bg)
            ampm_wrap.pack(side="left", padx=(10, 0))
            ampm_btn = self._btn(ampm_wrap, "AM", self._toggle_ampm,
                                 kind="ghost", size="md")
            ampm_btn.pack()
            tk.Label(ampm_wrap, text="period", bg=bg, fg=c["fg_dim"],
                     font=self.font_l).pack(pady=(2, 0))
            self._ampm_btn = ampm_btn
        else:
            self._ampm_btn = None

        # ---- label row ----
        lbl_row = tk.Frame(parent, bg=bg)
        lbl_row.pack(fill="x", pady=(2, 12))
        e_wrap = tk.Frame(lbl_row, bg=self._chip_bg)
        e_wrap.pack(fill="x", expand=True, ipady=2)
        e = tk.Entry(e_wrap, textvariable=self.var_alarm_lbl,
                     bg=self._chip_bg, fg=c["fg"],
                     insertbackground=c["fg"], bd=0, relief="flat",
                     font=self.font_b)
        e.pack(fill="x", expand=True, padx=10, pady=6)
        self._setup_placeholder(e, self.var_alarm_lbl, "Label (Wake up, Meeting…)")

        # ---- primary actions ----
        actions = tk.Frame(parent, bg=bg)
        actions.pack(fill="x")
        self.a_btn_set = self._btn(actions, "Set Alarm", self._on_set_alarm,
                                   kind="accent", size="lg")
        self.a_btn_set.pack(side="left", fill="x", expand=True, padx=(0, 6))
        self.a_btn_delete = self._btn(actions, "Delete", self._on_delete_alarm,
                                      kind="ghost")
        self.a_btn_delete.pack(side="left")

        # ---- ring/snooze action row (only shown when ringing/snoozed) ----
        ring_actions = tk.Frame(parent, bg=bg)
        # not packed yet — _refresh will pack/unpack as state changes
        self.a_ring_actions = ring_actions
        self.a_btn_dismiss = self._btn(ring_actions, "Dismiss", self._on_dismiss,
                                       kind="danger", size="lg")
        self.a_btn_snooze5 = self._btn(ring_actions, f"Snooze {DEFAULT_SNOOZE_MIN}m",
                                       lambda: self._on_snooze(DEFAULT_SNOOZE_MIN),
                                       kind="ghost", size="lg")
        self.a_btn_snooze10 = self._btn(ring_actions, f"Snooze {EXTRA_SNOOZE_MIN}m",
                                        lambda: self._on_snooze(EXTRA_SNOOZE_MIN),
                                        kind="ghost", size="lg")
        # they get packed in _refresh
        self.a_actions = actions

    def _toggle_ampm(self):
        cur = self.var_alarm_ampm.get()
        new = "PM" if cur == "AM" else "AM"
        self.var_alarm_ampm.set(new)
        if self._ampm_btn:
            self._ampm_btn.configure(text=new)

    # ---------- handlers ----------
    def _start_preset(self, minutes: int):
        self.svc.start_timer(minutes * 60, label="")
        self._refresh()

    def _start_custom(self):
        try:
            hrs  = int(self.var_hr.get() or "0")
            mins = int(self.var_min.get() or "0")
            secs = int(self.var_sec.get() or "0")
        except Exception:
            hrs, mins, secs = 0, 0, 0
        total = max(1, hrs * 3600 + mins * 60 + secs)
        lbl = self._real_value(self.var_lbl)
        self.svc.start_timer(total, label=lbl)
        self._refresh()

    def _on_primary(self):
        ph = self.svc.timer.phase
        if ph == "running":
            self.svc.pause_timer()
        elif ph == "paused":
            self.svc.resume_timer()
        elif ph == "completed":
            self.svc.ack_timer_completion()
            self.svc.reset_timer()
        else:
            # idle/canceled — start with current spinbox values
            self._start_custom()
        self._refresh()

    def _on_reset(self):
        self.svc.reset_timer()
        self._refresh()

    def _on_cancel(self):
        self.svc.cancel_timer()
        self._refresh()

    def _on_set_alarm(self):
        try:
            h = int(self.var_alarm_h.get() or "0")
            m = int(self.var_alarm_m.get() or "0")
        except Exception:
            h, m = 0, 0
        if not self.app.use_24h:
            ampm = (self.var_alarm_ampm.get() or "AM").upper()
            h = h % 12
            if ampm == "PM": h += 12
        h = max(0, min(23, h))
        m = max(0, min(59, m))
        lbl = self._real_value(self.var_alarm_lbl)
        self.svc.set_alarm(h, m, self.app.use_24h, label=lbl)
        self._refresh()

    def _on_delete_alarm(self):
        self.svc.delete_alarm()
        self._refresh()

    def _on_dismiss(self):
        self.svc.dismiss_alarm()
        self._refresh()

    def _on_snooze(self, minutes):
        self.svc.snooze_alarm(minutes)
        self._refresh()

    # ---------- refresh ----------
    def _refresh(self):
        if self._destroyed: return
        c = self.app.colors

        # ---- timer ----
        t = self.svc.timer
        ph = t.phase
        rem = t.remaining_s()
        self.t_clock.configure(text=_format_mmss(rem))
        if ph == "completed":
            self.t_clock.configure(fg=TIMER_DONE_COLOR)
        elif ph == "paused":
            self.t_clock.configure(fg=c["fg_dim"])
        elif ph == "running":
            self.t_clock.configure(fg=c["fg"])
        else:
            self.t_clock.configure(fg=c["fg_dim"] if t.total_s == 0 else c["fg"])

        self.t_label_lbl.configure(text=t.label or "")
        self.t_total_lbl.configure(
            text=(f"of {_format_mmss(t.total_s)}" if t.total_s > 0 else "Ready")
        )
        self._set_state_pill(self.t_state_pill, ph, kind="timer")

        # primary button label + restyle
        if ph == "running":
            self.t_btn_primary.configure(text="Pause")
            self._restyle_btn(self.t_btn_primary, "ghost")
        elif ph == "paused":
            self.t_btn_primary.configure(text="Resume")
            self._restyle_btn(self.t_btn_primary, "accent")
        elif ph == "completed":
            self.t_btn_primary.configure(text="Done")
            self._restyle_btn(self.t_btn_primary, "success")
        else:
            self.t_btn_primary.configure(text="Start")
            self._restyle_btn(self.t_btn_primary, "accent")

        self._draw_progress()

        # ---- alarm ----
        a = self.svc.alarm
        ap = a.phase
        if ap in ("scheduled", "ringing", "snoozed"):
            tstr = a.time_str(force_24h=self.app.use_24h)
            self.a_clock.configure(text=tstr)
            if ap == "ringing":
                self.a_clock.configure(fg=ALARM_PULSE_COLOR)
            elif ap == "snoozed":
                self.a_clock.configure(fg=c["fg_dim"])
            else:
                self.a_clock.configure(fg=c["fg"])
            self.a_label_lbl.configure(text=a.label or "")
            until = a.time_until_s()
            self.a_until_lbl.configure(
                text=("ringing now" if ap == "ringing"
                      else f"in {_format_human_delta(until)}"
                      if ap == "scheduled"
                      else f"snoozed · rings in {_format_human_delta(until)}")
            )
        else:
            self.a_clock.configure(text="--:--", fg=c["fg_dim"])
            self.a_label_lbl.configure(text="")
            self.a_until_lbl.configure(text="No alarm set")

        self._set_state_pill(self.a_state_pill, ap, kind="alarm")

        # ---- show ringing/snoozed action set ----
        try: self.a_ring_actions.pack_forget()
        except Exception: pass
        for w in (self.a_btn_dismiss, self.a_btn_snooze5, self.a_btn_snooze10):
            try: w.pack_forget()
            except Exception: pass
        if ap == "ringing":
            self.a_ring_actions.pack(fill="x", pady=(10, 0))
            self.a_btn_dismiss.pack(side="left", fill="x", expand=True, padx=(0, 6))
            self.a_btn_snooze5.pack(side="left", padx=(0, 6))
            self.a_btn_snooze10.pack(side="left")
        elif ap == "snoozed":
            self.a_ring_actions.pack(fill="x", pady=(10, 0))
            self.a_btn_dismiss.pack(side="left", fill="x", expand=True)

    def _set_state_pill(self, pill, phase, kind="timer"):
        """Color the small status pill based on state."""
        c = self.app.colors
        # Map (kind, phase) -> (text, bg_color)
        text = phase
        bg = self._chip_bg
        fg = c["fg_dim"]
        if kind == "timer":
            if phase == "running":
                text, bg, fg = "running", self._tint(TIMER_COLOR, -0.20), "#FFFFFF"
            elif phase == "paused":
                text, bg, fg = "paused", self._chip_bg, c["fg_dim"]
            elif phase == "completed":
                text, bg, fg = "done", self._tint(TIMER_DONE_COLOR, -0.20), "#FFFFFF"
            elif phase == "canceled":
                text, bg, fg = "canceled", self._chip_bg, c["fg_dim"]
            else:
                text, bg, fg = "idle", self._chip_bg, c["fg_dim"]
        elif kind == "alarm":
            if phase == "scheduled":
                text, bg, fg = "scheduled", self._chip_bg, c["fg"]
            elif phase == "ringing":
                text, bg, fg = "RINGING", self._tint(ALARM_PULSE_COLOR, -0.20), "#FFFFFF"
            elif phase == "snoozed":
                text, bg, fg = "snoozed", self._chip_bg, c["fg_dim"]
            elif phase == "dismissed":
                text, bg, fg = "dismissed", self._chip_bg, c["fg_dim"]
            elif phase == "canceled":
                text, bg, fg = "canceled", self._chip_bg, c["fg_dim"]
            else:
                text, bg, fg = "no alarm", self._chip_bg, c["fg_dim"]
        try:
            pill.configure(text=text.upper(), bg=bg, fg=fg)
        except Exception:
            pass

    @staticmethod
    def _timer_state_text(ph):
        return {
            "idle": "idle",
            "running": "running",
            "paused": "paused",
            "completed": "done",
            "canceled": "canceled",
        }.get(ph, "")

    @staticmethod
    def _alarm_state_text(ap):
        return {
            "none": "no alarm",
            "scheduled": "scheduled",
            "ringing": "RINGING",
            "snoozed": "snoozed",
            "dismissed": "dismissed",
            "canceled": "canceled",
        }.get(ap, "")

    def _auto_refresh(self):
        if self._destroyed: return
        self._refresh()
        self._tick_after = self.after(250, self._auto_refresh)

    # ---------- positioning ----------
    def _position_under_island(self):
        try:
            self.update_idletasks()
            iw = self.app.root.winfo_width()
            ih = self.app.root.winfo_height()
            ix = self.app.root.winfo_rootx()
            iy = self.app.root.winfo_rooty()
            pw = self.winfo_reqwidth()
            ph = self.winfo_reqheight()
            x = ix + (iw - pw) // 2
            y = iy + ih + 6
            sw = self.winfo_screenwidth()
            x = max(8, min(sw - pw - 8, x))
            self.geometry(f"+{x}+{y}")
        except Exception:
            pass

    def close(self):
        if self._closing or self._destroyed: return
        self._closing = True
        try:
            if self._tick_after:
                self.after_cancel(self._tick_after)
                self._tick_after = None
        except Exception: pass
        self._destroyed = True
        try: self.destroy()
        except Exception: pass
        try:
            self.app.timer_panel = None
        except Exception: pass


# ---------------- app ----------------
class App:
    def __init__(self, root: tk.Tk):
        self.root=root
        self.root.overrideredirect(True)
        self.root.attributes("-topmost", True)
        self.root.attributes("-alpha", OPACITY)
        self.root.configure(bg="black")
        self.root.attributes("-transparentcolor","black")
        self.root.bind("<Escape>", lambda e:self.quit())
        self.root.bind("<Control-Shift-w>", lambda e:self.toggle_click_through())

        # services
        self.media=MediaService(poll_ms=MEDIA_POLL_MS)
        self.power=PowerService(poll_ms=POWER_POLL_MS)
        self.audio=AudioPeakService()
        self.timeralarm = TimerAlarmService(on_event=self._on_ta_event)
        self.timer_panel = None
        self._ta_pulse_phase = 0.0
        self._ta_pulse_after = None
        self._ta_pulse_active = False
        self._timer_ring_photo = None  # persistent ref so Tk doesn't GC it

        # shortcuts (no volume keystrokes)
        self.root.bind("<space>", lambda e:self.media.play_pause())
        self.root.bind("<Control-Right>", lambda e:self.media.next())
        self.root.bind("<Control-Left>",  lambda e:self.media.prev())

        # DPI / metrics
        self.hwnd = self.root.winfo_id()
        self.scale = _dpi_scale(self.hwnd)
        self.font_sz = max(10, int(round(BASE_FONT_SIZE*self.scale)))
        self.pad_x = int(round(PADDING_X*self.scale))
        self.pad_y = int(round(PADDING_Y*self.scale))
        self.radius = int(round(RADIUS*self.scale))
        self.space  = int(round(SPACE_PX*self.scale))
        self.vol_w  = int(round(VOL_PILL_WIDTH_PX*self.scale))
        self.vol_h  = int(round(VOL_PILL_HEIGHT_PX*self.scale))
        self.expanded_h = int(round(EXPANDED_HEIGHT_PX*self.scale))
        self.collapsed_h = int(round(COLLAPSED_HEIGHT_PX*self.scale))
        self.ring_diam = int(round(RING_DIAM_PX*self.scale))
        self.ring_thick = max(2, int(round(RING_THICK_PX*self.scale)))
        self.text_pad_px = max(6, int(round(6*self.scale)))
        self.time_pad_px = max(6, int(round(8*self.scale)))
        self.media_min_collapsed = int(round(MEDIA_MIN_COLLAPSED_PX*self.scale))
        self.media_max_collapsed = int(round(MEDIA_MAX_COLLAPSED_PX*self.scale))
        self.media_min_expanded  = int(round(MEDIA_MIN_EXPANDED_PX*self.scale))
        self.media_max_expanded  = int(round(MEDIA_MAX_EXPANDED_PX*self.scale))

        # theme / fonts — always dark
        self.theme_name = "dark"
        self.colors = DARK
        fam = _choose_font()
        self.font_main = tkfont.Font(family=fam, size=self.font_sz)
        self.font_bold = tkfont.Font(family=fam, size=self.font_sz, weight="bold")
        self.font_light = tkfont.Font(family=fam, size=self.font_sz - 1)  # Lighter weight for secondary text
        self.font_icons = tkfont.Font(family="Segoe UI Symbol", size=max(12, self.font_sz + 1))
        # Compact strip uses the same proportional UI font as the rest of the
        # island. Segoe UI / SF Pro both ship with tabular (constant-width)
        # digits by default, so the strip width stays stable across per-second
        # clock updates without needing a monospaced font like Consolas.
        self.font_compact = tkfont.Font(family=fam, size=self.font_sz)

        # rounded bg
        self.bg = tk.Label(self.root, bg="black", bd=0, highlightthickness=0)
        self.bg.place(relx=0.5, rely=0.5, anchor="center")

        # frame (grid)
        self.frame = tk.Frame(self.root, bg=self.colors["bg"], bd=0, highlightthickness=0)
        self.frame.place(relx=0.5, rely=0.5, anchor="center")

        # controls cluster with Apple-style spacing
        btn_pad = max(2, int(4 * self.scale))
        self.controls_wrap = tk.Frame(self.frame, bg=self.colors["bg"])
        
        # Use cleaner icons and better sizing
        icon_fg = self.colors["fg"]
        icon_fg_dim = self.colors["fg_dim"]
        
        self.btn_prev = tk.Label(self.controls_wrap, text="⏮", font=self.font_icons, 
                                  bg=self.colors["bg"], fg=icon_fg_dim, padx=btn_pad, pady=btn_pad)
        self.btn_pp = tk.Label(self.controls_wrap, text="⏯", font=self.font_icons,
                                bg=self.colors["bg"], fg=icon_fg, padx=btn_pad, pady=btn_pad)
        self.btn_next = tk.Label(self.controls_wrap, text="⏭", font=self.font_icons,
                                  bg=self.colors["bg"], fg=icon_fg_dim, padx=btn_pad, pady=btn_pad)
        
        # Enhanced hover effects - brighten on hover
        def make_hover_handlers(btn, is_main=False):
            normal_fg = icon_fg if is_main else icon_fg_dim
            hover_fg = "#FFFFFF"
            def on_enter(e):
                btn.config(cursor="hand2", fg=hover_fg)
            def on_leave(e):
                btn.config(cursor="", fg=normal_fg)
            return on_enter, on_leave
        
        for btn, cb, is_main in [
            (self.btn_prev, self._on_prev, False),
            (self.btn_pp, self._on_pp, True),
            (self.btn_next, self._on_next, False)
        ]:
            btn.pack(side="left", padx=(0, self.space))
            btn.bind("<Button-1>", cb)
            enter_h, leave_h = make_hover_handlers(btn, is_main)
            btn.bind("<Enter>", enter_h)
            btn.bind("<Leave>", leave_h)
        
        _Tooltip(self.btn_prev, "Previous")
        _Tooltip(self.btn_pp,   "Play/Pause")
        _Tooltip(self.btn_next, "Next")

        # Sound wave (expanded view — always shown next to transport controls)
        def _sw_theme(): return self.colors
        sw_h = max(12, int(self.font_sz * 1.1))
        sw_w = max(20, int(self.font_sz * 1.8))
        self.soundwave = SoundWaveCanvas(
            self.controls_wrap, get_theme=_sw_theme,
            num_bars=4, width=sw_w, height=sw_h,
            bg=self.colors["bg"]
        )
        self.soundwave.pack(side="left", padx=(max(2, self.space//2), 0))

        self.controls_wrap.grid(row=0, column=0, padx=(self.pad_x, 0), sticky="w")

        # volume (pill)
        self.volume = VolumeService()
        self.volpill=None
        if self.volume.ok:
            def theme_getter(): return self.colors
            self.volpill = VolumePill(
                self.frame, width=self.vol_w, height=self.vol_h,
                get_theme=theme_getter,
                get_cb=self.volume.get, set_cb=self.volume.set,
                get_muted_cb=self.volume.get_muted,
                set_muted_cb=self.volume.set_muted,
                step=VOL_STEP, bg=self.colors["bg"]
            )
            self.volpill.grid(row=0,column=1,padx=(self.space,self.space),sticky="w")
            self._tick_volume_sync()

        # media area
        self.media_wrap=tk.Frame(self.frame,bg=self.colors["bg"])
        self.media_wrap.grid(row=0,column=2,padx=(self.space,self.space),sticky="we")
        self.media_wrap.pack_propagate(False)

        self.var_badge=tk.StringVar(value="")
        self.var_media=tk.StringVar(value="—")
        self.collapsed_mode_options = ("Title + Time", "Title Only", "Time Only")
        self.var_collapsed_mode = tk.StringVar(value=self.collapsed_mode_options[0])
        self.lbl_badge=tk.Label(self.media_wrap,textvariable=self.var_badge,bg=self.colors["bg"],
                                fg=self.colors["fg"], font=(fam, self.font_sz-1, "bold"), anchor="w")
        self.lbl_media=tk.Label(self.media_wrap,textvariable=self.var_media,bg=self.colors["bg"],
                                fg=self.colors["fg"], font=self.font_main, anchor="w", justify="left")
        # Album art label — hidden until artwork is available; packed before lbl_media
        art_h = max(24, self.expanded_h - 2 * self.pad_y - 2)
        self._art_size = min(40, art_h)
        self.album_art_lbl = tk.Label(self.media_wrap, bg=self.colors["bg"],
                                      bd=0, highlightthickness=0)
        self._art_photo    = None   # persistent PhotoImage reference (prevents GC)
        self._art_key_shown = ""    # track key of currently displayed art

        # Extra horizontal padding to prevent text clipping by rounded pill corners
        self.pill_edge_pad = max(12, int(self.radius * 0.65))
        if SHOW_APP_BADGE:
            self.lbl_badge.pack(side="left", padx=(0, self.space//2))
        # album_art_lbl is NOT packed here; _sync_album_art handles it dynamically
        self.lbl_media.pack(side="left", fill="both", expand=True, anchor="w",
                            padx=(0, max(6, int(self.radius * 0.3))))

        # Marquee strip — replaces lbl_media in collapsed mode for smooth scrolling
        strip_h = max(18, self.font_sz + 8)
        def _marquee_theme(): return self.colors
        self.marquee = MarqueeCanvas(
            self.media_wrap,
            get_theme=_marquee_theme,
            font=self.font_compact,
            height=strip_h,
            bg=self.colors["bg"],
        )
        # Compact sound wave for collapsed mode (optional, shown left of marquee)
        sw_c_h = max(10, int(self.font_sz * 0.9))
        sw_c_w = max(14, int(self.font_sz * 1.3))
        self.soundwave_compact = SoundWaveCanvas(
            self.media_wrap, get_theme=_marquee_theme,
            num_bars=3, width=sw_c_w, height=sw_c_h,
            bg=self.colors["bg"]
        )
        # Not packed yet — _apply_collapsed_state manages show/hide

        # gear: collapsed-mode selector
        self.mode_btn = tk.Label(self.frame, text="⚙", bg=self.colors["bg"],
                                 fg=self.colors["fg"], font=self.font_icons, cursor="hand2")
        self.mode_btn.bind("<Button-1>", self._open_mode_menu)
        self.mode_btn.bind("<Enter>", lambda _e: self.mode_btn.config(cursor="hand2"))
        self.mode_btn.bind("<Leave>", lambda _e: self.mode_btn.config(cursor=""))
        _Tooltip(self.mode_btn, "Settings")
        self.mode_menu = tk.Menu(self.root, tearoff=0)
        for opt in self.collapsed_mode_options:
            self.mode_menu.add_radiobutton(label=opt, variable=self.var_collapsed_mode,
                                           value=opt, command=self._on_collapsed_mode_change)
        self.mode_menu.add_separator()
        self.use_24h = USE_24H
        self.show_seconds = SHOW_SECONDS
        self.show_battery_collapsed = True
        self.show_mute_collapsed = True   # show ✕ prefix in compact strip when muted
        self._is_muted = False            # cached mute state, updated by volume sync tick
        self.var_24h = tk.BooleanVar(value=self.use_24h)
        self.var_seconds = tk.BooleanVar(value=self.show_seconds)
        self.var_battery_collapsed = tk.BooleanVar(value=self.show_battery_collapsed)
        self.var_mute_collapsed = tk.BooleanVar(value=self.show_mute_collapsed)
        self.mode_menu.add_checkbutton(label="24-hour clock", variable=self.var_24h,
                                       command=self._toggle_24h)
        self.mode_menu.add_checkbutton(label="Show seconds", variable=self.var_seconds,
                                       command=self._toggle_seconds)
        self.mode_menu.add_checkbutton(label="Show battery (collapsed)", variable=self.var_battery_collapsed,
                                       command=self._toggle_battery_collapsed)
        self.mode_menu.add_checkbutton(label="Show mute indicator (collapsed)", variable=self.var_mute_collapsed,
                                       command=self._toggle_mute_collapsed)
        self.show_soundwave_collapsed = False
        self.var_soundwave_collapsed = tk.BooleanVar(value=self.show_soundwave_collapsed)
        self.mode_menu.add_checkbutton(label="Show sound wave (collapsed)", variable=self.var_soundwave_collapsed,
                                       command=self._toggle_soundwave_collapsed)
        self.show_controls_collapsed = False
        self.var_controls_collapsed = tk.BooleanVar(value=self.show_controls_collapsed)
        self.mode_menu.add_checkbutton(label="Show media controls (collapsed)", variable=self.var_controls_collapsed,
                                       command=self._toggle_controls_collapsed)
        self.cycle_collapsed = False
        self.var_cycle_collapsed = tk.BooleanVar(value=self.cycle_collapsed)
        self.mode_menu.add_checkbutton(label="Cycle content (collapsed)", variable=self.var_cycle_collapsed,
                                       command=self._toggle_cycle_collapsed)
        self.rotate_title_collapsed = False
        self.var_rotate_title_collapsed = tk.BooleanVar(value=self.rotate_title_collapsed)
        self.mode_menu.add_checkbutton(label="Rotating title (collapsed)", variable=self.var_rotate_title_collapsed,
                                       command=self._toggle_rotate_title_collapsed)
        self.mode_menu.add_separator()
        self.pin_expanded = False
        self.var_pin_expanded = tk.BooleanVar(value=self.pin_expanded)
        self.mode_menu.add_checkbutton(label="Pin expanded (always show)", variable=self.var_pin_expanded,
                                       command=self._toggle_pin_expanded)
        self._mode_btn_extra = 0
        try:
            self.mode_btn.update_idletasks()
            self._mode_btn_extra = self.mode_btn.winfo_reqwidth() + self.space//2
        except Exception:
            self._mode_btn_extra = self.space
        # mode (gear) at column 4, timer icon at column 3
        self.mode_btn.grid(row=0, column=4, padx=(0, self.space//2), sticky="e")

        # timer/alarm icon
        self.btn_timer = tk.Label(self.frame, text="⏱", bg=self.colors["bg"],
                                  fg=self.colors["fg"], font=self.font_icons,
                                  cursor="hand2")
        self.btn_timer.bind("<Button-1>", self._toggle_timer_panel)
        self.btn_timer.bind("<Enter>", lambda _e: self.btn_timer.config(cursor="hand2"))
        self.btn_timer.bind("<Leave>", lambda _e: self.btn_timer.config(cursor=""))
        _Tooltip(self.btn_timer, "Timer & Alarm")
        try:
            self.btn_timer.update_idletasks()
            self._timer_btn_extra = self.btn_timer.winfo_reqwidth() + self.space//2
        except Exception:
            self._timer_btn_extra = self.space
        self.btn_timer.grid(row=0, column=3, padx=(0, self.space//2), sticky="e")

        # divider - subtle vertical separator
        cidx=5
        if SHOW_DIVIDER:
            self.lbl_div = tk.Label(self.frame, text="│", bg=self.colors["bg"], 
                                     fg=self.colors["divider"], font=(fam, self.font_sz - 2))
            self.lbl_div.grid(row=0, column=cidx, padx=(self.space, self.space), sticky="w")
            cidx += 1
        else:
            self.lbl_div=None

        # right cluster - time and date with Apple typography
        self.var_date=tk.StringVar(value="")
        self.var_time=tk.StringVar(value="")
        self.var_batt=tk.StringVar(value="")
        self._battery_charging = False
        # Date in lighter weight, time in bold - Apple style hierarchy
        self.lbl_date = tk.Label(self.frame, textvariable=self.var_date, bg=self.colors["bg"], 
                                  fg=self.colors["fg_dim"], font=self.font_light, anchor="e")
        self.lbl_time = tk.Label(self.frame, textvariable=self.var_time, bg=self.colors["bg"], 
                                  fg=self.colors["fg"], font=self.font_bold, anchor="e")
        self.lbl_batt = tk.Label(self.frame, textvariable=self.var_batt, bg=self.colors["bg"], 
                                  fg=self.colors["fg_dim"], font=self.font_light, anchor="e")
        self.lbl_date.grid(row=0, column=cidx, padx=(0, self.space), sticky="e"); cidx += 1
        self.lbl_time.grid(row=0, column=cidx, padx=(0, self.space), sticky="e"); cidx += 1
        self.lbl_batt.grid(row=0, column=cidx, padx=(0, self.pad_x), sticky="e"); cidx += 1

        # only media column expands
        for i in range(cidx+1): self.frame.grid_columnconfigure(i, weight=0)
        self.frame.grid_columnconfigure(2, weight=1)

        # dragging
        self.root.bind("<Button-1>", self._start_drag)
        self.root.bind("<B1-Motion>", self._on_drag)
        self.root.bind("<ButtonRelease-1>", self._stop_drag)

        # state
        self._badge = ""
        self._full_media_text = ""
        self.width=0
        self._click=False
        self._anim_after=None
        self._animating=False
        self._collapse_after = None
        self._buffer_phase = 0
        self._buffer_after = None
        self._buffer_started = 0.0

        # NEW: remember raw fields for smarter truncation
        self._last_title = ""
        self._last_artist = ""
        self._last_meta_at = 0.0
        self._last_paused   = True
        self._cycle_idx     = 0
        self._cycle_after   = None
        self._cycle_items   = []
        self._meta_hold_s = 3.0
        self._bg_cache = {}
        self._bg_cache_order = []

        # local media clock
        self._track_id = ""
        self._pos_cache = 0.0
        self._dur_cache = 0.0
        self._rate_cache = 1.0
        self._last_wall = time.monotonic()
        self._last_ratio = 0.0

        # start collapsed
        self.collapsed = True
        for w in (self.root, self.frame):
            w.bind("<Enter>", self._hover_enter)
            w.bind("<Leave>", self._hover_leave)

        # initial paint
        self._tick_time(initial=True)
        self._apply_collapsed_state(initial=True)
        self.root.deiconify()
        self.root.after(120, self._tick_media_poll)
        self.root.after(600, self._tick_battery)
        self.root.after(180, self._tick_timer_alarm)
        self.root.after(2000, self._watchdog)
        # Pre-warm the bg image cache so the very first hover/collapse animation
        # has zero PIL work inside the animation loop (staggered to not block UI).
        self.root.after(800, self._prewarm_bg_cache)

    # ---------- measuring & helpers ----------
    def _measure(self, font, text): return font.measure(text)

    def _prewarm_bg_cache(self, idx=0):
        """Staggered startup pre-render of envelope animation frames.
        One PIL render per call (≤10 ms), then reschedules itself so the UI
        loop is never blocked for a perceptible stretch."""
        try:
            if not hasattr(self, '_prewarm_sizes'):
                cw, ch, _, _, _ = self._desired_layout(True)
                ew, eh, _, _, _ = self._desired_layout(False)
                # Envelope is always max(collapsed, expanded) dims
                env_w = max(cw, ew)
                env_h = max(ch, eh)
                sizes = []
                for i in range(1, 13):
                    t = _ease_out_expo(i / 12)
                    sizes.append((max(10, round(cw + (ew - cw) * t)),
                                  max(10, round(ch + (eh - ch) * t)),
                                  env_w, env_h))
                    sizes.append((max(10, round(ew + (cw - ew) * t)),
                                  max(10, round(eh + (ch - eh) * t)),
                                  env_w, env_h))
                self._prewarm_sizes = sizes
            sizes = self._prewarm_sizes
            if idx < len(sizes):
                pw, ph, env_w, env_h = sizes[idx]
                self._get_bg_image_in_env(pw, ph, env_w, env_h)
                self.root.after(35, self._prewarm_bg_cache, idx + 1)
        except Exception:
            pass

    def _time_sample(self):
        """Return a widest-expected time string to reserve space for clipping."""
        use_24 = getattr(self, "use_24h", USE_24H)
        show_sec = getattr(self, "show_seconds", SHOW_SECONDS)
        if use_24:
            return "23:59:59" if show_sec else "23:59"
        return "12:59:59 PM" if show_sec else "12:59 PM"

    def _ellipsize(self, text, font, max_px):
        if max_px<=0: return ""
        if self._measure(font,text) <= max_px: return text
        ell="…"; ell_w=self._measure(font,ell)
        lo,hi,res=0,len(text),""
        while lo<=hi:
            mid=(lo+hi)//2
            s=text[:mid]
            if self._measure(font,s)+ell_w <= max_px:
                res=s; lo=mid+1
            else:
                hi=mid-1
        return res+ell if res else ell

    def _buffer_label(self):
        """Apple-style waiting indicator"""
        dots = "·" * (1 + (self._buffer_phase % 3))  # Middle dot for cleaner look
        return f"Listening {dots}"

    def _set_buffering(self, on: bool):
        if on:
            if self._buffer_after is None:
                self._buffer_phase = 0
                def tick():
                    self._buffer_phase = (self._buffer_phase + 1) % 3
                    self._buffer_after = self.root.after(500, tick)  # Slightly slower for elegance
                self._buffer_after = self.root.after(500, tick)
        else:
            if self._buffer_after is not None:
                try: self.root.after_cancel(self._buffer_after)
                except Exception: pass
                self._buffer_after = None

    def _should_show_buffer(self, st) -> bool:
        _ = st  # listening placeholder disabled
        return False

    def _is_empty_media_display(self, text: str) -> bool:
        if not text:
            return True
        t = str(text).strip()
        if not t:
            return True
        return t in {"—", "â€”", "-", "–", "â€“"}

    def _collapsed_battery_text(self):
        show_batt = bool(getattr(self, "show_battery_collapsed", True))
        if not show_batt or not hasattr(self, "var_batt"):
            return ""
        batt_raw = (self.var_batt.get() or "").strip()
        if not batt_raw:
            return ""
        batt_short = batt_raw.replace("BAT ", "").replace("AC ", "").strip()
        if not batt_short:
            return ""
        if getattr(self, "_battery_charging", False):
            return f"⚡ {batt_short}"
        return batt_short

    def _rounded_rect(self, canvas, x0, y0, x1, y1, r, **kw):
        """Draw a rounded rectangle on a Tk canvas."""
        r = max(0, min(r, (x1 - x0)/2, (y1 - y0)/2))
        if r == 0:
            return canvas.create_rectangle(x0, y0, x1, y1, **kw)
        canvas.create_arc(x0, y0, x0+2*r, y0+2*r, start=90, extent=90, style="arc", **kw)
        canvas.create_arc(x1-2*r, y0, x1, y0+2*r, start=0, extent=90, style="arc", **kw)
        canvas.create_arc(x1-2*r, y1-2*r, x1, y1, start=270, extent=90, style="arc", **kw)
        canvas.create_arc(x0, y1-2*r, x0+2*r, y1, start=180, extent=90, style="arc", **kw)
        canvas.create_line(x0+r, y0, x1-r, y0, **kw)
        canvas.create_line(x1, y0+r, x1, y1-r, **kw)
        canvas.create_line(x1-r, y1, x0+r, y1, **kw)
        canvas.create_line(x0, y1-r, x0, y0+r, **kw)

    # NEW: Always include artist in expanded view; cap only the title part
    def _compose_media_text(self, collapsed: bool, max_px: int) -> str:
        title = (self._last_title or "").strip()
        artist = (self._last_artist or "").strip()
        if collapsed:
            return self._compose_collapsed_display(title, artist, self._badge, max_px)

        # expanded: try to keep full artist visible
        if not title and not artist:
            return "—"
        if not artist:
            return self._ellipsize(title, self.font_main, max_px)

        sep = " — "
        artist_part = sep + artist
        artist_w = self._measure(self.font_main, artist_part)
        avail_for_title = max_px - artist_w
        if avail_for_title <= 0:
            # Ultra-narrow: trim both sides but keep both visible minimally
            # Reserve one ellipsis for title
            min_title = "…"
            min_title_w = self._measure(self.font_main, min_title)
            rest = max(0, max_px - min_title_w)
            right = self._ellipsize(artist_part, self.font_main, rest)
            return min_title + right
        t = self._ellipsize(title, self.font_main, avail_for_title)
        return f"{t}{artist_part}"

    def _ta_compact_prefix(self) -> str:
        """Short timer/alarm prefix for the compact strip ('' if idle)."""
        svc = getattr(self, "timeralarm", None)
        if svc is None: return ""
        primary = svc.primary_state()
        if primary == "idle": return ""
        t = svc.timer
        a = svc.alarm

        if primary == "alarm_ringing":
            lbl = a.label.strip()
            return f"🔔 {lbl}" if lbl else "🔔 Alarm"
        if primary == "timer_completed":
            lbl = t.label.strip()
            return f"✓ {lbl} done" if lbl else "✓ Timer done"
        if primary == "timer_running":
            return f"⏱ {_format_mmss(t.remaining_s())}"
        if primary == "timer_paused":
            return f"⏸ {_format_mmss(t.remaining_s())}"
        if primary == "alarm_snoozed":
            return f"💤 {a.time_str(force_24h=self.use_24h)}"
        if primary == "alarm_scheduled":
            return f"🔔 {a.time_str(force_24h=self.use_24h)}"
        return ""

    def _compose_ta_compact(self, max_px: int):
        """
        Compact-view text WITH timer/alarm + the user's normal mode content
        (title/time per current mode). Returns None when the system is idle so
        callers fall through to the regular composer.
        """
        prefix = self._ta_compact_prefix()
        if not prefix:
            return None
        if max_px <= 0:
            return ""
        sep = "  "  # tight gap between TA prefix and rest
        cf = self.font_compact
        prefix_w = self._measure(cf, prefix + sep)

        # If even the prefix doesn't fit, ellipsize it alone.
        if prefix_w >= max_px:
            return self._ellipsize(prefix, cf, max_px)

        rest_max = max(0, max_px - prefix_w)
        # Compose the user's normal mode content with the remaining space.
        rest = self._compose_collapsed_display_main(
            (self._last_title or "").strip(),
            (self._last_artist or "").strip(),
            (self._badge or "").strip(),
            rest_max,
        )
        if rest and rest != "-":
            return f"{prefix}{sep}{rest}"
        return prefix

    def _compose_collapsed_display(self, title: str, artist: str, app: str, max_px: int) -> str:
        # Timer + alarm take priority and are *prepended* to the normal display
        # so the user keeps seeing time/title alongside the countdown.
        prefix = self._ta_compact_prefix()
        cf = self.font_compact
        if prefix:
            sep = "  "
            prefix_w = self._measure(cf, prefix + sep)
            if prefix_w >= max_px:
                return self._ellipsize(prefix, cf, max_px)
            rest_max = max(0, max_px - prefix_w)
            rest = self._compose_collapsed_display_main(title, artist, app, rest_max)
            if rest and rest != "-":
                return f"{prefix}{sep}{rest}"
            return prefix
        return self._compose_collapsed_display_main(title, artist, app, max_px)

    def _compose_collapsed_display_main(self, title: str, artist: str, app: str, max_px: int) -> str:
        # Use the tabular font for measurements: digits are uniform width so the
        # strip's pixel width doesn't change every second when seconds tick.
        cf = self.font_compact
        mode = getattr(self, "var_collapsed_mode", None)
        mode_val = mode.get() if mode is not None else "Title + Time"
        primary = title or artist or (app or "").strip()
        time_txt = (self.var_time.get() if hasattr(self, "var_time") else "") or ""
        batt_short = self._collapsed_battery_text()
        aux_parts = [p for p in (time_txt, batt_short) if p]
        aux_txt = "  ".join(aux_parts)

        if max_px <= 0:
            return ""

        if mode_val == "Time Only":
            base = aux_txt or time_txt or batt_short or "--:--"
            return self._ellipsize(base, cf, max_px)

        if mode_val == "Title Only":
            if primary and batt_short:
                spacer = "  "
                batt_need = self._measure(cf, spacer + batt_short)
                if batt_need < max_px:
                    title_part = self._ellipsize(primary, cf, max_px - batt_need)
                    return f"{title_part}{spacer}{batt_short}".strip()
            base = primary or aux_txt or time_txt or batt_short or ""
            return self._ellipsize(base, cf, max_px) if base else "-"

        spacer = "  "
        time_part = aux_txt or time_txt or batt_short or "--:--"
        time_need = self._measure(cf, time_part + spacer)
        if time_need >= max_px:
            return self._ellipsize(time_part, cf, max_px)
        remaining = max_px - time_need
        if remaining <= 0 or not primary:
            return time_part
        title_part = self._ellipsize(primary, cf, remaining)
        combo = f"{time_part}{spacer}{title_part}".rstrip()
        return combo if combo else "-"


    def _reserved_right_px(self, collapsed):
        if collapsed: return self.pad_x
        px = 0
        if self.lbl_div: px += self._measure(self.font_bold,"·")+self.space
        date_s = self.var_date.get() or "Mon, Sep 15"
        time_now = self.var_time.get() or ""
        time_sample = self._time_sample()
        time_w = max(self._measure(self.font_bold, time_now), self._measure(self.font_bold, time_sample))
        batt_s = self.var_batt.get() or ""
        px += self._measure(self.font_main,date_s) + self.space
        px += time_w + self.space
        if batt_s: px += self._measure(self.font_main,batt_s) + self.space
        px += self.pad_x + self.time_pad_px
        return px

    def _media_controls_px(self, collapsed):
        if collapsed: return 0
        return getattr(self, "_mode_btn_extra", 0) + getattr(self, "_timer_btn_extra", 0)

    def _left_controls_px(self, collapsed):
        if collapsed and not getattr(self, "show_controls_collapsed", False):
            return self.pad_x
        icons = self._measure(self.font_icons,"⏮")+self._measure(self.font_icons,"⏯")+self._measure(self.font_icons,"⏭")
        # sound wave is inside controls_wrap — include its width
        sw_w = getattr(self.soundwave, "_W", 0) if hasattr(self, "soundwave") else 0
        px = self.pad_x + icons + self.space + sw_w + max(2, self.space // 2)
        if self.volpill is not None and not collapsed:
            px += self.vol_w + self.space
        return px

    def _desired_layout(self, collapsed):
        sw = self.root.winfo_screenwidth()
        ta_active = collapsed and bool(self._ta_compact_prefix())
        # When timer/alarm is active in compact mode, allow the strip to grow
        # so the countdown can sit alongside title/time instead of replacing it.
        if ta_active:
            max_ratio = max(MAX_WIDTH_RATIO_COLLAPSED, 0.32)
            min_w_px  = max(MIN_WIDTH_COLLAPSED_PX, 340)
        else:
            max_ratio = MAX_WIDTH_RATIO_COLLAPSED if collapsed else MAX_WIDTH_RATIO_EXPANDED
            min_w_px  = MIN_WIDTH_COLLAPSED_PX if collapsed else MIN_WIDTH_EXPANDED_PX

        max_w = int(sw * max_ratio)
        min_w = int(max(min_w_px * self.scale, (140 if collapsed else 440) * self.scale))

        right_px = self._reserved_right_px(collapsed)
        left_px  = self._left_controls_px(collapsed)

        ctrl_px      = self._media_controls_px(collapsed)
        measure_font = self.font_compact if collapsed else self.font_main
        text_pad_extra = self.pill_edge_pad if collapsed else 0

        if collapsed:
            # ── Collapsed: stable pill width ────────────────────────────────
            # Size the pill for the TIME portion only.  The title scrolls inside
            # the marquee — we never grow the pill to fit the full title text.
            cf = self.font_compact

            # Mute prefix reservation
            mute_px = 0
            if (getattr(self, 'show_mute_collapsed', True)
                    and getattr(self, '_is_muted', False)):
                mute_px = self._measure(cf, "✕  ")

            # Use the widest time string we expect so the pill never jitters
            time_sample = self._time_sample()
            time_txt    = (self.var_time.get() if hasattr(self, "var_time") else "") or ""
            time_stable_w = max(self._measure(cf, time_txt),
                                self._measure(cf, time_sample))
            batt_short = self._collapsed_battery_text()
            batt_px = (self._measure(cf, "  " + batt_short) if batt_short else 0)

            # No extra space for title — pill is sized for time only.
            # The marquee scrolls (or ellipsizes) the title within that space.
            title_preview_px = 0

            # For ta_active, reserve just enough for the short countdown string
            if ta_active:
                ta_txt = self._ta_compact_prefix()
                title_preview_px = self._measure(cf, ta_txt) + int(16 * self.scale)

            text_px = mute_px + time_stable_w + batt_px + title_preview_px
            text_px = min(max(text_px, self.media_min_collapsed),
                         self.media_max_collapsed)

            # disp is still computed (for backwards-compat returns) but width
            # is driven by the stable calculation above
            available_span = max_w - left_px - right_px - ctrl_px
            remaining      = max(60, available_span - text_pad_extra - mute_px)
            disp = self._compose_media_text(True, remaining)

        else:
            # ── Expanded: fit the actual content ────────────────────────────
            mute_px = 0
            available_span = max_w - left_px - right_px - ctrl_px
            remaining = max(240, available_span - text_pad_extra)
            disp = self._compose_media_text(False, remaining)
            text_px = self._measure(measure_font, disp)
            min_media = self.media_min_expanded
            max_media = self.media_max_expanded
            text_px = min(max(text_px, min_media), max_media)

        final_w = (left_px + text_px + right_px + ctrl_px
                   + self.text_pad_px + self.space + text_pad_extra)
        final_w = max(min_w, min(final_w, max_w))
        final_h = self.collapsed_h if collapsed else self.expanded_h
        return final_w, final_h, disp, left_px, right_px

    def _get_bg_image(self, w, h):
        bg_w = max(1, w - int(self.pad_x*0.25))
        bg_h = max(1, h - int(self.pad_y*0.25))
        key = (bg_w, bg_h, int(round(RADIUS*self.scale)), self.colors["bg"])
        cached = self._bg_cache.get(key)
        if cached is not None:
            return cached
        rounded = _rounded_with_shadow(bg_w, bg_h, int(round(RADIUS*self.scale)), self.colors["bg"])
        self._bg_cache[key] = rounded
        self._bg_cache_order.append(key)
        while len(self._bg_cache_order) > BACKGROUND_CACHE_LIMIT:
            oldest = self._bg_cache_order.pop(0)
            self._bg_cache.pop(oldest, None)
        return rounded

    def _get_bg_image_in_env(self, pill_w, pill_h, env_w, env_h):
        """PIL image (env_w × env_h) with the pill centered inside it.
        Transparent surrounds render as black via -transparentcolor, so the
        window can stay at env_w × env_h the whole animation without resizing."""
        bg_w = max(1, pill_w - int(self.pad_x * 0.25))
        bg_h = max(1, pill_h - int(self.pad_y * 0.25))
        r    = int(round(RADIUS * self.scale))
        fill = self.colors["bg"]
        key  = (env_w, env_h, bg_w, bg_h, r, fill)
        cached = self._bg_cache.get(key)
        if cached is not None:
            return cached
        pad = 4
        im   = Image.new("RGBA", (env_w + pad * 2, env_h + pad * 2), (0, 0, 0, 0))
        draw = ImageDraw.Draw(im)
        x0 = (env_w - bg_w) // 2 + pad
        y0 = (env_h - bg_h) // 2 + pad
        x1, y1 = x0 + bg_w - 1, y0 + bg_h - 1
        draw.rounded_rectangle([x0, y0 + 1, x1, y1 + 1], r, fill=(0, 0, 0, 72))
        draw.rounded_rectangle([x0 + 1, y0 + 2, x1 - 1, y1], max(1, r - 1), fill=(0, 0, 0, 28))
        draw.rounded_rectangle([x0, y0, x1, y1], r, fill=fill)
        draw.rounded_rectangle([x0 + 1, y0 + 1, x1 - 1, y1 - 1], max(1, r - 1),
                               outline=(255, 255, 255, 42), width=1)
        draw.rounded_rectangle([x0, y0, x1, y1], r, outline=(0, 0, 0, 55), width=1)
        photo = ImageTk.PhotoImage(im)
        self._bg_cache[key] = photo
        self._bg_cache_order.append(key)
        while len(self._bg_cache_order) > BACKGROUND_CACHE_LIMIT:
            self._bg_cache.pop(self._bg_cache_order.pop(0), None)
        return photo

    def _apply_geometry(self, w, h, left_px=None, right_px=None):
        self.width = w
        if left_px is None or right_px is None:
            _, _, _, left_px, right_px = self._desired_layout(self.collapsed)
        self.root.geometry(f"{w}x{h}+{self._center_x(w)}+{SNAP_TOP_MARGIN_PX}")
        rounded = self._get_bg_image(w, h)
        self.bg.configure(image=rounded); self.bg.image=rounded
        ctrl_px = self._media_controls_px(self.collapsed)
        media_w = max(40, w - right_px - left_px - ctrl_px)
        self.media_wrap.configure(width=media_w, height=h - int(2*self.pad_y))
        self.frame.place(relx=0.5, rely=0.5, anchor="center",
                         width=w - int(2*self.pad_x),
                         height=h - int(2*self.pad_y))
        # no progress bar

    def _center_x(self, w=None):
        sw = self.root.winfo_screenwidth()
        ww = self.width if w is None else w
        return max(0, int((sw - ww)/2))

    def _open_mode_menu(self, event=None):
        if getattr(self, "mode_menu", None) is None:
            return
        try:
            self.mode_menu.tk_popup(event.x_root, event.y_root)
        finally:
            try: self.mode_menu.grab_release()
            except Exception: pass
            # Popup interactions can bypass normal <Leave>; re-check collapse after closing.
            self._schedule_collapse_check(50)

    # ---------- timer / alarm ----------
    def _toggle_timer_panel(self, _event=None):
        # Don't open while collapsed.
        if self.collapsed:
            self.collapsed = False
            self._apply_collapsed_state()
        if self.timer_panel is not None:
            try: self.timer_panel.close()
            except Exception: pass
            self.timer_panel = None
            return
        try:
            self.timer_panel = TimerAlarmPanel(self, self.timeralarm)
        except Exception:
            self.timer_panel = None

    def _on_ta_event(self, name, _payload=None):
        # Called from the (UI) thread that invoked tick(); keep light.
        if name == "timer_completed":
            self.timeralarm.play_alert("timer")
            self.timeralarm.show_notification(
                "Timer done",
                self.timeralarm.timer.label or "Your timer finished."
            )
            if not REDUCE_MOTION:
                self._start_ta_pulse(TIMER_DONE_COLOR, cycles=4)
        elif name == "alarm_ringing":
            self.timeralarm.play_alert("alarm")
            self.timeralarm.show_notification(
                "Alarm",
                self.timeralarm.alarm.label or self.timeralarm.alarm.time_str(self.use_24h)
            )
            if not REDUCE_MOTION:
                self._start_ta_pulse(ALARM_PULSE_COLOR, cycles=999)
        elif name in ("alarm_dismissed", "alarm_snoozed", "alarm_auto_dismissed",
                      "timer_canceled", "timer_reset", "alarm_deleted"):
            self._stop_ta_pulse()

    def _start_ta_pulse(self, color, cycles=4):
        # Subtle pulse on the timer/alarm icon. Stops when state is acked or
        # the configured cycle count is exhausted (alarm uses many cycles, but
        # tick() will dismiss after ALARM_RING_TIMEOUT_S).
        self._stop_ta_pulse()
        self._ta_pulse_active = True
        self._ta_pulse_phase = 0.0
        self._ta_pulse_color = color
        self._ta_pulse_cycles_left = cycles

        base_fg = self.colors["fg"]
        def step():
            if not self._ta_pulse_active:
                try: self.btn_timer.configure(fg=base_fg)
                except Exception: pass
                return
            self._ta_pulse_phase += 0.18
            t = (math.sin(self._ta_pulse_phase) + 1) / 2  # 0..1
            try:
                # Blend btn_timer color toward pulse color.
                bh = self._ta_pulse_color.lstrip("#")
                br, bg, bb = (int(bh[i:i+2], 16) for i in (0, 2, 4))
                fh = base_fg.lstrip("#")
                fr, fg2, fbb = (int(fh[i:i+2], 16) for i in (0, 2, 4))
                r = int(fr + (br - fr) * t)
                g = int(fg2 + (bg - fg2) * t)
                b = int(fbb + (bb - fbb) * t)
                self.btn_timer.configure(fg=f"#{r:02x}{g:02x}{b:02x}")
            except Exception:
                pass
            if self._ta_pulse_phase >= math.pi * 2:
                self._ta_pulse_phase = 0.0
                self._ta_pulse_cycles_left -= 1
                if self._ta_pulse_cycles_left <= 0:
                    self._stop_ta_pulse()
                    return
            self._ta_pulse_after = self.root.after(60, step)
        step()

    def _stop_ta_pulse(self):
        self._ta_pulse_active = False
        if self._ta_pulse_after:
            try: self.root.after_cancel(self._ta_pulse_after)
            except Exception: pass
            self._ta_pulse_after = None
        try: self.btn_timer.configure(fg=self.colors["fg"])
        except Exception: pass

    def _tick_timer_alarm(self):
        try:
            self.timeralarm.tick()
        except Exception:
            pass
        try:
            if self.collapsed:
                self._refresh_collapsed_display()
            else:
                self._update_timer_ring_btn()
        except Exception:
            pass
        self.root.after(TIMER_TICK_MS, self._tick_timer_alarm)

    def _refresh_collapsed_display(self):
        """Lightweight repaint of media text + width when collapsed."""
        try:
            target_w, target_h, disp, left_px, right_px = self._desired_layout(True)
            # Update marquee with fresh full text
            self._update_marquee()
            if not self._animating and abs(target_w - self.root.winfo_width()) > 24:
                self._apply_geometry(target_w, target_h, left_px, right_px)
        except Exception:
            pass

    def _recalc_mode_btn_width(self):
        try:
            self.mode_btn.update_idletasks()
            self._mode_btn_extra = self.mode_btn.winfo_reqwidth() + self.space//2
        except Exception:
            self._mode_btn_extra = self.space

    def _on_collapsed_mode_change(self):
        self._recalc_mode_btn_width()
        self._apply_collapsed_state()
        self._schedule_collapse_check(50)

    def _toggle_24h(self):
        self.use_24h = bool(self.var_24h.get())
        self._tick_time()
        self._schedule_collapse_check(50)

    def _toggle_seconds(self):
        self.show_seconds = bool(self.var_seconds.get())
        self._tick_time()
        self._schedule_collapse_check(50)

    def _toggle_battery_collapsed(self):
        self.show_battery_collapsed = bool(self.var_battery_collapsed.get())
        self._apply_collapsed_state()
        self._schedule_collapse_check(50)

    def _toggle_mute_collapsed(self):
        self.show_mute_collapsed = bool(self.var_mute_collapsed.get())
        if self.collapsed:
            self._refresh_collapsed_display()
        self._schedule_collapse_check(50)

    def _toggle_soundwave_collapsed(self):
        self.show_soundwave_collapsed = bool(self.var_soundwave_collapsed.get())
        if self.collapsed:
            self._apply_collapsed_state()

    def _toggle_controls_collapsed(self):
        self.show_controls_collapsed = bool(self.var_controls_collapsed.get())
        if self.collapsed:
            self._apply_collapsed_state()

    # ── cycling content in collapsed mode ─────────────────────────────────────
    _CYCLE_MS = 4000   # how long each item is shown before rotating

    def _toggle_cycle_collapsed(self):
        self.cycle_collapsed = bool(self.var_cycle_collapsed.get())
        if self.collapsed:
            if self.cycle_collapsed:
                self._cycle_idx = 0
                self._start_cycle()
            else:
                self._stop_cycle()
            self._update_marquee()

    def _build_cycle_items(self) -> list:
        """Return the list of strings to rotate through in collapsed mode."""
        items = []
        # Timer / alarm prefix always goes first when active
        svc = getattr(self, "timeralarm", None)
        if svc is not None and svc.primary_state() != "idle":
            ta = self._ta_compact_prefix()
            if ta:
                items.append(ta)
        title  = (self._last_title  or "").strip()
        artist = (self._last_artist or "").strip()
        if title:
            items.append(title)
        if artist:
            items.append(artist)
        time_txt   = (self.var_time.get() if hasattr(self, "var_time") else "") or ""
        batt_short = self._collapsed_battery_text()
        time_batt  = "  ".join(p for p in (time_txt, batt_short) if p)
        if time_batt:
            items.append(time_batt)
        return items if items else ["—"]

    def _start_cycle(self):
        """Begin or restart the cycle timer."""
        self._stop_cycle()
        items = self._build_cycle_items()
        self._cycle_items = items
        if len(items) > 1:
            self._cycle_after = self.root.after(self._CYCLE_MS, self._advance_cycle)

    def _stop_cycle(self):
        if self._cycle_after:
            try: self.root.after_cancel(self._cycle_after)
            except Exception: pass
            self._cycle_after = None

    def _advance_cycle(self):
        """Move to the next item, refresh the marquee, and reschedule."""
        if not getattr(self, "collapsed", True) or not getattr(self, "cycle_collapsed", False):
            self._cycle_after = None
            return
        items = self._build_cycle_items()
        self._cycle_items = items
        self._cycle_idx   = (self._cycle_idx + 1) % max(1, len(items))
        self._update_marquee()       # set_text triggers marquee _reset automatically
        self._cycle_after = self.root.after(self._CYCLE_MS, self._advance_cycle)

    def _toggle_rotate_title_collapsed(self):
        self.rotate_title_collapsed = bool(self.var_rotate_title_collapsed.get())
        if hasattr(self, "marquee"):
            try:
                self.marquee.set_force_scroll(
                    self.rotate_title_collapsed and bool(getattr(self, "collapsed", True))
                )
            except Exception:
                pass
        if self.collapsed:
            self._update_marquee()

    def _toggle_pin_expanded(self):
        self.pin_expanded = bool(self.var_pin_expanded.get())
        if self.pin_expanded:
            # Immediately expand and stay there
            if self.collapsed:
                self.collapsed = False
                self._apply_collapsed_state()
        else:
            # Let hover logic take over — collapse now if pointer not inside
            self._schedule_collapse_check(50)

    def _with_mute_prefix(self, text: str) -> str:
        """Prepend mute glyph when muted. Width is already reserved in _desired_layout."""
        if self.show_mute_collapsed and self._is_muted:
            if text and text not in ("—", ""):
                return "✕  " + text
            return "✕"
        return text

    def _compose_collapsed_full_text(self) -> str:
        """Full (non-ellipsized) combined time+title string for the marquee."""
        cf = self.font_compact
        title  = (self._last_title  or "").strip()
        artist = (self._last_artist or "").strip()
        primary = title or artist or (self._badge or "").strip()

        svc = getattr(self, "timeralarm", None)
        if svc is not None and svc.primary_state() != "idle":
            ta_prefix = self._ta_compact_prefix()
        else:
            ta_prefix = ""

        time_txt  = (self.var_time.get() if hasattr(self, "var_time") else "") or ""
        batt_short = self._collapsed_battery_text()

        mode_val = (self.var_collapsed_mode.get()
                    if hasattr(self, "var_collapsed_mode") else "Title + Time")

        if mode_val == "Time Only":
            base = "  ".join(p for p in (time_txt, batt_short) if p) or "--:--"
            return (ta_prefix + "  " + base).strip() if ta_prefix else base

        aux = "  ".join(p for p in (time_txt, batt_short) if p)
        spacer = "  "
        time_part = aux or time_txt or batt_short or "--:--"

        if mode_val == "Title Only":
            parts = [p for p in (primary, batt_short) if p]
            content = spacer.join(parts) if parts else (aux or "--")
            return (ta_prefix + spacer + content).strip() if ta_prefix else content

        # "Title + Time" — return full un-clipped text
        if ta_prefix:
            if primary:
                return f"{ta_prefix}{spacer}{time_part}{spacer}{primary}"
            return f"{ta_prefix}{spacer}{time_part}"
        if primary:
            return f"{time_part}{spacer}{primary}"
        return time_part

    def _update_marquee(self):
        """Push current display text to the marquee strip (both modes)."""
        if not hasattr(self, "marquee"):
            return
        if getattr(self, "collapsed", True):
            if getattr(self, "rotate_title_collapsed", False):
                # Rotating-title mode: continuous ticker with time + track info
                title      = (self._last_title  or "").strip()
                artist     = (self._last_artist or "").strip()
                time_txt   = (self.var_time.get() if hasattr(self, "var_time") else "") or ""
                batt_short = self._collapsed_battery_text()
                parts = [p for p in (time_txt, batt_short, title, artist) if p]
                ticker = "  ·  ".join(parts) if parts else "—"
                self.marquee.set_text(self._with_mute_prefix(ticker))
                return
            if getattr(self, "cycle_collapsed", False):
                # Cycling mode: show the item at the current cycle index
                items = self._build_cycle_items()
                idx   = getattr(self, "_cycle_idx", 0) % max(1, len(items))
                text  = items[idx] if items else "—"
                self.marquee.set_text(self._with_mute_prefix(text))
                return
            # Normal collapsed: timer/alarm text or full time+title string
            svc = getattr(self, "timeralarm", None)
            if svc is not None and svc.primary_state() != "idle":
                ta = self._ta_compact_prefix()
                if ta:
                    self.marquee.set_text(self._with_mute_prefix(ta))
                    return
            full = self._compose_collapsed_full_text()
            self.marquee.set_text(self._with_mute_prefix(full))
        else:
            # Expanded: full "title — artist" with play prefix; marquee scrolls if long
            title  = (self._last_title  or "").strip()
            artist = (self._last_artist or "").strip()
            if title and artist:
                text = f"{title} — {artist}"
            elif title:
                text = title
            elif artist:
                text = artist
            else:
                text = "—"
            if not getattr(self, "_last_paused", True) and text not in ("—", ""):
                text = "▸ " + text
            self.marquee.set_text(text)

    # ---------- drag / click-through ----------
    def _start_drag(self,e):
        if self.root.winfo_y() <= ALLOW_DRAG_BAND_PX:
            self._drag=True; self._drag_off=(e.x,e.y)
    def _on_drag(self,e):
        if not getattr(self,"_drag",False): return
        dx,dy=e.x-self._drag_off[0], e.y-self._drag_off[1]
        x=self.root.winfo_x()+dx; y=max(0,min(self.root.winfo_y()+dy,ALLOW_DRAG_BAND_PX))
        self.root.geometry(f"+{x}+{y}")
    def _stop_drag(self,_):
        if getattr(self,"_drag",False):
            self._drag=False
            self.root.geometry(f"+{self._center_x()}+{SNAP_TOP_MARGIN_PX}")
    def toggle_click_through(self):
        try:
            GWL_EXSTYLE=-20; WS_EX_TRANSPARENT=0x20; WS_EX_LAYERED=0x80000
            user32=ctypes.windll.user32
            get=user32.GetWindowLongW; set=user32.SetWindowLongW
            hwnd=self.hwnd; ex=get(hwnd, GWL_EXSTYLE)
            if not getattr(self,"_click",False):
                set(hwnd,GWL_EXSTYLE, ex|WS_EX_TRANSPARENT|WS_EX_LAYERED); self._click=True
            else:
                set(hwnd,GWL_EXSTYLE, ex & ~WS_EX_TRANSPARENT); self._click=False
        except Exception: pass

    # ---------- hover/collapse with animation ----------
    def _hover_enter(self, _=None):
        if self._collapse_after:
            try: self.root.after_cancel(self._collapse_after)
            except Exception: pass
            self._collapse_after = None
        if self.collapsed:
            self.collapsed = False
            self._apply_collapsed_state()
    def _hover_leave(self, _=None):
        if getattr(self, "pin_expanded", False):
            return
        self._schedule_collapse_check(HOVER_COLLAPSE_DELAY_MS)

    def _schedule_collapse_check(self, delay_ms=HOVER_COLLAPSE_DELAY_MS):
        if self._collapse_after:
            try: self.root.after_cancel(self._collapse_after)
            except Exception: pass
        self._collapse_after = self.root.after(max(1, int(delay_ms)), self._run_collapse_check)

    def _run_collapse_check(self):
        self._collapse_after = None
        self._maybe_collapse_pointer()

    def _pointer_inside_island_or_panel(self):
        """True if the cursor is inside the island OR the timer/alarm popover.
        Includes a small bridge so the gap between them counts as 'inside'."""
        try:
            x, y = self.root.winfo_pointerx(), self.root.winfo_pointery()
            rx, ry = self.root.winfo_rootx(), self.root.winfo_rooty()
            w, h = self.root.winfo_width(), self.root.winfo_height()
            BRIDGE = 16  # px tolerance around edges & in the gap to the panel
            in_island = (rx - BRIDGE <= x < rx + w + BRIDGE) and \
                        (ry - BRIDGE <= y < ry + h + BRIDGE)
            if in_island:
                return True
            panel = getattr(self, "timer_panel", None)
            if panel is not None and not getattr(panel, "_destroyed", True):
                try:
                    px = panel.winfo_rootx(); py = panel.winfo_rooty()
                    pw = panel.winfo_width(); ph = panel.winfo_height()
                    in_panel = (px - BRIDGE <= x < px + pw + BRIDGE) and \
                               (py - BRIDGE <= y < py + ph + BRIDGE)
                    if in_panel:
                        return True
                    # Bridge the gap directly above the panel (between island bottom
                    # and panel top) so brief diagonal cursor moves don't collapse.
                    bridge_x0 = min(rx, px) - BRIDGE
                    bridge_x1 = max(rx + w, px + pw) + BRIDGE
                    bridge_y0 = ry + h
                    bridge_y1 = py
                    if bridge_y1 > bridge_y0:
                        in_bridge = (bridge_x0 <= x < bridge_x1) and \
                                    (bridge_y0 <= y < bridge_y1)
                        if in_bridge:
                            return True
                except Exception:
                    pass
            return False
        except Exception:
            return False

    def _maybe_collapse_pointer(self):
        if getattr(self, "pin_expanded", False):
            return
        try:
            if self._pointer_inside_island_or_panel():
                return
            if not self.collapsed:
                self.collapsed = True
                self._apply_collapsed_state()
        except Exception:
            if not self.collapsed:
                self.collapsed = True
                self._apply_collapsed_state()

    def _ease(self, t): 
        """Keep collapse/expand crisp; overshoot costs frames and looks worse in Tk."""
        return _ease_out_quart(t)

    def _animate_to(self, target_w, target_h, left_px, right_px):
        if self._anim_after:
            try: self.root.after_cancel(self._anim_after)
            except Exception: pass
            self._anim_after = None

        cur_w = float(self.root.winfo_width() or self.width or target_w)
        cur_h = float(self.root.winfo_height() or
                      (self.collapsed_h if self.collapsed else self.expanded_h))

        if abs(cur_w - target_w) <= 4 and abs(cur_h - target_h) <= 2:
            self._animating = False
            self._apply_geometry(target_w, target_h, left_px, right_px)
            return

        self._animating = True
        # ── Envelope strategy ────────────────────────────────────────────────
        # Set the OS window to the LARGEST size (envelope) ONCE before playback.
        # During animation every callback only swaps the bg image + updates the
        # inner Tk frame — zero Win32 SetWindowPos calls, so DWM never stalls.
        STEPS    = 12   # 12 × 16 ms ≈ 192 ms
        INTERVAL = 16   # ms
        sw, sh   = cur_w, cur_h
        env_w    = max(int(sw), target_w)
        env_h    = max(int(sh), target_h)
        ctrl_px  = self._media_controls_px(self.collapsed)

        # Single OS resize to the envelope (often a no-op when collapsing because
        # the window is already at expanded size).
        self.width = env_w
        self.root.geometry(f"{env_w}x{env_h}+{self._center_x(env_w)}+{SNAP_TOP_MARGIN_PX}")

        # Pre-render every frame (PIL front-loaded; playback is pure image swap)
        frames = []
        for i in range(1, STEPS + 1):
            t      = _ease_out_expo(i / STEPS)
            w      = max(10, round(sw + (target_w - sw) * t))
            h      = max(10, round(sh + (target_h - sh) * t))
            bg_img = self._get_bg_image_in_env(w, h, env_w, env_h)
            mw     = max(40, w - right_px - left_px - ctrl_px)
            frames.append((w, h, bg_img, mw))

        n = [0]

        def step():
            if n[0] >= STEPS:
                self._animating = False
                # Final snap: resize window to exact target (no-op if env == target)
                self._apply_geometry(target_w, target_h, left_px, right_px)
                return
            w, h, bg_img, mw = frames[n[0]]
            n[0] += 1
            # No root.geometry() call — window stays at envelope throughout
            self.media_wrap.configure(width=mw, height=h - int(2 * self.pad_y))
            self.frame.place(relx=0.5, rely=0.5, anchor="center",
                             width=w - int(2 * self.pad_x),
                             height=h - int(2 * self.pad_y))
            self.bg.configure(image=bg_img)
            self.bg.image = bg_img
            self._anim_after = self.root.after(INTERVAL, step)

        step()

    def _apply_collapsed_state(self, initial=False):
        collapsed = self.collapsed

        # left cluster
        _show_ctrl = (not collapsed) or getattr(self, "show_controls_collapsed", False)
        if _show_ctrl:
            try: self.controls_wrap.grid()
            except Exception: pass
            if self.volpill is not None:
                try: self.volpill.grid()
                except Exception: pass
        else:
            try: self.controls_wrap.grid_remove()
            except Exception: pass
            if self.volpill is not None:
                try: self.volpill.grid_remove()
                except Exception: pass

        # gear, divider & right cluster
        if self.mode_btn is not None:
            try: (self.mode_btn.grid_remove if collapsed else self.mode_btn.grid)()
            except Exception: pass
        if getattr(self, "btn_timer", None) is not None:
            try: (self.btn_timer.grid_remove if collapsed else self.btn_timer.grid)()
            except Exception: pass
            if collapsed and getattr(self, "timer_panel", None) is not None:
                try: self.timer_panel.close()
                except Exception: pass
        if self.lbl_div is not None:
            (self.lbl_div.grid_remove if collapsed else self.lbl_div.grid)()
        for w in (self.lbl_date, self.lbl_time, self.lbl_batt):
            try: (w.grid_remove if collapsed else w.grid)()
            except Exception: pass
        # ring removed

        # badge + media alignment
        if collapsed:
            try: self.lbl_badge.pack_forget()
            except Exception: pass
            try: self.album_art_lbl.pack_forget()
            except Exception: pass
            try: self.lbl_media.pack_forget()
            except Exception: pass
            # Hide compact soundwave; conditionally re-show before marquee
            if hasattr(self, "soundwave_compact"):
                try: self.soundwave_compact.pack_forget()
                except Exception: pass
            if getattr(self, "show_soundwave_collapsed", False) and hasattr(self, "soundwave_compact"):
                try:
                    self.soundwave_compact.pack(side="left",
                                                padx=(0, max(2, self.space // 2)))
                except Exception: pass
            # Marquee (after optional compact wave)
            if hasattr(self, "marquee"):
                try:
                    self.marquee.set_font(self.font_compact)
                    self.marquee.set_force_scroll(
                        getattr(self, "rotate_title_collapsed", False)
                    )
                    self.marquee.pack(side="left", fill="both", expand=True)
                    self.marquee.activate()
                except Exception: pass
            # Start cycle timer if option is on; reset index on each collapse
            if getattr(self, "cycle_collapsed", False):
                self._cycle_idx = 0
                self._start_cycle()
        else:
            # Expanded: use marquee for scrolling long titles
            self._stop_cycle()   # don't cycle while expanded
            if hasattr(self, "marquee"):
                try: self.marquee.set_force_scroll(False)
                except Exception: pass
            if hasattr(self, "soundwave_compact"):
                try: self.soundwave_compact.pack_forget()
                except Exception: pass
            try: self.lbl_media.pack_forget()
            except Exception: pass
            if SHOW_APP_BADGE:
                try: self.lbl_badge.pack(side="left", padx=(0, self.space//2))
                except Exception: pass
            if getattr(self, "_art_photo", None) is not None:
                try:
                    self.album_art_lbl.pack(side="left", padx=(0, 6))
                except Exception: pass
            if hasattr(self, "marquee"):
                try:
                    exp_strip_h = max(18, self.font_sz + 8)
                    self.marquee.configure(height=exp_strip_h)
                    self.marquee.set_font(self.font_main)
                    self.marquee.pack(side="left", fill="both", expand=True, anchor="w",
                                      padx=(0, max(6, int(self.radius * 0.3))))
                    self.marquee.activate()
                except Exception: pass

        # compute desired layout + apply text
        st = self.media.get()
        buffering = self._should_show_buffer(st)
        target_w, target_h, disp, left_px, right_px = self._desired_layout(collapsed)
        if buffering:
            disp = self._buffer_label()
        elif self._is_empty_media_display(disp):
            disp = (st.app or "").strip() or "No media"

        if collapsed:
            # Feed the marquee with full (un-ellipsized) text
            self._update_marquee()
        else:
            # Marquee handles expanded display too; just feed it
            self._last_paused = bool(st.paused)
            self._update_marquee()

        if initial: self._apply_geometry(target_w, target_h, left_px, right_px)
        else:       self._animate_to(target_w, target_h, left_px, right_px)

        # Sync timer ring button state whenever collapse/expand changes
        try:
            self._update_timer_ring_btn()
        except Exception:
            pass

    # ---------- ring rendering ----------
    def _render_ring_img(self, ratio: float):
        """Apple-style media progress ring (used by internal callers)."""
        return self._render_timer_ring_img_ex("running", ratio)

    def _render_timer_ring_img_ex(self, phase: str, ratio: float) -> "ImageTk.PhotoImage":
        """
        Render a circular progress ring sized for btn_timer.
        phase: 'running' | 'paused' | 'completed'
        ratio: 0.0-1.0 elapsed fraction
        """
        S = max(20, self.ring_diam)
        SS = S * 4
        TH = max(4, int(self.ring_thick * 4))
        pad = TH // 2 + 4

        img = Image.new("RGBA", (SS, SS), (0, 0, 0, 0))
        draw = ImageDraw.Draw(img)
        cx = cy = SS // 2
        r = (SS - 2 * pad) // 2

        if phase == "completed":
            arc_color_hex = TIMER_DONE_COLOR
            ratio = 1.0
            track_a = 90
            arc_a = 255
        elif phase == "paused":
            arc_color_hex = TIMER_COLOR
            track_a = 80
            arc_a = 160  # dimmed to signal paused
        else:  # running
            arc_color_hex = TIMER_COLOR
            track_a = 55
            arc_a = 255

        # Track ring
        draw.arc([cx-r, cy-r, cx+r, cy+r], start=-90, end=270,
                 width=TH, fill=(80, 80, 80, track_a))

        ratio = max(0.0, min(1.0, float(ratio)))
        if ratio > 0:
            rc = arc_color_hex.lstrip('#')
            rgb = tuple(int(rc[i:i+2], 16) for i in (0, 2, 4))
            rgba = rgb + (arc_a,)
            ang1 = -90 + 360 * ratio
            draw.arc([cx-r, cy-r, cx+r, cy+r], start=-90, end=ang1, width=TH, fill=rgba)
            # Round caps at start and end
            cap_r = TH / 2
            for ang in (-90.0, ang1):
                rad = math.radians(ang)
                x = cx + r * math.cos(rad)
                y = cy + r * math.sin(rad)
                draw.ellipse([x-cap_r, y-cap_r, x+cap_r, y+cap_r], fill=rgba)

        # Paused indicator: two small vertical bars in centre
        if phase == "paused" and not REDUCE_MOTION:
            bar_h = max(4, SS // 6)
            bar_w = max(2, SS // 14)
            gap   = max(1, SS // 16)
            x_l = cx - gap - bar_w
            x_r = cx + gap
            y0  = cy - bar_h // 2
            y1  = cy + bar_h // 2
            fg = (200, 200, 200, 200)
            draw.rectangle([x_l, y0, x_l + bar_w, y1], fill=fg)
            draw.rectangle([x_r, y0, x_r + bar_w, y1], fill=fg)

        img = img.resize((S, S), Image.LANCZOS)
        return ImageTk.PhotoImage(img)

    def _update_ring(self, ratio):
        self._last_ratio = max(0.0, min(1.0, float(ratio or 0.0)))

    def _update_timer_ring_btn(self):
        """
        In expanded mode: replace btn_timer emoji with a live ring image.
        In collapsed mode: restore emoji (compact strip text shows the countdown).
        """
        phase = self.timeralarm.timer.phase
        show_ring = (not self.collapsed) and phase not in ("idle", "canceled")
        if show_ring:
            ratio = self.timeralarm.timer.progress()
            img = self._render_timer_ring_img_ex(phase, ratio)
            self._timer_ring_photo = img
            try:
                self.btn_timer.configure(image=img, text="", compound="none")
            except Exception:
                pass
        else:
            self._timer_ring_photo = None
            try:
                self.btn_timer.configure(image="", text="⏱", compound="none")
            except Exception:
                pass

    # ---------- album art ----------
    def _make_art_photo(self, data: bytes) -> "ImageTk.PhotoImage | None":
        """Convert raw thumbnail bytes to a rounded-corner PhotoImage for the island."""
        from io import BytesIO
        try:
            img = Image.open(BytesIO(data)).convert("RGBA")
            w, h = img.size
            mn = min(w, h)
            l, t = (w - mn) // 2, (h - mn) // 2
            sz = self._art_size
            img = img.crop((l, t, l + mn, t + mn)).resize((sz, sz), Image.LANCZOS)
            mask = Image.new("L", (sz, sz), 0)
            ImageDraw.Draw(mask).rounded_rectangle(
                [0, 0, sz - 1, sz - 1], radius=max(4, sz // 5), fill=255
            )
            out = Image.new("RGBA", (sz, sz), (0, 0, 0, 0))
            out.paste(img, mask=mask)
            return ImageTk.PhotoImage(out)
        except Exception:
            return None

    def _sync_album_art(self):
        """
        Called on the UI thread from _do_media_poll.
        Reads artwork from MediaService cache; creates/updates the album_art_lbl
        only when the track identity key changes, avoiding repeated PhotoImage creation.
        """
        try:
            art_bytes, art_key = self.media.get_artwork()
            if art_key == self._art_key_shown:
                return  # nothing changed
            self._art_key_shown = art_key
            if art_bytes:
                photo = self._make_art_photo(art_bytes)
                self._art_photo = photo  # keep reference so Tkinter doesn't GC it
                if photo is not None:
                    self.album_art_lbl.configure(image=photo)
                    if not self.collapsed:
                        # Pack before marquee (expanded mode uses marquee instead of lbl_media)
                        try:
                            ref = getattr(self, "marquee", self.lbl_media)
                            self.album_art_lbl.pack(
                                side="left", padx=(0, 6), before=ref
                            )
                        except Exception:
                            pass
                    return
            # No art or conversion failed — hide
            self._art_photo = None
            self.album_art_lbl.configure(image="")
            try:
                self.album_art_lbl.pack_forget()
            except Exception:
                pass
        except Exception:
            pass

    # ---------- periodic ticks ----------
    def _tick_time(self, initial=False):
        now=datetime.now()
        use_24 = getattr(self, "use_24h", USE_24H)
        show_sec = getattr(self, "show_seconds", SHOW_SECONDS)
        fmt = ("%H:%M:%S" if show_sec else "%H:%M") if use_24 else ("%I:%M:%S %p" if show_sec else "%I:%M %p")
        t=now.strftime(fmt);  t=t[1:] if (not use_24 and t.startswith("0")) else t
        d=now.strftime("%a, %b %-d" if sys.platform!="win32" else "%a, %b %#d")
        self.var_time.set(t); self.var_date.set(d)
        # Keep marquee text current (time is part of the scrolling strip)
        if getattr(self, "collapsed", False):
            try: self._update_marquee()
            except Exception: pass
        self.root.after(1000, self._tick_time)

    def _tick_media_poll(self):
        try:
            self._do_media_poll()
        except Exception:
            pass
        self.root.after(UI_TICK_MS, self._tick_media_poll)

    def _do_media_poll(self):
        st=self.media.get()
        self._badge = st.app
        full = f"{st.title} — {st.artist}".strip(" —")
        self._full_media_text = full
        # NEW: store raw fields for truncation logic
        has_meta = bool((st.title or "").strip() or (st.artist or "").strip())
        now_mono = time.monotonic()
        if has_meta:
            self._last_title = st.title or ""
            self._last_artist = st.artist or ""
            self._last_meta_at = now_mono
        else:
            keep_cached = (now_mono - self._last_meta_at) <= self._meta_hold_s
            if not keep_cached:
                self._last_title = ""
                self._last_artist = ""
        buffering = self._should_show_buffer(st)
        self._set_buffering(buffering)

        # Tk can occasionally miss a <Leave> when the island is resizing, so
        # re-check hover state on the media tick as a fallback.
        if not self.collapsed and not self._animating:
            self._maybe_collapse_pointer()
            if self.collapsed:
                return

        track_id = f"{st.title}|{st.artist}|{round(st.duration or 0)}"
        if track_id != self._track_id:
            self._track_id = track_id
            self._pos_cache = st.position or 0.0
            self._dur_cache = st.duration or 0.0
            self._rate_cache = st.rate or 1.0
            self._last_wall = time.monotonic()
        else:
            if st.duration and st.duration > 0:
                self._dur_cache = st.duration
            if st.position and abs(st.position - self._pos_cache) > 1.25:
                self._pos_cache = st.position
            if st.rate and st.rate > 0:
                self._rate_cache = st.rate

        audio_playing = self.audio.ok and self.audio.is_playing()
        if self._dur_cache <= 0 and (full or audio_playing):
            self._dur_cache = float(FALLBACK_DURATION_SEC)

        playback_paused = bool(st.paused)
        effective_rate = st.rate if (st.rate is not None) else self._rate_cache or 1.0
        should_advance = (self._dur_cache > 0) and (audio_playing or (not playback_paused) or effective_rate > 0.05)
        if not should_advance and st.position:
            self._pos_cache = st.position

        now = time.monotonic()
        if self._dur_cache > 0 and should_advance:
            dt = max(0.0, now - self._last_wall)
            self._pos_cache = min(self._dur_cache, self._pos_cache + dt * (self._rate_cache or 1.0))
        self._last_wall = now

        target_w, target_h, disp, left_px, right_px = self._desired_layout(self.collapsed)
        if buffering:
            disp = self._buffer_label()
        elif self._is_empty_media_display(disp):
            disp = (st.app or "").strip() or "No media"
        self._last_paused = bool(st.paused)
        if self.collapsed:
            # Marquee handles its own text — just update it each tick
            self._update_marquee()
            ratio = 0.0
        else:
            # Marquee handles expanded display too
            self._update_marquee()
            ratio = (self._pos_cache / self._dur_cache) if self._dur_cache>0 else 0.0

        self._update_ring(ratio)

        # Update sound wave animation
        playing_now = not bool(st.paused) or (self.audio.ok and self.audio.is_playing())
        try: self.soundwave.set_playing(playing_now)
        except Exception: pass
        try: self.soundwave_compact.set_playing(playing_now)
        except Exception: pass

        self._sync_album_art()

        if not self._animating and abs(target_w - self.root.winfo_width()) > 24:
            self._apply_geometry(target_w, target_h, left_px, right_px)

        def en(lbl,ok): lbl.configure(fg=self.colors["fg"] if ok else self.colors["fg_dim"])
        en(self.btn_prev, HAS_SMTC and st.can_prev)
        en(self.btn_next, HAS_SMTC and st.can_next)
        en(self.btn_pp,   HAS_SMTC and st.can_playpause)

    def _tick_battery(self):
        pct,chg = self.power.get()
        self._battery_charging = bool(chg) if chg is not None else False
        if pct is None:
            text = ""
        elif chg is None:
            text = f"{pct}%"
        else:
            text = f"{'AC' if chg else 'BAT'} {pct}%"
        self.var_batt.set(text)
        self.root.after(250, self._tick_battery)


    def _tick_volume_sync(self):
        """Occasionally sync the pill to external system volume and mute changes."""
        if self.volume.ok and self.volpill is not None:
            try:
                # Re-acquire endpoint when default playback device has changed
                self.volume.refresh()
                cur = self.volume.get()
                if cur is not None:
                    self.volpill.set_percent(cur, animate=(abs(cur - int(round(self.volpill._display))) >= 2))
                new_muted = self.volume.get_muted()
                self.volpill.update_muted(new_muted)
                # Keep cached flag in sync; refresh compact strip if mute state flipped
                if new_muted != self._is_muted:
                    self._is_muted = new_muted
                    if self.collapsed:
                        self._refresh_collapsed_display()
            except Exception:
                pass
        self.root.after(500, self._tick_volume_sync)

    def _watchdog(self):
        """Periodically ensure the island window is visible and correctly positioned.
        Recovers from Win+D, UAC dialogs, display changes, and other events that
        can bury or hide overrideredirect windows on Windows."""
        try:
            state = self.root.wm_state()
            if state != "normal":
                self.root.deiconify()
            self.root.attributes("-topmost", True)
            self.root.lift()
            sw = self.root.winfo_screenwidth()
            sh = self.root.winfo_screenheight()
            wx, wy = self.root.winfo_x(), self.root.winfo_y()
            ww, wh = self.root.winfo_width(), self.root.winfo_height()
            if ww > 0 and (wx < -ww or wx > sw or wy < 0 or wy > sh):
                self._apply_geometry(ww, wh)
            if self._animating and self._anim_after is None:
                self._animating = False
        except Exception:
            pass
        self.root.after(3000, self._watchdog)

    # ---------- life & events ----------
    def _on_prev(self,_): self.media.prev()
    def _on_pp(self,_): self.media.play_pause()
    def _on_next(self,_): self.media.next()

    def _adjust_volume(self, delta):
        if not self.volume.ok or self.volpill is None: return
        cur = self.volume.get() or 0
        new = max(0, min(100, int(cur + delta)))
        try: self.volume.set(new)
        except Exception: pass
        self.volpill.set_percent(new, animate=True, pulse=True)

    def quit(self):
        try: self.media.shutdown()
        except Exception: pass
        try: self.power.shutdown()
        except Exception: pass
        try: self.timeralarm.shutdown()
        except Exception: pass
        try:
            if self.timer_panel is not None:
                self.timer_panel.close()
        except Exception: pass
        self.root.destroy()

# -------- Power/Volume services --------
class PowerService:
    def __init__(self, poll_ms=POWER_POLL_MS):
        self.poll_ms=poll_ms; self._running=True
        self._percent=None; self._charging=None
        threading.Thread(target=self._loop, daemon=True).start()
    def shutdown(self): self._running=False
    def get(self): return self._percent, self._charging
    def _loop(self):
        while self._running:
            pct, charging = None, None
            if HAS_PSUTIL:
                try:
                    bat=psutil.sensors_battery()
                    if bat is not None:
                        pct = int(round(bat.percent))
                        charging = bool(bat.power_plugged)
                except Exception:
                    pct, charging = None, None
            if pct is None:
                pct, charging = _read_windows_battery()
            self._percent, self._charging = pct, charging
            time.sleep(max(0.25, self.poll_ms/1000.0))

class VolumeService:
    def __init__(self):
        self.ok = HAS_PYCAW
        self.endpoint = None
        self._device_id = None  # ID of the device currently backing self.endpoint
        if not self.ok: return
        try:
            dev = AudioUtilities.GetSpeakers()
            iface = dev.Activate(IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
            self.endpoint = ctypes.cast(iface, ctypes.POINTER(IAudioEndpointVolume))
            try:
                self._device_id = dev.GetId()
            except Exception:
                pass
        except Exception:
            self.ok = False

    def refresh(self):
        """Re-acquire the endpoint when the default playback device changes.
        Call once per UI tick so mute/volume always reflect the active device."""
        if not self.ok:
            return
        try:
            dev = AudioUtilities.GetSpeakers()
            try:
                new_id = dev.GetId()
            except Exception:
                new_id = None
            # Skip re-activation if we can confirm it's the same device
            if (new_id is not None and self._device_id is not None
                    and new_id == self._device_id):
                return
            iface = dev.Activate(IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
            self.endpoint = ctypes.cast(iface, ctypes.POINTER(IAudioEndpointVolume))
            self._device_id = new_id
        except Exception:
            pass

    def get(self):
        if not self.ok or self.endpoint is None: return None
        try: return int(round(self.endpoint.GetMasterVolumeLevelScalar()*100))
        except Exception: return None
    def set(self, pct):
        if not self.ok or self.endpoint is None: return
        try:
            pct=max(0,min(100,int(float(pct))))
            self.endpoint.SetMasterVolumeLevelScalar(pct/100.0, None)
        except Exception: pass
    def get_muted(self) -> bool:
        if not self.ok or self.endpoint is None: return False
        try: return bool(self.endpoint.GetMute())
        except Exception: return False
    def set_muted(self, muted: bool):
        if not self.ok or self.endpoint is None: return
        try: self.endpoint.SetMute(int(bool(muted)), None)
        except Exception: pass

# ---- entry ----
def _maybe_relaunch_with_smtc() -> bool:
    """If current runtime lacks SMTC bindings, relaunch with Python 3.11 when available."""
    if HAS_SMTC:
        return False
    if os.environ.get("DI_RELAUNCHED") == "1":
        return False
    if os.environ.get("DI_BOOTSTRAPPED") == "1":
        return False
    if "--install" in sys.argv[1:]:
        return False

    script = os.path.abspath(__file__)
    cwd = os.path.dirname(script) or None
    check_code = "from winsdk.windows.media.control import GlobalSystemMediaTransportControlsSessionManager"

    # First: try to bootstrap winsdk into the currently running interpreter.
    try:
        chk = subprocess.run([sys.executable, "-c", check_code], capture_output=True, text=True, timeout=6)
        if chk.returncode != 0:
            pip_ok = subprocess.run(
                [sys.executable, "-m", "pip", "--version"],
                capture_output=True,
                text=True,
                timeout=6,
            ).returncode == 0
            if pip_ok:
                subprocess.run(
                    [sys.executable, "-m", "pip", "install", "--disable-pip-version-check", "winsdk"],
                    capture_output=True,
                    text=True,
                    timeout=25,
                )
            chk2 = subprocess.run([sys.executable, "-c", check_code], capture_output=True, text=True, timeout=6)
            if chk2.returncode == 0:
                env = os.environ.copy()
                env["DI_BOOTSTRAPPED"] = "1"
                subprocess.Popen([sys.executable, script], cwd=cwd, env=env)
                return True
    except Exception:
        pass

    launchers = []
    py_launcher = shutil.which("py")
    if py_launcher:
        launchers.append([py_launcher, "-3.11"])
    py311_root = os.path.join(os.path.expanduser("~"), "AppData", "Local", "Programs", "Python", "Python311")
    for exe in ("pythonw.exe", "python.exe"):
        p = os.path.join(py311_root, exe)
        if os.path.exists(p):
            launchers.append([p])

    tried = set()
    for base in launchers:
        key = tuple(base)
        if key in tried:
            continue
        tried.add(key)
        try:
            chk = subprocess.run(base + ["-c", check_code], capture_output=True, text=True, timeout=6)
            if chk.returncode != 0:
                continue
            env = os.environ.copy()
            env["DI_RELAUNCHED"] = "1"
            subprocess.Popen(base + [script], cwd=cwd, env=env)
            return True
        except Exception:
            continue

    return False

def main():
    root=tk.Tk()
    app=App(root)
    root.mainloop()

if __name__=="__main__":
    if len(sys.argv)>1 and sys.argv[1]=="--install":
        try:
            startup=os.path.join(os.environ["APPDATA"],"Microsoft","Windows","Start Menu","Programs","Startup")
            lnk=os.path.join(startup,"DynamicIsland.lnk")
            import win32com.client  # type: ignore
            shell=win32com.client.Dispatch("WScript.Shell")
            sc=shell.CreateShortCut(lnk)
            sc.Targetpath=sys.executable
            sc.Arguments=os.path.abspath(__file__)
            sc.WorkingDirectory=os.path.dirname(os.path.abspath(__file__))
            sc.save(); print("Added to Startup.")
        except Exception as e:
            print(f"Startup shortcut failed: {e}")
    if _maybe_relaunch_with_smtc():
        sys.exit(0)
    main()

