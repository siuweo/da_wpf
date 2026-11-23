"""Do chieu cao lon nhat cua vat so voi mat bang tai, tu video depth (+ RGB) cua D455.
Ban thoi gian thuc: KHONG RANSAC, KHONG anh nen.

Mat phang bang tai duoc do 1 LAN (hieu chinh) roi luu vao plane.json.
Moi frame chi can:
  1. Chieu cao = khoang cach vuong goc den mat phang co dinh (tu bu do nghieng camera).
  2. Vat = pixel cao hon mat phang > HEIGHT_THRESH_MM, gop manh, lap lo.
     Canh depth (buoc nhay lon) giup khep vien vat; pixel tren canh bi loai khi lay max.
  3. Chieu cao lon nhat lay tren ban do da loc trung vi (1 pixel nhieu khong day max len).
Chi xu ly vung ROI -> nhanh hon ~5 lan so voi ca frame.

Buoc 1 - hieu chinh (1 lan, bang tai TRONG, khong co vat, khong co tay):
  python max_height.py --calibrate                      (30 frame dau cua depth_color.mp4)
  python max_height.py --calibrate --frame 1000 --n 60  (60 frame tu frame 1000)
  -> luu plane.json. Phai hieu chinh lai neu camera bi di chuyen / va cham.
Buoc 2 - do:
  python max_height.py                                  (depth_color.mp4 + rgb.mp4)
  python max_height.py depth_raw_16bit rgb.mp4
  python max_height.py --no-show

Phim: SPACE/P tam dung | N frame ke tiep khi tam dung | Q/ESC thoat.
DEPTH_MIN_M / DEPTH_MAX_M phai GIONG HET luc ghi video gia mau.
Dung voi camera truc tiep: xem class Detector (process() nhan depth tinh bang mm, vung ROI).
"""

import argparse
import json
import time
from pathlib import Path

import cv2
import numpy as np

# ====================== CAU HINH ======================
DEPTH_MIN_M = 0.1          # Phai giong script ghi
DEPTH_MAX_M = 0.5          # Phai giong script ghi
DEPTH_SCALE = 0.001        # m / don vi Z16 (xem dong "Depth scale" khi ghi), cho PNG 16-bit
ROI = (251, 45, 380, 392)  # Vung xet (x1, y1, x2, y2), tinh ca bien. None = ca frame
HFOV_DEG = 90.0            # FOV ngang camera RGB D455 (depth da align theo RGB), xap xi
TRIGGER_LINE = 450
PLANE_FILE = Path("plane.json")

# Hieu chinh mat phang
INLIER_MM = 2.0            # Diem cach mat phang < bay nhieu mm -> thuoc be mat
RECAL_WARN_MM = 3.0        # Be mat lech khoi mat phang da luu > muc nay -> canh bao hieu chinh lai
DRIFT_ALPHA = 0.05         # Toc do bam theo do troi cham cua be mat (0 = tat). Bu nhiet camera, rung bang tai

# Vat
HEIGHT_THRESH_MM = 2.5     # Pixel cao hon mat phang > muc nay -> thuoc vat (de ve mask)
MIN_PEAK_MM = 8.0          # Chi bao co vat neu dinh cao >= muc nay (bo go/nhan tren bang tai)
EDGE_STEP_MM = 4.0         # Buoc nhay depth giua 2 pixel lien ke > muc nay -> canh
MIN_BLOB_AREA = 80         # Bo vat (sau khi gop manh) nho hon (pixel)
SPECK_NEIGHBORS = 3        # Pixel cao phai co >= bay nhieu pixel cao trong o 3x3 (bo dom le, giu vanh mong)
SHADOW_LINK = 2            # Lo depth (bong stereo) cach vung cao <= bay nhieu pixel -> tinh la vien vat
GROUP_KERNEL = 9           # Gop cac manh gan nhau thanh 1 vat (vd. vanh coc mong bi dut)
SMOOTH_KERNEL = 5          # Loc trung vi truoc khi lay max (3 hoac 5)
TOP_PERCENTILE = 99        # In them chieu cao o phan vi nay
APPROX_EPS = 0.02          # Lam gon vien vat (ti le chu vi). 0 = giu nguyen vien

