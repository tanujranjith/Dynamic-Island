"""
DynamicIsland — Notch Bar (Docked, Compact + Subtle Expand)
- Top-center notch slab (flat top, rounded bottom, soft shadow, optional camera dot)
- SMTC best-session media (robust Chrome/Edge titles), playback controls
- Play/Pause icon flips (⏵ / ⏸)
- Optional system volume slider (pycaw)
- Time/Date (no clipping) + Battery
- Auto theme (light/dark), DPI aware
- Click-through toggle: Ctrl+Shift+W
"""

import sys, os, time, threading, asyncio, ctypes
from dataclasses import dataclass
from datetime import datetime
import tkinter as tk
from tkinter import ttk
import tkinter.font as tkfont
from PIL import Image, ImageTk, ImageDraw

# ---------- optional deps ----------
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

# ---------- config (compact) ----------
OPACITY = 0.94
BASE_FONT_SIZE = 9
SHOW_SECONDS = True
USE_24H = False
MEDIA_POLL_MS = 900
POWER_POLL_MS = 30000

# notch look (floating slab)
NOTCH_HEIGHT_PX =25       # compact base height
NOTCH_RADIUS_PX = 20
NOTCH_SHADOW     = True
SHOW_CAMERA_DOT  = True
CAMERA_DOT_PX    = 6

# paddings / layout
PADDING_X = 10
PADDING_Y = 3
SPACE_PX  = 4
VOL_WIDTH_PX = 55
MAX_WIDTH_RATIO = 0.50
MIN_WIDTH_PX    = 42
SNAP_TOP_MARGIN_PX = 0

# animation
EXPAND_HEIGHT_DELTA_PX = 4     # +px when playing
EXPAND_WIDTH_FACTOR    = 1.06  # +% width when playing
ANIM_INTERVAL_MS       = 1    # smaller = smoother
ANIM_STEP_PX           = 1

# theme colors
FORCE_THEME = None  # "dark" | "light" | None
DARK  = dict(bg="#2B2B2B", fg="#FFFFFF", divider="#A8A8A8", fg_dim="#BFBFBF")
LIGHT = dict(bg="#F3F3F3", fg="#111111", divider="#5E5E5E", fg_dim="#555555")

# ---------- helpers ----------
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

