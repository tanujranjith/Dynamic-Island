"""
DynamicIsland — Narrow, Smooth Hover, Halo-Free Edges (single file)
- Fixed window width (no per-frame recenter jitters)
- Hover expand via geometry hit-testing + polling (works everywhere on the pill)
- Halo-free edges using a binary mask (no semi-transparent fringe vs. chroma key)
- Optional soft shadow toggle (off by default to keep edges ultra-clean)
- SMTC media + album art; controls only when expanded; thin progress hairline
- Time/Date/Battery on the right
- Click-through toggle: Ctrl+Shift+W
"""

import sys, os, time, threading, asyncio, ctypes, io
from dataclasses import dataclass
from datetime import datetime
import tkinter as tk
from tkinter import ttk
import tkinter.font as tkfont
from PIL import Image, ImageTk, ImageDraw, ImageFilter

# ---------- optional deps ----------
try:
    import psutil
    HAS_PSUTIL = True
except Exception:
    HAS_PSUTIL = False

try:
    from winsdk.windows.media.control import GlobalSystemMediaTransportControlsSessionManager as MediaManager
    from winsdk.windows.media.control import GlobalSystemMediaTransportControlsSessionPlaybackStatus as PlaybackStatus
    from winsdk.windows.storage.streams import DataReader
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

# ---------- config (narrow + smooth + crisp) ----------
TRANSPARENT_CHROMA = "#010101"  # unique color key (never draw this in the pill)
WINDOW_OPACITY = 1.0

BASE_FONT_SIZE = 10
SHOW_SECONDS = True
USE_24H = False

PILL_BASE_HEIGHT_PX = 24
PADDING_X = 8
PADDING_Y = 4
SPACE_PX  = 4

# Keep it narrow
FIXED_COMPACT_WIDTH_PX = 250   # << adjust to taste (230–280 is nice)
CONTAINER_EXTRA_PX     = 16

# Animation @ ~60 Hz
ANIM_INTERVAL_MS = 16
SPRING = 0.22
DAMP   = 0.32

# Expand rules (on pill, not window)
PLAY_HEIGHT_DELTA_PX  = 5
PLAY_WIDTH_FACTOR     = 1.05
HOVER_HEIGHT_DELTA_PX = 9
HOVER_WIDTH_FACTOR    = 1.15

# Progress hairline
HAIRLINE_PX = 1

# Edges & shadow
SHADOW_SOFT = False  # False = halo-free crisp edge (recommended with chroma key)
SHADOW_ALPHA = 140   # only used if SHADOW_SOFT = True
AA_SCALE = 8         # mask supersample scale (higher -> cleaner contour)

# Colors
FORCE_THEME = None  # "dark" | "light" | None
DARK  = dict(bg="#222528", fg="#FFFFFF", fg_dim="#BFC3C9", track="#3A3F45", accent="#0A84FF")
LIGHT = dict(bg="#F2F3F4", fg="#111111", fg_dim="#5A5F66", track="#D5D8DC", accent="#0A84FF")

# ---------- helpers ----------
def _dpi_scale(hwnd: int) -> float:
    try:
        user32 = ctypes.windll.user32
        try:
            user32.GetDpiForWindow.restype = ctypes.c_uint
            return max(0.85, user32.GetDpiForWindow(hwnd) / 96.0)
        except Exception:
            user32.GetDpiForSystem.restype = ctypes.c_uint
            return max(0.85, user32.GetDpiForSystem() / 96.0)
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

