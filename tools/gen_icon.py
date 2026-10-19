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
sizes = [16, 20, 24, 30, 32, 48, 64, 128, 256]
frames = [img.resize((s, s), Image.LANCZOS) for s in sizes]

out.parent.mkdir(parents=True, exist_ok=True)
frames[0].save(out, format="ICO", append_images=frames[1:], sizes=[(s, s) for s in sizes])
print(f"saved: {out}")