# Theo doi vat qua cac frame (lam muot hinh dang + chieu cao, 1 ket qua / vat)
TRACK_MAX_DIST = 30        # Vat moi cach vi tri du doan <= bay nhieu pixel -> cung 1 vat
TRACK_MAX_MISSES = 5       # Mat dau bay nhieu frame lien tiep -> vat da di qua
TRACK_MIN_FRAMES = 3       # Vat xuat hien it hon -> bo qua (nhieu, tay luot qua)
POS_ALPHA, POS_BETA = 0.4, 0.1  # Loc alpha-beta cho tam vat (bam theo bang tai, it tre)
SHAPE_ALPHA = 0.2          # Lam muot kich thuoc / goc cua hinh chu nhat
HEIGHT_WINDOW = 9          # Chieu cao hien thi = trung vi N frame gan nhat
PRINT_FRAMES = True        # In dong ket qua moi frame (False: chi in tong ket moi vat)

# Hien thi
HEIGHT_VIS_MAX_MM = 30.0   # Thang mau ban do chieu cao 0..muc nay

# Giai ma video gia mau
JET_MAX_DIST = 60          # Mau lech qua xa thang JET -> depth khong hop le
DARK_THRESH = 50           # Pixel gan den (depth = 0 luc ghi) -> khong hop le
# ======================================================


# ---------------- Giai ma depth ----------------
def build_jet_lut():
    """Bang tra 64x64x64 (BGR >> 2) -> (chi so JET 0-255, khoang cach mau)."""
    jet = cv2.applyColorMap(np.arange(256, dtype=np.uint8).reshape(-1, 1), cv2.COLORMAP_JET)
    jet = jet.reshape(256, 3).astype(np.int32)
    levels = np.arange(64) * 4 + 2
    b, g, r = np.meshgrid(levels, levels, levels, indexing="ij")
    cells = np.stack([b.ravel(), g.ravel(), r.ravel()], axis=1)
    idx = np.empty(len(cells), np.uint8)
    dist = np.empty(len(cells), np.float32)
    step = 8192
    for s in range(0, len(cells), step):
        d2 = ((cells[s:s + step, None, :] - jet[None]) ** 2).sum(axis=2)
        best = d2.argmin(axis=1)
        idx[s:s + step] = best
        dist[s:s + step] = np.sqrt(d2[np.arange(len(best)), best])
    return idx.reshape(64, 64, 64), dist.reshape(64, 64, 64)


def jet_to_depth_m(bgr, lut):
    """Anh gia mau JET -> depth (m), NaN = khong hop le. Nguoc voi depth_to_colormap khi ghi."""
    idx_lut, dist_lut = lut
    q = bgr >> 2
    b, g, r = q[..., 0], q[..., 1], q[..., 2]
    idx = idx_lut[b, g, r].astype(np.float32)
    depth = DEPTH_MAX_M - idx * ((DEPTH_MAX_M - DEPTH_MIN_M) / 255.0)
    invalid = (bgr.max(axis=2) < DARK_THRESH) | (dist_lut[b, g, r] > JET_MAX_DIST)
    depth[invalid] = np.nan
    return depth


def z16_to_depth_m(z16):
    depth = z16.astype(np.float32) * DEPTH_SCALE
    depth[z16 == 0] = np.nan
    return depth


def depth_frames(path, lut, sl=None):
    """Sinh tung frame depth (m). sl: (slice_y, slice_x) -> chi giai ma vung nay (nhanh hon)."""
    sl = sl or (slice(None), slice(None))
    if path.is_dir():
        files = sorted(path.glob("*.png"))
        if not files:
            raise RuntimeError(f"Khong co file PNG trong {path}")
        for f in files:
            z16 = cv2.imread(str(f), cv2.IMREAD_UNCHANGED)
            if z16 is None or z16.dtype != np.uint16:
                raise RuntimeError(f"{f.name} khong phai PNG depth 16-bit")
            yield z16_to_depth_m(z16[sl])
    else:
        cap = cv2.VideoCapture(str(path))
        if not cap.isOpened():
            raise RuntimeError(f"Khong mo duoc video {path}")
        try:
            while True:
                ok, frame = cap.read()
                if not ok:
                    break
                yield jet_to_depth_m(frame[sl], lut)
        finally:
            cap.release()


