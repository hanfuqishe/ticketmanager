"""把产品 Logo 的纯色背景抠成透明，边缘羽化。

用法: python logo_transparent.py   （需在项目根目录运行）
源图: logo/  （原始 Endpoint Central.jpg / OPM.jpg / MDM.png）
输出: src/TicketManager/Assets/Logos/
"""
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path.cwd()
SRC_DIR = ROOT / "logo"  # 原始图片
OUT_DIR = ROOT / "src" / "TicketManager" / "Assets" / "Logos"

# (源文件名, 目标文件名, 容差)。容差越大抠得越多；对纯色背景 55~60 合适。
JOBS = [
    ("Endpoint Central.jpg", "EndpointCentral.png", 60),
    ("OPM.jpg", "OPM.png", 60),
    ("MDM.png", "MDM.png", 55),
]


def make_transparent(src: Path, dst: Path, tol: int, feather: int = 30) -> None:
    im = Image.open(src).convert("RGBA")
    arr = np.array(im, dtype=np.int16)  # H,W,4
    h, w = arr.shape[:2]
    # 四角采样背景色（忽略已是透明的角；若四角都透明则默认纯白背景）
    corners = [arr[1, 1], arr[1, w - 2], arr[h - 2, 1], arr[h - 2, w - 2]]
    samples = [c[:3] for c in corners if c[3] > 0]
    bg = np.mean(samples, axis=0).astype(np.int16) if samples else np.array([255, 255, 255], dtype=np.int16)
    rgb = arr[:, :, :3]
    dist = np.abs(rgb - bg).sum(axis=2).astype(np.float32)  # Manhattan 距离
    orig_alpha = arr[:, :, 3].astype(np.float32)
    # 关键：距离 < tol 的像素一律完全透明（背景本体，含纯白 vs 采样色的细微差）；
    # 只在 tol..tol+feather 这一段做线性羽化（背景→主体的边缘过渡），其余保留原 alpha。
    hi = tol + feather
    a = np.where(dist < tol, 0.0, np.where(dist < hi, 255.0 * (dist - tol) / feather, 255.0))
    a = np.minimum(a, orig_alpha)
    arr[:, :, 3] = a.astype(np.uint8)
    Image.fromarray(arr.astype(np.uint8), "RGBA").save(dst, "PNG")
    print(f"{src.name} -> {dst.name}  (bg={tuple(bg)}, tol={tol}, feather={feather})")


if __name__ == "__main__":
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    for src, dst, tol in JOBS:
        s = SRC_DIR / src
        if s.exists():
            make_transparent(s, OUT_DIR / dst, tol)
        else:
            print(f"skip missing {s}")
