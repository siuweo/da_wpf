"""Camera service cho Intel RealSense D455 - phat hinh anh RGB + depth va TACH VAT tren bang tai cho GUI.

Mot tien trinh DUY NHAT so huu camera (D455 chi mo duoc boi 1 chuong trinh). Hinh anh duoc phat
dang MJPEG qua HTTP de GUI (Window > Camera) hoac trinh duyet xem truc tiep:

    http://<host>:7501/              trang xem thu trong trinh duyet
    http://<host>:7501/color.mjpg    RGB + vat dang theo doi (ROI, hinh chu nhat lam muot, id, chieu cao)
    http://<host>:7501/depth.mjpg    depth gia mau (JET, gan = do, xa = xanh)
    http://<host>:7501/height.mjpg   ban do chieu cao so voi bang tai (vung ROI) + mask vat tho + vat dang theo doi
    GET  http://<host>:7501/status     trang thai (JSON): hieu chinh, do troi, vat dang trong ROI, vat vua di qua
    POST http://<host>:7501/calibrate  hieu chinh lai mat phang bang tai (bang tai phai TRONG)

Tach vat dung mh.Detector + mh.Tracker cua max_height.py (xem camera/object_isolation.py). Mat phang bang tai:
  - camera that : plane_live.json  (giong live_max_height.py)
  - phat lai    : plane.json       (giong max_height.py, cho video gia mau)
Camera that nap cau hinh d455.json (xuat tu RealSense Viewer) neu co - phai GIONG luc hieu chinh.

Depth da duoc align theo RGB -> pixel (u, v) tren cac anh la cung mot diem.

Cach chay (tu thu muc du an):
    python camera/camera_service.py                       # D455 that
    python camera/camera_service.py --replay rgb.mp4 depth_color.mp4   # phat lai video da ghi
    python camera/camera_service.py --host 0.0.0.0        # cho phep may khac trong mang xem

Ctrl+C de dung.
"""

import argparse
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import cv2
import numpy as np

from object_isolation import ROOT, ObjectIsolator, mh

# ====================== CAU HINH ======================
WIDTH = 640                # anh mau; depth duoc align ve kich thuoc nay (ROI + mat phang tinh theo no)
HEIGHT = 480
FPS = 30
DEPTH_MIN_M = 0.1          # thang mau depth: gan nhat (m) - giong max_height.DEPTH_MIN_M
DEPTH_MAX_M = 0.5          # thang mau depth: xa nhat (m)
JPEG_QUALITY = 80
HTTP_PORT = 7501
BOUNDARY = "frame"
D455_JSON = ROOT / "d455.json"
PLANE_LIVE = ROOT / "plane_live.json"
PLANE_REPLAY = ROOT / "plane.json"
DEPTH_EXPOSURE_US = None   # None = theo JSON. Vd 6000: tat auto, 6 ms (giam nhoe khi bang tai chay)
CHANNELS = ("color", "depth", "height")
# ======================================================


class FrameHub:
    """Giu frame JPEG moi nhat cua tung luong; cac client HTTP cho frame moi qua Condition."""

    def __init__(self, channels):
        self._cond = threading.Condition()
        self._jpeg = {c: None for c in channels}
        self._seq = 0
        self._clients = 0
        self.running = True

    @property
    def has_clients(self):
        return self._clients > 0

    def publish(self, frames):
        """frames: {channel: anh BGR}. Chi nen JPEG khi co nguoi xem."""
        if self._clients == 0:
            return
        encoded = {}
        for ch, img in frames.items():
            ok, buf = cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, JPEG_QUALITY])
            if ok:
                encoded[ch] = buf.tobytes()
        with self._cond:
            self._jpeg.update(encoded)
            self._seq += 1
            self._cond.notify_all()

    def wait(self, channel, last_seq, timeout=2.0):
        """Tra ve (seq, jpeg) khi co frame moi hon last_seq; jpeg = None neu het thoi gian."""
        with self._cond:
            self._cond.wait_for(lambda: self._seq != last_seq or not self.running, timeout)
            if self._seq == last_seq:
                return last_seq, None
            return self._seq, self._jpeg.get(channel)

    def add_client(self, delta):
        with self._cond:
            self._clients += delta

    def stop(self):
        with self._cond:
            self.running = False
            self._cond.notify_all()


INDEX_HTML = b"""<!doctype html><html><head><meta charset="utf-8"><title>D455 camera service</title>
<style>body{background:#1e1e1e;color:#ddd;font-family:sans-serif;margin:12px}
img{max-width:32%;border:1px solid #444;vertical-align:top}pre{font-size:12px}</style></head><body>
<p>D455 camera service - RGB + vat / Depth / Chieu cao (ROI)
 <button onclick="if(confirm('Bang tai phai TRONG (khong vat, khong tay). Hieu chinh?'))
 fetch('/calibrate',{method:'POST'}).then(r=>r.json()).then(j=>alert(j.msg))">Hieu chinh bang tai</button></p>
<img src="/color.mjpg"> <img src="/depth.mjpg"> <img src="/height.mjpg"><pre id="s"></pre>
<script>setInterval(()=>fetch('/status').then(r=>r.json()).then(j=>
 document.getElementById('s').textContent=JSON.stringify(j,null,1)).catch(()=>{}),500)</script>
</body></html>"""


