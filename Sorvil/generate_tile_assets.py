#!/usr/bin/env python3
"""Gera os PNGs de tile/ícone do app UWP a partir de logo.svg.

Roda uma vez (script descartável, não faz parte do build) sempre que o
logo.svg mudar. Lê o SVG (fundo transparente, traço branco) e escreve os
PNGs em Sorvil/Assets/ com a convenção de nome scale-N que o empacotador
do UWP reconhece automaticamente.
"""
import io
import os

import cairosvg
from PIL import Image

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SVG_PATH = os.path.join(REPO_ROOT, "logo.svg")
ASSETS_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "Assets")

SCALES = [100, 140, 200, 240]

# (nome_base, largura_base, altura_base, proporcao_do_icone_dentro_do_canvas)
# A proporção usa a MENOR dimensão do canvas como referência, pra manter a
# mesma folga visual em tiles quadradas e largas.
TILE_SPECS = [
    ("Square44x44Logo", 44, 44, 0.65),
    ("Square71x71Logo", 71, 71, 0.50),
    ("Square150x150Logo", 150, 150, 0.55),
    ("Wide310x150Logo", 310, 150, 0.50),
    ("SplashScreen", 620, 300, 0.68),
]


def render_icon(size_px):
    """Renderiza o SVG (quadrado) num PNG em memória de size_px x size_px."""
    png_bytes = cairosvg.svg2png(
        url=SVG_PATH, output_width=size_px, output_height=size_px
    )
    return Image.open(io.BytesIO(png_bytes)).convert("RGBA")


def make_canvas(base_w, base_h, scale, icon_ratio):
    w = round(base_w * scale / 100)
    h = round(base_h * scale / 100)
    canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))

    icon_size = round(min(w, h) * icon_ratio)
    icon = render_icon(icon_size)

    x = (w - icon_size) // 2
    y = (h - icon_size) // 2
    canvas.paste(icon, (x, y), icon)
    return canvas


def main():
    os.makedirs(ASSETS_DIR, exist_ok=True)
    generated = []

    for name, base_w, base_h, ratio in TILE_SPECS:
        for scale in SCALES:
            canvas = make_canvas(base_w, base_h, scale, ratio)
            filename = f"{name}.scale-{scale}.png"
            path = os.path.join(ASSETS_DIR, filename)
            canvas.save(path)
            generated.append((filename, canvas.size))

    # StoreLogo: arquivo único (sem sufixo de escala), 50x50, já referenciado
    # em Package.appxmanifest -> Properties/Logo.
    store_logo = Image.new("RGBA", (50, 50), (0, 0, 0, 0))
    icon = render_icon(round(50 * 0.65))
    x = (50 - icon.size[0]) // 2
    y = (50 - icon.size[1]) // 2
    store_logo.paste(icon, (x, y), icon)
    store_logo.save(os.path.join(ASSETS_DIR, "StoreLogo.png"))
    generated.append(("StoreLogo.png", store_logo.size))

    print(f"Gerados {len(generated)} arquivos em {ASSETS_DIR}:")
    for filename, size in generated:
        print(f"  {filename}  ({size[0]}x{size[1]})")


if __name__ == "__main__":
    main()
