"""Build the app's original filled-outline icon resources using only the standard library.

The drawings use the navigation artwork's 24-unit grid and 1.75-unit rounded
strokes. Filled outlines let WinUI PathIcon inherit its host's foreground color.
Run from any directory; --check verifies the checked-in generated resources.
"""

from __future__ import annotations

import argparse
import math
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
RESOURCES = ROOT / "src/WinOptimizer.UI/Features/Common/Resources"
KEY = "{http://schemas.microsoft.com/winfx/2006/xaml}Key"


class Drawing:
    """Accumulate clockwise filled strokes, with optional badge-scale coordinates."""

    def __init__(self, scale: float = 1, x: float = 0, y: float = 0) -> None:
        self.scale, self.x, self.y = scale, x, y
        self.parts: list[str] = []

    def point(self, x: float, y: float) -> str:
        """Format an authoring point in the output coordinate space."""
        return f"{x * self.scale + self.x:.3f},{y * self.scale + self.y:.3f}"

    def line(self, x1: float, y1: float, x2: float, y2: float) -> None:
        """Draw one round-ended stroke as a filled capsule."""
        length = math.hypot(x2 - x1, y2 - y1)
        if not length:
            return
        radius = .875
        dx, dy = (y2 - y1) * radius / length, -(x2 - x1) * radius / length
        a, b = self.point(x1 + dx, y1 + dy), self.point(x2 + dx, y2 + dy)
        c, d = self.point(x2 - dx, y2 - dy), self.point(x1 - dx, y1 - dy)
        r = f"{radius * self.scale:.3f}"
        self.parts.append(f"M{a}L{b}A{r},{r} 0 0 1 {c}L{d}A{r},{r} 0 0 1 {a}Z")

    def poly(self, *points: tuple[float, float]) -> None:
        """Join consecutive authoring points with rounded strokes."""
        for a, b in zip(points, points[1:]):
            self.line(*a, *b)

    def rect(self, x: float, y: float, width: float, height: float) -> None:
        """Draw a rectangular outline with rounded joins."""
        self.poly((x, y), (x + width, y), (x + width, y + height), (x, y + height), (x, y))

    def arc(self, x: float, y: float, radius: float, start: float, end: float) -> None:
        """Draw a clockwise circular stroke with round caps or a complete ring."""
        half = .875
        outer, inner = (radius + half) * self.scale, (radius - half) * self.scale

        def at(r: float, angle: float) -> str:
            return self.point(x + r * math.cos(math.radians(angle)), y + r * math.sin(math.radians(angle)))

        if end - start >= 360:
            a, b = at(radius + half, 0), at(radius + half, 180)
            c, d = at(radius - half, 0), at(radius - half, 180)
            self.parts.append(f"M{a}A{outer:.3f},{outer:.3f} 0 1 1 {b}A{outer:.3f},{outer:.3f} 0 1 1 {a}Z"
                              f"M{c}A{inner:.3f},{inner:.3f} 0 1 0 {d}A{inner:.3f},{inner:.3f} 0 1 0 {c}Z")
            return
        a, b = at(radius + half, start), at(radius + half, end)
        c, d = at(radius - half, end), at(radius - half, start)
        cap, large = half * self.scale, int(end - start > 180)
        self.parts.append(f"M{a}A{outer:.3f},{outer:.3f} 0 {large} 1 {b}A{cap:.3f},{cap:.3f} 0 0 1 {c}"
                          f"A{inner:.3f},{inner:.3f} 0 {large} 0 {d}A{cap:.3f},{cap:.3f} 0 0 1 {a}Z")

    def circle(self, x: float, y: float, radius: float) -> None:
        """Draw a complete circular outline."""
        self.arc(x, y, radius, 0, 360)


