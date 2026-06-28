"""
DynamicIsland (Tkinter) — Slim Top-Center Widget
Playback + Volume + Auto-Resize + No-Clipping + Active App Badge

Deps:
  pip install pillow psutil winsdk
Optional (volume):
  pip install pycaw comtypes
"""

import sys, os, time, threading, asyncio, ctypes
from dataclasses import dataclass
from datetime import datetime
import tkinter as tk
from tkinter import ttk
import tkinter.font as tkfont
from PIL import Image, ImageTk, ImageDraw

# -------- optional imports --------
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
    import comtypes  # type: ignore
    HAS_PYCAW = True
except Exception:
    pass

# -------- config (smaller, cleaner) --------
OPACITY = 0.90
BASE_FONT_SIZE = 10           # was 12
SHOW_SECONDS = True
USE_24H = False
MEDIA_POLL_MS = 1000
POWER_POLL_MS = 30000
RADIUS = 20                   # was 15
PADDING_X = 10                # was 12
PADDING_Y = 6                 # was 8
ALLOW_DRAG_BAND_PX = 100
SNAP_TOP_MARGIN_PX = 2

MAX_WIDTH_RATIO = 0.30
MIN_WIDTH_PX   = 420          # was 460
VOL_WIDTH_PX   = 60           # was 90
SPACE_PX       = 4            # was 10
SHOW_DIVIDER   = True
SHOW_APP_BADGE = False

FORCE_THEME = None  # "dark" | "light" | None
# readable divider colors (no alpha)
DARK  = dict(bg="#2B2B2B", fg="#FFFFFF", divider="#A8A8A8", fg_dim="#BFBFBF")
LIGHT = dict(bg="#F3F3F3", fg="#111111", divider="#5E5E5E", fg_dim="#555555")

# -------- utils --------
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

def _theme() -> str:
    if FORCE_THEME in ("dark", "light"):
        return FORCE_THEME
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") as k:
            v,_ = winreg.QueryValueEx(k, "AppsUseLightTheme")
            return "light" if v == 1 else "dark"
    except Exception:
        return "dark"

def _rounded(w,h,r,fill):
    img = Image.new("RGBA", (w,h), (0,0,0,0))
    ImageDraw.Draw(img).rounded_rectangle([(0,0),(w,h)], r, fill=fill)
    return ImageTk.PhotoImage(img)

class Tooltip:
    def __init__(self, widget, text_func, pad=(8,6)):
        self.widget = widget; self.text_func = text_func
        self.pad = pad; self.tip=None
        widget.bind("<Enter>", self._show); widget.bind("<Leave>", self._hide)
        widget.bind("<Motion>", self._move)
    def _show(self,_):
        if self.tip or not self.text_func: return
        t = self.text_func() or ""
        if not t: return
        self.tip = tk.Toplevel(self.widget); self.tip.overrideredirect(True)
        self.tip.attributes("-topmost", True)
        f = tk.Frame(self.tip,bg="#222",bd=0); f.pack()
        tk.Label(f,text=t,bg="#222",fg="#fff",font=("Segoe UI",9),justify="left").pack(
            padx=self.pad[0],pady=self.pad[1])
    def _move(self,e):
        if self.tip: self.tip.geometry(f"+{e.x_root+12}+{e.y_root+12}")
    def _hide(self,_):
        if self.tip: self.tip.destroy(); self.tip=None

# -------- services --------
@dataclass
class MediaState:
    title: str = ""
    artist: str = ""
    app: str = ""      # active app (Spotify, Edge, etc.)
    paused: bool = False
    can_prev: bool = False
    can_next: bool = False
    can_playpause: bool = True

# --- replace your existing MediaService with this one ---
from dataclasses import dataclass
import asyncio, threading

@dataclass
class MediaState:
    title: str = ""
    artist: str = ""
    app: str = ""
    paused: bool = False
    can_prev: bool = False
    can_next: bool = False
    can_playpause: bool = True

