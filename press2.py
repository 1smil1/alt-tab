# -*- coding: utf-8 -*-
# Press Alt+` then capture + crop around net9 head (slot 0)
import ctypes, time
from PIL import ImageGrab

user32 = ctypes.windll.user32
VK_MENU = 0x12
VK_OEM_3 = 0xC0

user32.keybd_event(VK_MENU, 0, 0, 0)
time.sleep(0.05)
user32.keybd_event(VK_OEM_3, 0, 0, 0)
time.sleep(0.05)
user32.keybd_event(VK_OEM_3, 0, 2, 0)
time.sleep(2.5)
ImageGrab.grab().save('D:/window_switcher/hold2.png')
time.sleep(0.5)
user32.keybd_event(VK_MENU, 0, 2, 0)
print('done')