# Original skeletons. Each callback draws one recognizable subject in a 24-unit box.
SHAPES = {
    "Person": lambda d: (d.circle(12, 7, 3), d.poly((5, 21), (5, 18), (7, 14), (17, 14), (19, 18), (19, 21))),
    "People": lambda d: (d.circle(8, 7, 3), d.circle(17, 8, 2), d.poly((2, 20), (3, 15), (13, 15), (14, 20)), d.poly((16, 15), (20, 15), (21, 20))),
    "Lock": lambda d: (d.rect(5, 10, 14, 11), d.arc(12, 9, 4, 180, 360), d.line(8, 9, 8, 10), d.line(16, 9, 16, 10), d.line(12, 15, 12, 17)),
    "Shield": lambda d: (d.poly((12, 3), (20, 6), (19, 15), (16, 19), (12, 22), (8, 19), (5, 15), (4, 6), (12, 3))),
    "Eye": lambda d: (d.poly((2, 12), (6, 7), (12, 5), (18, 7), (22, 12), (18, 17), (12, 19), (6, 17), (2, 12)), d.circle(12, 12, 3)),
    "Fingerprint": lambda d: (d.arc(12, 12, 8, 190, 350), d.arc(12, 12, 5, 170, 370), d.arc(12, 12, 2, 160, 390), d.line(4, 13, 3, 18), d.line(7, 14, 6, 21), d.poly((12, 12), (12, 17), (10, 22)), d.poly((17, 13), (17, 18), (15, 22)), d.line(20, 14, 20, 19)),
    "Camera": lambda d: (d.poly((3, 8), (7, 8), (9, 5), (15, 5), (17, 8), (21, 8), (21, 20), (3, 20), (3, 8)), d.circle(12, 14, 3)),
    "Microphone": lambda d: (d.rect(9, 3, 6, 12), d.arc(12, 12, 7, 0, 180), d.line(12, 19, 12, 22), d.line(8, 22, 16, 22)),
    "Bell": lambda d: (d.arc(12, 10, 6, 180, 360), d.poly((6, 10), (6, 15), (3, 18), (21, 18), (18, 15), (18, 10)), d.line(10, 21, 14, 21), d.line(12, 3, 12, 4)),
    "Speaker": lambda d: (d.poly((3, 9), (7, 9), (12, 5), (12, 19), (7, 15), (3, 15), (3, 9)), d.arc(12, 12, 5, 315, 405), d.arc(12, 12, 9, 315, 405)),
    "Music": lambda d: (d.poly((8, 18), (8, 5), (20, 3), (20, 16)), d.line(8, 9, 20, 7), d.circle(5, 19, 3), d.circle(17, 17, 3)),
    "Video": lambda d: (d.rect(3, 6, 12, 13), d.poly((15, 10), (22, 7), (22, 18), (15, 15))),
    "Monitor": lambda d: (d.rect(3, 4, 18, 13), d.line(12, 17, 12, 21), d.line(7, 21, 17, 21)),
    "Laptop": lambda d: (d.rect(5, 4, 14, 13), d.poly((5, 17), (2, 21), (22, 21), (19, 17))),
    "Phone": lambda d: (d.rect(7, 2, 10, 20), d.line(10, 5, 14, 5), d.line(11, 19, 13, 19)),
    "Keyboard": lambda d: (d.rect(2, 6, 20, 13), d.line(5, 10, 6, 10), d.line(10, 10, 11, 10), d.line(15, 10, 16, 10), d.line(19, 10, 19, 10.1), d.line(5, 14, 6, 14), d.line(10, 14, 19, 14)),
    "Mouse": lambda d: (d.arc(12, 9, 6, 180, 360), d.arc(12, 16, 6, 0, 180), d.line(6, 9, 6, 16), d.line(18, 9, 18, 16), d.line(12, 4, 12, 10)),
    "Chip": lambda d: (d.rect(6, 6, 12, 12), d.rect(9, 9, 6, 6), *(d.line(x, 3, x, 6) for x in (9, 15)), *(d.line(x, 18, x, 21) for x in (9, 15)), *(d.line(3, y, 6, y) for y in (9, 15)), *(d.line(18, y, 21, y) for y in (9, 15))),
    "Memory": lambda d: (d.rect(2, 6, 20, 11), d.rect(5, 9, 5, 5), d.rect(14, 9, 5, 5), *(d.line(x, 17, x, 21) for x in (5, 9, 15, 19))),
    "Card": lambda d: (d.rect(3, 5, 18, 13), d.line(5, 21, 5, 18), d.circle(15, 11.5, 3), d.line(6, 9, 8, 9), d.line(6, 13, 8, 13)),
    "Fan": lambda d: (d.circle(12, 12, 2), d.poly((10, 10), (5, 4), (12, 3), (14, 10)), d.poly((14, 10), (21, 5), (21, 12), (14, 14)), d.poly((14, 14), (19, 21), (12, 21), (10, 14)), d.poly((10, 14), (3, 19), (3, 12), (10, 10))),
    "Drive": lambda d: (d.rect(4, 3, 16, 18), d.circle(12, 10, 4), d.line(12, 10, 17, 6), d.line(7, 18, 9, 18), d.line(16, 18, 17, 18)),
    "Disc": lambda d: (d.circle(12, 12, 9), d.circle(12, 12, 2), d.arc(12, 12, 6, 210, 270)),
    "Battery": lambda d: (d.rect(2, 7, 18, 10), d.line(23, 10, 23, 14), d.line(6, 10, 6, 14), d.line(10, 10, 10, 14)),
    "Power": lambda d: (d.arc(12, 12, 8, 305, 595), d.line(12, 2, 12, 12)),
    "Bolt": lambda d: (d.poly((14, 2), (5, 14), (11, 14), (9, 22), (20, 9), (13, 9), (14, 2))),
    "Moon": lambda d: (d.arc(12, 12, 9, 50, 290), d.arc(16, 8, 8, 80, 260)),
    "Bulb": lambda d: (d.arc(12, 9, 6, 145, 395), d.poly((7, 13), (9, 17), (15, 17), (17, 13)), d.line(9, 20, 15, 20), d.line(11, 23, 13, 23)),
    "Clock": lambda d: (d.circle(12, 12, 9), d.poly((12, 6), (12, 12), (16, 14))),
    "Calendar": lambda d: (d.rect(3, 5, 18, 16), d.line(3, 10, 21, 10), d.line(8, 2, 8, 7), d.line(16, 2, 16, 7), d.line(7, 14, 10, 14), d.line(14, 14, 17, 14), d.line(7, 18, 10, 18)),
    "Refresh": lambda d: (d.arc(12, 12, 8, 210, 530), d.poly((4, 4), (5, 9), (10, 8))),
    "Search": lambda d: (d.circle(10, 10, 6), d.line(15, 15, 22, 22)),
    "Folder": lambda d: (d.poly((3, 20), (3, 5), (9, 5), (12, 8), (21, 8), (21, 20), (3, 20))),
    "File": lambda d: (d.poly((5, 2), (14, 2), (20, 8), (20, 22), (5, 22), (5, 2)), d.poly((14, 2), (14, 8), (20, 8)), d.line(8, 13, 16, 13), d.line(8, 17, 14, 17)),
    "CodeFile": lambda d: (d.poly((4, 2), (15, 2), (21, 8), (21, 22), (4, 22), (4, 2)), d.poly((15, 2), (15, 8), (21, 8)), d.poly((9, 12), (6, 15), (9, 18)), d.poly((16, 12), (19, 15), (16, 18))),
    "Code": lambda d: (d.poly((7, 5), (2, 12), (7, 19)), d.poly((17, 5), (22, 12), (17, 19)), d.line(14, 3, 10, 21)),
    "Terminal": lambda d: (d.rect(2, 4, 20, 16), d.poly((6, 9), (10, 12), (6, 15)), d.line(13, 16, 18, 16)),
    "Table": lambda d: (d.rect(3, 3, 18, 18), d.line(3, 9, 21, 9), d.line(9, 3, 9, 21), d.line(3, 15, 21, 15), d.line(15, 9, 15, 21)),
    "List": lambda d: (d.line(8, 5, 21, 5), d.line(8, 12, 21, 12), d.line(8, 19, 21, 19), d.line(3, 5, 3.2, 5), d.line(3, 12, 3.2, 12), d.line(3, 19, 3.2, 19)),
    "Tree": lambda d: (d.rect(8, 2, 8, 5), d.poly((12, 7), (12, 12), (4, 12), (4, 17)), d.poly((12, 12), (20, 12), (20, 17)), d.rect(1, 17, 6, 5), d.rect(17, 17, 6, 5)),
    "Database": lambda d: (d.rect(4, 4, 16, 16), d.line(4, 10, 20, 10), d.line(4, 15, 20, 15), d.line(7, 7, 9, 7), d.line(7, 13, 9, 13), d.line(7, 18, 9, 18)),
    "Package": lambda d: (d.poly((3, 7), (12, 3), (21, 7), (21, 18), (12, 22), (3, 18), (3, 7), (12, 11), (21, 7)), d.line(12, 11, 12, 22), d.line(7, 5, 16, 9)),
    "Download": lambda d: (d.line(12, 3, 12, 16), d.poly((7, 11), (12, 16), (17, 11)), d.poly((3, 17), (3, 21), (21, 21), (21, 17))),
    "Cloud": lambda d: (d.arc(9, 12, 6, 160, 320), d.arc(16, 14, 6, 220, 450), d.line(7, 20, 16, 20), d.arc(7, 16, 4, 90, 230)),
    "Network": lambda d: (d.rect(8, 2, 8, 6), d.line(12, 8, 12, 12), d.poly((4, 17), (4, 12), (20, 12), (20, 17)), d.rect(1, 17, 6, 5), d.rect(17, 17, 6, 5)),
    "Globe": lambda d: (d.circle(12, 12, 9), d.poly((12, 3), (8, 8), (8, 16), (12, 21), (16, 16), (16, 8), (12, 3)), d.line(3, 12, 21, 12)),
    "Wifi": lambda d: (d.arc(12, 22, 18, 235, 305), d.arc(12, 22, 12, 235, 305), d.arc(12, 22, 6, 235, 305), d.line(12, 21, 12, 21.2)),
    "Router": lambda d: (d.rect(3, 13, 18, 8), d.line(6, 13, 4, 3), d.line(18, 13, 20, 3), d.line(7, 17, 10, 17), d.line(17, 17, 18, 17)),
    "Link": lambda d: (d.poly((10, 5), (7, 2), (2, 7), (7, 12)), d.poly((14, 19), (17, 22), (22, 17), (17, 12)), d.line(7, 7, 17, 17)),
    "Share": lambda d: (d.circle(5, 12, 3), d.circle(19, 5, 3), d.circle(19, 19, 3), d.line(8, 10, 16, 6), d.line(8, 14, 16, 18)),
    "Usb": lambda d: (d.line(12, 21, 12, 3), d.poly((9, 6), (12, 3), (15, 6)), d.poly((12, 16), (6, 12), (6, 8)), d.poly((12, 12), (18, 9), (18, 5)), d.circle(6, 6, 2), d.rect(16, 2, 4, 3)),
    "Pin": lambda d: (d.circle(12, 9, 6), d.poly((6, 9), (7, 15), (12, 22), (17, 15), (18, 9)), d.circle(12, 9, 2)),
    "Map": lambda d: (d.poly((2, 5), (8, 2), (16, 6), (22, 3), (22, 19), (16, 22), (8, 18), (2, 21), (2, 5)), d.line(8, 2, 8, 18), d.line(16, 6, 16, 22)),
    "Cog": lambda d: (d.poly((9, 3), (15, 3), (16, 6), (20, 7), (22, 12), (19, 15), (18, 20), (12, 22), (9, 19), (4, 18), (2, 12), (5, 9), (6, 4), (9, 3)), d.circle(12, 12, 4)),
    "Sliders": lambda d: (d.line(3, 6, 8, 6), d.line(13, 6, 21, 6), d.circle(10.5, 6, 2.5), d.line(3, 12, 14, 12), d.line(19, 12, 21, 12), d.circle(16.5, 12, 2.5), d.line(3, 19, 5, 19), d.line(10, 19, 21, 19), d.circle(7.5, 19, 2.5)),
    "Brush": lambda d: (d.poly((14, 3), (21, 10), (14, 17), (7, 10), (14, 3)), d.poly((7, 10), (3, 15), (9, 21), (14, 17)), d.line(3, 21, 6, 18)),
    "Palette": lambda d: (d.poly((12, 3), (19, 5), (22, 10), (20, 14), (15, 14), (14, 16), (16, 20), (12, 22), (5, 19), (2, 12), (5, 5), (12, 3)), d.circle(7, 9, 1.2), d.circle(12, 7, 1.2), d.circle(18, 9, 1.2), d.circle(7, 15, 1.2)),
    "Contrast": lambda d: (d.circle(12, 12, 9), d.line(12, 3, 12, 21), d.line(15, 6, 15, 18), d.line(18, 9, 18, 15)),
    "Image": lambda d: (d.rect(3, 3, 18, 18), d.circle(8, 8, 2), d.poly((3, 18), (9, 12), (13, 16), (17, 11), (21, 16))),
    "Layers": lambda d: (d.poly((2, 8), (12, 2), (22, 8), (12, 14), (2, 8)), d.poly((2, 13), (12, 19), (22, 13)), d.poly((4, 19), (12, 23), (20, 19))),
    "Grid": lambda d: (d.rect(3, 3, 7, 7), d.rect(14, 3, 7, 7), d.rect(3, 14, 7, 7), d.rect(14, 14, 7, 7)),
    "Window": lambda d: (d.rect(3, 3, 18, 18), d.line(3, 8, 21, 8), d.line(6, 5.5, 7, 5.5)),
    "Store": lambda d: (d.poly((3, 9), (5, 3), (19, 3), (21, 9), (3, 9)), d.rect(4, 9, 16, 13), d.rect(9, 14, 6, 8), d.line(9, 3, 8, 9), d.line(15, 3, 16, 9)),
    "Taskbar": lambda d: (d.rect(3, 4, 18, 16), d.line(3, 15, 21, 15), d.line(7, 17.5, 8, 17.5), d.line(11, 17.5, 12, 17.5), d.line(15, 17.5, 16, 17.5)),
    "Sidebar": lambda d: (d.rect(3, 3, 18, 18), d.line(15, 3, 15, 21), d.line(18, 7, 18, 10), d.line(18, 14, 18, 17)),
    "Cursor": lambda d: (d.poly((4, 2), (5, 19), (10, 15), (14, 22), (18, 20), (14, 13), (21, 11), (4, 2))),
    "Select": lambda d: (d.rect(3, 3, 18, 18), d.poly((7, 12), (11, 16), (18, 8))),
    "Resize": lambda d: (d.poly((3, 9), (3, 3), (9, 3)), d.poly((15, 21), (21, 21), (21, 15)), d.line(3, 3, 10, 10), d.line(14, 14, 21, 21)),
    "Text": lambda d: (d.line(3, 4, 21, 4), d.line(12, 4, 12, 21), d.line(8, 21, 16, 21), d.line(3, 4, 3, 7), d.line(21, 4, 21, 7)),
    "Message": lambda d: (d.poly((3, 3), (21, 3), (21, 17), (10, 17), (4, 22), (4, 17), (3, 17), (3, 3)), d.line(7, 8, 17, 8), d.line(7, 12, 14, 12)),
    "Book": lambda d: (d.poly((12, 5), (8, 3), (2, 3), (2, 20), (8, 20), (12, 22), (16, 20), (22, 20), (22, 3), (16, 3), (12, 5), (12, 22))),
    "Newspaper": lambda d: (d.rect(4, 3, 17, 18), d.poly((4, 8), (1, 8), (1, 21), (4, 21)), d.rect(7, 6, 5, 5), d.line(15, 6, 18, 6), d.line(15, 10, 18, 10), d.line(7, 15, 18, 15), d.line(7, 18, 15, 18)),
    "Bookmark": lambda d: (d.poly((6, 3), (18, 3), (18, 22), (12, 17), (6, 22), (6, 3))),
    "Star": lambda d: (d.poly((12, 2), (15, 9), (22, 10), (17, 15), (18, 22), (12, 18), (6, 22), (7, 15), (2, 10), (9, 9), (12, 2))),
    "Trash": lambda d: (d.poly((5, 8), (6, 21), (18, 21), (19, 8)), d.line(3, 5, 21, 5), d.poly((8, 5), (8, 2), (16, 2), (16, 5)), d.line(10, 10, 10, 17), d.line(14, 10, 14, 17)),
    "Eraser": lambda d: (d.poly((14, 2), (22, 10), (11, 21), (5, 21), (1, 17), (14, 2)), d.line(8, 9, 16, 17)),
    "Broom": lambda d: (d.line(14, 9, 21, 2), d.poly((12, 7), (18, 13), (12, 21), (4, 22), (2, 19), (4, 12), (12, 7)), d.line(4, 12, 12, 21), d.line(8, 16, 5, 21)),
    "Toolbox": lambda d: (d.rect(3, 8, 18, 13), d.poly((8, 8), (8, 3), (16, 3), (16, 8)), d.line(3, 13, 9, 13), d.line(15, 13, 21, 13), d.rect(9, 11, 6, 5)),
    "Wrench": lambda d: (d.poly((14, 4), (14, 8), (18, 10), (22, 8), (21, 14), (17, 16), (13, 15), (6, 22), (2, 18), (10, 11), (9, 7), (11, 3), (14, 4))),
    "Gauge": lambda d: (d.arc(12, 14, 9, 150, 390), d.line(5, 20, 19, 20), d.line(12, 14, 18, 8), d.line(5, 14, 7, 14), d.line(12, 6, 12, 8)),
    "Chart": lambda d: (d.poly((3, 3), (3, 21), (22, 21)), d.poly((6, 16), (10, 10), (15, 13), (21, 5))),
    "Bars": lambda d: (d.line(3, 21, 22, 21), d.rect(5, 13, 3, 8), d.rect(12, 8, 3, 13), d.rect(19, 3, 3, 18)),
    "Pulse": lambda d: (d.poly((2, 13), (6, 13), (9, 4), (14, 21), (18, 10), (20, 13), (23, 13))),
    "Controller": lambda d: (d.poly((7, 6), (17, 6), (20, 9), (22, 19), (19, 21), (15, 16), (9, 16), (5, 21), (2, 19), (4, 9), (7, 6)), d.line(6, 9, 6, 14), d.line(3.5, 11.5, 8.5, 11.5), d.circle(16, 10, 1), d.circle(19, 13, 1)),
    "Robot": lambda d: (d.rect(4, 6, 16, 15), d.line(12, 2, 12, 6), d.circle(8, 12, 1.5), d.circle(16, 12, 1.5), d.line(8, 17, 16, 17), d.line(1, 10, 1, 16), d.line(23, 10, 23, 16)),
    "Brain": lambda d: (d.poly((12, 4), (8, 2), (4, 5), (5, 9), (2, 12), (5, 16), (5, 20), (9, 22), (12, 20), (15, 22), (19, 20), (19, 16), (22, 12), (19, 9), (20, 5), (16, 2), (12, 4), (12, 20)), d.line(5, 9, 8, 10), d.line(19, 9, 16, 10), d.line(5, 16, 8, 15), d.line(19, 16, 16, 15)),
    "Printer": lambda d: (d.rect(3, 8, 18, 10), d.rect(6, 2, 12, 6), d.rect(6, 14, 12, 8), d.line(17, 11, 18, 11)),
    "Building": lambda d: (d.rect(5, 2, 14, 20), d.rect(9, 16, 6, 6), *(d.line(x, y, x + 1, y) for x in (8, 14) for y in (6, 10, 13))),
    "Home": lambda d: (d.poly((2, 11), (12, 2), (22, 11)), d.poly((5, 9), (5, 22), (19, 22), (19, 9)), d.rect(10, 15, 4, 7)),
    "Briefcase": lambda d: (d.rect(2, 7, 20, 14), d.poly((8, 7), (8, 3), (16, 3), (16, 7)), d.line(2, 13, 22, 13), d.line(12, 11, 12, 15)),
    "Wallet": lambda d: (d.rect(3, 4, 18, 17), d.rect(14, 11, 8, 6), d.line(3, 8, 21, 8), d.line(17, 14, 18, 14)),
    "Megaphone": lambda d: (d.poly((3, 10), (9, 10), (20, 4), (20, 20), (9, 14), (3, 14), (3, 10)), d.poly((6, 14), (8, 22), (12, 22), (10, 15))),
    "Play": lambda d: (d.circle(12, 12, 9), d.poly((9, 7), (18, 12), (9, 17), (9, 7))),
    "Stop": lambda d: (d.circle(12, 12, 9), d.rect(8, 8, 8, 8)),
    "Alert": lambda d: (d.poly((12, 2), (23, 21), (1, 21), (12, 2)), d.line(12, 8, 12, 14), d.line(12, 18, 12, 18.2)),
    "Check": lambda d: (d.circle(12, 12, 9), d.poly((7, 12), (11, 16), (18, 8))),
    "Cancel": lambda d: (d.circle(12, 12, 9), d.line(8, 8, 16, 16), d.line(16, 8, 8, 16)),
    "Arrow": lambda d: (d.line(3, 12, 21, 12), d.poly((15, 6), (21, 12), (15, 18))),
    "Swap": lambda d: (d.line(3, 7, 21, 7), d.poly((17, 3), (21, 7), (17, 11)), d.line(21, 17, 3, 17), d.poly((7, 13), (3, 17), (7, 21))),
    "Ruler": lambda d: (d.rect(2, 6, 20, 12), *(d.line(x, 6, x, 10 if x % 8 else 13) for x in (5, 8, 11, 14, 17, 20))),
    "Pen": lambda d: (d.poly((3, 16), (16, 3), (21, 8), (8, 21), (2, 22), (3, 16)), d.line(13, 6, 18, 11)),
    "Flask": lambda d: (d.poly((9, 3), (9, 10), (3, 20), (4, 22), (20, 22), (21, 20), (15, 10), (15, 3)), d.line(7, 3, 17, 3), d.line(6, 16, 18, 16)),
    "Bug": lambda d: (d.rect(7, 6, 10, 15), d.line(12, 6, 12, 21), d.line(8, 6, 5, 2), d.line(16, 6, 19, 2), *(d.line(3, y, 7, y) for y in (10, 15, 20)), *(d.line(17, y, 21, y) for y in (10, 15, 20))),
    "Rocket": lambda d: (d.poly((9, 6), (16, 2), (22, 2), (22, 8), (18, 15), (9, 6)), d.circle(17, 7, 2), d.poly((9, 6), (3, 7), (2, 14), (9, 13)), d.poly((18, 15), (17, 21), (10, 22), (11, 15)), d.line(6, 17, 2, 21)),
    "Bed": lambda d: (d.rect(3, 10, 18, 8), d.line(3, 7, 3, 21), d.line(21, 15, 21, 21), d.rect(4, 6, 6, 4)),
    "School": lambda d: (d.poly((2, 8), (12, 3), (22, 8), (12, 13), (2, 8)), d.poly((6, 11), (6, 17), (12, 21), (18, 17), (18, 11)), d.line(22, 8, 22, 17)),
    "Medical": lambda d: (d.rect(3, 7, 18, 14), d.poly((8, 7), (8, 3), (16, 3), (16, 7)), d.line(12, 11, 12, 17), d.line(9, 14, 15, 14)),
    "Vr": lambda d: (d.poly((3, 6), (21, 6), (22, 18), (15, 18), (12, 14), (9, 18), (2, 18), (3, 6)), d.circle(7, 11, 2), d.circle(17, 11, 2)),
}


