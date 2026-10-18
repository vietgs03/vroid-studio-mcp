#!/usr/bin/env python3
"""Tiny client for the VRoid Studio MCP endpoint, for scripted design work.

    from vroid import V
    v = V()                          # prod (17891); V(port=17893) for the dev sandbox
    v.call("enter", section="Body", category="Whole Body")
    v.sheet("Hairstyle", "Back", out="shots/back.png")   # contact sheet of preset thumbnails
    v.shot("shots/front.png", region="body")              # framed screenshot saved to disk
"""
import base64
import io
import json
import os
import sys
import urllib.request

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))


class VroidError(RuntimeError):
    pass


class V:
    def __init__(self, port=17891, timeout=400):
        self.url = f"http://127.0.0.1:{port}/mcp"
        self.timeout = timeout
        self._id = 0

    def raw(self, name, args):
        self._id += 1
        body = {"jsonrpc": "2.0", "id": self._id, "method": "tools/call", "params": {"name": name, "arguments": args}}
        req = urllib.request.Request(self.url, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=self.timeout) as r:
            doc = json.load(r)
        res = doc["result"]
        texts = [c.get("text", "") for c in res.get("content", []) if c.get("type") == "text"]
        imgs = [base64.b64decode(c["data"]) for c in res.get("content", []) if c.get("type") == "image"]
        return bool(res.get("isError")), texts, imgs

    def call(self, _tool, **args):
        err, texts, _ = self.raw(_tool, {k: v for k, v in args.items() if v is not None})
        text = "\n".join(texts)
        if err:
            raise VroidError(f"{_tool}: {text}")
        try:
            return json.loads(text)
        except (ValueError, TypeError):
            return text

    def try_call(self, _tool, **args):
        try:
            return self.call(_tool, **args)
        except VroidError as e:
            return {"error": str(e)}

    # ---- convenience ----
    def set(self, section, category, path, value):
        return self.call("set_param", section=section, category=category, index=path, value=str(value))

    def select(self, section, category, index):
        return self.call("select_preset", section=section, category=category, index=int(index))

    def presets(self, section, category):
        return self.call("presets", section=section, category=category)

    def bones(self):
        return self.call("bones")

    def shot(self, path, region=None, angle=None, size=None):
        if region:
            self.call("camera", action="frame", region=region, angle=angle)
        err, texts, imgs = self.raw("screenshot", {"mode": "inline"})
        if err or not imgs:
            raise VroidError(f"screenshot: {texts}")
        im = Image.open(io.BytesIO(imgs[0])).convert("RGB")
        if size:
            im.thumbnail((size, size))
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        im.save(path)
        return path

    def previews(self, section, category, indexes):
        """Thumbnails for preset indexes (12 per call); returns [(index, name, PIL.Image)]."""
        out = []
        names = {p["index"]: p["name"] for p in self.presets(section, category)}
        for i in range(0, len(indexes), 12):
            chunk = indexes[i:i + 12]
            err, texts, imgs = self.raw("preset_preview_list", {"section": section, "category": category, "indexes": chunk, "mode": "inline"})
            if err:
                raise VroidError(f"preview: {texts}")
            for idx, data in zip(chunk, imgs):
                out.append((idx, names.get(idx, str(idx)), Image.open(io.BytesIO(data)).convert("RGB")))
        return out

    def sheet(self, section, category, out, indexes=None, cols=8, cell=200):
        """Contact sheet of preset thumbnails labelled with their index (skips the clear entry)."""
        ps = self.presets(section, category)
        if indexes is None:
            indexes = [p["index"] for p in ps if p["name"] != "(clear)"]
        items = self.previews(section, category, indexes)
        rows = (len(items) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * cell, rows * (cell + 18)), "white")
        d = ImageDraw.Draw(sheet)
        try:
            font = ImageFont.truetype("DejaVuSans.ttf", 13)
        except OSError:
            font = ImageFont.load_default()
        for k, (idx, name, im) in enumerate(items):
            im = im.copy(); im.thumbnail((cell, cell))
            x, y = (k % cols) * cell, (k // cols) * (cell + 18)
            sheet.paste(im, (x + (cell - im.width) // 2, y))
            d.text((x + 3, y + cell + 1), f"{idx} {name[:26]}", fill=(200, 0, 0), font=font)
        os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
        sheet.save(out)
        return out, [(i, n) for i, n, _ in items]


def side_by_side(paths, out, height=700, labels=None):
    ims = []
    for p in paths:
        im = Image.open(p).convert("RGB")
        im = im.resize((int(im.width * height / im.height), height))
        ims.append(im)
    W = sum(i.width for i in ims)
    canvas = Image.new("RGB", (W, height + (22 if labels else 0)), "white")
    x = 0
    d = ImageDraw.Draw(canvas)
    for k, im in enumerate(ims):
        canvas.paste(im, (x, 22 if labels else 0))
        if labels:
            d.text((x + 6, 4), labels[k], fill=(0, 0, 0))
        x += im.width
    canvas.save(out)
    return out


if __name__ == "__main__":
    v = V(port=int(os.environ.get("VROID_PORT", "17891")))
    name = sys.argv[1]
    args = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}
    print(json.dumps(v.call(name, **args), ensure_ascii=False, indent=1))
