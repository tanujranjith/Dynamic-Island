# Python versions (archive)

The original **Dynamic Island** was prototyped in Python with Tkinter before being
rewritten as the native C# + WPF app in the repository root. These files are kept
for history and reference — they are **not** the maintained version and are not
built by the solution.

Each file is a self-contained script. They explore different shapes, dock
positions, animation styles, and feature sets.

## The versions

| File | What it is |
| --- | --- |
| `og -- DO NOT CHANGE OR EDIT.py` | The original baseline island. Kept untouched as the reference starting point. |
| `Animated og version.py` | Apple-ish hover island with a smooth orange progress **ring** and an animated `−  XX%  +` volume pill. The most polished animated prototype. |
| `DynamicIsland_ApplePlus.py` | Canvas-only build (no frame leaks) — soft-shadow pill, dark theme, centered "Title — Artist", SMTC media controls, time/date. |
| `DynamicIsland_Premium.py` | "Premium" iteration with a fuller feature set and refined layout. |
| `dynamic_island_vision.py` | Adds optional **webcam person-detection** (a presence dot). Local processing; see `requirements-vision.txt`. |
| `docked to top best so far.py` | Top-center **notch slab** — flat top, rounded bottom, docked, compact with a subtle expand. Robust Chrome/Edge SMTC titles. |
| `pill shape best far.py` | Slim top-center pill — playback + volume + auto-resize, no clipping, active-app badge. |
| `left docked.py` | The slim widget docked to the **bottom-left** instead of top-center. |
| `Test.py` | Narrow single-file build focused on smooth hover and **halo-free edges** (binary-mask transparency, no chroma-key fringe). |
| `deepseek/Deepseek 1–3.py` | Three exploratory iterations generated during prompt experiments. |

## Running a prototype

Requires **Python 3.10+** on Windows.

```powershell
# Core dependencies (most versions)
pip install pillow psutil winsdk

# Optional — system volume control
pip install pycaw comtypes

python ".\Animated og version.py"
```

For the camera-presence build:

```powershell
pip install -r requirements-vision.txt
python ".\dynamic_island_vision.py"
```

> SMTC media (now-playing) needs `winsdk`; volume control needs `pycaw`. If an
> optional dependency is missing, those features degrade gracefully and the rest
> of the island still runs.
