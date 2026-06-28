import tkinter as tk
from tkinter import ttk, Canvas
import math
import time
from PIL import Image, ImageDraw, ImageTk
import winsdk.windows.media.control as wmc
import asyncio
import threading
import ctypes
from ctypes import wintypes
import sys

# Constants for window styling
PILL_WIDTH = 60
ISLAND_WIDTH = 450
SHEET_WIDTH = 500
SHEET_HEIGHT = 300
ANIMATION_DURATION = 180  # ms
RADIUS = 10
ACRYLIC_ALPHA = 200  # 0-255

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
        self.root.geometry(f"{PILL_WIDTH}x40+{self.x_pos}+{self.y_pos}")
        
        # State management
        self.current_state = "pill"
        self.target_state = "pill"
        self.animation_start = 0
        self.is_hovering = False
        
        # Media info
        self.media_title = "No media"
        self.media_artist = ""
        self.is_playing = False
        
        # Create canvas for drawing
        self.canvas = Canvas(self.root, bg='black', highlightthickness=0, width=PILL_WIDTH, height=40)
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
        
        # Draw rounded rectangle background with acrylic effect
        self.draw_acrylic_background(0, 0, PILL_WIDTH, 40, RADIUS)
        
        # Draw media icon and text
        self.canvas.create_text(PILL_WIDTH//2, 20, text="▶", fill="white", font=("Segoe UI", 10))
        self.canvas.create_text(PILL_WIDTH//2 + 10, 20, text="Music", fill="white", font=("Segoe UI", 9))
    
    def draw_island(self, width):
        self.canvas.delete("all")
        self.canvas.config(width=width, height=40)
        
        # Draw rounded rectangle background with acrylic effect
        self.draw_acrylic_background(0, 0, width, 40, RADIUS)
        
        # Draw media information
        self.canvas.create_text(30, 20, text="▶", fill="white", font=("Segoe UI", 10))
        
        # Truncate text if too long
        title = self.media_title
        if len(title) > 20:
            title = title[:17] + "..."
            
        self.canvas.create_text(50, 13, text=title, anchor="w", fill="white", font=("Segoe UI", 9))
        self.canvas.create_text(50, 27, text=self.media_artist, anchor="w", fill="white", font=("Segoe UI", 8))
        
        # Draw media controls
        self.canvas.create_text(width - 60, 20, text="⏮", fill="white", font=("Segoe UI", 10))
        self.canvas.create_text(width - 40, 20, text="⏯", fill="white", font=("Segoe UI", 10))
        self.canvas.create_text(width - 20, 20, text="⏭", fill="white", font=("Segoe UI", 10))
    
    def draw_acrylic_background(self, x, y, width, height, radius):
        # Create a temporary image with PIL for the acrylic effect
        bg_image = Image.new('RGBA', (width, height), (40, 40, 40, ACRYLIC_ALPHA))
        draw = ImageDraw.Draw(bg_image)
        
        # Draw rounded rectangle
        draw.rounded_rectangle([(0, 0), (width-1, height-1)], radius=radius, fill=(40, 40, 40, ACRYLIC_ALPHA))
        
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
            # Check if click is on play/pause button (simplified)
            x = event.x
            width = self.canvas.winfo_width()
            if width - 40 <= x <= width - 20:
                self.toggle_playback()
    
    def toggle_playback(self):
        # Placeholder for playback toggle functionality
        print("Toggling playback")
        self.is_playing = not self.is_playing
    
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
                self.root.geometry(f"{int(current_width)}x40+{int(x_pos)}+{self.y_pos}")
            else:
                current_width = ISLAND_WIDTH - (ISLAND_WIDTH - PILL_WIDTH) * self.ease_out(progress)
                self.draw_island(int(current_width))
                x_pos = self.x_pos - (current_width - PILL_WIDTH) // 2
                self.root.geometry(f"{int(current_width)}x40+{int(x_pos)}+{self.y_pos}")
                
                if progress >= 1:
                    self.draw_pill()
                    self.root.geometry(f"{PILL_WIDTH}x40+{self.x_pos}+{self.y_pos}")
        
        # Schedule next animation frame
        self.root.after(10, self.animate)
    
    def ease_out(self, t):
        # Cubic ease-out function for smooth animation
        return 1 - (1 - t) ** 3
    
    async def get_media_info(self):
        sessions = await wmc.GlobalSystemMediaTransportControlsSessionManager.request_async()
        current_session = sessions.get_current_session()
        
        if current_session:
            info = await current_session.try_get_media_properties_async()
            playback_info = current_session.get_playback_info()
            
            self.media_title = info.title if info.title else "Unknown Title"
            self.media_artist = info.artist if info.artist else "Unknown Artist"
            self.is_playing = playback_info.playback_status == wmc.MediaPlaybackStatus.PLAYING
            
            # Auto-expand when media changes
            if self.current_state == "pill":
                self.target_state = "island"
                self.animation_start = time.time()
                self.root.after(1400, self.collapse_after_delay)
    
    def collapse_after_delay(self):
        if not self.is_hovering and self.current_state == "island":
            self.target_state = "pill"
            self.animation_start = time.time()
    
    def start_media_monitor(self):
        # Create a new event loop for this thread
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        
        while True:
            try:
                loop.run_until_complete(self.get_media_info())
            except Exception as e:
                print(f"Error getting media info: {e}")
            time.sleep(2)  # Check every 2 seconds
    
    def run(self):
        self.root.mainloop()

if __name__ == "__main__":
    island = DynamicIsland()
    island.run()