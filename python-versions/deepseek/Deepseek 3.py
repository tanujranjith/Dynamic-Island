import tkinter as tk
from tkinter import ttk, Canvas
import math
import time
from PIL import Image, ImageDraw, ImageTk, ImageFilter
import winsdk.windows.media.control as wmc
import asyncio
import threading
import ctypes
from ctypes import wintypes
import sys

# Constants for window styling
PILL_WIDTH = 84
PILL_HEIGHT = 32
ISLAND_WIDTH = 450
SHEET_WIDTH = 500
SHEET_HEIGHT = 300
ANIMATION_DURATION = 180  # ms
RADIUS = 20  # More rounded corners
ACRYLIC_ALPHA = 180  # Slightly more transparent

class DynamicIsland:
    def __init__(self):
        self.root = tk.Tk()
        self.root.overrideredirect(True)
        self.root.attributes("-topmost", True)
        self.root.attributes("-transparentcolor", "black")
        self.root.configure(bg='black')
        
        # Get screen dimensions
        screen_width = self.root.winfo_screenwidth()
        self.x_pos = (screen_width - PILL_WIDTH) // 2
        self.y_pos = 10
        
        # Set initial window size and position
        self.root.geometry(f"{PILL_WIDTH}x{PILL_HEIGHT}+{self.x_pos}+{self.y_pos}")
        
        # State management
        self.current_state = "pill"
        self.target_state = "pill"
        self.animation_start = 0
        self.is_hovering = False
        
        # Media info
        self.media_title = "No media"
        self.media_artist = ""
        self.is_playing = False
        self.current_session = None
        
        # Create canvas for drawing
        self.canvas = Canvas(self.root, bg='black', highlightthickness=0, width=PILL_WIDTH, height=PILL_HEIGHT)
        self.canvas.pack(fill=tk.BOTH, expand=True)
        
        # Bind events
        self.canvas.bind("<Enter>", self.on_enter)
        self.canvas.bind("<Leave>", self.on_leave)
        self.canvas.bind("<Button-1>", self.on_click)
        
        # Draw initial state
        self.draw_pill()
        
        # Start media monitoring in a separate thread
        self.media_thread = threading.Thread(target=self.start_media_monitor, daemon=True)
        self.media_thread.start()
        
        # Animation loop
        self.animate()
        
    def draw_pill(self):
        self.canvas.delete("all")
        self.canvas.config(width=PILL_WIDTH, height=PILL_HEIGHT)
        
        # Draw rounded rectangle background with acrylic effect
        self.draw_acrylic_background(0, 0, PILL_WIDTH, PILL_HEIGHT, RADIUS)
        
        # Draw media icon and text with proper spacing
        icon_x = PILL_WIDTH // 3
        text_x = PILL_WIDTH // 3 + 12
        
        self.canvas.create_text(icon_x, PILL_HEIGHT//2, text="▶", fill="white", font=("Segoe UI", 10))
        self.canvas.create_text(text_x, PILL_HEIGHT//2, text="Music", fill="white", font=("Segoe UI", 9))
    
    def draw_island(self, width):
        self.canvas.delete("all")
        self.canvas.config(width=width, height=PILL_HEIGHT)
        
        # Draw rounded rectangle background with acrylic effect
        self.draw_acrylic_background(0, 0, width, PILL_HEIGHT, RADIUS)
        
        # Draw media information with proper spacing
        play_icon = "⏸" if self.is_playing else "▶"
        icon_x = 25
        title_x = 50
        artist_x = 50
        controls_x = width - 40
        
        self.canvas.create_text(icon_x, PILL_HEIGHT//2, text=play_icon, fill="white", font=("Segoe UI", 10))
        
        # Truncate text if too long
        title = self.media_title
        if len(title) > 25:
            title = title[:22] + "..."
            
        self.canvas.create_text(title_x, PILL_HEIGHT//2 - 6, text=title, anchor="w", fill="white", font=("Segoe UI", 9))
        self.canvas.create_text(artist_x, PILL_HEIGHT//2 + 6, text=self.media_artist, anchor="w", fill="white", font=("Segoe UI", 8))
        
        # Draw media controls with proper spacing and alignment
        control_spacing = 20
        prev_x = controls_x - control_spacing * 2
        play_x = controls_x - control_spacing
        next_x = controls_x
        
        self.canvas.create_text(prev_x, PILL_HEIGHT//2, text="⏮", fill="white", font=("Segoe UI", 10), tags="prev")
        self.canvas.create_text(play_x, PILL_HEIGHT//2, text=play_icon, fill="white", font=("Segoe UI", 10), tags="play")
        self.canvas.create_text(next_x, PILL_HEIGHT//2, text="⏭", fill="white", font=("Segoe UI", 10), tags="next")
    
    def draw_acrylic_background(self, x, y, width, height, radius):
        # Create a temporary image with PIL for the acrylic effect
        bg_image = Image.new('RGBA', (width, height), (0, 0, 0, 0))
        draw = ImageDraw.Draw(bg_image)
        
        # Draw rounded rectangle with a dark semi-transparent fill
        draw.rounded_rectangle([(0, 0), (width-1, height-1)], radius=radius, fill=(40, 40, 40, ACRYLIC_ALPHA))
        
        # Apply a slight blur for the acrylic effect
        bg_image = bg_image.filter(ImageFilter.GaussianBlur(radius=1))
        
        # Convert to PhotoImage and add to canvas
        self.bg_photo = ImageTk.PhotoImage(bg_image)
        self.canvas.create_image(width//2, height//2, image=self.bg_photo)
    
    def on_enter(self, event):
        self.is_hovering = True
        if self.current_state == "pill":
            self.target_state = "island"
            self.animation_start = time.time()
    
    def on_leave(self, event):
        self.is_hovering = False
        if self.current_state == "island":
            self.target_state = "pill"
            self.animation_start = time.time()
    
    def on_click(self, event):
        if self.current_state == "island":
            x = event.x
            width = self.canvas.winfo_width()
            
            # Control positions
            control_spacing = 20
            controls_x = width - 40
            prev_x = controls_x - control_spacing * 2
            play_x = controls_x - control_spacing
            next_x = controls_x
            
            # Check which control was clicked with a wider hit area
            if prev_x - 10 <= x <= prev_x + 10:
                self.previous_track()
            elif play_x - 10 <= x <= play_x + 10:
                self.toggle_playback()
            elif next_x - 10 <= x <= next_x + 10:
                self.next_track()
    
    async def toggle_playback_async(self):
        if self.current_session:
            try:
                if self.is_playing:
                    await self.current_session.try_pause_async()
                else:
                    await self.current_session.try_play_async()
            except Exception as e:
                print(f"Error toggling playback: {e}")
    
    def toggle_playback(self):
        # Run the async function in a new thread
        threading.Thread(target=self.run_async, args=(self.toggle_playback_async(),), daemon=True).start()
    
    async def previous_track_async(self):
        if self.current_session:
            try:
                await self.current_session.try_skip_previous_async()
            except Exception as e:
                print(f"Error with previous track: {e}")
    
    def previous_track(self):
        threading.Thread(target=self.run_async, args=(self.previous_track_async(),), daemon=True).start()
    
    async def next_track_async(self):
        if self.current_session:
            try:
                await self.current_session.try_skip_next_async()
            except Exception as e:
                print(f"Error with next track: {e}")
    
    def next_track(self):
        threading.Thread(target=self.run_async, args=(self.next_track_async(),), daemon=True).start()
    
    def run_async(self, coroutine):
        try:
            loop = asyncio.new_event_loop()
            asyncio.set_event_loop(loop)
            loop.run_until_complete(coroutine)
            loop.close()
        except Exception as e:
            print(f"Error in async operation: {e}")
    
    def animate(self):
        now = time.time()
        
        # Handle state transitions
        if self.current_state != self.target_state:
            progress = (now - self.animation_start) * 1000 / ANIMATION_DURATION
            if progress >= 1:
                self.current_state = self.target_state
                progress = 1
            
            if self.target_state == "island":
                current_width = PILL_WIDTH + (ISLAND_WIDTH - PILL_WIDTH) * self.ease_out(progress)
                self.draw_island(int(current_width))
                x_pos = self.x_pos - (current_width - PILL_WIDTH) // 2
                self.root.geometry(f"{int(current_width)}x{PILL_HEIGHT}+{int(x_pos)}+{self.y_pos}")
            else:
                current_width = ISLAND_WIDTH - (ISLAND_WIDTH - PILL_WIDTH) * self.ease_out(progress)
                self.draw_island(int(current_width))
                x_pos = self.x_pos - (current_width - PILL_WIDTH) // 2
                self.root.geometry(f"{int(current_width)}x{PILL_HEIGHT}+{int(x_pos)}+{self.y_pos}")
                
                if progress >= 1:
                    self.draw_pill()
                    self.root.geometry(f"{PILL_WIDTH}x{PILL_HEIGHT}+{self.x_pos}+{self.y_pos}")
        
        # Schedule next animation frame
        self.root.after(8, self.animate)  # Increased to ~125 FPS
    
    def ease_out(self, t):
        # Cubic ease-out function for smooth animation
        return 1 - (1 - t) ** 3
    
    async def get_media_info(self):
        try:
            sessions = await wmc.GlobalSystemMediaTransportControlsSessionManager.request_async()
            self.current_session = sessions.get_current_session()
            
            if self.current_session:
                info = await self.current_session.try_get_media_properties_async()
                playback_info = self.current_session.get_playback_info()
                
                if info:
                    self.media_title = info.title if info.title else "Unknown Title"
                    self.media_artist = info.artist if info.artist else "Unknown Artist"
                
                if playback_info:
                    self.is_playing = playback_info.playback_status == wmc.MediaPlaybackStatus.PLAYING
                
                # Auto-expand when media changes
                if self.current_state == "pill" and self.media_title != "No media":
                    self.target_state = "island"
                    self.animation_start = time.time()
                    self.root.after(1400, self.collapse_after_delay)
        except Exception as e:
            print(f"Error getting media info: {e}")
    
    def collapse_after_delay(self):
        if not self.is_hovering and self.current_state == "island":
            self.target_state = "pill"
            self.animation_start = time.time()
    
    def start_media_monitor(self):
        # Create a new event loop for this thread
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        
        # Faster polling (every 300ms instead of 2 seconds)
        while True:
            try:
                loop.run_until_complete(self.get_media_info())
            except Exception as e:
                print(f"Error in media monitor: {e}")
            time.sleep(0.3)  # Much faster polling (300ms)
    
    def run(self):
        self.root.mainloop()

if __name__ == "__main__":
    island = DynamicIsland()
    island.run()