def frame_size(path):
    """(h, w) cua nguon depth."""
    if path.is_dir():
        f = next(iter(sorted(path.glob("*.png"))), None)
        img = cv2.imread(str(f), cv2.IMREAD_UNCHANGED) if f else None
        if img is None:
            raise SystemExit(f"Khong doc duoc PNG trong {path}")
        return img.shape[:2]
    cap = cv2.VideoCapture(str(path))
    if not cap.isOpened():
        raise SystemExit(f"Khong mo duoc video {path}")
    size = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT)), int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    cap.release()
    return size


def roi_slices(shape):
    """ROI -> (slice_y, slice_x), da cat theo kich thuoc frame."""
    h, w = shape
    if ROI is None:
        return slice(0, h), slice(0, w)
    x1, y1, x2, y2 = ROI
    return slice(max(y1, 0), min(y2 + 1, h)), slice(max(x1, 0), min(x2 + 1, w))


# ---------------- Mat phang (hinh hoc) ----------------
def plane_geometry(coef, shape):
    """Mat phang 1/Z = a*u + b*v + c (u, v pixel ca frame) -> (d_mm, phap tuyen don vi n)."""
    h, w = shape
    f = (w / 2) / np.tan(np.radians(HFOV_DEG / 2))
    a, b, c = coef
    nd = np.array([a * f, b * f, c + a * w / 2 + b * h / 2])  # = n / d
    d = 1.0 / np.linalg.norm(nd)
    return d, nd * d


def robust_plane(u, v, z):
    """Binh phuong toi thieu, lap lai bo diem ngoai lai (vat, lo, nhieu). Khong RANSAC."""
    M = np.column_stack([u, v, np.ones_like(u)])
    inv = 1.0 / z
    keep = np.ones(len(z), dtype=bool)
    for _ in range(8):
        coef, *_ = np.linalg.lstsq(M[keep], inv[keep], rcond=None)
        res = 1.0 / (M @ coef) - z  # mm, >0: gan camera hon (cao hon mat phang)
        mad = 1.4826 * np.median(np.abs(res[keep]))
        new_keep = np.abs(res) < max(3 * mad, INLIER_MM)
        if np.array_equal(new_keep, keep):
            break
        keep = new_keep
    return coef, res, keep


def fit_and_save_plane(crops_mm, shape, source, frames, plane_file=None):
    """Fit mat phang tu cac frame bang tai trong (vung ROI, mm, NaN = khong hop le) va luu file.

    Dung chung cho hieu chinh tu video va tu camera truc tiep. Tra ve dict da luu.
    """
    plane_file = plane_file or PLANE_FILE
    sl = roi_slices(shape)
    y0, x0 = sl[0].start, sl[1].start
    us, vs, zs = [], [], []
    for z in crops_mm:
        yy, xx = np.nonzero(~np.isnan(z))
        pick = slice(None, None, 4)  # Lay 1/4 diem moi frame la du
        us.append(xx[pick] + x0)
        vs.append(yy[pick] + y0)
        zs.append(z[yy[pick], xx[pick]])
    u, v, z = (np.concatenate(a).astype(np.float64) for a in (us, vs, zs))
    if len(z) < 500:
        raise RuntimeError("Qua it diem depth hop le trong ROI de hieu chinh.")
    coef, res, keep = robust_plane(u, v, z)

    d, nrm = plane_geometry(coef, shape)
    tilt = float(np.degrees(np.arccos(abs(nrm[2]))))
    zfit = 1.0 / (coef[0] * u + coef[1] * v + coef[2])
    rms = float(np.sqrt(np.mean(res[keep] ** 2)))
    data = {"coef": [float(c) for c in coef], "shape": list(shape), "roi": ROI,
            "tilt_deg": tilt, "distance_mm": float(d), "rms_mm": rms,
            "source": str(source), "frames": frames,
            "time": time.strftime("%Y-%m-%d %H:%M:%S")}
    Path(plane_file).write_text(json.dumps(data, indent=2))

    print(f"Hieu chinh tu {len(crops_mm)} frame ({source})")
    print(f"  Diem dung de fit: {keep.sum()} / {len(z)} ({100 * keep.mean():.0f}%)")
    print(f"  Khoang cach camera -> bang tai: {d:.1f} mm | nghieng {tilt:.2f} deg")
    print(f"  Z cua bang tai trong ROI: {zfit.min():.1f} - {zfit.max():.1f} mm "
          f"(chenh {np.ptp(zfit):.1f} mm do nghieng -> 1 nguong Z co dinh se sai chung nay)")
    print(f"  Do gho ghe be mat (RMS): {rms:.2f} mm")
    if keep.mean() < 0.8:
        print("  CANH BAO: >20% diem bi loai - co the co vat/tay trong cac frame hieu chinh.")
    print(f"Da luu {plane_file}")
    return data