def _pill_image_binary(w: int, h: int, fill_rgb: tuple[int,int,int], add_soft_shadow: bool) -> ImageTk.PhotoImage:
    """
    Halo-free capsule suited for Tk's color-key transparency:
    - Build a high-res binary mask (AA_SCALE×), downsample, then THRESHOLD to 0/255.
    - Composite with *opaque* fill (no semi-transparent edge → no chroma halo).
    - Optional Gaussian shadow rendered underneath (can cause slight halo; off by default).
    """
    SS = max(4, int(AA_SCALE))
    r  = max(1, h // 2)
    W, H = w * SS, h * SS

    # 1) Build high-res binary mask of a capsule
    mask_hr = Image.new("L", (W, H), 0)
    d = ImageDraw.Draw(mask_hr)
    rr = r * SS
    d.rectangle((rr, 0, W - rr, H), fill=255)
    d.ellipse((0, 0, rr * 2, H),             fill=255)
    d.ellipse((W - rr * 2, 0, W, H),         fill=255)

    # 2) Downsample then hard-threshold to binary (0/255) – NO semi-transparency
    mask = mask_hr.resize((w, h), Image.LANCZOS)
    mask = mask.point(lambda p: 255 if p >= 128 else 0, mode="L")

    # 3) Compose opaque pill on chroma background (outside → chroma, inside → fill)
    bg = Image.new("RGB", (w, h), _hex_to_rgb(TRANSPARENT_CHROMA))
    pill_rgb = Image.new("RGB", (w, h), fill_rgb)
    img_rgb = Image.composite(pill_rgb, bg, mask)

    # 4) Optional soft shadow (may introduce subtle halo; disabled by default)
    if add_soft_shadow:
        sh = Image.new("L", (w, h), 0)
        ImageDraw.Draw(sh).bitmap((0, 0), mask, fill=SHADOW_ALPHA)
        sh = sh.filter(ImageFilter.GaussianBlur(2.0))
        # put shadow below by blending towards darker behind; here we just darken.
        shadow_rgb = Image.new("RGB", (w, h), (0, 0, 0))
        img_rgb = Image.composite(img_rgb, shadow_rgb, sh)

    # Convert to Tk
    img_rgba = Image.new("RGBA", (w, h))
    img_rgba.paste(img_rgb, (0, 0))
    return ImageTk.PhotoImage(img_rgba)

def _hex_to_rgb(hx: str) -> tuple[int,int,int]:
    hx = hx.lstrip("#")
    if len(hx)==6:
        return int(hx[0:2],16), int(hx[2:4],16), int(hx[4:6],16)
    return (1,1,1)

def _placeholder_art(size: int, fg="#CCCCCC", bg="#7B7B7B"):
    im = Image.new("RGB", (size, size), bg)
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).ellipse((0,0,size,size), fill=255)
    d = ImageDraw.Draw(im)
    s = size
    d.ellipse((int(0.58*s), int(0.20*s), int(0.78*s), int(0.40*s)), fill=fg)
    d.rectangle((int(0.62*s), int(0.18*s), int(0.66*s), int(0.62*s)), fill=fg)
    d.ellipse((int(0.48*s), int(0.56*s), int(0.64*s), int(0.72*s)), fill=fg)
    out = Image.new("RGBA", (size, size), (0,0,0,0))
    out.paste(im, (0,0), m)
    return out

def _circle_img_from_bytes(b: bytes | None, size: int, ring=False, ring_color="#0A84FF", ring_w=2):
    try:
        if b:
            im = Image.open(io.BytesIO(b)).convert("RGB").resize((size, size), Image.LANCZOS)
        else:
            im = _placeholder_art(size).convert("RGB")
    except Exception:
        im = _placeholder_art(size).convert("RGB")

    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).ellipse((0,0,size,size), fill=255)
    out = Image.new("RGBA", (size, size), (0,0,0,0))
    out.paste(im, (0,0), mask)

    if ring:
        d = ImageDraw.Draw(out)
        inset = max(1, ring_w)//2 + 1
        d.ellipse((inset, inset, size-inset-1, size-inset-1), outline=ring_color, width=ring_w)
    return ImageTk.PhotoImage(out)

# ---------- services ----------
@dataclass
class MediaState:
    title: str = ""
    artist: str = ""
    paused: bool = False
    can_prev: bool = False
    can_next: bool = False
    can_playpause: bool = True
    art_bytes: bytes | None = None
    progress: float = 0.0

