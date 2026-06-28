# DynamicIsland_ApplePlus_canvas_pro.py
"""
Canvas-only Dynamic Island (Apple-ish) — clean corners, SMTC media, volume pill
- Single transparent Canvas (no frame leaks)
- Soft-shadow pill, dark theme
- Center: "Title — Artist" (smart ellipsize)
- Left: prev / play-pause / next (wired to Windows SMTC if available)
- Right: time + date
- Bottom-left: volume pill "-  XX%  +" using Pycaw if available (graceful fallback)

Run:
  python DynamicIsland_ApplePlus_canvas_pro.py
Optional:
  pip install winsdk pycaw comtypes pillow
"""

import sys, math, time, asyncio, ctypes, ctypes.wintypes as wt
import tkinter as tk
import tkinter.font as tkfont
from datetime import datetime
from PIL import Image, ImageTk, ImageDraw, ImageFilter

# ---- Optional media/volume deps ----
HAS_SMTC = False
try:
    from winsdk.windows.media.control import GlobalSystemMediaTransportControlsSessionManager as MediaManager
    from winsdk.windows.media.control import GlobalSystemMediaTransportControlsSessionPlaybackStatus as PlaybackStatus
    HAS_SMTC = True
except Exception:
    pass

HAS_PYCAW = False
try:
    from comtypes import CLSCTX_ALL  # type: ignore
    from pycaw.pycaw import AudioUtilities, IAudioEndpointVolume  # type: ignore
    HAS_PYCAW = True
except Exception:
    pass

TRANSPARENT_KEY = "#010203"
ACCENT = "#0a84ff"
BG  = "#111113"
FG  = "#FFFFFF"
DIV = "#2E2E30"
FG_DIM = "#9A9AA0"

RADIUS = 22
HEIGHT = 88
PADDING_X = 16
PADDING_Y = 10
SPACE = 10

SHOW_SECONDS=True; USE_24H=False

def dpi_scale(hwnd: int) -> float:
    try:
        u = ctypes.windll.user32
        try:
            u.GetDpiForWindow.restype = ctypes.c_uint
            return max(0.5, u.GetDpiForWindow(hwnd) / 96.0)
        except Exception:
            u.GetDpiForSystem.restype = ctypes.c_uint
            return max(0.5, u.GetDpiForSystem() / 96.0)
    except Exception:
        return 1.0

def rounded_pill_bitmap(w, h, r, fill):
    pad = 10
    img = Image.new("RGBA", (w+pad*2, h+pad*2), (0,0,0,0))
    # shadow
    s = Image.new("RGBA", (w, h), (0,0,0,0))
    g = ImageDraw.Draw(s)
    g.rounded_rectangle([0,0,w-1,h-1], r, fill=(0,0,0,170))
    s = s.filter(ImageFilter.GaussianBlur(8))
    img.alpha_composite(s, (pad, pad+2))
    # body
    body = Image.new("RGBA", (w, h), (0,0,0,0))
    gb = ImageDraw.Draw(body)
    gb.rounded_rectangle([0,0,w-1,h-1], r, fill=fill)
    # hairlines
    gb.rounded_rectangle([1,1,w-2,h-2], max(1,r-1), outline=(255,255,255,38), width=1)
    gb.rounded_rectangle([0,0,w-1,h-1], r, outline=(0,0,0,55), width=1)
    img.alpha_composite(body, (pad, pad))
    return ImageTk.PhotoImage(img), pad

def disable_win11_corners(hwnd):
    try:
        dwm = ctypes.windll.dwmapi
        DWMWA_WINDOW_CORNER_PREFERENCE = 33
        DWM_WINDOW_CORNER_PREFERENCE_DONOTROUND = 1
        attr = wt.DWORD(DWMWA_WINDOW_CORNER_PREFERENCE)
        val  = wt.DWORD(DWM_WINDOW_CORNER_PREFERENCE_DONOTROUND)
        dwm.DwmSetWindowAttribute(wt.HWND(hwnd), attr, ctypes.byref(val), ctypes.sizeof(val))
    except Exception:
        pass