def badge(d: Drawing, name: str) -> None:
    """Draw the action/state mark on a reduced subject without importing artwork."""
    marks = {
        "Add": lambda: (d.line(17, 14, 17, 22), d.line(13, 18, 21, 18)),
        "Remove": lambda: d.line(13, 18, 22, 18),
        "Alert": lambda: (d.line(19, 13, 19, 18), d.line(19, 22, 19, 22.1)),
        "Check": lambda: d.poly((13, 18), (16, 21), (22, 14)),
        "Search": lambda: (d.circle(17, 17, 4), d.line(20, 20, 23, 23)),
        "Clock": lambda: (d.circle(18, 18, 5), d.poly((18, 15), (18, 18), (21, 19))),
        "Download": lambda: (d.line(18, 13, 18, 22), d.poly((14, 18), (18, 22), (22, 18))),
        "Upload": lambda: (d.line(18, 22, 18, 13), d.poly((14, 17), (18, 13), (22, 17))),
        "Key": lambda: (d.circle(16, 16, 3), d.line(18, 18, 23, 23), d.line(20, 21, 22, 19)),
        "Lock": lambda: (d.rect(14, 17, 8, 6), d.arc(18, 17, 3, 180, 360)),
        "Cog": lambda: (d.circle(18, 18, 4), d.circle(18, 18, 1), d.line(18, 12, 18, 14), d.line(18, 22, 18, 23), d.line(12, 18, 14, 18), d.line(22, 18, 23, 18)),
        "Star": lambda: d.poly((18, 12), (20, 16), (23, 17), (20, 19), (20, 23), (17, 21), (14, 23), (15, 19), (12, 17), (16, 16), (18, 12)),
        "Sync": lambda: (d.arc(18, 18, 5, 210, 530), d.poly((13, 12), (14, 16), (18, 15))),
        "Message": lambda: d.poly((13, 13), (23, 13), (23, 20), (18, 20), (14, 23), (14, 20), (13, 20), (13, 13)),
    }
    marks[name]()


