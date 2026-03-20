"""
Generate logo.ico for Systema — neon dark tech aesthetic.
Creates a stylized "S" mark on a deep dark background with blue/green gradient.
"""
import math
from PIL import Image, ImageDraw, ImageFont, ImageFilter
import os

def lerp_color(c1, c2, t):
    return tuple(int(c1[i] + (c2[i] - c1[i]) * t) for i in range(len(c1)))

def draw_icon(size):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    pad = size * 0.06
    r = size * 0.18  # corner radius of background

    # ── Background rounded square ──
    bg_color = (13, 15, 20, 255)   # #0D0F14
    border_color = (0, 180, 255, 180)  # #00B4FF semi

    # Draw rounded rect background
    x0, y0, x1, y1 = pad, pad, size - pad, size - pad
    draw.rounded_rectangle([x0, y0, x1, y1], radius=r, fill=bg_color)

    # ── Glow ring (outer border) ──
    glow_size = int(size * 0.04)
    for i in range(glow_size, 0, -1):
        alpha = int(80 * (1 - i / glow_size))
        c = (0, 180, 255, alpha)
        draw.rounded_rectangle(
            [x0 - i * 0.3, y0 - i * 0.3, x1 + i * 0.3, y1 + i * 0.3],
            radius=r + i * 0.3, outline=c, width=1
        )

    draw.rounded_rectangle([x0, y0, x1, y1], radius=r,
                            outline=(0, 180, 255, 160), width=max(1, int(size * 0.025)))

    # ── Draw stylized "S" using bezier-like arcs ──
    # We'll draw a thick S using two arcs (top half reversed, bottom half)
    cx, cy = size / 2, size / 2
    s = size * 0.32   # half-size of the S shape
    lw = max(2, int(size * 0.1))   # line width

    # S is made from 2 arcs:
    #   Top arc:    center at (cx, cy - s*0.5), right-to-left (blue)
    #   Bottom arc: center at (cx, cy + s*0.5), left-to-right (green)

    top_cx, top_cy = cx, cy - s * 0.45
    bot_cx, bot_cy = cx, cy + s * 0.45
    arc_r = s * 0.55

    # Number of gradient steps
    steps = max(12, size // 8)

    def draw_arc_gradient(center_x, center_y, radius, start_deg, end_deg, col1, col2, width):
        """Draw a thick arc with gradient color."""
        total = end_deg - start_deg
        for i in range(steps):
            t = i / steps
            ang = math.radians(start_deg + total * t)
            color = lerp_color(col1, col2, t)
            # Thick arc via multiple circles along the arc path
            for w in range(-width // 2, width // 2 + 1):
                offset_r = radius + w
                px = center_x + math.cos(ang) * offset_r
                py = center_y + math.sin(ang) * offset_r
                dot = max(1, int(size * 0.025))
                draw.ellipse([px - dot, py - dot, px + dot, py + dot], fill=color)

    # Blue → Cyan for top arc (180° to 0° = right half going up)
    blue   = (0, 180, 255, 255)
    cyan   = (0, 229, 200, 255)
    green  = (0, 229, 160, 255)

    # Top arc: from 0° to 180° (upper semicircle going left)
    draw_arc_gradient(top_cx, top_cy, arc_r, 0, 180, blue, cyan, lw)

    # Bottom arc: from 180° to 360° (lower semicircle going right)
    draw_arc_gradient(bot_cx, bot_cy, arc_r, 180, 360, cyan, green, lw)

    # ── Inner glow / soft light on the S ──
    blurred = img.filter(ImageFilter.GaussianBlur(radius=max(1, size // 32)))
    img = Image.alpha_composite(blurred, img)

    return img


def make_ico(output_path):
    sizes = [256, 128, 64, 48, 32, 16]
    images = []
    for s in sizes:
        frame = draw_icon(s)
        images.append(frame)

    # Save as .ico with all sizes
    images[0].save(
        output_path,
        format="ICO",
        sizes=[(s, s) for s in sizes],
        append_images=images[1:]
    )
    print(f"Saved: {output_path}")
    for i, s in enumerate(sizes):
        print(f"  {s}x{s}: {images[i].size}")


if __name__ == "__main__":
    out = os.path.join(os.path.dirname(__file__), "..", "src", "Systema", "Assets", "logo.ico")
    out = os.path.normpath(out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    make_ico(out)