class MediaService:
    """
    Uses Windows SMTC but *selects the best session*:
      1) Prefer PLAYING sessions
      2) Prefer sessions with non-empty title/artist
      3) Fall back gracefully
    This avoids Chrome/Edge returning a 'blank' session and fixes missing titles like 'Circles'.
    """
    def __init__(self, poll_ms=1000):
        self.poll_ms = poll_ms
        self._state = MediaState()
        self._lock = threading.Lock()
        self._loop = asyncio.new_event_loop()
        threading.Thread(target=self._loop.run_forever, daemon=True).start()
        self._session_cache = None
        asyncio.run_coroutine_threadsafe(self._poll(), self._loop)

    def shutdown(self):
        try: self._loop.call_soon_threadsafe(self._loop.stop)
        except Exception: pass

    def get(self) -> MediaState:
        with self._lock:
            return MediaState(**self._state.__dict__)

    # Public controls
    def play_pause(self):
        if not HAS_SMTC: return
        asyncio.run_coroutine_threadsafe(self._toggle_pp(), self._loop)
    def next(self):
        if not HAS_SMTC: return
        asyncio.run_coroutine_threadsafe(self._ctrl("next"), self._loop)
    def prev(self):
        if not HAS_SMTC: return
        asyncio.run_coroutine_threadsafe(self._ctrl("prev"), self._loop)

    # ---- internals ----
    async def _manager(self):
        try:
            mgr = await MediaManager.request_async()
            return mgr
        except Exception:
            return None

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
        """
        Score all sessions and return the 'best':
          +3 if PLAYING
          +2 if PAUSED
          +1 if STOPPED (rarely useful)
          +2 if title present
          +1 if artist present
        """
        try:
            sessions = list(mgr.get_sessions())
        except Exception:
            sessions = []

        best, best_score = None, -1
        for s in sessions:
            try:
                info = s.get_playback_info()
                status = getattr(info, "playback_status", None)
                score = 0
                if status == PlaybackStatus.PLAYING: score += 3
                elif status == PlaybackStatus.PAUSED: score += 2
                elif status == PlaybackStatus.STOPPED: score += 1

                props = await s.try_get_media_properties_async()
                title = (props.title or "").strip()
                artist = (props.artist or "").strip()
                if title: score += 2
                if artist: score += 1
            except Exception:
                continue

            if score > best_score:
                best, best_score = s, score

        # If nothing scored, fall back to current_session
        if best is None:
            try:
                best = mgr.get_current_session()
            except Exception:
                best = None
        return best

    async def _poll(self):
        while True:
            try:
                if HAS_SMTC:
                    mgr = await self._manager()
                    if mgr:
                        session = await self._select_best_session(mgr)
                    else:
                        session = None

                    self._session_cache = session
                    if session:
                        props = await session.try_get_media_properties_async()
                        title  = (props.title  or "").strip()
                        artist = (props.artist or "").strip()

                        paused, can_prev, can_next, can_pp = False, False, False, True
                        try:
                            info = session.get_playback_info()
                            status = info.playback_status
                            paused = status in (PlaybackStatus.PAUSED, PlaybackStatus.STOPPED)
                            ctrls  = info.controls
                            can_prev = getattr(ctrls, "is_previous_enabled", True)
                            can_next = getattr(ctrls, "is_next_enabled", True)
                            can_pp   = getattr(ctrls, "is_play_pause_toggle_enabled", True) or getattr(ctrls, "is_play_enabled", True)
                        except Exception:
                            pass

                        aumid = ""
                        try: aumid = str(session.source_app_user_model_id)
                        except Exception: pass
                        appname = self._map_app(aumid)

                        self._set(title, artist, appname, paused, can_prev, can_next, can_pp)
                    else:
                        self._set("", "", "", False, False, False, False)
                else:
                    # psutil fallback (no SMTC): just expose app names; titles can't be read here
                    appname = ""
                    if HAS_PSUTIL:
                        try:
                            wanted = {"spotify.exe","music.ui.exe","vlc.exe","chrome.exe","msedge.exe"}
                            names = { (p.info.get("name") or "").lower()
                                      for p in psutil.process_iter(["name"])
                                      if (p.info.get("name") or "").lower() in wanted }
                            appname = ", ".join(sorted(n.replace(".exe","").capitalize() for n in names))
                        except Exception:
                            pass
                    self._set("", "", appname, False, False, False, False)
            except Exception:
                pass
            await asyncio.sleep(max(0.2, self.poll_ms/1000.0))

    def _set(self, title, artist, app, paused, prv, nxt, pp):
        with self._lock:
            self._state.title = title or ""
            self._state.artist = artist or ""
            self._state.app = app or ""
            self._state.paused = bool(paused)
            self._state.can_prev = bool(prv)
            self._state.can_next = bool(nxt)
            self._state.can_playpause = bool(pp)

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
            if not s: return
            if which=="next": await s.try_skip_next_async()
            elif which=="prev": await s.try_skip_previous_async()
        except Exception:
            pass