# Explicit semantic aliases preserve the names supplied by the domain catalogues.
# Variants are composed from the same original subjects and original state marks.
ICONS = [
    ("Person", "", "HumanGreeting"), ("People", "", "AccountSupervisor"),
    ("Person", "Cog", "AccountCog"), ("Person", "Key", "AccountLockOpen PersonKey"),
    ("Person", "Off", "AccountOff"), ("Person", "Message", "AccountTieVoice AccountVoice PersonFeedback"),
    ("Lock", "", "LockClosedKey EyeLock LockOpenAlertOutline"),
    ("Shield", "", "Security ShieldAccount"), ("Shield", "Check", "ShieldCheck"),
    ("Shield", "Alert", "ShieldError"), ("Shield", "Lock", "ShieldLock"),
    ("Shield", "Off", "ShieldOff"), ("Shield", "Sync", "ShieldSync"),
    ("Eye", "", "FileEyeOutline TableEye"), ("Fingerprint", "", "Fingerprint"),
    ("Camera", "", "Camera"), ("Camera", "Off", "CameraOff"),
    ("Microphone", "", "Microphone MicrophoneQuestion"), ("Microphone", "Off", "MicrophoneOff"),
    ("Microphone", "Message", "MicrophoneMessage"),
    ("Bell", "", "Bell BellRingOutline"), ("Bell", "Alert", "BellAlert"),
    ("Bell", "Check", "BellCheck"), ("Bell", "Cog", "BellCog"),
    ("Bell", "Off", "BellOff"), ("Bell", "Add", "BellPlus"),
    ("Speaker", "", "VolumeHigh VolumeMedium DesktopSpeaker MonitorSpeaker"), ("Speaker", "Off", "VolumeOff"),
    ("Music", "", "Music"), ("Video", "", "Video"),
    ("Monitor", "", "Monitor DesktopClassic MonitorMultiple MonitorScreenshot MonitorShare RemoteDesktop MonitorShimmer"),
    ("Monitor", "Off", "MonitorOff"), ("Monitor", "Lock", "MonitorLock"),
    ("Monitor", "Download", "MonitorArrowDown"), ("Monitor", "Search", "MonitorEye"),
    ("Laptop", "", "Laptop"), ("Phone", "", "Cellphone CellphoneLink CellphoneWireless PhoneClassic"),
    ("Phone", "Lock", "CellphoneLock"), ("Phone", "Alert", "PhoneAlert"),
    ("Keyboard", "", "Keyboard KeyboardOutline AppleKeyboardShift"), ("Keyboard", "Off", "KeyboardOff"),
    ("Mouse", "", "Mouse MouseVariant"), ("Chip", "", "Chip Cpu64Bit"),
    ("Memory", "", "Memory"), ("Memory", "Download", "MemoryArrowDown"),
    ("Card", "", "ExpansionCard SmartCard"), ("Fan", "", "Fan"),
    ("Drive", "", "Harddisk"), ("Drive", "Add", "HarddiskPlus"), ("Drive", "Remove", "HarddiskRemove"),
    ("Disc", "", "DiscPlayer"), ("Battery", "", "Battery20 BatteryOutline"),
    ("Battery", "Alert", "BatteryAlert"), ("Battery", "Add", "BatteryCharging"),
    ("Power", "", "PowerSettings"), ("Bolt", "", "LightningBolt FlashAuto FlashRedEye NotebookLightning"),
    ("Bolt", "Alert", "FlashAlert"), ("Moon", "", "WeatherNight Sleep PowerSleep LightbulbNight"),
    ("Bulb", "", "LightbulbOn LightbulbOnOutline"), ("Bulb", "Off", "LightbulbOff StringLightsOff"),
    ("Clock", "", "Alarm ClockStart ClipboardTextClockOutline DocumentTextClock Timeline TimerPause"),
    ("Clock", "Alert", "ClockAlertOutline TimelineAlert"),
    ("Calendar", "", "CalendarMonth CalendarWeekBegin"), ("Calendar", "Clock", "CalendarClock"),
    ("Refresh", "", "Restart Cached History BackupRestore"), ("Refresh", "Alert", "RestartAlert"),
    ("Refresh", "Off", "RestartOff"), ("Search", "", "Magnify MagnifyScan SearchSparkle"),
    ("Folder", "", "FolderOpen FolderList FolderMultiple FolderTable FolderZip FolderAccount"),
    ("Folder", "Alert", "FolderAlert"), ("Folder", "Clock", "FolderClockOutline"), ("Folder", "Search", "FolderSearch"),
    ("File", "", "FileDocument FileWord DocumentEndnote ScriptText ScriptTextOutline FileHidden DocumentQuestionMark"),
    ("File", "Check", "FileDocumentCheck"), ("File", "Lock", "DocumentLock"),
    ("File", "Star", "FileStarFourPointsOutline"), ("CodeFile", "", "FileCode"),
    ("Code", "", "CodeBraces CodeBracesBox Api"), ("Terminal", "", "Console PowerShell Powershell"),
    ("Table", "", "FileExcel FileTableBoxOutline"), ("Table", "Star", "TableStar"),
    ("List", "", "ListBox FormatListBulleted FormDropdown MenuOpen"), ("Tree", "", "FileTree BranchCompare"),
    ("Database", "Off", "DatabaseOff"), ("Database", "Search", "DatabaseSearch"), ("Database", "Sync", "DatabaseSync"),
    ("Package", "", "PackageVariant"), ("Package", "Add", "PackageVariantPlus"),
    ("Package", "Remove", "PackageVariantClosedMinus PackageVariantRemove"), ("Package", "Download", "PackageDown"),
    ("Package", "Alert", "ArchiveAlert"), ("Package", "Search", "ArchiveSearch"),
    ("Package", "Sync", "ArchiveSync"), ("Package", "Off", "CubeOffOutline"),
    ("Download", "", "Download TrayFull"), ("Cloud", "", "FileCloud CloudQuestion"),
    ("Cloud", "Off", "CloudOff"), ("Cloud", "Search", "CloudSearch"),
    ("Cloud", "Sync", "CloudSync"), ("Cloud", "Upload", "CloudUpload CloudUploadOutline"),
    ("Network", "", "NetworkOutline Connection TransitConnectionVariant Dns"),
    ("Network", "Off", "NetworkOff NetworkOffOutline LanDisconnect"),
    ("Globe", "", "Wan Navigation MicrosoftBing"), ("Globe", "Search", "GlobeSearch"), ("Globe", "Off", "WebOff"),
    ("Wifi", "", "Wifi Nfc"), ("Wifi", "Off", "WifiOff"), ("Router", "", "Router"),
    ("Link", "", "LinkVariant Vpn"), ("Share", "", "Share ShareAndroid ShareVariant"), ("Share", "Off", "ShareOff"),
    ("Usb", "", "Usb UsbPort"), ("Pin", "", "MapMarker Pin"), ("Pin", "Off", "MapMarkerOff"),
    ("Map", "", "MapOutline"), ("Cog", "", "Cog ApplicationCog"), ("Cog", "Off", "CogOff"),
    ("Sliders", "", "Tune LayoutRowTwoFocusTopSettings"), ("Brush", "", "BrushVariant FormatPaint"),
    ("Palette", "", "PaletteOutline Color"), ("Contrast", "", "ContrastCircle Opacity"),
    ("Image", "", "Image ImageCircle ImageOutline ImageFilterHdr Hdr HighDefinition DocumentImage"),
    ("Image", "Off", "ImageOff"), ("Image", "Remove", "ImageRemove"),
    ("Layers", "", "ImageMultiple ImageStack BoxShadow"),
    ("Grid", "", "Apps ViewGrid ViewDashboard MonitorDashboard Widgets StarBoxMultipleOutline"),
    ("Window", "", "WindowApps Application DockWindow WindowRestore Tab TabUnselected PanelTopExpand ViewCompact"),
    ("Window", "Alert", "WindowAd Advertisements"),
    ("Store", "", "StoreMicrosoft StorefrontOutline"),
    ("Taskbar", "", "DockBottom"), ("Sidebar", "", "DockRight"),
    ("Cursor", "", "CursorDefault CursorClick CursorDefaultClickOutline"),
    ("Select", "", "Select CheckboxMarked SelectionDrag"), ("Select", "Off", "SelectOff VectorPolylineRemove"),
    ("Resize", "", "Resize ResizeImage ArrowTopLeftBoldOutline ArrowCollapseDown"),
    ("Text", "", "FormatSize Numeric SizeXxs AlphaCBox TextShadow SlideTextCursor Translate"),
    ("Text", "Remove", "TextBoxRemove"), ("Message", "", "MessageText MessageBadge TooltipText"),
    ("Message", "Alert", "MessageAlert"), ("Message", "Remove", "ChatMinus ChatRemove"),
    ("Book", "", "BookDefault"), ("Book", "Sync", "BookSync"),
    ("Bookmark", "", "BookmarkMultiple"), ("Newspaper", "", "Newspaper"),
    ("Star", "", "CreationOutline"), ("Star", "Cog", "StarCog"),
    ("Trash", "", "TrashCanOutline"), ("Eraser", "", "EraserVariant"), ("Broom", "", "Broom"),
    ("Toolbox", "", "Toolbox"), ("Wrench", "", "ProgressWrench"), ("Wrench", "Clock", "WrenchClock"),
    ("Gauge", "", "Speedometer SpeedometerSlow TopSpeed"), ("Chart", "", "ChartLine TrendingDown TrendingUp"),
    ("Bars", "", "ChartBar ChartBox"), ("Pulse", "", "PulseSquare Stethoscope"),
    ("Controller", "", "MicrosoftXbox"), ("Controller", "Alert", "XboxControllerError"),
    ("Robot", "", "Robot BotSparkle"), ("Robot", "Off", "RobotOff"), ("Brain", "", "BrainCircuit"),
    ("Printer", "", "Printer Printer3d Fax"), ("Printer", "Off", "PrinterOff"),
    ("Building", "", "OfficeBuilding"), ("Home", "", "Home"), ("Briefcase", "Search", "BriefcaseSearch"),
    ("Wallet", "", "Wallet CurrencySign"), ("Megaphone", "", "BullhornVariant"),
    ("Play", "", "Play PlayBox Animation"), ("Stop", "", "RecordRec"), ("Alert", "", "AlertCircle AlertOctagon"),
    ("Check", "", "Check"), ("Cancel", "", "Cancel"), ("Arrow", "", "Arrow"), ("Swap", "", "SwapHorizontal"),
    ("Ruler", "", "RulerSquare"), ("Pen", "", "NotebookEdit NotepadEdit PenSparkle AutoFix"), ("Pen", "Off", "PenOff"),
    ("Flask", "", "TestTube"), ("Bug", "", "Bug"), ("Rocket", "", "RocketLaunch"),
    ("Bed", "Clock", "BedClock"), ("School", "", "SchoolOutline"), ("Medical", "", "MedicalBag"),
    ("Vr", "", "VirtualReality"), ("Monitor", "Star", "TelevisionAmbientLight"), ("Globe", "Search", "Radar"),
]