class Service:
    """Gan nguon anh, bo tach vat va HTTP. Bo tach vat duoc tao o frame dau (khi biet kich thuoc anh)."""

    def __init__(self, hub, plane_file, source):
        self.hub = hub
        self.plane_file = plane_file
        self.source = source
        self.iso = None
        self._iso_ready = threading.Event()

    def on_frame(self, color, depth_bgr, z_roi_mm):
        """color: BGR; depth_bgr: depth gia mau ca frame; z_roi_mm: depth (mm) vung ROI, NaN = khong hop le."""
        if self.iso is None:
            self.iso = ObjectIsolator(color.shape[:2], self.plane_file, self.source)
            self._iso_ready.set()
        result = self.iso.process(z_roi_mm)
        if self.hub.has_clients:
            frames = {"color": self.iso.draw(color, result), "height": self.iso.height_view(result)}
            if depth_bgr is not None:      # nguon bo qua to mau depth khi chua co nguoi xem
                frames["depth"] = depth_bgr
            self.hub.publish(frames)

    def status(self):
        return self.iso.status() if self.iso is not None else {"calibrated": False, "error": "Chua co frame"}

    def calibrate(self):
        if not self._iso_ready.wait(3.0):
            return False, "Chua co frame tu camera."
        return self.iso.request_calibration()


def make_handler(hub, service):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *args):   # tat log moi request
            pass

        def send_json(self, code, obj):
            body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            path = self.path.split("?")[0]
            if path == "/":
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(INDEX_HTML)))
                self.end_headers()
                self.wfile.write(INDEX_HTML)
            elif path == "/status":
                self.send_json(200, service.status())
            elif path.endswith(".mjpg") and path[1:-5] in CHANNELS:
                self.stream(path[1:-5])
            else:
                self.send_error(404)

        def do_POST(self):
            path = self.path.split("?")[0]
            if path == "/calibrate":
                ok, msg = service.calibrate()
                self.send_json(202 if ok else 409, {"ok": ok, "msg": msg})
            else:
                self.send_error(404)

        def stream(self, channel):
            self.send_response(200)
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Content-Type", f"multipart/x-mixed-replace; boundary={BOUNDARY}")
            self.end_headers()
            peer = f"{self.client_address[0]}:{self.client_address[1]}"
            print(f"[HTTP] {peer} xem luong {channel}")
            hub.add_client(+1)
            seq = -1
            try:
                while hub.running:
                    seq, jpeg = hub.wait(channel, seq)
                    if jpeg is None:
                        continue
                    self.wfile.write(
                        f"--{BOUNDARY}\r\nContent-Type: image/jpeg\r\n"
                        f"Content-Length: {len(jpeg)}\r\n\r\n".encode("ascii"))
                    self.wfile.write(jpeg)
                    self.wfile.write(b"\r\n")
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass
            finally:
                hub.add_client(-1)
                print(f"[HTTP] {peer} ngat luong {channel}")

    return Handler


def depth_to_colormap(depth_m):
    """Depth (m, 0 = khong hop le) -> anh BGR gia mau JET, cung thang mau voi script ghi."""
    clipped = np.clip(depth_m, DEPTH_MIN_M, DEPTH_MAX_M)
    depth_8 = ((DEPTH_MAX_M - clipped) * (255.0 / (DEPTH_MAX_M - DEPTH_MIN_M))).astype(np.uint8)
    out = cv2.applyColorMap(depth_8, cv2.COLORMAP_JET)
    out[depth_m <= 0] = 0
    return out