class MediaService:
    def __init__(self):
        self._loop = None
        self._title = ""
        self._artist = ""
        self._paused = False
        if HAS_SMTC:
            self._loop = asyncio.new_event_loop()
            import threading
            threading.Thread(target=self._loop.run_forever, daemon=True).start()
            asyncio.run_coroutine_threadsafe(self._poll(), self._loop)

    def title_artist(self): return (self._title, self._artist, self._paused)

    def ctrl(self, which):
        if not HAS_SMTC or self._loop is None: return
        async def _do():
            try:
                mgr = await MediaManager.request_async()
                s = mgr.get_current_session()
                if not s: return
                if which=="pp":
                    try: await s.try_toggle_play_pause_async()
                    except Exception: pass
                elif which=="prev":
                    try: await s.try_skip_previous_async()
                    except Exception: pass
                elif which=="next":
                    try: await s.try_skip_next_async()
                    except Exception: pass
            except Exception:
                pass
        asyncio.run_coroutine_threadsafe(_do(), self._loop)

    async def _poll(self):
        while True:
            try:
                mgr = await MediaManager.request_async()
                s = mgr.get_current_session()
                if s:
                    props = await s.try_get_media_properties_async()
                    self._title  = (props.title or "").strip()
                    self._artist = (props.artist or "").strip()
                    try:
                        info = s.get_playback_info()
                        self._paused = info.playback_status == PlaybackStatus.PAUSED
                    except Exception:
                        self._paused = False
                else:
                    self._title = self._artist = ""
                    self._paused = False
            except Exception:
                self._title = self._artist = ""
                self._paused = False
            await asyncio.sleep(0.12)

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
        try:
            return int(round(self.endpoint.GetMasterVolumeLevelScalar()*100))
        except Exception:
            return None
    def set(self, pct):
        if not self.ok: return
        try:
            pct = max(0, min(100, int(pct)))
            self.endpoint.SetMasterVolumeLevelScalar(pct/100.0, None)
        except Exception:
            pass