class PowerService:
    def __init__(self, poll_ms=POWER_POLL_MS):
        self.poll_ms=poll_ms; self._running=True
        self._percent=None; self._charging=None
        threading.Thread(target=self._loop, daemon=True).start()
    def shutdown(self): self._running=False
    def get(self): return self._percent, self._charging
    def _loop(self):
        while self._running:
            if not HAS_PSUTIL:
                self._percent=self._charging=None
            else:
                try:
                    bat=psutil.sensors_battery()
                    if bat is None: self._percent=self._charging=None
                    else:
                        self._percent=int(round(bat.percent)); self._charging=bool(bat.power_plugged)
                except Exception:
                    self._percent=self._charging=None
            time.sleep(max(1.0, self.poll_ms/1000.0))

class VolumeService:
    def __init__(self):
        self.ok = HAS_PYCAW
        if not self.ok: return
        try:
            dev = AudioUtilities.GetSpeakers()
            iface = dev.Activate(IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
            self.endpoint = ctypes.cast(iface, ctypes.POINTER(IAudioEndpointVolume))
        except Exception:
            self.ok = False
    def get(self):
        if not self.ok: return None
        try: return int(round(self.endpoint.GetMasterVolumeLevelScalar()*100))
        except Exception: return None
    def set(self, pct):
        if not self.ok: return
        try:
            pct=max(0,min(100,int(float(pct))))
            self.endpoint.SetMasterVolumeLevelScalar(pct/100.0, None)
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

        # services early
        self.media=MediaService(poll_ms=MEDIA_POLL_MS)
        self.power=PowerService(poll_ms=POWER_POLL_MS)

        # shortcuts
        self.root.bind("<space>", lambda e:self.media.play_pause())
        self.root.bind("<Control-Right>", lambda e:self.media.next())
        self.root.bind("<Control-Left>",  lambda e:self.media.prev())

        # DPI / metrics (smaller height)
        self.hwnd = self.root.winfo_id()
        self.scale = _dpi_scale(self.hwnd)
        self.font_sz = max(9, int(round(BASE_FONT_SIZE*self.scale)))
        self.pad_x = int(round(PADDING_X*self.scale))
        self.pad_y = int(round(PADDING_Y*self.scale))
        self.radius = int(round(RADIUS*self.scale))
        self.height = int(round(40*self.scale))        # was 52
        self.space  = int(round(SPACE_PX*self.scale))
        self.vol_w  = int(round(VOL_WIDTH_PX*self.scale))

        # theme / fonts
        self.theme_name=_theme()
        self.colors = DARK if self.theme_name=="dark" else LIGHT
        self.font_main = tkfont.Font(family="Segoe UI", size=self.font_sz)
        self.font_bold = tkfont.Font(family="Segoe UI", size=self.font_sz, weight="bold")
        self.font_icons= tkfont.Font(family="Segoe UI Symbol", size=max(10,self.font_sz))

        # rounded bg
        self.bg = tk.Label(self.root,bg="black",bd=0,highlightthickness=0)
        self.bg.place(relx=0.5,rely=0.5,anchor="center")

        # frame (grid)
        self.frame = tk.Frame(self.root, bg=self.colors["bg"], bd=0, highlightthickness=0)
        self.frame.place(relx=0.5, rely=0.5, anchor="center")

        # controls cluster (subframe)
        btn_pad=max(1,int(3*self.scale))
        self.controls_wrap=tk.Frame(self.frame,bg=self.colors["bg"])
        self.btn_prev=tk.Label(self.controls_wrap,text="⏮",font=self.font_icons,bg=self.colors["bg"],fg=self.colors["fg"],padx=btn_pad,pady=btn_pad)
        self.btn_pp  =tk.Label(self.controls_wrap,text="⏯",font=self.font_icons,bg=self.colors["bg"],fg=self.colors["fg"],padx=btn_pad,pady=btn_pad)
        self.btn_next=tk.Label(self.controls_wrap,text="⏭",font=self.font_icons,bg=self.colors["bg"],fg=self.colors["fg"],padx=btn_pad,pady=btn_pad)
        for b,cb in ((self.btn_prev,self._on_prev),(self.btn_pp,self._on_pp),(self.btn_next,self._on_next)):
            b.pack(side="left", padx=(0,self.space//2))
            b.bind("<Button-1>", cb); b.bind("<Enter>", lambda e,w=b:w.config(cursor="hand2"))
            b.bind("<Leave>", lambda e,w=b:w.config(cursor=""))
        self.controls_wrap.grid(row=0, column=0, padx=(self.pad_x,0), sticky="w")

        # volume (robust Horizontal style)
        self.volume = VolumeService()
        self.scale_vol=None
        if self.volume.ok:
            self.var_vol=tk.IntVar(value=self.volume.get() or 50)
            style = ttk.Style()
            try: style.theme_use(style.theme_use())
            except Exception: pass
            base_style   = "Horizontal.TScale"
            custom_style = "Di.Horizontal.TScale"
            try:
                base_layout = style.layout(base_style)
                style.layout(custom_style, base_layout)
            except Exception:
                style.layout(custom_style, [
                    ("Horizontal.Scale.trough", {
                        "sticky": "we",
                        "children": [("Horizontal.Scale.slider", {"side": "left", "sticky": ""})]
                    })
                ])
            track = "#3A3A3A" if self.theme_name=="dark" else "#D0D0D0"
            style.configure(custom_style, troughcolor=track, background=self.colors["bg"])
            style.map(custom_style, background=[("active", track)])

            self.scale_vol=ttk.Scale(self.frame,from_=0,to=100,orient="horizontal",
                                     length=self.vol_w,command=self._on_volume_change,style=custom_style)
            self.scale_vol.set(self.var_vol.get())
            self.scale_vol.grid(row=0,column=1,padx=(self.space,self.space),sticky="w")

        # media area: badge + title
        self.media_wrap=tk.Frame(self.frame,bg=self.colors["bg"],height=self.height - int(2*self.pad_y))
        self.media_wrap.grid(row=0,column=2,padx=(self.space,self.space),sticky="w")
        self.media_wrap.pack_propagate(False)

        self.var_badge=tk.StringVar(value="")
        self.var_media=tk.StringVar(value="—")
        self.lbl_badge=tk.Label(self.media_wrap,textvariable=self.var_badge,bg=self.colors["bg"],
                                fg=self.colors["fg"], font=("Segoe UI", self.font_sz-1, "bold"), anchor="w")
        self.lbl_badge.pack(side="left", padx=(0, self.space//2))
        self.lbl_media=tk.Label(self.media_wrap,textvariable=self.var_media,bg=self.colors["bg"],
                                fg=self.colors["fg"], font=self.font_main, anchor="w")
        self.lbl_media.pack(side="left", fill="both", expand=True)
        Tooltip(self.lbl_media, text_func=lambda: self._full_media_text)

        # divider
        cidx=3
        if SHOW_DIVIDER:
            self.lbl_div=tk.Label(self.frame,text="•",bg=self.colors["bg"],fg=self.colors["divider"],font=self.font_main)
            self.lbl_div.grid(row=0,column=cidx,padx=(0,self.space),sticky="w"); cidx+=1
        else:
            self.lbl_div=None

        # right cluster
        self.var_date=tk.StringVar(value="")
        self.var_time=tk.StringVar(value="")
        self.var_batt=tk.StringVar(value="")
        self.lbl_date=tk.Label(self.frame,textvariable=self.var_date,bg=self.colors["bg"],fg=self.colors["fg"],font=self.font_main)
        self.lbl_time=tk.Label(self.frame,textvariable=self.var_time,bg=self.colors["bg"],fg=self.colors["fg"],font=self.font_bold)
        self.lbl_batt=tk.Label(self.frame,textvariable=self.var_batt,bg=self.colors["bg"],fg=self.colors["fg"],font=self.font_main)
        self.lbl_date.grid(row=0,column=cidx,padx=(0,self.space),sticky="e"); cidx+=1
        self.lbl_time.grid(row=0,column=cidx,padx=(0,self.space),sticky="e"); cidx+=1
        self.lbl_batt.grid(row=0,column=cidx,padx=(0,self.pad_x),sticky="e")

        # only media column expands
        for i in range(cidx+1):
            self.frame.grid_columnconfigure(i, weight=0)
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

        # initial draw + timers
        self._tick_time(initial=True)
        self._layout_resize()
        self.root.deiconify()
        self.root.after(150, self._tick_media)
        self.root.after(500, self._tick_battery)
        self.root.after(3000, self._tick_theme)

    # ---------- measuring & layout ----------
    def _measure(self, font, text): return font.measure(text)

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

    def _reserved_right_px(self):
        worst_time = "11:59 PM" if not USE_24H else "23:59"
        if SHOW_SECONDS: worst_time = "11:59:59 PM" if not USE_24H else "23:59:59"
        worst_date = "Wed, Sep 30"
        worst_batt = "🔌 100%"
        px = 0
        if self.lbl_div: px += self._measure(self.font_main,"•")+self.space
        px += self._measure(self.font_main,worst_date)+self.space
        px += self._measure(self.font_bold,worst_time)+self.space
        if self.var_batt.get(): px += self._measure(self.font_main,worst_batt)
        px += self.pad_x
        return px

    def _left_controls_px(self):
        icons = self._measure(self.font_icons,"⏮")+self._measure(self.font_icons,"⏯")+self._measure(self.font_icons,"⏭")
        px = self.pad_x + icons + self.space
        if self.scale_vol is not None: px += self.vol_w + self.space
        return px

    def _layout_resize(self):
        sw = self.root.winfo_screenwidth()
        max_w = int(sw*MAX_WIDTH_RATIO)
        min_w = int(max(MIN_WIDTH_PX*self.scale, 360*self.scale))
        right_px = self._reserved_right_px()
        left_px  = self._left_controls_px()

        badge = f"[{self._badge}]" if (SHOW_APP_BADGE and self._badge) else ""
        self.var_badge.set(badge)
        media_full = f"{badge} {self._full_media_text}".strip()

        remaining = max(60, max_w - left_px - right_px)
        disp = self._ellipsize(media_full, self.font_main, remaining) if media_full else "—"
        self.var_media.set(disp)

        final_w = left_px + self._measure(self.font_main, disp) + right_px
        final_w = max(min_w, min(final_w, max_w))

        if final_w != self.width:
            self.width = final_w
            self.root.geometry(f"{final_w}x{self.height}+{self._center_x()}+{SNAP_TOP_MARGIN_PX}")
            rounded=_rounded(final_w - int(self.pad_x*0.25),
                             self.height - int(self.pad_y*0.25),
                             int(round(RADIUS*self.scale)), self.colors["bg"])
            self.bg.configure(image=rounded); self.bg.image=rounded

        media_w = max(40, final_w - right_px - left_px)
        self.media_wrap.configure(width=media_w, height=self.height - int(2*self.pad_y))
        self.frame.place(relx=0.5, rely=0.5, anchor="center",
                         width=final_w - int(2*self.pad_x),
                         height=self.height - int(2*self.pad_y))

    def _center_x(self):
        sw = self.root.winfo_screenwidth()
        return max(0, int((sw - self.width)/2))

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

    # ---------- events ----------
    def _on_prev(self,_): self.media.prev()
    def _on_pp(self,_): self.media.play_pause()
    def _on_next(self,_): self.media.next()
    def _on_volume_change(self,val):
        if self.volume.ok:
            try: self.volume.set(int(float(val)))
            except Exception: pass

    # ---------- ticks ----------
    def _tick_time(self, initial=False):
        now=datetime.now()
        fmt = ("%H:%M:%S" if SHOW_SECONDS else "%H:%M") if USE_24H else ("%I:%M:%S %p" if SHOW_SECONDS else "%I:%M %p")
        t=now.strftime(fmt);  t=t[1:] if (not USE_24H and t.startswith("0")) else t
        d=now.strftime("%a, %b %-d" if sys.platform!="win32" else "%a, %b %#d")
        self.var_time.set(t); self.var_date.set(d)
        self._layout_resize()
        self.root.after(500 if SHOW_SECONDS else 1000, self._tick_time)

    def _tick_media(self):
        st=self.media.get()
        self._badge = st.app
        full = f"{st.title} — {st.artist}".strip(" —")
        self._full_media_text = full
        disp_full = ("⏸ "+full) if (full and st.paused) else (full or "")
        self.var_media.set(disp_full if disp_full else "—")

        def en(lbl,ok): lbl.configure(fg=self.colors["fg"] if ok else self.colors["fg_dim"])
        en(self.btn_prev, HAS_SMTC and st.can_prev)
        en(self.btn_next, HAS_SMTC and st.can_next)
        en(self.btn_pp,   HAS_SMTC and st.can_playpause)

        self._layout_resize()
        self.root.after(300, self._tick_media)

    def _tick_battery(self):
        pct,chg=self.power.get()
        self.var_batt.set("" if pct is None else f"{'🔌' if chg else '🔋'} {pct}%")
        self._layout_resize()
        self.root.after(1500, self._tick_battery)

    def _tick_theme(self):
        th=_theme()
        if th!=self.theme_name:
            self.theme_name=th; self.colors=DARK if th=="dark" else LIGHT
            for w in (self.frame,self.controls_wrap,self.media_wrap,
                      self.btn_prev,self.btn_pp,self.btn_next,
                      self.lbl_badge,self.lbl_media,self.lbl_date,self.lbl_time,self.lbl_batt):
                try: w.configure(bg=self.colors["bg"], fg=self.colors["fg"])
                except Exception: pass
            if self.lbl_div: self.lbl_div.configure(bg=self.colors["bg"], fg=self.colors["divider"])
            # restyle slider on theme change
            if self.scale_vol is not None:
                style = ttk.Style()
                track = "#3A3A3A" if self.theme_name=="dark" else "#D0D0D0"
                try:
                    style.configure("Di.Horizontal.TScale", troughcolor=track, background=self.colors["bg"])
                    style.map("Di.Horizontal.TScale", background=[("active", track)])
                except Exception: pass
            self._layout_resize()
        self.root.after(3000, self._tick_theme)

    # ---------- life ----------
    def quit(self):
        try: self.media.shutdown()
        except Exception: pass
        try: self.power.shutdown()
        except Exception: pass
        self.root.destroy()

# ---- entry ----
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
    main()