def calibrate(path, lut, start, n):
    shape = frame_size(path)
    sl = roi_slices(shape)
    crops = []
    for i, dm in enumerate(depth_frames(path, lut, sl)):
        if i < start:
            continue
        if i >= start + n:
            break
        crops.append(dm * 1000.0)
    if not crops:
        raise SystemExit(f"Khong co frame {start} trong {path}")
    fit_and_save_plane(crops, shape, path, [start, start + len(crops) - 1])


def load_plane(shape, plane_file=None):
    plane_file = Path(plane_file or PLANE_FILE)
    if not plane_file.exists():
        raise SystemExit(f"Chua co {plane_file}. Chay truoc: python max_height.py --calibrate")
    data = json.loads(plane_file.read_text())
    if tuple(data["shape"]) != tuple(shape):
        raise SystemExit(f"{plane_file} hieu chinh cho frame {data['shape']}, nguon hien tai {list(shape)}."
                         " Hieu chinh lai.")
    if data.get("roi") is not None and ROI is not None and tuple(data["roi"]) != tuple(ROI):
        print(f"Canh bao: ROI da doi tu luc hieu chinh {data['roi']} -> nen hieu chinh lai.")
    return data


# ---------------- Xu ly 1 frame ----------------
def fill_holes(mask):
    """Lap kin ben trong moi vien ngoai."""
    m = mask.astype(np.uint8) * 255
    contours, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    out = np.zeros_like(m)
    if APPROX_EPS > 0:
        contours = [cv2.approxPolyDP(c, APPROX_EPS * cv2.arcLength(c, True), True) for c in contours]
    if contours:
        cv2.drawContours(out, contours, -1, 255, thickness=cv2.FILLED)
    return out > 0


