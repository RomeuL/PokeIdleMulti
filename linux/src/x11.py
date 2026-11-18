#!/usr/bin/python3
"""Embed full Chromium app windows in the deck, without iframes or WebView.

All X11 operations run on this one connection. Chromium's normal profile,
extensions, downloads, popup windows, and accessibility remain available.
"""
import json
import sys
import time
from Xlib import X, display, error

d = display.Display()
root = d.screen().root
windows = {}
parents = {}


def find_window(wm_class):
    def walk(window):
        try:
            classes = window.get_wm_class() or ()
            if wm_class.lower() in [str(c).lower() for c in classes]:
                geometry = window.get_geometry()
                # Chromium also creates an unmapped 10x10 IPC window with the
                # same WM_CLASS. Only the real app window can host a pane.
                if geometry.width >= 100 and geometry.height >= 100:
                    return window
            for child in window.query_tree().children:
                result = walk(child)
                if result:
                    return result
        except error.XError:
            pass
    return walk(root)


def run(method, a):
    if method == 'inspect':
        result = {}
        for name, window in windows.items():
            try:
                g = window.get_geometry()
                result[name] = {'id': window.id, 'parent': window.query_tree().parent.id,
                                'x': g.x, 'y': g.y, 'width': g.width, 'height': g.height}
            except error.XError:
                pass
        return result
    if method == 'screenshot':
        import struct
        import zlib
        from pathlib import Path
        g = root.get_geometry()
        pixels = root.get_image(0, 0, g.width, g.height, X.ZPixmap, 0xffffffff).data
        rows = bytearray()
        for y in range(g.height):
            rows.append(0)
            for x in range(g.width):
                offset = (y * g.width + x) * 4
                rows.extend((pixels[offset + 2], pixels[offset + 1], pixels[offset]))
        def chunk(kind, body):
            return struct.pack('!I', len(body)) + kind + body + struct.pack('!I', zlib.crc32(kind + body) & 0xffffffff)
        png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('!IIBBBBB', g.width, g.height, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b'')
        target = Path(a['path'])
        if not str(target).startswith('/data/exports/'):
            raise ValueError('Screenshot destination must be in /data/exports')
        target.write_bytes(png)
        return str(target)
    if method == 'find':
        window = find_window(a['class'])
        if window:
            windows[a['class']] = window
            return window.id
        return None
    if method == 'embed':
        window = windows.get(a['class']) or find_window(a['class'])
        parent = windows.get(a['parent']) or find_window(a['parent'])
        if not window or not parent:
            return False
        windows[a['class']] = window
        window.unmap()
        d.sync()
        # Let the window manager finish withdrawing its old frame. Otherwise
        # its delayed UnmapNotify handling can reparent our pane back to root.
        for _ in range(50):
            if window.query_tree().parent.id == root.id:
                break
            time.sleep(0.01)
            d.sync()
        window.change_attributes(override_redirect=True)
        window.reparent(parent, 0, 0)
        window.map()
        d.sync()
        parents[a['class']] = parent
        return True
    if method == 'bounds':
        window = windows.get(a['class'])
        if not window:
            return False
        parent = parents.get(a['class'])
        if parent and window.query_tree().parent.id != parent.id:
            window.change_attributes(override_redirect=True)
            window.reparent(parent, 0, 0)
            window.map()
            d.sync()
        if not a.get('visible', True):
            # Do not unmap: a mapped browser keeps its background lifecycle.
            window.configure(x=-32000, y=-32000)
        else:
            window.configure(x=max(0, int(a['x'])), y=max(0, int(a['y'])),
                             width=max(1, int(a['width'])), height=max(1, int(a['height'])))
        d.sync()
        return True
    if method == 'focus':
        window = windows.get(a['class'])
        if window:
            window.set_input_focus(X.RevertToParent, X.CurrentTime)
            d.sync()
        return True
    if method == 'screenshot':
        return {'width': d.screen().width_in_pixels, 'height': d.screen().height_in_pixels}
    raise ValueError('Unknown X11 operation')


for line in sys.stdin:
    request = {}
    try:
        request = json.loads(line)
        result = run(request['method'], request['args'])
        print(json.dumps({'id': request['id'], 'result': result}), flush=True)
    except Exception as exc:
        print(json.dumps({'id': request.get('id', 0), 'error': str(exc)}), flush=True)