# ---------------------------------------------------------------------------
# Nguon anh: D455 that hoac phat lai video. Moi nguon goi on_frame(color_bgr, depth_bgr, z_roi_mm).
# ---------------------------------------------------------------------------
def run_realsense(hub, on_frame, json_path):
    import pyrealsense2 as rs
    from live_max_height import apply_json, find_d455   # cung cach nap cau hinh voi ban do truc tiep

    dev = find_d455(rs.context())
    if dev is None:
        raise SystemExit("Khong tim thay Intel RealSense D455. Kiem tra cap USB 3, nguon va driver.")
    dw, dh, dfps = WIDTH, HEIGHT, FPS
    if json_path is not None and json_path.exists():
        dev, (dw, dh, dfps) = apply_json(dev, json_path)
    else:
        print(f"CANH BAO: khong co {json_path} - dung cai dat mac dinh cua camera"
              " (phai giong luc hieu chinh mat phang).")
    serial = dev.get_info(rs.camera_info.serial_number)
    if DEPTH_EXPOSURE_US is not None:
        sensor = dev.first_depth_sensor()
        sensor.set_option(rs.option.enable_auto_exposure, 0)
        sensor.set_option(rs.option.exposure, float(DEPTH_EXPOSURE_US))

    pipeline = rs.pipeline()
    config = rs.config()
    config.enable_device(serial)
    config.enable_stream(rs.stream.color, WIDTH, HEIGHT, rs.format.bgr8, FPS)
    config.enable_stream(rs.stream.depth, dw, dh, rs.format.z16, dfps)
    profile = pipeline.start(config)
    try:
        depth_scale = profile.get_device().first_depth_sensor().get_depth_scale()
        to_mm = np.float32(depth_scale * 1000.0)
        align = rs.align(rs.stream.color)
        sl = mh.roi_slices((HEIGHT, WIDTH))
        print(f"D455 S/N {serial}: RGB {WIDTH}x{HEIGHT}@{FPS}, depth {dw}x{dh}@{dfps} -> align ve RGB,"
              f" depth scale {depth_scale} m, ROI {mh.ROI}")
        while hub.running:
            frames = align.process(pipeline.wait_for_frames(timeout_ms=5000))
            color_frame, depth_frame = frames.get_color_frame(), frames.get_depth_frame()
            if not color_frame or not depth_frame:
                continue
            color = np.asanyarray(color_frame.get_data())
            z16 = np.asanyarray(depth_frame.get_data())
            z16_roi = z16[sl]
            z_roi = z16_roi.astype(np.float32) * to_mm
            z_roi[z16_roi == 0] = np.nan
            depth_bgr = depth_to_colormap(z16.astype(np.float32) * depth_scale) if hub.has_clients else None
            on_frame(color, depth_bgr, z_roi)
    finally:
        pipeline.stop()


def run_replay(hub, on_frame, rgb_path, depth_path):
    """Phat lai rgb.mp4 + depth_color.mp4 (lap lai) theo dung FPS cua video. Depth giai ma tu mau JET."""
    print("Dang dung bang giai ma depth gia mau (vai giay)...")
    lut = mh.build_jet_lut()
    while hub.running:
        rgb_cap, depth_cap = cv2.VideoCapture(rgb_path), cv2.VideoCapture(depth_path)
        if not rgb_cap.isOpened() or not depth_cap.isOpened():
            raise SystemExit(f"Khong mo duoc video {rgb_path} / {depth_path}")
        period = 1.0 / (rgb_cap.get(cv2.CAP_PROP_FPS) or FPS)
        print(f"Phat lai {rgb_path} + {depth_path} ({1.0 / period:.0f} FPS, lap lai)")
        next_t = time.perf_counter()
        try:
            while hub.running:
                ok1, color = rgb_cap.read()
                ok2, depth_bgr = depth_cap.read()
                if not ok1 or not ok2:
                    break
                sl = mh.roi_slices(depth_bgr.shape[:2])
                on_frame(color, depth_bgr, mh.jet_to_depth_m(depth_bgr[sl], lut) * 1000.0)
                next_t += period
                time.sleep(max(0.0, next_t - time.perf_counter()))
        finally:
            rgb_cap.release()
            depth_cap.release()


def main():
    ap = argparse.ArgumentParser(description="Phat hinh anh D455 (RGB + depth) va tach vat cho GUI qua HTTP.")
    ap.add_argument("--host", default="127.0.0.1",
                    help="dia chi lang nghe (mac dinh 127.0.0.1; 0.0.0.0 = moi may trong mang)")
    ap.add_argument("--port", type=int, default=HTTP_PORT)
    ap.add_argument("--replay", nargs=2, metavar=("RGB_MP4", "DEPTH_MP4"),
                    help="khong dung camera, phat lai 2 video da ghi")
    ap.add_argument("--json", type=Path, default=D455_JSON, help="cau hinh D455 tu RealSense Viewer")
    ap.add_argument("--plane", type=Path, default=None,
                    help="file mat phang bang tai (mac dinh plane_live.json; --replay: plane.json)")
    args = ap.parse_args()

    plane = args.plane or (PLANE_REPLAY if args.replay else PLANE_LIVE)
    hub = FrameHub(CHANNELS)
    service = Service(hub, plane, "replay " + args.replay[1] if args.replay else "live D455")
    server = ThreadingHTTPServer((args.host, args.port), make_handler(hub, service))
    server.daemon_threads = True
    threading.Thread(target=server.serve_forever, daemon=True).start()
    print(f"Camera service: http://{args.host}:{args.port}/  (Ctrl+C de dung)")

    try:
        if args.replay:
            run_replay(hub, service.on_frame, *args.replay)
        else:
            run_realsense(hub, service.on_frame, args.json)
    except KeyboardInterrupt:
        print("\nDung camera service.")
    finally:
        hub.stop()
        server.shutdown()


if __name__ == "__main__":
    main()