class MediaService:
    def __init__(self, poll_ms=900):
        self.poll_ms = poll_ms
        self._state = MediaState()
        self._lock = threading.Lock()
        self._loop = asyncio.new_event_loop()
        threading.Thread(target=self._loop.run_forever, daemon=True).start()
        self._session = None
        asyncio.run_coroutine_threadsafe(self._poll(), self._loop)

    def shutdown(self):
        try: self._loop.call_soon_threadsafe(self._loop.stop)
        except: pass

    def get(self):
        with self._lock:
            return MediaState(**self._state.__dict__)

    def play_pause(self):
        if HAS_SMTC: asyncio.run_coroutine_threadsafe(self._toggle("pp"), self._loop)
    def next(self):
        if HAS_SMTC: asyncio.run_coroutine_threadsafe(self._toggle("next"), self._loop)
    def prev(self):
        if HAS_SMTC: asyncio.run_coroutine_threadsafe(self._toggle("prev"), self._loop)

    async def _mgr(self):
        try: return await MediaManager.request_async()
        except: return None

    async def _best(self, mgr):
        try: sessions=list(mgr.get_sessions())
        except: sessions=[]
        best=None; best_score=-1
        for s in sessions:
            try:
                info=s.get_playback_info(); status=getattr(info,"playback_status",None)
                score=0
                if status==PlaybackStatus.PLAYING: score+=3
                elif status==PlaybackStatus.PAUSED: score+=2
                props=await s.try_get_media_properties_async()
                if props.title: score+=2
                if props.artist: score+=1
            except: continue
            if score>best_score: best,best_score=s,score
        return best or (mgr.get_current_session() if mgr else None)

    async def _read_thumbnail_bytes(self, props):
        try:
            thumb = getattr(props, "thumbnail", None)
            if not thumb: return None
            stream = await thumb.open_read_async()
            size = stream.size
            reader = DataReader(stream)
            await reader.load_async(size)
            try:
                data = reader.read_bytes(size)
                return bytes(data)
            except Exception:
                buf = reader.read_buffer(size)
                import array
                arr = array.array('B', buf)
                return arr.tobytes()
        except Exception:
            return None

    async def _poll(self):
        while True:
            try:
                mgr = await self._mgr()
                s = await self._best(mgr) if mgr else None
                self._session = s
                if s:
                    props = await s.try_get_media_properties_async()
                    title=(props.title or "").strip()
                    artist=(props.artist or "").strip()
                    art_bytes = await self._read_thumbnail_bytes(props)

                    paused=False; prv=nxt=pp=True; progress=0.0
                    try:
                        info=s.get_playback_info()
                        paused=info.playback_status in (PlaybackStatus.PAUSED, PlaybackStatus.STOPPED)
                        c=info.controls
                        prv=getattr(c,"is_previous_enabled",True)
                        nxt=getattr(c,"is_next_enabled",True)
                        pp=getattr(c,"is_play_pause_toggle_enabled",True) or getattr(c,"is_play_enabled",True)
                        tl = s.get_timeline_properties()
                        pos  = getattr(tl,"position",None)
                        start= getattr(tl,"start_time",None)
                        end  = getattr(tl,"end_time",None)
                        if pos and start and end:
                            dur = (end - start).total_seconds() if hasattr(end, "total_seconds") else 0
                            cur = (pos - start).total_seconds() if hasattr(pos, "total_seconds") else 0
                            if dur and dur>0: progress = max(0.0, min(1.0, cur/dur))
                    except: pass

                    self._set(title,artist,paused,prv,nxt,pp,art_bytes,progress)
                else:
                    self._set("", "", False, False, False, False, None, 0.0)
            except: pass
            await asyncio.sleep(self.poll_ms/1000.0)

    def _set(self,t,a,p,prv,nxt,pp,art,prog):
        with self._lock:
            self._state.title=t; self._state.artist=a; self._state.paused=p
            self._state.can_prev=prv; self._state.can_next=nxt; self._state.can_playpause=pp
            self._state.art_bytes = art
            self._state.progress = prog

    async def _toggle(self,which):
        s=self._session
        if not s: return
        try:
            if which=="pp":     await s.try_toggle_play_pause_async()
            elif which=="next": await s.try_skip_next_async()
            elif which=="prev": await s.try_skip_previous_async()
        except: pass

