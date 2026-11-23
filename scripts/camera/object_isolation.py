"""Tach vat tren bang tai (object isolation) cho camera service.

Dung nguyen bo xu ly cua max_height.py (thu muc goc du an - KHONG sua file do), giong live_max_height.py:
  mh.Detector : mat phang bang tai co dinh -> ban do chieu cao -> tung vat (dinh cao, hinh chu nhat xoay,
                cham bien ROI)
  mh.Tracker  : ghep vat qua cac frame -> id on dinh, hinh chu nhat + chieu cao lam muot,
                1 dong tong ket khi vat di qua
Lop nay them:
  * hieu chinh theo yeu cau tu GUI (luong HTTP goi request_calibration(), luong camera thuc hien),
  * ve len anh RGB + ban do chieu cao de xem tren GUI,
  * trang thai (JSON) cho GET /status.

Toa do o day van la PIXEL anh mau (depth da align theo RGB) + chieu cao (mm) so voi bang tai.
Quy doi sang toa do robot la buoc sau.
"""

import sys
import threading
import time
from collections import deque
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parent.parent      # thu muc du an (chua max_height.py, plane*.json)
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))
import max_height as mh  # noqa: E402

CALIB_FRAMES = 30          # so frame gom khi hieu chinh (giong live_max_height.py)
HEIGHT_VIS_SCALE = 2       # phong to ban do chieu cao (ROI nho) cho de nhin tren GUI
PASSED_KEEP = 20           # so vat da di qua giu lai trong /status


def _long_side(w, h, a):
    """Hinh chu nhat OpenCV (w, h, goc) -> (dai, rong, goc canh dai trong (-90, 90])."""
    if w < h:
        w, h, a = h, w, a + 90.0
    return w, h, (a + 90.0) % 180.0 - 90.0