class Island:
    def __init__(self, root: tk.Tk):
        self.root = root
        root.overrideredirect(True)
        root.attributes("-topmost", True)
        root.configure(bg=TRANSPARENT_KEY)
        try: root.attributes("-transparentcolor", TRANSPARENT_KEY)
        except Exception: pass
        disable_win11_corners(root.winfo_id())

        self.scale = dpi_scale(root.winfo_id())
        self.radius = int(round(RADIUS*self.scale))
        self.height = int(round(HEIGHT*self.scale))
        self.pad_x = int(round(PADDING_X*self.scale))
        self.pad_y = int(round(PADDING_Y*self.scale))
        self.space = int(round(SPACE*self.scale))

        fam = "Segoe UI"
        try:
            fams=set(tkfont.families())
            for n in ("SF Pro Text","SF Pro Display","San Francisco","SF Pro","Segoe UI Variable","Segoe UI"):
                if n in fams: fam=n; break
        except Exception: pass
        self.font  = tkfont.Font(family=fam, size=max(12, int(12*self.scale)))
        self.fontB = tkfont.Font(family=fam, size=max(12, int(12*self.scale)), weight="bold")

        sw = root.winfo_screenwidth()
        self.width = max(600, int(sw*0.62))
        self.canvas = tk.Canvas(root, width=self.width, height=self.height,
                                bg=TRANSPARENT_KEY, highlightthickness=0, bd=0)
        self.canvas.pack(fill="both", expand=True)
        self.pill_img, self.pill_pad = rounded_pill_bitmap(self.width - 2*self.pad_x, self.height - 2*self.pad_y, self.radius, BG)
        self.pill_id = self.canvas.create_image(self.width//2, self.height//2, image=self.pill_img)

        # Layout anchors
        self.left_x = self.pad_x + self.pill_pad + self.space
        self.center_x = self.width//2
        self.right_x  = self.width - (self.pad_x + self.pill_pad + self.space)
        self.baseline = self.height//2

        # Buttons
        off = int(12*self.scale)
        self.btn_prev = self._button(self.left_x, self.baseline - off, "⏮", tag="prev")
        self.btn_pp   = self._button(self.left_x + int(28*self.scale), self.baseline - off, "⏯", tag="pp")
        self.btn_next = self._button(self.left_x + int(56*self.scale), self.baseline - off, "⏭", tag="next")
        self.canvas.tag_bind("prev","<Button-1>", lambda e: self.media.ctrl("prev"))
        self.canvas.tag_bind("pp",  "<Button-1>", lambda e: self.media.ctrl("pp"))
        self.canvas.tag_bind("next","<Button-1>", lambda e: self.media.ctrl("next"))

        # Volume pill (bottom-left)
        self.vol_x = self.left_x
        self.vol_y = self.baseline + int(12*self.scale)
        self.vol_w = int(150*self.scale); self.vol_h = int(26*self.scale)
        self.vol_value =  self._get_volume() or 50
        self._draw_volume_pill()
        self.canvas.tag_bind("vol_minus","<Button-1>", lambda e: self._bump_volume(-3))
        self.canvas.tag_bind("vol_plus","<Button-1>",  lambda e: self._bump_volume(+3))
        self.canvas.tag_bind("vol_all", "<Enter>", lambda e: self.canvas.config(cursor="hand2"))
        self.canvas.tag_bind("vol_all", "<Leave>", lambda e: self.canvas.config(cursor=""))

        # Media text
        self.media_id = self.canvas.create_text(self.center_x, self.baseline - int(2*self.scale),
                                                text="—", fill=FG, font=self.font, anchor="c")

        # Time/date
        self.time_id = self.canvas.create_text(self.right_x, self.baseline - int(10*self.scale),
                                               text="", fill=FG, font=self.fontB, anchor="e")
        self.date_id = self.canvas.create_text(self.right_x, self.baseline + int(10*self.scale),
                                               text="", fill=FG_DIM, font=self.font, anchor="e")

        root.geometry(f"{self.width}x{self.height}+{int((sw-self.width)/2)}+2")

        # Dragging
        self.canvas.bind("<Button-1>", self._start_drag)
        self.canvas.bind("<B1-Motion>", self._on_drag)

        # Services
        self.media = MediaService()
        self.volume = VolumeService()

        self.tick_clock()
        self.tick_media()
        self.tick_volume_sync()

    # ---- UI helpers ----
    def _button(self, x, y, text, tag):
        r = int(6*self.scale); w = int(22*self.scale); h = int(22*self.scale)
        rect = self.canvas.create_rectangle(x, y, x+w, y+h, outline=DIV, width=1, fill=BG, tags=(tag,))
        glyph = self.canvas.create_text(x+w//2, y+h//2, text=text, fill=FG, font=self.font, tags=(tag,))
        return rect

    def _draw_volume_pill(self):
        x=self.vol_x; y=self.vol_y; w=self.vol_w; h=self.vol_h; r=int(h/2)
        # back
        self.canvas.delete("vol_all")
        self.canvas.create_rectangle(x, y, x+w, y+h, outline=DIV, width=1, fill=BG, tags=("vol_all",))
        # minus / plus / pct
        minus_x = x + int(14*self.scale)
        plus_x  = x + w - int(14*self.scale)
        mid_x   = (x+x+w)//2
        self.canvas.create_text(minus_x, y+h//2, text="–", fill=FG, font=self.font, tags=("vol_minus","vol_all"))
        self.canvas.create_text(plus_x,  y+h//2, text="+", fill=FG, font=self.font, tags=("vol_plus","vol_all"))
        pct = f"{int(self.vol_value)}%"
        self.canvas.create_text(mid_x, y+h//2, text=pct, fill=FG, font=self.font, tags=("vol_all",))

    def _get_volume(self):
        try:
            if self.volume.ok:
                v = self.volume.get()
                if v is not None: return v
        except Exception: pass
        return None

    def _set_volume(self, pct):
        try:
            if self.volume.ok:
                self.volume.set(pct)
        except Exception: pass

    def _bump_volume(self, delta):
        v = self._get_volume()
        if v is None: v = self.vol_value
        v = max(0, min(100, int(v + delta)))
        self._set_volume(v)
        self.vol_value = v
        self._draw_volume_pill()

    # ---- text measurement & ellipsis ----
    def _measure(self, font, text):
        try:
            f = self.font if font is None else font
            return f.measure(text)
        except Exception:
            return len(text)*8

    def _ellipsize(self, text, font, max_px):
        if self._measure(font, text) <= max_px:
            return text
        ell="…"; ell_w=self._measure(font, ell)
        lo,hi,res=0,len(text),""
        while lo<=hi:
            mid=(lo+hi)//2
            s=text[:mid]
            if self._measure(font, s) + ell_w <= max_px:
                res=s; lo=mid+1
            else:
                hi=mid-1
        return (res+ell) if res else ell

    # ---- ticks ----
    def tick_clock(self):
        now = datetime.now()
        fmt = ("%H:%M:%S" if SHOW_SECONDS else "%H:%M") if USE_24H else ("%I:%M:%S %p" if SHOW_SECONDS else "%I:%M %p")
        t = now.strftime(fmt); t = t[1:] if (not USE_24H and t.startswith("0")) else t
        d = now.strftime("%a, %b %#d") if sys.platform=="win32" else now.strftime("%a, %b %-d")
        self.canvas.itemconfigure(self.time_id, text=t)
        self.canvas.itemconfigure(self.date_id, text=d)
        self.root.after(500 if SHOW_SECONDS else 900, self.tick_clock)

    def tick_media(self):
        title, artist, paused = self.media.title_artist()
        # compute max width for center text
        right_sample = "Wed, Sep 30"  # rough width proxy
        right_px = max(self._measure(self.fontB,"23:59:59" if SHOW_SECONDS else "23:59"), self._measure(self.font,right_sample)) + self.pad_x + self.space
        left_px  = (self._measure(self.font,"⏮")+self._measure(self.font,"⏯")+self._measure(self.font,"⏭")) + self.pad_x + self.space + int(60*self.scale)
        max_px = max(60, self.width - left_px - right_px)
        if title or artist:
            sep = " — " if artist else ""
            disp = f"{title}{sep}{artist}"
        else:
            disp = "—"
        disp = self._ellipsize(disp, self.font, max_px)
        if paused and disp != "—":
            disp = "⏸ " + disp
        self.canvas.itemconfigure(self.media_id, text=disp)
        self.root.after(120, self.tick_media)

    def tick_volume_sync(self):
        v = self._get_volume()
        if v is not None and abs(v - self.vol_value) >= 2:
            self.vol_value = v
            self._draw_volume_pill()
        self.root.after(420, self.tick_volume_sync)

    # ---- drag window ----
    def _start_drag(self, e):
        self._drag = (e.x_root, e.y_root, self.root.winfo_x(), self.root.winfo_y())
    def _on_drag(self, e):
        if not hasattr(self, "_drag"): return
        x0,y0,wx,wy = self._drag
        dx,dy = e.x_root-x0, e.y_root-y0
        self.root.geometry(f"+{wx+dx}+{wy+dy}")

def main():
    root = tk.Tk()
    root.title("DynamicIsland Canvas Pro")
    Island(root)
    root.mainloop()

if __name__ == "__main__":
    main()
