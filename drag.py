# -*- coding: utf-8 -*-
# Simulate a card drag: Alt+` opens the overlay, mouse down at source,
# stepped move to target, mid-drag screenshot, release, after screenshot.
# Usage: python drag.py sx sy tx ty   (physical px)
import ctypes, sys, time
from PIL import ImageGrab

user32 = ctypes.windll.user32
sx, sy, tx, ty = int(sys.argv[1]), int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])

user32.keybd_event(0x12, 0, 0, 0)   # Alt down
user32.keybd_event(0xC0, 0, 0, 0)   # backtick backtick
time.sleep(1.2)

user32.SetCursorPos(sx, sy)
time.sleep(0.25)
user32.mouse_event(0x0002, 0, 0, 0, 0)  # left down
time.sleep(0.15)

for i in range(1, 21):
    user32.SetCursorPos(sx + (tx - sx) * i // 20, sy + (ty - sy) * i // 20)
    time.sleep(0.03)
time.sleep(0.5)
ImageGrab.grab().save('D:/window_switcher/drag_preview.png')

user32.mouse_event(0x0004, 0, 0, 0, 0)  # left up
time.sleep(1.0)
ImageGrab.grab().save('D:/window_switcher/drag_after.png')

user32.keybd_event(0xC0, 0, 2, 0)  # backtick up
user32.keybd_event(0x12, 0, 2, 0)  # Alt up
print('done')
