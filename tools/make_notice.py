"""Turns web/notice.txt and web/shop.txt into the pictures the client's browser panels show.

The client's built-in browser (Awesomium 1.6.5) crashes on current Windows as soon as it has to draw text, so the
pages must not contain any: each page is one picture with the text painted into it. This tool paints them.

    web/notice.txt  ->  web/notice.png  (shown by web/notice.html)
    web/shop.txt    ->  web/shop.png    (shown by web/shop.html)

Text format: the first line is the headline; a line starting with "- " is a bullet; an empty line is a gap.
The server reads the files on every request, so run the tool and reopen the screen - no restart.

    python tools/make_notice.py                 # both pages, default sizes
    python tools/make_notice.py 380 285         # another size for the notice picture (width height)
"""
import os
import sys
import textwrap

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WEB = os.path.join(ROOT, "web")

BACKGROUND = (20, 22, 28)
HEADLINE = (255, 179, 32)
TEXT = (232, 232, 234)
ACCENT = (154, 208, 255)

DEFAULT_TEXT = {
    "notice": "Welcome to the server\n\n- Edit web/notice.txt and run tools/make_notice.py\n- Reopen the main screen to see the change\n",
    "shop": "Store\n\nEdit web/shop.txt and run tools/make_notice.py.\n",
}

PAGE = """<html><head><title></title></head><body style="margin:0;padding:0;background:#14161c;overflow:hidden"><img src="{name}.png" width="{width}" height="{height}" style="display:block;border:0" alt=""></body></html>
"""


def font(size, bold=False):
    for name in (("arialbd.ttf", "tahomabd.ttf", "DejaVuSans-Bold.ttf") if bold else ("arial.ttf", "tahoma.ttf", "DejaVuSans.ttf")):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def render(name, width, height):
    source = os.path.join(WEB, name + ".txt")
    if not os.path.exists(source):
        with open(source, "w", encoding="utf-8") as handle:
            handle.write(DEFAULT_TEXT[name])
    with open(source, encoding="utf-8-sig") as handle:
        lines = handle.read().splitlines()

    image = Image.new("RGB", (width, height), BACKGROUND)
    draw = ImageDraw.Draw(image)
    head, body = font(max(15, width // 19), bold=True), font(max(11, width // 29))
    margin, y = 14, 12
    per_line = max(10, int((width - 2 * margin - 14) / (body.size * 0.52)))

    first = True
    for line in lines:
        if first and line.strip():
            draw.text((margin, y), line.strip(), font=head, fill=HEADLINE)
            y += head.size + 10
            draw.line((margin, y - 5, width - margin, y - 5), fill=(60, 64, 76), width=1)
            first = False
            continue
        if not line.strip():
            y += body.size // 2
            continue
        bullet = line.lstrip().startswith("- ")
        text = line.lstrip()[2:] if bullet else line.strip()
        for index, part in enumerate(textwrap.wrap(text, per_line) or [""]):
            if y + body.size > height - 6:
                break
            if bullet and index == 0:
                draw.ellipse((margin + 2, y + body.size // 2 - 2, margin + 6, y + body.size // 2 + 2), fill=ACCENT)
            draw.text((margin + (14 if bullet else 0), y), part, font=body, fill=TEXT)
            y += body.size + 5

    image.save(os.path.join(WEB, name + ".png"))
    with open(os.path.join(WEB, name + ".html"), "w", encoding="utf-8", newline="\n") as handle:
        handle.write(PAGE.format(name=name, width=width, height=height))
    print("web/%s.png  %dx%d" % (name, width, height))


def main():
    os.makedirs(WEB, exist_ok=True)
    width = int(sys.argv[1]) if len(sys.argv) > 2 else 380
    height = int(sys.argv[2]) if len(sys.argv) > 2 else 285
    render("notice", width, height)
    render("shop", 760, 420)
    return 0


if __name__ == "__main__":
    sys.exit(main())
