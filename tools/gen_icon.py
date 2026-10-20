import sys
from pathlib import Path
from PIL import Image

# 用法: python tools/gen_icon.py <源图路径> [输出ico路径]
# 输出默认到 PrtScr.App/Assets/app.ico
if len(sys.argv) < 2:
    print("用法: python tools/gen_icon.py <源图路径> [输出ico路径]")
    raise SystemExit(2)
src = Path(sys.argv[1])
out = (
    Path(sys.argv[2])
    if len(sys.argv) > 2
    else Path(__file__).resolve().parent.parent / "src" / "PrtScr.App" / "Assets" / "app.ico"
)

img = Image.open(src).convert("RGBA")

# 居中裁成正方形：截图工具图标在 16px 下也要能看出主体，先保证构图不歪
w, h = img.size
side = min(w, h)
left = (w - side) // 2
top = (h - side) // 2
square = img.crop((left, top, left + side, top + side))

# ICO 必须提供多尺寸，Windows 标题栏/任务栏/托盘才会各取所需；
# Pillow 的 ICO 保存会按 sizes 从这张基准图缩放，所以基准图必须足够大（256×256）。
# 之前用 16×16 做基准，结果只生成了 16×16，标题栏被放大后发糊。
base = square.resize((256, 256), Image.LANCZOS)
sizes = [16, 20, 24, 30, 32, 40, 48, 64, 128, 256]

out.parent.mkdir(parents=True, exist_ok=True)
base.save(out, format="ICO", sizes=[(s, s) for s in sizes])
print(f"saved: {out} ({out.stat().st_size} bytes)")