class Detector:
    """Do chieu cao trong ROI voi mat phang co dinh. Dung duoc cho video hoac camera truc tiep."""

    def __init__(self, plane, shape):
        self.sl = roi_slices(shape)
        self.y0, self.x0 = self.sl[0].start, self.sl[1].start
        coef = plane["coef"]
        d, _ = plane_geometry(coef, shape)
        self.d = float(d)  # float Python -> phep nhan giu float32
        self.tilt = plane["tilt_deg"]
        vv, uu = np.mgrid[self.sl].astype(np.float32)
        self.inv_fit = (coef[0] * uu + coef[1] * vv + coef[2]).astype(np.float32)  # 1/Z mat phang
        self.k_link = np.ones((2 * SHADOW_LINK + 1, 2 * SHADOW_LINK + 1), np.uint8)
        self.k_group = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (GROUP_KERNEL, GROUP_KERNEL))
        self.offset = 0.0  # Do troi hien tai cua be mat so voi mat phang da luu (mm)

    def process(self, z):
        """z: depth (mm, float32) CHI vung ROI, NaN = khong hop le. Tra ve dict ket qua."""
        valid = ~np.isnan(z)
        z0 = np.where(valid, z, 0).astype(np.float32)
        height = (self.d * (1.0 - z0 * self.inv_fit)).astype(np.float32, copy=False)  # mm, >0 = phia camera
        height -= np.float32(self.offset)  # Bu do troi (uoc luong tu cac frame truoc)
        height[~valid] = 0

        # Canh depth
        zb = cv2.medianBlur(z0, 5)
        gx = cv2.Sobel(zb, cv2.CV_32F, 1, 0, ksize=3) / 8.0
        gy = cv2.Sobel(zb, cv2.CV_32F, 0, 1, ksize=3) / 8.0
        near_invalid = cv2.dilate((~valid).astype(np.uint8), np.ones((5, 5), np.uint8)) > 0
        edges = (cv2.magnitude(gx, gy) > EDGE_STEP_MM) & ~near_invalid

        # Vat: nguong chieu cao -> bo dom le (giu vanh mong) -> noi bong stereo + canh lan can
        #      -> gop manh -> lap lo
        tall = (valid & (height > HEIGHT_THRESH_MM)).astype(np.uint8)
        count = cv2.boxFilter(tall, cv2.CV_16U, (3, 3), normalize=False)
        tall = tall & (count >= SPECK_NEIGHBORS).astype(np.uint8)
        near = cv2.dilate(tall, self.k_link) > 0
        # Vien vat thuong la vong lo depth (bong stereo) hoac canh depth -> dung de khep vien
        shape = (tall > 0) | (near & (~valid | edges))
        shape = cv2.morphologyEx(shape.astype(np.uint8), cv2.MORPH_CLOSE, self.k_group) > 0
        shape = fill_holes(shape)
        n, labels, stats, _ = cv2.connectedComponentsWithStats(shape.astype(np.uint8), connectivity=8)
        tall_count = np.bincount(labels.ravel(), weights=tall.ravel(), minlength=n)
        keep = np.zeros(n, dtype=bool)
        keep[1:] = (stats[1:, cv2.CC_STAT_AREA] >= MIN_BLOB_AREA) & (tall_count[1:] >= MIN_BLOB_AREA // 4)
        obj = keep[labels]  # Hinh vat day du (gom ca bong); do chieu cao chi dung pixel hop le

        # Do troi: be mat (khong phai vat) nam cach mat phang bao nhieu -> cap nhat cham
        surf = valid & ~obj & (np.abs(height) < 3 * INLIER_MM)
        surf_vals = height[surf][::4]
        if len(surf_vals) > 500:
            self.offset += DRIFT_ALPHA * float(np.median(surf_vals))
        result = {"height": height, "valid": valid, "edges": edges, "obj": obj,
                  "objects": [], "best": None, "surf_offset": self.offset}
        ids = np.nonzero(keep)[0]
        if len(ids) == 0:
            return result

        # Tung vat: dinh cao (ban do loc trung vi, bo pixel tren canh), hinh chu nhat xoay
        edge_band = cv2.dilate(edges.astype(np.uint8), np.ones((3, 3), np.uint8)) > 0
        smooth = cv2.medianBlur(height, SMOOTH_KERNEL)
        rh, rw = obj.shape
        for k in ids:
            x, y, w, h = stats[k, :4]
            comp = labels[y:y + h, x:x + w] == k
            v = valid[y:y + h, x:x + w]
            pick = comp & v & ~edge_band[y:y + h, x:x + w]
            if not pick.any():
                pick = comp & v
            if not pick.any():
                continue
            ys, xs = np.nonzero(pick)
            hs = smooth[y:y + h, x:x + w][ys, xs]
            j = int(hs.argmax())
            if hs[j] < MIN_PEAK_MM:
                continue  # Chi la go / nhieu tren be mat
            cy, cx = np.nonzero(comp)
            pts = np.column_stack([cx + x + self.x0, cy + y + self.y0]).astype(np.float32)
            result["objects"].append({
                "h": float(hs[j]), "x": int(xs[j] + x) + self.x0, "y": int(ys[j] + y) + self.y0,
                "z": float(z0[y + ys[j], x + xs[j]]),
                "hp": float(np.percentile(height[y:y + h, x:x + w][ys, xs], TOP_PERCENTILE)),
                "px": int(comp.sum()),
                "rect": cv2.minAreaRect(pts),  # ((cx, cy), (w, h), goc) toa do ca frame
                "border": bool(x == 0 or y == 0 or x + w >= rw or y + h >= rh),  # cham bien ROI
            })
        result["objects"].sort(key=lambda o: -o["h"])
        if result["objects"]:
            result["best"] = result["objects"][0]
        return result


# ---------------- Theo doi vat qua cac frame ----------------
class Track:
    """1 vat dang di qua ROI: tam (loc alpha-beta), kich thuoc/goc (EMA), lich su chieu cao."""

    def __init__(self, tid, obj, frame, v0=(0.0, 0.0)):
        (cx, cy), (w, h), a = obj["rect"]
        self.id, self.first, self.last = tid, frame, frame
        self.cx, self.cy = cx, cy
        self.vx, self.vy = v0  # Bat dau voi van toc bang tai da biet
        self.w, self.h, self.a = w, h, a
        self.misses = 0
        self.heights = []   # (chieu cao, cham bien ROI?)
        self.obj = obj      # Do do cua frame gan nhat
        self._add(obj)

    def _add(self, obj):
        self.heights.append((obj["h"], obj["border"]))
        self.obj = obj

    def predict(self):
        return self.cx + self.vx, self.cy + self.vy

    def update(self, obj, frame):
        (rx, ry), (w, h), a = obj["rect"]
        px, py = self.predict()
        ex, ey = rx - px, ry - py
        self.cx, self.cy = px + POS_ALPHA * ex, py + POS_ALPHA * ey
        self.vx += POS_BETA * ex
        self.vy += POS_BETA * ey
        # Hinh chu nhat (w, h, a) == (h, w, a + 90): chon cach doc gan goc hien tai nhat
        d = a - self.a
        k = round(d / 90.0)
        d -= 90.0 * k
        if k % 2:
            w, h = h, w
        self.a += SHAPE_ALPHA * d
        self.w += SHAPE_ALPHA * (w - self.w)
        self.h += SHAPE_ALPHA * (h - self.h)
        self.misses = 0
        self.last = frame
        self._add(obj)

    def coast(self):
        """Khong thay vat frame nay: di tiep theo van toc du doan."""
        self.cx, self.cy = self.predict()
        self.misses += 1

    def height_now(self):
        """Chieu cao hien thi: trung vi N frame gan nhat (uu tien frame khong cham bien ROI)."""
        recent = self.heights[-HEIGHT_WINDOW:]
        inside = [h for h, b in recent if not b]
        return float(np.median(inside or [h for h, _ in recent]))

    def summary(self):
        """Ket qua cuoi cho vat: chi dung frame vat nam tron trong ROI neu co du."""
        inside = [h for h, b in self.heights if not b]
        hs = np.array(inside if len(inside) >= TRACK_MIN_FRAMES else [h for h, _ in self.heights])
        return {"id": self.id, "h": float(np.median(hs)), "p10": float(np.percentile(hs, 10)),
                "p90": float(np.percentile(hs, 90)), "n": len(hs), "partial": len(inside) < TRACK_MIN_FRAMES,
                "frames": (self.first, self.last)}

    def box(self):
        return cv2.boxPoints(((self.cx, self.cy), (self.w, self.h), self.a)).astype(np.int32)


class Tracker:
    def __init__(self):
        self.tracks, self.next_id = [], 1
        self.belt_v = np.zeros(2)  # Van toc bang tai (pixel/frame), hoc tu cac vat da theo doi

    def update(self, objects, frame):
        """Ghep vat cua frame nay voi cac vat dang theo doi. Tra ve danh sach vat vua di qua (da xong)."""
        pairs = []
        for ti, t in enumerate(self.tracks):
            px, py = t.predict()
            for oi, o in enumerate(objects):
                d = np.hypot(o["rect"][0][0] - px, o["rect"][0][1] - py)
                if d <= TRACK_MAX_DIST:
                    pairs.append((d, ti, oi))
        used_t, used_o = set(), set()
        for d, ti, oi in sorted(pairs):
            if ti in used_t or oi in used_o:
                continue
            self.tracks[ti].update(objects[oi], frame)
            used_t.add(ti)
            used_o.add(oi)
        for ti, t in enumerate(self.tracks):
            if ti not in used_t:
                t.coast()
            elif len(t.heights) >= 5:
                self.belt_v += 0.05 * (np.array([t.vx, t.vy]) - self.belt_v)
        for oi, o in enumerate(objects):
            if oi in used_o:
                continue
            # Manh vo cua 1 vat dang theo doi (vat bi tach lam 2 vai frame) -> khong tao vat moi
            cx, cy = o["rect"][0]
            if any(np.hypot(cx - t.cx, cy - t.cy) <= TRACK_MAX_DIST for t in self.tracks):
                continue
            self.tracks.append(Track(self.next_id, o, frame, tuple(self.belt_v)))
            self.next_id += 1
        done = [t for t in self.tracks if t.misses > TRACK_MAX_MISSES]
        self.tracks = [t for t in self.tracks if t.misses <= TRACK_MAX_MISSES]
        return [t.summary() for t in done if len(t.heights) >= TRACK_MIN_FRAMES]

    def flush(self):
        """Ket thuc video: tra ve tong ket cac vat con dang theo doi."""
        done, self.tracks = self.tracks, []
        return [t.summary() for t in done if len(t.heights) >= TRACK_MIN_FRAMES]

    def visible(self):
        """Vat dang hien thi (da xuat hien du so frame, chua mat dau)."""
        return [t for t in self.tracks if len(t.heights) >= TRACK_MIN_FRAMES and t.misses == 0]


def format_summary(s):
    note = " (chi thay 1 phan, cham bien ROI)" if s["partial"] else ""
    return (f"=> VAT #{s['id']}: cao {s['h']:.1f} mm  (p10-p90 {s['p10']:.1f}-{s['p90']:.1f},"
            f" {s['n']} frame, frame {s['frames'][0]}-{s['frames'][1]}){note}")


def draw_tracks(img, tracks, offset=(0, 0)):
    """Ve hinh chu nhat da lam muot + id + chieu cao. offset: tru di neu img la anh ROI."""
    ox, oy = offset
    for t in tracks:
        box = t.box() - np.array([ox, oy], np.int32)
        cv2.polylines(img, [box], True, (0, 0, 255), 2)
        x, y = box[:, 0].min(), box[:, 1].min()
        cv2.putText(img, f"#{t.id} {t.height_now():.1f} mm", (int(x), int(max(y - 6, 12))),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 255), 2)