class PowerService:
    def __init__(self,poll_ms=30000):
        self.poll_ms=poll_ms; self._running=True
        self._pct=None; self._chg=None
        threading.Thread(target=self._loop,daemon=True).start()
    def shutdown(self): self._running=False
    def get(self): return self._pct,self._chg
    def _loop(self):
        while self._running:
            if HAS_PSUTIL:
                try:
                    b=psutil.sensors_battery()
                    if b: self._pct=int(round(b.percent)); self._chg=b.power_plugged
                except: self._pct=self._chg=None
            time.sleep(self.poll_ms/1000.0)

class VolumeService:
    def __init__(self):
        self.ok=HAS_PYCAW
        if not self.ok: return
        try:
            dev=AudioUtilities.GetSpeakers()
            iface=dev.Activate(IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
            self.endpoint=ctypes.cast(iface, ctypes.POINTER(IAudioEndpointVolume))
        except: self.ok=False
    def get(self):
        if not self.ok: return None
        try: return int(round(self.endpoint.GetMasterVolumeLevelScalar()*100))
        except: return None
    def set(self,pct):
        if not self.ok: return
        try: self.endpoint.SetMasterVolumeLevelScalar(max(0,min(100,int(pct)))/100.0, None)
        except: pass

# ---------- app ----------
class App:
    def __init__(self, root: tk.Tk):
        self.root=root
        self.root.overrideredirect(True)
        self.root.attributes("-topmost", True)
        self.root.attributes("-alpha", WINDOW_OPACITY)
        self.root.configure(bg=TRANSPARENT_CHROMA)
        self.root.attributes("-transparentcolor", TRANSPARENT_CHROMA)
        self.root.bind("<Escape>", lambda e:self.quit())
        self.root.bind("<Control-Shift-w>", lambda e:self.toggle_click_through())

        # services
        self.media=MediaService()
        self.power=PowerService()
        self.volume=VolumeService()

        # shortcuts
        self.root.bind("<space>", lambda e:self.media.play_pause())
        self.root.bind("<Control-Right>", lambda e:self.media.next())
        self.root.bind("<Control-Left>",  lambda e:self.media.prev())

        # metrics
        self.hwnd=self.root.winfo_id()
        self.scale=_dpi_scale(self.hwnd)
        self.font_sz=max(9, int(round(BASE_FONT_SIZE*self.scale)))
        self.pad_x=int(round(PADDING_X*self.scale))
        self.pad_y=int(round(PADDING_Y*self.scale))
        self.space=int(round(SPACE_PX*self.scale))
        self.base_height=int(round(PILL_BASE_HEIGHT_PX*self.scale))

        # fixed compact width + container width
        self.compact_w = int(round(FIXED_COMPACT_WIDTH_PX * self.scale))
        self.max_factor = max(HOVER_WIDTH_FACTOR, PLAY_WIDTH_FACTOR)
        self.container_w = self.compact_w + int(round(CONTAINER_EXTRA_PX * self.scale)) \
                           + int(round(self.compact_w*(self.max_factor-1.0)))

        # state (float anim)
        self.height_f = float(self.base_height)
        self.expand_factor_f = 1.0
        self.v_height = 0.0
        self.v_factor = 0.0
        self._hover=False
        self._click=False
        self._drag=False

        # theme & fonts
        self.theme_name=_theme()
        self.colors=DARK if self.theme_name=="dark" else LIGHT
        self.font_main=tkfont.Font(family="Segoe UI", size=self.font_sz)
        self.font_bold=tkfont.Font(family="Segoe UI", size=self.font_sz, weight="bold")

        # window (fixed size)
        self.root.geometry(f"{self.container_w}x{int(self.height_f)}+{self._center_x()}+0")

        # background & inner frame
        self.bg=tk.Label(self.root,bg=TRANSPARENT_CHROMA,bd=0,highlightthickness=0)
        self.bg.place(relx=0.5, rely=0.0, anchor="n")
        self.frame=tk.Frame(self.root,bg=self.colors["bg"],bd=0,highlightthickness=0)
        self.frame.place(relx=0.5, rely=0.0, anchor="n")

        # left: album art chip
        self.art_label=tk.Label(self.frame,bg=self.colors["bg"],bd=0,highlightthickness=0)
        self.art_label.grid(row=0,column=0,padx=(self.pad_x,self.space),sticky="w")
        self.art_label.bind("<Button-1>", lambda e:self.media.play_pause())

        # controls (only expanded)
        self.ctrl=tk.Frame(self.frame,bg=self.colors["bg"])
        self.btn_prev=tk.Label(self.ctrl,text="⏮",bg=self.colors["bg"],fg=self.colors["fg"],font=self.font_main)
        self.btn_pp  =tk.Label(self.ctrl,text="⏵",bg=self.colors["bg"],fg=self.colors["fg"],font=self.font_bold)
        self.btn_next=tk.Label(self.ctrl,text="⏭",bg=self.colors["bg"],fg=self.colors["fg"],font=self.font_main)
        for b, cb in ((self.btn_prev, self.media.prev),
                      (self.btn_pp,   self.media.play_pause),
                      (self.btn_next, self.media.next)):
            b.pack(side="left", padx=(0,self.space//2))
            b.bind("<Button-1>", lambda e, fn=cb: fn())
            b.bind("<Enter>", lambda e,w=b:w.config(cursor="hand2"))
            b.bind("<Leave>", lambda e,w=b:w.config(cursor=""))
        self.ctrl.grid(row=0,column=1,padx=(0,self.space),sticky="w")

        # optional volume (only expanded)
        self.scale_vol=None
        if self.volume.ok:
            self.var_vol=tk.IntVar(value=self.volume.get() or 50)
            style=ttk.Style()
            base="Horizontal.TScale"; custom="Di.Horizontal.TScale"
            try: style.layout(custom, style.layout(base))
            except Exception: pass
            style.configure(custom, troughcolor=self.colors["track"], background=self.colors["bg"])
            style.map(custom, background=[("active", self.colors["track"])])
            self.scale_vol=ttk.Scale(self.frame,from_=0,to=100,orient="horizontal",
                                     length=int(62*self.scale),command=self._on_volume,style=custom)
            self.scale_vol.set(self.var_vol.get())
            self.scale_vol.grid(row=0,column=2,padx=(0,self.space),sticky="w")

        # media text (center)
        self.var_media=tk.StringVar(value="—")
        self.lbl_media=tk.Label(self.frame,textvariable=self.var_media,bg=self.colors["bg"],
                                fg=self.colors["fg"],font=self.font_bold,anchor="w")
        self.lbl_media.grid(row=0,column=3,padx=(0,self.space),sticky="we")

        # right group — date (dim), time (bold), battery
        self.var_date=tk.StringVar(value="")
        self.var_time=tk.StringVar(value="")
        self.var_batt=tk.StringVar(value="")
        right = tk.Frame(self.frame, bg=self.colors["bg"])
        self.lbl_date=tk.Label(right,textvariable=self.var_date,bg=self.colors["bg"],
                               fg=self.colors["fg_dim"],font=self.font_main)
        self.lbl_time=tk.Label(right,textvariable=self.var_time,bg=self.colors["bg"],
                               fg=self.colors["fg"],font=self.font_bold)
        self.lbl_batt=tk.Label(right,textvariable=self.var_batt,bg=self.colors["bg"],
                               fg=self.colors["fg"],font=self.font_main)
        self.lbl_date.pack(side="left", padx=(0,self.space))
        self.lbl_time.pack(side="left")
        self.lbl_batt.pack(side="left", padx=(self.space,0))
        right.grid(row=0,column=4,padx=(self.space, self.pad_x + int(4*self.scale)),sticky="e")

        for i in range(5): self.frame.grid_columnconfigure(i,weight=0)
        self.frame.grid_columnconfigure(3,weight=1)

        # hairline progress
        self.hair=tk.Canvas(self.frame,
                            height=max(1, int(round(HAIRLINE_PX*self.scale))),
                            bg=self.colors["bg"],bd=0,highlightthickness=0)
        self.hair.place(relx=0.5, rely=1.0, anchor="s")
        self._progress=0.0

        # timers
        self._full_media_text=""
        self.root.deiconify()
        self._tick_time()
        self.root.after(300,self._tick_media)
        self.root.after(1500,self._tick_batt)
        self.root.after(2500,self._tick_theme)

        # anim + hover polling
        self._anim_loop()
        self._hover_poll()

        # dragging
        self.root.bind("<Button-1>", self._start_drag)
        self.root.bind("<B1-Motion>", self._on_drag)
        self.root.bind("<ButtonRelease-1>", self._stop_drag)

    # --- layout & drawing ---
    def _album_px(self):
        inner_h = int(self.height_f) - int(2 * self.pad_y)
        return max(18, int(round(min(inner_h, 22 * self.scale))))

    def _reserved_right_px(self):
        worst_time = "11:59 PM" if not USE_24H else "23:59"
        if SHOW_SECONDS: worst_time = "11:59:59 PM" if not USE_24H else "23:59:59"
        worst_date = "Wed, Sep 30"
        worst_batt = "🔌 100%"
        px = 0
        px += self._measure(self.font_main,worst_date) + self.space
        px += self._measure(self.font_bold,worst_time) + self.space
        px += self._measure(self.font_main,worst_batt) + self.pad_x
        return px

    def _left_px(self, expanded: bool):
        chip = self._album_px() + self.space + self.pad_x
        ctrl = 0
        if expanded:
            ctrl += self._measure(self.font_main,"⏮⏵⏭") + self.space
            if self.scale_vol is not None:
                ctrl += int(62*self.scale) + self.space
        return chip + ctrl

    def _measure(self, font, text): return font.measure(text)

    def _ellipsize(self, text, font, max_px):
        if max_px<=0: return ""
        if self._measure(font, text) <= max_px: return text
        ell="…"; ell_w=self._measure(font, ell)
        lo,hi,res=0,len(text),""
        while lo<=hi:
            mid=(lo+hi)//2; s=text[:mid]
            if self._measure(font, s)+ell_w <= max_px: res=s; lo=mid+1
            else: hi=mid-1
        return res+ell if res else ell

    def _target_dims(self):
        target_h = int(round(self.base_height))
        target_factor = 1.0
        if getattr(self, "_is_playing", False):
            target_h += int(round(PLAY_HEIGHT_DELTA_PX*self.scale))
            target_factor = max(target_factor, PLAY_WIDTH_FACTOR)
        if self._hover and not self._click:
            target_h += int(round(HOVER_HEIGHT_DELTA_PX*self.scale))
            target_factor = max(target_factor, HOVER_WIDTH_FACTOR)
        return float(target_h), float(target_factor)

    def _layout(self, force=False):
        pill_w = int(round(self.compact_w * self.expand_factor_f))
        h_int = int(round(self.height_f))

        if force:
            self.root.geometry(f"{self.container_w}x{h_int}+{self._center_x()}+0")

        # Build halo-free pill image on demand
        bgimg = _pill_image_binary(
            pill_w, h_int,
            _hex_to_rgb(self.colors["bg"]),
            SHADOW_SOFT
        )
        self.bg.configure(image=bgimg); self.bg.image=bgimg

        # place content frame to pill width
        self.frame.place(relx=0.5, rely=0.0, anchor="n",
                         width=pill_w - int(2*self.pad_x),
                         height=h_int - int(2*self.pad_y))

        # expanded controls
        expanded = self.expand_factor_f >= 1.08
        if expanded and not self._click:
            self.ctrl.grid()
            if self.scale_vol is not None: self.scale_vol.grid()
        else:
            self.ctrl.grid_remove()
            if self.scale_vol is not None: self.scale_vol.grid_remove()

        # text width
        right_px = self._reserved_right_px()
        left_px  = self._left_px(expanded)
        remaining = max(40, pill_w - left_px - right_px)
        self.var_media.set(self._ellipsize(self._full_media_text or "—", self.font_bold, remaining))

        # hairline progress
        hair_w = max(10, pill_w - int(2*self.pad_x) - 8)
        self.hair.configure(width=hair_w)
        self.hair.place_configure(relx=0.5, x=0, rely=1.0, y=0, anchor="s")
        self.hair.delete("all")
        prog = max(0.0, min(1.0, getattr(self, "_progress", 0.0)))
        self.hair.create_rectangle(0, 0, int(prog * hair_w),
                                   max(1, int(round(HAIRLINE_PX*self.scale))),
                                   outline="", fill=self.colors["accent"])

        # album art
        chip_px = self._album_px()
        ring = bool(getattr(self, "_is_playing", False))
        ring_w = max(1, int(self.scale))
        self._art_img = _circle_img_from_bytes(getattr(self, "_art_bytes", None), chip_px,
                                               ring=ring, ring_color=self.colors["accent"], ring_w=ring_w)
        self.art_label.configure(image=self._art_img, width=chip_px, height=chip_px)
        self.art_label.image = self._art_img

    # --- animation + hover polling ---
    def _anim_loop(self):
        th, tf = self._target_dims()

        # height spring
        dh = th - self.height_f
        ah = SPRING*dh - DAMP*self.v_height
        self.v_height += ah
        self.height_f += self.v_height
        if abs(dh) < 0.5 and abs(self.v_height) < 0.2:
            self.height_f = th; self.v_height = 0.0

        # width spring
        df = tf - self.expand_factor_f
        af = SPRING*df - DAMP*self.v_factor
        self.v_factor += af
        self.expand_factor_f += self.v_factor
        if abs(df) < 0.003 and abs(self.v_factor) < 0.0015:
            self.expand_factor_f = tf; self.v_factor = 0.0

        self._layout()
        self.root.after(ANIM_INTERVAL_MS, self._anim_loop)

    def _hover_poll(self):
        # get global pointer
        px, py = self.root.winfo_pointerx(), self.root.winfo_pointery()
        # window top-left
        wx, wy = self.root.winfo_x(), self.root.winfo_y()
        x, y = px - wx, py - wy
        self._set_hover(self._inside_pill(x, y) and not self._click)
        self.root.after(33, self._hover_poll)  # ~30 Hz poll

    def _inside_pill(self, x: int, y: int) -> bool:
        pill_w = int(round(self.compact_w * self.expand_factor_f))
        h = int(round(self.height_f))
        if h <= 0 or pill_w <= 0: return False
        if x < 0 or y < 0 or x > self.container_w or y > h: return False

        left  = (self.container_w - pill_w) // 2
        right = left + pill_w
        top, bottom = 0, h
        r = h / 2.0
        cx_left  = left + r
        cx_right = right - r
        cy = h / 2.0

        if y < top or y > bottom: return False
        if (x >= cx_left) and (x <= cx_right): return True
        if x < cx_left:
            dx, dy = x - cx_left, y - cy
            return (dx*dx + dy*dy) <= (r*r)
        if x > cx_right:
            dx, dy = x - cx_right, y - cy
            return (dx*dx + dy*dy) <= (r*r)
        return False

    def _set_hover(self, val: bool):
        changed = (bool(val) != self._hover)
        self._hover = bool(val)
        if changed:
            self._layout()

    # --- events/ticks ---
    def _on_volume(self, val):
        if self.volume.ok:
            try: self.volume.set(int(float(val)))
            except: pass

    def _tick_time(self):
        now=datetime.now()
        fmt = ("%H:%M:%S" if SHOW_SECONDS else "%H:%M") if USE_24H else ("%I:%M:%S %p" if SHOW_SECONDS else "%I:%M %p")
        t=now.strftime(fmt);  t=t[1:] if (not USE_24H and t.startswith("0")) else t
        d=now.strftime("%a, %b %-d" if sys.platform!="win32" else "%a, %b %#d")
        self.var_time.set(t); self.var_date.set(d)
        self.root.after(500 if SHOW_SECONDS else 1000, self._tick_time)

    def _tick_media(self):
        st=self.media.get()
        full = f"{st.title} — {st.artist}".strip(" —")
        is_playing = bool(full) and not st.paused

        self._full_media_text = full or "—"
        self.btn_pp.config(text=("⏸" if is_playing else "⏵"))

        def en(lbl, ok): lbl.configure(fg=self.colors["fg"] if ok else self.colors["fg_dim"])
        en(self.btn_prev, HAS_SMTC and st.can_prev)
        en(self.btn_next, HAS_SMTC and st.can_next)
        en(self.btn_pp,   HAS_SMTC and st.can_playpause)

        self._art_bytes = st.art_bytes
        self._is_playing = is_playing
        self._progress = float(st.progress or 0.0)

        self.root.after(300, self._tick_media)

    def _tick_batt(self):
        pct,chg=self.power.get()
        self.var_batt.set("" if pct is None else f"{'🔌' if chg else '🔋'} {pct}%")
        self.root.after(2000, self._tick_batt)

    def _tick_theme(self):
        th=_theme()
        if th!=self.theme_name:
            self.theme_name=th; self.colors=DARK if th=="dark" else LIGHT
            for w in (self.frame,self.lbl_media,self.art_label,self.hair):
                try: w.configure(bg=self.colors["bg"])
                except: pass
            for w in (self.lbl_date,self.lbl_batt):
                try: w.configure(fg=self.colors["fg_dim"])
                except: pass
            for w in (self.lbl_time,self.lbl_media,self.btn_prev,self.btn_pp,self.btn_next):
                try: w.configure(fg=self.colors["fg"])
                except: pass
            self._layout(force=True)
        self.root.after(3000, self._tick_theme)

    # --- drag / clickthrough ---
    def _start_drag(self, e):
        if self.root.winfo_y() <= 100:
            self._drag=True; self._drag_off=(e.x_root - self.root.winfo_x(), e.y_root - self.root.winfo_y())
    def _on_drag(self, e):
        if not getattr(self,"_drag",False): return
        x=e.x_root - self._drag_off[0]; y=max(0, min(e.y_root - self._drag_off[1], 100))
        self.root.geometry(f"+{x}+{y}")
    def _stop_drag(self, _):
        if getattr(self,"_drag",False):
            self._drag=False
            self.root.geometry(f"+{self._center_x()}+0")

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
        except: pass

    # --- utils ---
    def _center_x(self):
        sw = self.root.winfo_screenwidth()
        return max(0, int((sw - self.container_w)/2))

    def quit(self):
        try: self.media.shutdown()
        except: pass
        try: self.power.shutdown()
        except: pass
        self.root.destroy()

# ---- run ----
def main():
    root = tk.Tk()
    app = App(root)
    root.mainloop()

if __name__ == "__main__":
    if len(sys.argv)>1 and sys.argv[1]=="--install":
        try:
            startup=os.path.join(os.environ["APPDATA"],"Microsoft","Windows","Start Menu","Programs","Startup")
            lnk=os.path.join(startup,"DynamicIsland_Pill.lnk")
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
