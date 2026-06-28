import tkinter as tk
import time
from datetime import datetime
import threading
import os
import sys
import asyncio
from PIL import Image, ImageTk, ImageDraw

# Try importing Windows-specific libraries with better error handling
try:
    from winsdk.windows.media.control import GlobalSystemMediaTransportControlsSessionManager as MediaManager
    HAS_MEDIA_API = True
except ImportError:
    HAS_MEDIA_API = False
    print("Windows Media API not available. Media detection will be limited.")

class DynamicIsland:
    def __init__(self, root):
        self.root = root
        self.setup_window()
        self.create_widgets()
        self.start_updates()
        
    def setup_window(self):
        # Make window stay on top and remove borders
        self.root.overrideredirect(True)
        self.root.attributes('-topmost', True)
        self.root.attributes('-alpha', 0.85)  # Set transparency (0-1)
        self.root.configure(bg='black')
        
        # Get screen dimensions
        screen_width = self.root.winfo_screenwidth()
        
        # Configure window dimensions and position
        self.width = 400
        self.height = 40
        self.x = (screen_width - self.width) // 2
        self.y = 0
        
        # Set window position and size
        self.root.geometry(f"{self.width}x{self.height}+{self.x}+{self.y}")
        
        # Make corners rounded using transparency
        self.root.attributes('-transparentcolor', 'black')
        

        
        # Add double-click to close
        self.root.bind("<Double-Button-1>", lambda e: self.root.destroy())
        
    def create_rounded_frame(self, width, height, radius, fill_color):
        # Create a rounded rectangle image 
        image = Image.new("RGBA", (width, height), (0, 0, 0, 0))
        draw = ImageDraw.Draw(image)
        
        # Draw rounded rectangle
        draw.rounded_rectangle([(0, 0), (width, height)], radius, fill=fill_color)
        
        return ImageTk.PhotoImage(image)
        
    def start_move(self, event):
        self.x = event.x
        self.y = event.y
        
    def stop_move(self, event):
        self.x = None
        self.y = None
        
    def on_motion(self, event):
        deltax = event.x - self.x
        deltay = event.y - self.y
        x = self.root.winfo_x() + deltax
        y = self.root.winfo_y() + deltay
        self.root.geometry(f"+{x}+{y}")
        
    def create_widgets(self):
        # Create rounded frame background
        self.bg_color = "#333333"
        rounded_bg = self.create_rounded_frame(self.width-20, self.height-10, 15, self.bg_color)
        
        # Background label to hold the rounded image
        self.bg_label = tk.Label(self.root, image=rounded_bg, bg="black")
        self.bg_label.image = rounded_bg  # Keep a reference
        self.bg_label.place(relx=0.5, rely=0.5, anchor='center')
        
        # Main frame
        self.frame = tk.Frame(self.root, bg=self.bg_color, bd=0)
        self.frame.place(relx=0.5, rely=0.5, anchor='center', width=self.width-40, height=self.height-15)
        
        # Time and date label
        self.time_label = tk.Label(self.frame, font=("Segoe UI", 10), bg=self.bg_color, fg='white')
        self.time_label.pack(side=tk.RIGHT, padx=10)
        
        # Music label
        self.music_label = tk.Label(self.frame, font=("Segoe UI", 10), bg=self.bg_color, fg='white', anchor='w')
        self.music_label.pack(side=tk.LEFT, padx=10, fill=tk.X, expand=True)
        
    def update_time(self):
        now = datetime.now()
        
        # Format time as HH:MM AM/PM (12-hour format)
        time_str = now.strftime("%I:%M %p")
        
        # Remove leading zero from hour if present (e.g., "01:30 PM" -> "1:30 PM")
        if time_str.startswith('0'):
            time_str = time_str[1:]
        
        # Format date as "Day, Month DD" (e.g., "Saturday, March 22")
        date_str = now.strftime("%A, %B %d")
        
        # Update the label with properly formatted time and date
        self.time_label.config(text=f"{time_str} • {date_str}")
        
        # Schedule the next update in 1 second
        self.root.after(1000, self.update_time)
        
    # Alternative media detection methods
    def get_media_info_alternative(self):
        """Fallback method to get media info using other approaches"""
        try:
            # Try to get media info from Windows volume mixer
            import psutil
            for proc in psutil.process_iter(['name']):
                if proc.info['name'] in ['spotify.exe', 'Music.UI.exe', 'vlc.exe', 'chrome.exe', 'msedge.exe']:
                    return f"Media playing ({proc.info['name'].replace('.exe', '')})"
            return "No media detected"
        except:
            return "Media detection unavailable"
        
    async def get_media_info(self):
        if not HAS_MEDIA_API:
            return self.get_media_info_alternative()
            
        try:
            sessions = await MediaManager.request_async()
            current_session = sessions.get_current_session()
            
            if current_session:
                info = await current_session.try_get_media_properties_async()
                
                # Extract the media info
                title = info.title
                artist = info.artist
                
                if title and artist:
                    return f"{title} - {artist}"
                elif title:
                    return title
                else:
                    return "No media info"
            return "Not playing"
        except Exception as e:
            return self.get_media_info_alternative()
        
    def update_media_info(self):
        try:
            if HAS_MEDIA_API:
                # For asyncio method
                loop = asyncio.new_event_loop()
                asyncio.set_event_loop(loop)
                media_info = loop.run_until_complete(self.get_media_info())
                loop.close()
            else:
                # For alternative method
                media_info = self.get_media_info_alternative()
            
            # Truncate if too long
            if len(media_info) > 30:
                media_info = media_info[:27] + "..."
                
            self.music_label.config(text=media_info)
        except Exception as e:
            self.music_label.config(text="Music info unavailable")
            
        self.root.after(2000, self.update_media_info)
        
    def start_updates(self):
        self.update_time()
        self.update_media_info()
        
def center_window(root):
    # Center the window at the top of the screen
    screen_width = root.winfo_screenwidth()
    window_width = 400
    x = (screen_width - window_width) // 2
    root.geometry(f"+{x}+0")  # Position at top center
        
def run_app():
    root = tk.Tk()
    app = DynamicIsland(root)
    center_window(root)
    root.mainloop()

if __name__ == "__main__":
    # Add auto-start with Windows option
    if len(sys.argv) > 1 and sys.argv[1] == "--install":
        # Create shortcut in startup folder
        startup_folder = os.path.join(os.environ["APPDATA"], "Microsoft", "Windows", "Start Menu", "Programs", "Startup")
        shortcut_path = os.path.join(startup_folder, "DynamicIsland.lnk")
        
        try:
            # Create shortcut using Windows API
            import win32com.client
            shell = win32com.client.Dispatch("WScript.Shell")
            shortcut = shell.CreateShortCut(shortcut_path)
            shortcut.Targetpath = sys.executable
            shortcut.Arguments = os.path.abspath(__file__)
            shortcut.WorkingDirectory = os.path.dirname(os.path.abspath(__file__))
            shortcut.save()
            print("Dynamic Island added to startup")
        except Exception as e:
            print(f"Could not create startup shortcut: {e}")
            print("You can manually add this program to startup.")
        
    run_app()