class ObjectIsolator:
    """Chay trong luong camera: process() moi frame. Cac ham con lai an toan goi tu luong khac."""

    def __init__(self, shape, plane_file, source):
        self.shape = tuple(shape)            # (h, w) anh mau
        self.plane_file = Path(plane_file)
        self.source = source
        self.sl = mh.roi_slices(self.shape)
        self.y0, self.x0 = self.sl[0].start, self.sl[1].start
        self.roi_h = self.sl[0].stop - self.sl[0].start
        self.roi_w = self.sl[1].stop - self.sl[1].start

        self._lock = threading.Lock()
        self._calib = None                   # list frame dang gom, None = khong hieu chinh
        self._calib_requested = False
        self._last_error = ""
        self._det = None
        self._plane = None
        self._tracker = mh.Tracker()
        self._passed = deque(maxlen=PASSED_KEEP)
        self._status = {}
        self._frames = 0
        self._fps = 0.0
        self._t_last = None
        self._proc_ms = 0.0

        if self.plane_file.exists():
            try:
                self._set_plane(mh.load_plane(self.shape, self.plane_file))
                print(f"[ISO] Dung mat phang {self.plane_file.name} ({self._plane['time']}, "
                      f"cach {self._plane['distance_mm']:.0f} mm, nghieng {self._plane['tilt_deg']:.2f} deg)")
            except SystemExit as exc:          # load_plane bao loi bang SystemExit
                self._last_error = str(exc)
                print(f"[ISO] {exc}")
        else:
            print(f"[ISO] Chua co {self.plane_file.name}: de bang tai TRONG roi bam 'Hieu chinh' tren GUI.")

    # ------------------------------------------------------------------ luong HTTP
    def request_calibration(self):
        """Yeu cau hieu chinh lai mat phang bang tai (bang tai phai TRONG). Tra ve (ok, thong bao)."""
        with self._lock:
            if self._calib is not None or self._calib_requested:
                return False, "Dang hieu chinh."
            self._calib_requested = True
        print(f"[ISO] Hieu chinh: gom {CALIB_FRAMES} frame, giu bang tai trong...")
        return True, f"Bat dau hieu chinh ({CALIB_FRAMES} frame)."

    def status(self):
        with self._lock:
            return dict(self._status)

    # ------------------------------------------------------------------ luong camera
    def _set_plane(self, plane):
        self._plane = plane
        self._det = mh.Detector(plane, self.shape)
        self._tracker = mh.Tracker()          # mat phang moi -> theo doi lai tu dau (giong live_max_height)

    def process(self, z_roi_mm):
        """z_roi_mm: depth (mm, float32) CHI vung ROI, NaN = khong hop le.
        Tra ve ket qua mh.Detector.process() (them "tracks": vat dang hien thi) hoac None."""
        t0 = time.perf_counter()
        self._frames += 1
        with self._lock:
            if self._calib_requested:
                self._calib_requested = False
                self._calib = []
                self._last_error = ""
            calib = self._calib

        result = None
        if calib is not None:
            calib.append(z_roi_mm)
            if len(calib) >= CALIB_FRAMES:
                try:
                    plane = mh.fit_and_save_plane(calib, self.shape, self.source,
                                                  [self._frames - len(calib) + 1, self._frames],
                                                  self.plane_file)
                    self._set_plane(plane)
                except RuntimeError as exc:
                    self._last_error = f"Hieu chinh that bai: {exc}"
                    print(f"[ISO] {self._last_error}")
                with self._lock:
                    self._calib = None
        elif self._det is not None:
            result = self._det.process(z_roi_mm)
            for summ in self._tracker.update(result["objects"], self._frames):
                print(f"[ISO] {mh.format_summary(summ)}")
                self._passed.appendleft(summ)
            result["tracks"] = self._tracker.visible()

        self._update_stats(t0, result)
        return result

    @staticmethod
    def _track_info(t):
        o = t.obj                                  # do do cua frame gan nhat
        length, width, ang = _long_side(t.w, t.h, t.a)
        return {
            "id": t.id,
            "u": round(float(t.cx), 1), "v": round(float(t.cy), 1),        # tam da loc (pixel)
            "angle_deg": round(float(ang), 1),
            "len_px": round(float(length), 1), "wid_px": round(float(width), 1),
            "area_px": o["px"],
            "h_mm": round(t.height_now(), 1),                                # trung vi N frame gan nhat
            "h_raw_mm": round(o["h"], 1),                                    # frame nay
            "h_p99_mm": round(o["hp"], 1),
            "peak_u": o["x"], "peak_v": o["y"],                              # diem cao nhat (pixel)
            "z_mm": round(o["z"], 1),                                        # depth tai dinh
            "edge": o["border"],                                             # cham bien ROI
            "vx_px": round(float(t.vx), 2), "vy_px": round(float(t.vy), 2),  # pixel / frame
            "frames": len(t.heights),
        }

    @staticmethod
    def _passed_info(s):
        return {"id": s["id"], "h_mm": round(s["h"], 1), "p10_mm": round(s["p10"], 1),
                "p90_mm": round(s["p90"], 1), "n": s["n"], "partial": s["partial"],
                "first": s["frames"][0], "last": s["frames"][1]}

    def _update_stats(self, t0, result):
        now = time.perf_counter()
        dt = (now - self._t_last) if self._t_last else 0.0
        self._t_last = now
        if dt > 0:
            self._fps = 0.9 * self._fps + 0.1 / dt if self._fps else 1.0 / dt
        self._proc_ms = 0.95 * self._proc_ms + 0.05 * 1000 * (now - t0) if self._frames > 1 else 1000 * (now - t0)

        plane = self._plane
        drift = float(self._det.offset) if self._det is not None else 0.0
        tracks = [self._track_info(t) for t in (result["tracks"] if result else [])]
        bv = self._tracker.belt_v
        with self._lock:
            calib = self._calib
            self._status = {
                "source": self.source,
                "frame": self._frames,
                "fps": round(self._fps, 1),
                "proc_ms": round(self._proc_ms, 1),
                "roi": list(mh.ROI) if mh.ROI is not None else None,
                "calibrated": plane is not None,
                "calibrating": None if calib is None else {"done": len(calib), "total": CALIB_FRAMES},
                "plane": None if plane is None else {
                    "file": self.plane_file.name, "time": plane["time"],
                    "distance_mm": round(plane["distance_mm"], 1), "tilt_deg": round(plane["tilt_deg"], 2),
                    "rms_mm": round(plane["rms_mm"], 2)},
                "drift_mm": round(drift, 2),
                "drift_warn": abs(drift) > mh.RECAL_WARN_MM,
                "belt_v_px": [round(float(bv[0]), 2), round(float(bv[1]), 2)],   # hoc tu cac vat (pixel/frame)
                "error": self._last_error,
                "t": time.time(),
                "objects": tracks,                                            # vat dang trong ROI
                "passed": [self._passed_info(s) for s in self._passed],       # vat vua di qua, moi nhat truoc
            }

    # ------------------------------------------------------------------ hien thi
    def draw(self, color, result):
        """Ve ROI, hinh chu nhat da lam muot + id + chieu cao (mh.draw_tracks) len ban sao anh mau."""
        out = color.copy()
        st = self.status()
        if mh.ROI is not None:
            cv2.rectangle(out, tuple(mh.ROI[:2]), tuple(mh.ROI[2:]), (0, 255, 0), 1)
            cv2.line(out, (mh.TRIGGER_LINE, mh.ROI[1]),(mh.TRIGGER_LINE, mh.ROI[3]),(0,255,0), 1, cv2.LINE_AA)
        if result is not None:
            mh.draw_tracks(out, result["tracks"])
        if st.get("calibrating"):
            c = st["calibrating"]
            text, col = f"HIEU CHINH {c['done']}/{c['total']} - giu bang tai trong", (0, 255, 255)
        elif not st.get("calibrated"):
            text, col = "CHUA HIEU CHINH - bang tai trong roi bam Hieu chinh", (0, 0, 255)
        else:
            text = f"{st['fps']:4.1f} fps | xu ly {st['proc_ms']:4.1f} ms | troi {st['drift_mm']:+.1f} mm"
            col = (0, 0, 255) if st["drift_warn"] else (0, 255, 255)
        cv2.putText(out, text, (10, 25), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 0, 0), 3)
        cv2.putText(out, text, (10, 25), cv2.FONT_HERSHEY_SIMPLEX, 0.55, col, 1)
        return out

    def height_view(self, result):
        """Ban do chieu cao vung ROI (0..HEIGHT_VIS_MAX_MM, TURBO) + vien vat tho (trang)
        + hinh chu nhat da lam muot, phong to de xem."""
        s = HEIGHT_VIS_SCALE
        if result is None:
            out = np.zeros((self.roi_h * s, self.roi_w * s, 3), np.uint8)
            cv2.putText(out, "chua hieu chinh" if self._plane is None else "dang hieu chinh",
                        (6, 20), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (200, 200, 200), 1)
            return out
        out = mh.height_to_colormap(result["height"], result["valid"])
        out = cv2.resize(out, None, fx=s, fy=s, interpolation=cv2.INTER_NEAREST)
        cnts, _ = cv2.findContours(result["obj"].astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        cv2.drawContours(out, [c * s + s // 2 for c in cnts], -1, (255, 255, 255), 1)   # mask tho frame nay
        off = np.array([self.x0, self.y0], np.float32)
        for t in result["tracks"]:
            box = ((t.box() - off) * s).astype(np.int32)
            col = (0, 165, 255) if t.obj["border"] else (0, 0, 255)          # cam = cham bien ROI
            cv2.polylines(out, [box], True, col, 2)
            label = f"#{t.id} {t.height_now():.1f}mm"
            (tw, th), _ = cv2.getTextSize(label, cv2.FONT_HERSHEY_SIMPLEX, 0.45, 1)
            org = (max(2, min(int(box[:, 0].min()), out.shape[1] - tw - 2)), max(th + 2, int(box[:, 1].min()) - 6))
            cv2.putText(out, label, org, cv2.FONT_HERSHEY_SIMPLEX, 0.45, (0, 0, 0), 3)
            cv2.putText(out, label, org, cv2.FONT_HERSHEY_SIMPLEX, 0.45, (255, 255, 255), 1)
        return out