# These resource names are consumed by production views and remain stable.
FEATURE_KEYS = {
    ("Lock", ""): "PrivacyIconPath", ("Power", ""): "PowerIconPath",
    ("Controller", ""): "GamingIconPath", ("Bell", ""): "NotificationIconPath",
    ("Brush", ""): "WindowsThemeIconPath", ("Taskbar", ""): "TaskbarIconPath",
    ("Grid", ""): "StartMenuIconPath", ("Folder", ""): "ExplorerIconPath",
    ("Package", "Download"): "ExternalAppsIconPath", ("Trash", ""): "DeleteForeverIconPath",
    ("Disc", ""): "WimUtilIconPath", ("CodeFile", ""): "AutounattendXmlIconPath",
    ("Memory", "Download"): "MemoryArrowDownIconPath", ("Wrench", "Clock"): "WrenchClockIconPath",
    ("Toolbox", ""): "ToolBoxIconPath",
}


def resource_key(shape: str, mark: str) -> str:
    """Keep feature-view keys stable while naming the remaining original drawings."""
    return FEATURE_KEYS.get((shape, mark), f"{shape}{mark}IconPath")


def outputs() -> dict[Path, str]:
    """Derive filled paths and the case-insensitive semantic lookup from the drawings."""
    aliases: dict[str, tuple[str, str]] = {}
    for shape, mark, names in ICONS:
        for name in names.split():
            previous = aliases.get(name.lower())
            if previous is not None and previous != (shape, mark):
                raise ValueError(f"Conflicting icon name: {name}")
            aliases[name.lower()] = (shape, mark)

    original = ET.parse(RESOURCES / "FeatureIcons.xaml").getroot()
    preserved = [(element.attrib[KEY], element.text) for element in original if KEY in element.attrib
                 and (element.attrib[KEY].startswith("Nav") or element.attrib[KEY].endswith("Symbol")
                      or element.attrib[KEY] in {"WindowsLogoIconPath", "BadgeDefaultIconPath"})]
    lines = ['<?xml version="1.0" encoding="utf-8"?>',
             '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
             '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
             '    <!-- Original app artwork. Filled outlines are generated by extras/generate-icons.py. -->',
             '    <!-- Navigation paths use NavButton\'s rounded stroke; other paths are filled outlines. -->']
    for key, data in preserved:
        lines.append(f'    <x:String x:Key="{key}">{data}</x:String>')
    for shape, mark in sorted(set(aliases.values()) | set(FEATURE_KEYS)):
        subject = Drawing(.72 if mark and mark != "Off" else 1)
        SHAPES[shape](subject)
        if mark == "Off":
            subject.line(3, 3, 21, 21)
        elif mark:
            overlay = Drawing()
            badge(overlay, mark)
            subject.parts.extend(overlay.parts)
        lines.append(f'    <x:String x:Key="{resource_key(shape, mark)}">F1 {" ".join(subject.parts)}</x:String>')
    lines.append('</ResourceDictionary>')
    catalog = '''using System.Collections.Generic;

namespace WinOptimizer.UI.Features.Common.Resources;

/// <summary>Maps catalogue icon names to the app's original vector resources.</summary>
internal static class CustomIconCatalog
{
    // Generated by extras/generate-icons.py from the original drawing definitions.
    private static readonly Dictionary<string, string> ResourceKeys = new(System.StringComparer.OrdinalIgnoreCase)
    {
'''
    catalog += ''.join(f'        ["{name}"] = "{resource_key(*shape)}",\n' for name, shape in sorted(aliases.items()))
    catalog += '''    };

    /// <summary>Resolves a known semantic name; unknown names have no artwork.</summary>
    internal static string? ResourceKeyFor(string name) => ResourceKeys.GetValueOrDefault(name);
}
'''
    return {RESOURCES / "FeatureIcons.xaml": '\n'.join(lines) + '\n', RESOURCES / "CustomIconCatalog.cs": catalog}


def main() -> None:
    """Generate resources explicitly, or check that authoring data and output agree."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    for path, content in outputs().items():
        if args.check:
            if path.read_text(encoding='utf-8-sig') != content:
                raise SystemExit(f"Regenerate original icons: {path.relative_to(ROOT)}")
        else:
            path.write_text(content, encoding='utf-8', newline='\n')
    print('Original icon resources are current.' if args.check else 'Original icon resources generated.')


if __name__ == '__main__':
    main()