# ---------------- Hien thi ----------------
def height_to_colormap(height, valid):
    h8 = (np.clip(height, 0, HEIGHT_VIS_MAX_MM) * (255.0 / HEIGHT_VIS_MAX_MM)).astype(np.uint8)
    out = cv2.applyColorMap(h8, cv2.COLORMAP_TURBO)
    out[~valid] = 0
    return out


def wait_key(paused):
    """Tra ve (paused, quit). SPACE/P: tam dung, N: frame ke tiep, Q/ESC: thoat."""
    while True:
        key = cv2.waitKey(30 if paused else 1) & 0xFF
        if key in (ord("q"), 27):
            return paused, True
        if key in (ord(" "), ord("p")):
            paused = not paused
            print("|| Tam dung (SPACE: tiep tuc, N: frame ke tiep)" if paused else "> Tiep tuc")
            if not paused:
                return paused, False
        elif key == ord("n") and paused:
            return paused, False
        elif not paused:
            return paused, False


def main():
    ap = argparse.ArgumentParser(description="In chieu cao lon nhat cua vat so voi bang tai.")
    ap.add_argument("depth", type=Path, nargs="?", default=Path("depth_color.mp4"),
                    help="depth_color.mp4 hoac thu muc PNG 16-bit (mac dinh depth_color.mp4)")
    ap.add_argument("rgb", type=Path, nargs="?", default=Path("rgb.mp4"),
                    help="rgb.mp4 de tham chieu (mac dinh rgb.mp4)")
    ap.add_argument("--calibrate", action="store_true", help="do mat phang bang tai trong, luu plane.json")
    ap.add_argument("--frame", type=int, default=0, help="frame bat dau khi hieu chinh")
    ap.add_argument("--n", type=int, default=30, help="so frame dung khi hieu chinh")
    ap.add_argument("--plane", type=Path, default=PLANE_FILE,
                    help="file mat phang (mac dinh plane.json; anh chup tu camera: plane_live.json)")
    ap.add_argument("--no-show", action="store_true", help="khong mo cua so, chi in ket qua")
    args = ap.parse_args()
    globals()["PLANE_FILE"] = args.plane

    lut = build_jet_lut()
    if args.calibrate:
        calibrate(args.depth, lut, args.frame, args.n)
        return

    shape = frame_size(args.depth)
    plane = load_plane(shape, args.plane)
    det = Detector(plane, shape)
    show = not args.no_show
    print(f"Depth: {args.depth} ({'PNG 16-bit' if args.depth.is_dir() else 'gia mau JET'})"
          f" | mat phang: {PLANE_FILE} ({plane['time']}, nghieng {plane['tilt_deg']:.2f} deg,"
          f" cach {plane['distance_mm']:.0f} mm)")

    rgb_cap = None
    if show:
        if args.rgb.exists():
            rgb_cap = cv2.VideoCapture(str(args.rgb))
        if rgb_cap is None or not rgb_cap.isOpened():
            print(f"Canh bao: khong mo duoc {args.rgb}, bo qua cua so RGB tham chieu.")
            rgb_cap = None
        print("Phim: SPACE/P tam dung | N frame ke tiep (khi tam dung) | Q/ESC thoat")

    tracker = Tracker()
    parts = []
    num_frames = 0
    t_proc = 0.0
    warned = False
    paused = False
    try:
        for i, depth_m in enumerate(depth_frames(args.depth, lut, det.sl)):
            num_frames += 1
            t = time.perf_counter()
            r = det.process(depth_m * 1000.0)
            for s in tracker.update(r["objects"], i):
                print(format_summary(s))
                parts.append(s)
            t_proc += time.perf_counter() - t

            off = r["surf_offset"]
            if not warned and abs(off) > RECAL_WARN_MM:
                print(f"CANH BAO: bang tai da troi {off:+.1f} mm so voi mat phang da luu"
                      " -> camera bi xe dich? Nen chay lai --calibrate.")
                warned = True
            if PRINT_FRAMES:
                vis = tracker.visible()
                if not vis:
                    print(f"frame {i:5d}: khong co vat | be mat troi {off:+.2f} mm")
                for tr in vis:
                    o = tr.obj
                    print(f"frame {i:5d}: #{tr.id} cao {o['h']:6.1f} mm (muot {tr.height_now():5.1f})"
                          f" tai (x={o['x']:3d}, y={o['y']:3d}) | Z {o['z']:6.1f} mm | vat {o['px']} px"
                          f"{' | cham bien ROI' if o['border'] else ''} | be mat troi {off:+.2f} mm")

            if show:
                hmap = height_to_colormap(r["height"], r["valid"])
                draw_tracks(hmap, tracker.visible(), (det.x0, det.y0))
                cv2.imshow("Chieu cao (ROI)", hmap)
                cv2.imshow("Vat | Canh depth (ROI) - tho, chua lam muot",
                           np.hstack([r["obj"], r["edges"]]).astype(np.uint8) * 255)
                if rgb_cap is not None:
                    ok, ref = rgb_cap.read()
                    if ok:
                        if ROI is not None:
                            cv2.rectangle(ref, ROI[:2], ROI[2:], (0, 255, 0), 1)
                        draw_tracks(ref, tracker.visible())
                        cv2.putText(ref, f"frame {i}", (10, 25),
                                    cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 255), 2)
                        cv2.imshow("RGB goc (tham chieu)", ref)
                paused, quit_ = wait_key(paused)
                if quit_:
                    break
    finally:
        if rgb_cap is not None:
            rgb_cap.release()
        if show:
            cv2.destroyAllWindows()

    for s in tracker.flush():
        print(format_summary(s))
        parts.append(s)
    print(f"\nDa xu ly {num_frames} frame | xu ly trung binh {1000 * t_proc / max(num_frames, 1):.1f} ms/frame"
          " (khong tinh giai ma video)")
    print(f"So vat da di qua: {len(parts)}")
    for s in parts:
        print("  " + format_summary(s)[3:])


if __name__ == "__main__":
    main()