def _notch_image(w, h, radius, fill, shadow=True, camera_dot=False, camera_px=6):
    """Flat top, rounded bottom corners; optional soft shadow + camera dot."""
    img = Image.new("RGBA", (w, h + (4 if shadow else 0)), (0,0,0,0))
    draw = ImageDraw.Draw(img)

    if shadow:
        sh_h = 4
        for i in range(sh_h):
            a = int(60 * (1 - i / sh_h))  # fade out
            draw.rounded_rectangle(
                [(2, i+2), (w-2, h+i+2)],
                radius=radius+2,
                fill=(0,0,0,a)
            )

    draw.rounded_rectangle([(0,0),(w,h)], radius=radius, fill=fill)
    draw.rectangle([(0,0),(w,h//2)], fill=fill)  # flatten top edge

    if camera_dot and camera_px > 0:
        cx = w // 2
        r = camera_px // 2
        draw.ellipse([(cx-r, 0+r), (cx+r, 0+camera_px)], fill=(20,20,20,230))

    return ImageTk.PhotoImage(img)

class Tooltip:
    def __init__(self, widget, text_func, pad=(8,6)):
        self.widget=widget; self.text_func=text_func
        self.pad=pad; self.tip=None
        widget.bind("<Enter>", self._show); widget.bind("<Leave>", self._hide)
        widget.bind("<Motion>", self._move)
    def _show(self,_):
        if self.tip or not self.text_func: return
        t=self.text_func() or ""
        if not t: return
        self.tip=tk.Toplevel(self.widget); self.tip.overrideredirect(True)
        self.tip.attributes("-topmost", True)
        f=tk.Frame(self.tip,bg="#222",bd=0); f.pack()
        tk.Label(f,text=t,bg="#222",fg="#fff",font=("Segoe UI",9)).pack(
            padx=self.pad[0],pady=self.pad[1])
    def _move(self,e):
        if self.tip: self.tip.geometry(f"+{e.x_root+12}+{e.y_root+12}")
    def _hide(self,_):
        if self.tip: self.tip.destroy(); self.tip=None

# ---------- services ----------
@dataclass
class MediaState:
    title: str = ""
    artist: str = ""
    paused: bool = False
    can_prev: bool = False
    can_next: bool = False
    can_playpause: bool = True

class MediaService:
    """Pick the best SMTC session (PLAYING + has metadata) so Chrome/Edge show titles."""
    def __init__(self, poll_ms=MEDIA_POLL_MS):
        self.poll_ms=poll_ms
        self._state=MediaState()
        self._lock=threading.Lock()
        self._loop=asyncio.new_event_loop()
        threading.Thread(target=self._loop.run_forever, daemon=True).start()
        self._session=None
        asyncio.run_coroutine_threadsafe(self._poll(), self._loop)

    def shutdown(self):
        try: self._loop.call_soon_threadsafe(self._loop.stop)
        except: pass

    def get(self): 
        with self._lock: return MediaState(**self._state.__dict__)

    def play_pause(self):  # callable for UI
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

    async def _poll(self):
        while True:
            try:
                mgr=await self._mgr()
                s=await self._best(mgr) if mgr else None
                self._session=s
                if s:
                    props=await s.try_get_media_properties_async()
                    title=(props.title or "").strip()
                    artist=(props.artist or "").strip()
                    paused=False; prv=nxt=pp=True
                    try:
                        info=s.get_playback_info()
                        paused=info.playback_status in (PlaybackStatus.PAUSED, PlaybackStatus.STOPPED)
                        c=info.controls
                        prv=getattr(c,"is_previous_enabled",True)
                        nxt=getattr(c,"is_next_enabled",True)
                        pp=getattr(c,"is_play_pause_toggle_enabled",True) or getattr(c,"is_play_enabled",True)
                    except: pass
                    self._set(title,artist,paused,prv,nxt,pp)
                else:
                    self._set("", "", False, False, False, False)
            except: pass
            await asyncio.sleep(self.poll_ms/1000.0)

    def _set(self,t,a,p,prv,nxt,pp):
        with self._lock:
            self._state.title=t; self._state.artist=a; self._state.paused=p
            self._state.can_prev=prv; self._state.can_next=nxt; self._state.can_playpause=pp

    async def _toggle(self,which):
        s=self._session
        if not s: return
        try:
            if which=="pp":   await s.try_toggle_play_pause_async()
            elif which=="next": await s.try_skip_next_async()
            elif which=="prev": await s.try_skip_previous_async()
        except: pass

class PowerService:
    def __init__(self,poll_ms=POWER_POLL_MS):
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
        self.root.attributes("-alpha", OPACITY)
        self.root.configure(bg="black")
        self.root.attributes("-transparentcolor","black")
        self.root.bind("<Escape>", lambda e:self.quit())
        self.root.bind("<Control-Shift-w>", lambda e:self.toggle_click_through())

        # services
        self.media=MediaService()
        self.power=PowerService()

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
        self.vol_w=int(round(VOL_WIDTH_PX*self.scale))
        self.base_height=int(round(NOTCH_HEIGHT_PX*self.scale))
        self.radius=int(round(NOTCH_RADIUS_PX*self.scale))
        self.height=self.base_height  # live animated height
        self.expand_factor=1.0        # live animated width factor (1.0 or EXPAND_WIDTH_FACTOR)

        # theme & fonts
        self.theme_name=_theme()
        self.colors=DARK if self.theme_name=="dark" else LIGHT
        self.font_main=tkfont.Font(family="Segoe UI", size=self.font_sz)
        self.font_bold=tkfont.Font(family="Segoe UI", size=self.font_sz, weight="bold")
        self.font_icons=tkfont.Font(family="Segoe UI Symbol", size=max(10,self.font_sz))

        # background & frame
        self.bg=tk.Label(self.root,bg="black",bd=0,highlightthickness=0)
        self.bg.place(relx=0.5, rely=0.0, anchor="n")
        self.frame=tk.Frame(self.root,bg=self.colors["bg"],bd=0,highlightthickness=0)
        self.frame.place(relx=0.5, rely=0.0, anchor="n")

        # controls cluster (clickable)
        ctrl=tk.Frame(self.frame,bg=self.colors["bg"])
        self.btn_prev=tk.Label(ctrl,text="⏮",font=self.font_icons,bg=self.colors["bg"],fg=self.colors["fg"])
        self.btn_pp  =tk.Label(ctrl,text="⏵",font=self.font_icons,bg=self.colors["bg"],fg=self.colors["fg"])  # flips
        self.btn_next=tk.Label(ctrl,text="⏭",font=self.font_icons,bg=self.colors["bg"],fg=self.colors["fg"])

        for b, cb in ((self.btn_prev, self.media.prev),
                      (self.btn_pp,  self.media.play_pause),
                      (self.btn_next,self.media.next)):
            b.pack(side="left", padx=(0,self.space//2))
            b.bind("<Button-1>", lambda e, fn=cb: fn())
            b.bind("<Enter>", lambda e,w=b:w.config(cursor="hand2"))
            b.bind("<Leave>", lambda e,w=b:w.config(cursor=""))

        ctrl.grid(row=0,column=0,padx=(self.pad_x,0),sticky="w")

        # volume slider
        self.volume=VolumeService()
        self.scale_vol=None
        if self.volume.ok:
            self.var_vol=tk.IntVar(value=self.volume.get() or 50)
            style=ttk.Style()
            base="Horizontal.TScale"; custom="Di.Horizontal.TScale"
            try: style.layout(custom, style.layout(base))
            except Exception: pass
            track="#3A3A3A" if self.theme_name=="dark" else "#D0D0D0"
            style.configure(custom, troughcolor=track, background=self.colors["bg"])
            style.map(custom, background=[("active", track)])
            self.scale_vol=ttk.Scale(self.frame,from_=0,to=100,orient="horizontal",
                                     length=self.vol_w,command=self._on_volume,style=custom)
            self.scale_vol.set(self.var_vol.get())
            self.scale_vol.grid(row=0,column=1,padx=(self.space,self.space),sticky="w")

        # media text (center)
        self.media_wrap=tk.Frame(self.frame,bg=self.colors["bg"],height=self.height-int(2*self.pad_y))
        self.media_wrap.grid(row=0,column=2,padx=(self.space,self.space),sticky="w")
        self.media_wrap.pack_propagate(False)
        self.var_media=tk.StringVar(value="—")
        self.lbl_media=tk.Label(self.media_wrap,textvariable=self.var_media,bg=self.colors["bg"],fg=self.colors["fg"],font=self.font_main,anchor="w")
        self.lbl_media.pack(side="left", fill="both", expand=True)
        Tooltip(self.lbl_media, text_func=lambda: self._full_media_text)

        # divider + right cluster
        cidx=3
        self.lbl_div=tk.Label(self.frame,text="•",bg=self.colors["bg"],fg=self.colors["divider"],font=self.font_main)
        self.lbl_div.grid(row=0,column=cidx,padx=(0,self.space),sticky="w"); cidx+=1
        self.var_date=tk.StringVar(value=""); self.var_time=tk.StringVar(value=""); self.var_batt=tk.StringVar(value="")
        self.lbl_date=tk.Label(self.frame,textvariable=self.var_date,bg=self.colors["bg"],fg=self.colors["fg"],font=self.font_main)
        self.lbl_time=tk.Label(self.frame,textvariable=self.var_time,bg=self.colors["bg"],fg=self.colors["fg"],font=self.font_bold)
        self.lbl_batt=tk.Label(self.frame,textvariable=self.var_batt,bg=self.colors["bg"],fg=self.colors["fg"],font=self.font_main)
        self.lbl_date.grid(row=0,column=cidx,padx=(0,self.space),sticky="e"); cidx+=1
        self.lbl_time.grid(row=0,column=cidx,padx=(0,self.space),sticky="e"); cidx+=1
        self.lbl_batt.grid(row=0,column=cidx,padx=(0,self.pad_x),sticky="e")
        for i in range(cidx+1): self.frame.grid_columnconfigure(i,weight=0)
        self.frame.grid_columnconfigure(2,weight=1)

        # dragging (top band)
        self.root.bind("<Button-1>", self._start_drag)
        self.root.bind("<B1-Motion>", self._on_drag)
        self.root.bind("<ButtonRelease-1>", self._stop_drag)

        # state
        self._full_media_text=""
        self.width=0
        self._click=False
        self._drag=False
        self._target_height=self.base_height
        self._anim_running=False

        # timers
        self._tick_time()
        self._layout()
        self.root.deiconify()
        self.root.after(300,self._tick_media)
        self.root.after(1500,self._tick_batt)
        self.root.after(2500,self._tick_theme)

    # --- measurements & layout ---
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

    def _reserved_right_px(self):
        worst_time = "11:59 PM" if not USE_24H else "23:59"
        if SHOW_SECONDS: worst_time = "11:59:59 PM" if not USE_24H else "23:59:59"
        worst_date = "Wed, Sep 30"
        worst_batt = "🔌 100%"
        px = 0
        px += self._measure(self.font_main,"•")+self.space
        px += self._measure(self.font_main,worst_date)+self.space
        px += self._measure(self.font_bold,worst_time)+self.space
        px += self._measure(self.font_main,worst_batt)
        px += self.pad_x
        return px

    def _left_controls_px(self):
        icons = self._measure(self.font_icons,"⏮")+self._measure(self.font_icons,"⏵")+self._measure(self.font_icons,"⏭")
        px = self.pad_x + icons + self.space
        if self.scale_vol is not None: px += self.vol_w + self.space
        return px

    def _layout(self):
        sw = self.root.winfo_screenwidth()
        max_w = int(sw * MAX_WIDTH_RATIO)
        min_w = int(max(MIN_WIDTH_PX*self.scale, 360*self.scale))
        right_px = self._reserved_right_px()
        left_px  = self._left_controls_px()

        remaining = max(60, max_w - left_px - right_px)
        disp = self._ellipsize(self._full_media_text or "—", self.font_main, remaining)
        self.var_media.set(disp)

        base_w = left_px + self._measure(self.font_main, disp) + right_px
        base_w = max(min_w, min(base_w, max_w))
        final_w = int(round(base_w * self.expand_factor))

        # window geometry (centered at the top)
        if final_w != self.width:
            self.width = final_w
            self.root.geometry(f"{final_w}x{self.height}+{self._center_x()}+{SNAP_TOP_MARGIN_PX}")

        # notch background
        bgimg = _notch_image(final_w, self.height, self.radius, self.colors["bg"],
                             shadow=NOTCH_SHADOW, camera_dot=SHOW_CAMERA_DOT, camera_px=CAMERA_DOT_PX)
        self.bg.configure(image=bgimg); self.bg.image=bgimg

        # content frame inside notch
        self.media_wrap.configure(width=max(40, final_w - right_px - left_px),
                                  height=self.height - int(2*self.pad_y))
        self.frame.place(relx=0.5, rely=0.0, anchor="n",
                         width=final_w - int(2*self.pad_x),
                         height=self.height - int(2*self.pad_y))

    def _center_x(self):
        sw = self.root.winfo_screenwidth()
        return max(0, int((sw - self.width)/2))

    # --- drag / clickthrough ---
    def _start_drag(self, e):
        if self.root.winfo_y() <= 100:
            self._drag=True; self._drag_off=(e.x, e.y)
    def _on_drag(self, e):
        if not getattr(self,"_drag",False): return
        dx,dy=e.x-self._drag_off[0], e.y-self._drag_off[1]
        x=self.root.winfo_x()+dx; y=max(0, min(self.root.winfo_y()+dy, 100))
        self.root.geometry(f"+{x}+{y}")
    def _stop_drag(self, _):
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
        except: pass

    # --- animation ---
    def _animate_to(self, target_h, target_factor):
        # height animation
        self._target_height = target_h
        if not self._anim_running:
            self._anim_running = True
            self._anim_tick(target_factor)
        else:
            # width factor will be updated on next tick anyway
            self._next_factor = target_factor

    def _anim_tick(self, target_factor):
        # move height 1px towards target each tick
        if self.height < self._target_height:
            self.height += ANIM_STEP_PX
        elif self.height > self._target_height:
            self.height -= ANIM_STEP_PX

        # ease width factor a bit
        cur = self.expand_factor
        target = getattr(self, "_next_factor", target_factor)
        self.expand_factor = cur + (target - cur) * 0.25

        self._layout()

        if self.height != self._target_height or abs(self.expand_factor - target) > 0.01:
            self.root.after(ANIM_INTERVAL_MS, lambda: self._anim_tick(target_factor))
        else:
            self.expand_factor = target
            self._layout()
            self._anim_running = False

    # --- events ---
    def _on_volume(self, val):
        if self.volume.ok:
            try: self.volume.set(int(float(val)))
            except: pass

    # --- ticks ---
    def _tick_time(self):
        now=datetime.now()
        fmt = ("%H:%M:%S" if SHOW_SECONDS else "%H:%M") if USE_24H else ("%I:%M:%S %p" if SHOW_SECONDS else "%I:%M %p")
        t=now.strftime(fmt);  t=t[1:] if (not USE_24H and t.startswith("0")) else t
        d=now.strftime("%a, %b %-d" if sys.platform!="win32" else "%a, %b %#d")
        self.var_time.set(t); self.var_date.set(d)
        self._layout()
        self.root.after(500 if SHOW_SECONDS else 1000, self._tick_time)

    def _tick_media(self):
        st=self.media.get()
        full = f"{st.title} — {st.artist}".strip(" —")
        is_playing = bool(full) and not st.paused

        # text & icon
        self._full_media_text = ("⏸ "+full) if (full and st.paused) else (full or "—")
        self.btn_pp.config(text=("⏸" if is_playing else "⏵"))

        # enable/disable colors
        def en(lbl, ok): lbl.configure(fg=self.colors["fg"] if ok else self.colors["fg_dim"])
        en(self.btn_prev, HAS_SMTC and st.can_prev)
        en(self.btn_next, HAS_SMTC and st.can_next)
        en(self.btn_pp,   HAS_SMTC and st.can_playpause)

        # animate: expand when playing, collapse when paused/idle
        target_h = self.base_height + (int(round(EXPAND_HEIGHT_DELTA_PX*self.scale)) if is_playing else 0)
        target_factor = (EXPAND_WIDTH_FACTOR if is_playing else 1.0)
        self._animate_to(target_h, target_factor)

        self.root.after(300, self._tick_media)

    def _tick_batt(self):
        pct,chg=self.power.get()
        self.var_batt.set("" if pct is None else f"{'🔌' if chg else '🔋'} {pct}%")
        self._layout()
        self.root.after(1500, self._tick_batt)

    def _tick_theme(self):
        th=_theme()
        if th!=self.theme_name:
            self.theme_name=th; self.colors=DARK if th=="dark" else LIGHT
            for w in (self.frame,self.lbl_media,self.lbl_date,self.lbl_time,self.lbl_batt,
                      self.btn_prev,self.btn_pp,self.btn_next):
                try: w.configure(bg=self.colors["bg"], fg=self.colors["fg"])
                except: pass
            self.lbl_div.configure(bg=self.colors["bg"], fg=self.colors["divider"])
            if self.scale_vol is not None:
                style=ttk.Style()
                track="#3A3A3A" if self.theme_name=="dark" else "#D0D0D0"
                try:
                    style.configure("Di.Horizontal.TScale", troughcolor=track, background=self.colors["bg"])
                    style.map("Di.Horizontal.TScale", background=[("active", track)])
                except: pass
            self._layout()
        self.root.after(3000, self._tick_theme)

    # --- life ---
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
    # right cluster vars (created after App so they exist)
    app.var_date = app.var_date  # just to keep lints happy
    root.mainloop()

if __name__ == "__main__":
    if len(sys.argv)>1 and sys.argv[1]=="--install":
        try:
            startup=os.path.join(os.environ["APPDATA"],"Microsoft","Windows","Start Menu","Programs","Startup")
            lnk=os.path.join(startup,"DynamicIsland_Notch.lnk")
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
