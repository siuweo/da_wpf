"""Hieu chinh camera D455 -> robot SCARA: tim ma tran 4x4 T (robot = T @ cam, don vi mm).

Camera (dau bang tai) va robot (cuoi bang tai) KHONG nhin/cham chung 1 cho -> bang tai mang tam
marker tu vung camera sang vung robot; quang duong bang tai di duoc do bang ENCODER (E6B2, PLC dem
xung, thanh ghi 32 bit cong don - doc tren GX Works).

    p_robot(E) = T @ p_cam + v * (E - E_cam)      v = mm bang tai / 1 xung (vecto, he robot)

Cac buoc (du lieu luu dan vao cam2robot_session.json, lam lai buoc nao cung duoc):
  1. target          tao anh marker ArUco de in (dan len tam phang cung)
  2. belt            do v: robot cham 1 diem tren bang tai, chay bang tai >= 300 mm, cham lai
                     (lam o trai / giua / phai bang tai)
  3. camera TEN      bang tai DUNG, tam marker trong tam camera: chup -> toa do 3D marker + E_cam
                     (TAT camera_service.py truoc: D455 chi mo duoc boi 1 chuong trinh)
  4. robot TEN       chay bang tai dua tam vao vung robot, DUNG: nhap E_rob + X Y Z robot khi cham
                     dau cong cu vao tam tung marker (doc tren GUI)
     Lap lai 3-4 voi tam o trai / giua / phai bang tai, xoay goc khac, co the 1 marker tren khoi cao.
  5. fit             tinh T (Kabsch), sai so tung diem, kiem tra cheo (bo tung lan dat) -> cam2robot.json
  6. check           kiem tra voi diem moi: chup, nhap E -> du doan toa do robot de cham thu

Cach chay (tu thu muc scripts/):
    python camera/calibrate_cam2robot.py target
    python camera/calibrate_cam2robot.py belt
    python camera/calibrate_cam2robot.py camera giua
    python camera/calibrate_cam2robot.py robot giua
    python camera/calibrate_cam2robot.py fit
    python camera/calibrate_cam2robot.py status

Ghi chu:
  * Toa do camera = he RealSense cua luong MAU 640x480 (x phai, y xuong, z ra truoc), depth da align
    theo mau, chieu nguoc bang intrinsics THAT cua luong mau.
  * Dau cong cu nam tren truc khop 4, giu NGUYEN goc Theta suot qua trinh cham. Lech Z co dinh cua
    dau cong cu duoc T hap thu.
  * Kich thuoc marker khi in khong can chinh xac (vi tri lay tu depth, khong tu kich thuoc).
"""

import argparse
import json
import sys
import time
from pathlib import Path

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parent.parent          # scripts/ (chua live_max_height.py, d455.json)
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

# ====================== CAU HINH ======================
SESSION_FILE = ROOT / "cam2robot_session.json"
OUTPUT_FILE = ROOT / "cam2robot.json"
D455_JSON = ROOT / "d455.json"
WIDTH, HEIGHT, FPS = 640, 480, 30   # phai giong camera_service.py
ARUCO_DICT = cv2.aruco.DICT_4X4_50
CAPTURE_FRAMES = 30                 # so frame lay trung vi khi chup
MIN_DETECTIONS = 20                 # marker phai thay trong >= bay nhieu frame
DEPTH_AREA = 0.8                    # lay depth trong vung giua marker (ti le canh)
MAX_POINT_STD_MM = 2.0              # do dao dong vi tri marker qua cac frame > muc nay -> canh bao
RMS_TARGET_MM = 2.0                 # muc tieu sai so khop
OUTLIER_MM = 5.0                    # diem lech > muc nay -> danh dau nghi sai
BELT_AXIS = np.array([0.0, 1.0, 0.0])   # bang tai chay doc truc Y robot (kiem tra so bo)
BELT_AXIS_WARN_DEG = 5.0
K_SPREAD_WARN = 0.005               # mm/xung giua cac lan do lech > 0.5 % -> canh bao
# ======================================================


# ---------------------------------------------------------------------------
# Phien lam viec (luu dan tung buoc)
# ---------------------------------------------------------------------------
def load_session(path):
    if Path(path).exists():
        return json.loads(Path(path).read_text(encoding="utf-8"))
    return {"belt": [], "sets": {}, "intrinsics": None}


def save_session(session, path):
    Path(path).write_text(json.dumps(session, indent=2, ensure_ascii=False), encoding="utf-8")


def ask(prompt):
    text = input(prompt).strip()
    if text.lower() in ("q", "quit", "exit"):
        raise KeyboardInterrupt
    return text


def ask_int(prompt):
    while True:
        text = ask(prompt)
        try:
            return int(text)
        except ValueError:
            print("  Nhap so nguyen (q = huy).")


def ask_xyz(prompt, allow_blank=False):
    while True:
        text = ask(prompt).replace(",", " ")
        if not text and allow_blank:
            return None
        try:
            vals = [float(t) for t in text.split()]
            if len(vals) == 3:
                return vals
        except ValueError:
            pass
        print("  Nhap 3 so: X Y Z (mm)" + (", Enter = bo qua" if allow_blank else "") + ", q = huy.")


def ask_yes(prompt):
    return ask(prompt + " (y/N): ").lower() in ("y", "yes", "c", "co")


# ---------------------------------------------------------------------------
# Toan hoc
# ---------------------------------------------------------------------------
def kabsch(P, Q):
    """Khop cung P -> Q (N x 3): tra ve R, t voi Q ~ R @ P + t."""
    P, Q = np.asarray(P, float), np.asarray(Q, float)
    pc, qc = P.mean(0), Q.mean(0)
    U, _, Vt = np.linalg.svd((P - pc).T @ (Q - qc))
    D = np.diag([1.0, 1.0, np.sign(np.linalg.det(Vt.T @ U.T))])
    R = Vt.T @ D @ U.T
    return R, qc - R @ pc


def to_matrix(R, t):
    T = np.eye(4)
    T[:3, :3], T[:3, 3] = R, t
    return T


def belt_vector(belt):
    """Cac lan do (A, E_a) -> (B, E_b): v (mm/xung, vecto he robot) theo binh phuong toi thieu."""
    if not belt:
        raise SystemExit("Chua co lan do bang tai - chay buoc 'belt' truoc.")
    dP = np.array([np.subtract(m["b"], m["a"]) for m in belt], float)
    dE = np.array([m["eb"] - m["ea"] for m in belt], float)
    if np.any(dE == 0):
        raise SystemExit("Co lan do bang tai voi E_b = E_a (bang tai khong chay / chua cong don xung?).")
    v = (dP * dE[:, None]).sum(0) / (dE ** 2).sum()
    k_each = np.linalg.norm(dP, axis=1) / np.abs(dE)
    travel = dP.sum(0)
    travel_dir = travel / np.linalg.norm(travel)          # huong bang tai chay toi (theo lan do)
    zs = [m["a"][2] for m in belt] + [m["b"][2] for m in belt]
    info = {
        "k_each": k_each,
        "k_spread": float(np.ptp(k_each) / k_each.mean()) if len(k_each) > 1 else 0.0,
        "travel_dir": travel_dir,
        "axis_deg": float(np.degrees(np.arccos(min(1.0, abs(travel_dir @ BELT_AXIS))))),
        "belt_z": float(np.median(zs)),
        "resid": [float(np.linalg.norm(dp - v * de)) for dp, de in zip(dP, dE)],
    }
    return v, info


def collect_pairs(session, v, sets=None):
    """Cac cap (diem camera, diem robot da tru quang duong bang tai) tu nhung lan dat da du du lieu."""
    P, Q, labels = [], [], []
    for name, s in session["sets"].items():
        if sets is not None and name not in sets:
            continue
        if s.get("e_rob") is None:
            continue
        shift = v * (s["e_rob"] - s["e_cam"])
        for mid, pc in s["cam"].items():
            pr = s.get("rob", {}).get(mid)
            if pr is None:
                continue
            P.append(pc)
            Q.append(np.asarray(pr, float) - shift)
            labels.append((name, int(mid)))
    return np.array(P, float).reshape(-1, 3), np.array(Q, float).reshape(-1, 3), labels


def fit_session(session):
    """Tinh T va cac so lieu kiem tra. Tra ve dict (dung cho 'fit' va kiem thu)."""
    v, binfo = belt_vector(session["belt"])
    P, Q, labels = collect_pairs(session, v)
    if len(P) < 3:
        raise SystemExit(f"Moi co {len(P)} cap diem camera-robot - can it nhat 3 (nen >= 8, nhieu lan dat).")
    if np.linalg.matrix_rank(P - P.mean(0), tol=5.0) < 2:
        raise SystemExit("Cac diem gan nhu thang hang - dat marker trai rong hon.")
    R, t = kabsch(P, Q)
    res = np.linalg.norm(P @ R.T + t - Q, axis=1)

    loo = {}
    names = sorted({n for n, _ in labels})
    if len(names) >= 2:
        for n in names:
            keep = [i for i, (m, _) in enumerate(labels) if m != n]
            test = [i for i, (m, _) in enumerate(labels) if m == n]
            if len(keep) < 3:
                continue
            Rk, tk = kabsch(P[keep], Q[keep])
            e = np.linalg.norm(P[test] @ Rk.T + tk - Q[test], axis=1)
            loo[n] = float(np.sqrt(np.mean(e ** 2)))
    return {"R": R, "t": t, "T": to_matrix(R, t), "v": v, "belt": binfo, "P": P, "Q": Q,
            "labels": labels, "res": res, "rms": float(np.sqrt(np.mean(res ** 2))),
            "max": float(res.max()), "loo": loo}


# ---------------------------------------------------------------------------
# Marker
# ---------------------------------------------------------------------------
def make_detector():
    params = cv2.aruco.DetectorParameters()
    params.cornerRefinementMethod = cv2.aruco.CORNER_REFINE_SUBPIX
    return cv2.aruco.ArucoDetector(cv2.aruco.getPredefinedDictionary(ARUCO_DICT), params)


def marker_center(c):
    """Tam that cua marker = giao 2 duong cheo (dung ca khi nhin xien, khac trung binh 4 goc)."""
    d1, d2 = c[2] - c[0], c[3] - c[1]
    s = np.linalg.solve(np.column_stack([d1, -d2]), c[1] - c[0])[0]
    return c[0] + s * d1


def deproject(intr, u, v, z_mm):
    """Pixel (u, v) + depth (mm) -> diem 3D camera (mm). intr: rs.intrinsics, hoac dict pinhole (kiem thu)."""
    if isinstance(intr, dict):
        return np.array([(u - intr["ppx"]) / intr["fx"] * z_mm, (v - intr["ppy"]) / intr["fy"] * z_mm, z_mm])
    import pyrealsense2 as rs
    return np.array(rs.rs2_deproject_pixel_to_point(intr, [float(u), float(v)], float(z_mm)))


def measure_markers(color, depth_mm, intr, detector):
    """Tim marker trong 1 frame. depth_mm: depth da align theo mau (mm, 0 = khong hop le).
    Tra ve {id: {"uv", "xyz" (mm, he camera), "corners"}}."""
    corners, ids, _ = detector.detectMarkers(color)
    out = {}
    if ids is None:
        return out
    for c, mid in zip(corners, ids.ravel()):
        c = c.reshape(4, 2).astype(np.float64)
        uv = marker_center(c)
        poly = ((c - uv) * DEPTH_AREA + uv).round().astype(np.int32)
        mask = np.zeros(depth_mm.shape[:2], np.uint8)
        cv2.fillConvexPoly(mask, poly, 1)
        vals = depth_mm[mask > 0]
        good = vals[vals > 0]
        if len(vals) == 0 or len(good) < 0.5 * len(vals):
            continue                                   # depth thieu (phan xa, bong) -> bo frame nay
        out[int(mid)] = {"uv": uv, "xyz": deproject(intr, uv[0], uv[1], float(np.median(good))), "corners": c}
    return out


def draw_markers(img, found):
    for mid, m in found.items():
        cv2.polylines(img, [m["corners"].round().astype(np.int32)], True, (0, 255, 0), 2)
        u, v = m["uv"]
        cv2.drawMarker(img, (int(round(u)), int(round(v))), (0, 0, 255), cv2.MARKER_CROSS, 12, 1)
        cv2.putText(img, f"{mid} z={m['xyz'][2]:.0f}", (int(u) + 8, int(v) - 8),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 255), 2)


# ---------------------------------------------------------------------------
# Camera
# ---------------------------------------------------------------------------
def intr_to_dict(intr):
    return {"width": intr.width, "height": intr.height, "fx": intr.fx, "fy": intr.fy, "ppx": intr.ppx,
            "ppy": intr.ppy, "model": str(intr.model), "coeffs": list(intr.coeffs)}


class Camera:
    """D455: mau 640x480 + depth align theo mau, cung cau hinh d455.json voi camera_service.py."""

    def __init__(self, json_path):
        import pyrealsense2 as rs
        from live_max_height import apply_json, find_d455
        dev = find_d455(rs.context())
        if dev is None:
            raise SystemExit("Khong tim thay D455 (camera_service.py dang mo camera? Tat no truoc).")
        dw, dh, dfps = WIDTH, HEIGHT, FPS
        if json_path.exists():
            dev, (dw, dh, dfps) = apply_json(dev, json_path)
        else:
            print(f"CANH BAO: khong co {json_path} - dung cai dat mac dinh (phai giong luc chay that).")
        config = rs.config()
        config.enable_device(dev.get_info(rs.camera_info.serial_number))
        config.enable_stream(rs.stream.color, WIDTH, HEIGHT, rs.format.bgr8, FPS)
        config.enable_stream(rs.stream.depth, dw, dh, rs.format.z16, dfps)
        self.pipeline = rs.pipeline()
        profile = self.pipeline.start(config)
        self.to_mm = np.float32(profile.get_device().first_depth_sensor().get_depth_scale() * 1000.0)
        self.intr = profile.get_stream(rs.stream.color).as_video_stream_profile().get_intrinsics()
        self.align = rs.align(rs.stream.color)
        print(f"D455: mau {WIDTH}x{HEIGHT}, depth {dw}x{dh}@{dfps} align ve mau | fx {self.intr.fx:.1f} "
              f"fy {self.intr.fy:.1f} c ({self.intr.ppx:.1f}, {self.intr.ppy:.1f})")

    def grab(self):
        frames = self.align.process(self.pipeline.wait_for_frames(timeout_ms=5000))
        c, d = frames.get_color_frame(), frames.get_depth_frame()
        if not c or not d:
            return None
        return np.asanyarray(c.get_data()).copy(), np.asanyarray(d.get_data()).astype(np.float32) * self.to_mm

    def close(self):
        self.pipeline.stop()


def capture_markers(cam, n=CAPTURE_FRAMES):
    """Xem truc tiep; SPACE: lay trung vi n frame. Tra ve {id: (xyz, std_mm)} hoac None neu thoat."""
    det = make_detector()
    win = "Camera -> robot: SPACE chup | Q thoat"
    try:
        while True:
            f = cam.grab()
            if f is None:
                continue
            found = measure_markers(f[0], f[1], cam.intr, det)
            vis = f[0].copy()
            draw_markers(vis, found)
            cv2.putText(vis, f"{len(found)} marker | SPACE: chup {n} frame | Q: thoat", (10, 22),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 255, 255), 2)
            cv2.imshow(win, vis)
            key = cv2.waitKey(1) & 0xFF
            if key in (ord("q"), 27):
                return None
            if key == ord(" "):
                break
        samples, got = {}, 0
        while got < n:
            f = cam.grab()
            if f is None:
                continue
            got += 1
            for mid, m in measure_markers(f[0], f[1], cam.intr, det).items():
                samples.setdefault(mid, []).append(m["xyz"])
            vis = f[0].copy()
            cv2.putText(vis, f"Dang chup {got}/{n} - giu yen", (10, 22), cv2.FONT_HERSHEY_SIMPLEX, 0.6,
                        (0, 0, 255), 2)
            cv2.imshow(win, vis)
            cv2.waitKey(1)
    finally:
        cv2.destroyWindow(win)

    result = {}
    for mid, pts in sorted(samples.items()):
        pts = np.array(pts)
        if len(pts) < MIN_DETECTIONS:
            print(f"  Marker {mid}: chi thay {len(pts)}/{n} frame - bo qua.")
            continue
        med = np.median(pts, axis=0)
        result[mid] = (med, float(np.sqrt(((pts - med) ** 2).sum(1).mean())))
    return result


# ---------------------------------------------------------------------------
# Lenh
# ---------------------------------------------------------------------------
def cmd_target(args):
    """Anh A4 (doc) chua cac marker, co vach chi tam o 4 phia de canh dau cong cu."""
    ids = []
    for part in args.ids.split(","):
        a, _, b = part.partition("-")
        ids += list(range(int(a), int(b or a) + 1))
    mm = args.dpi / 25.4
    page_w, page_h = int(210 * mm), int(297 * mm)
    size = int(args.size * mm)
    cell = int((args.size + 30) * mm)
    cols = max(1, (page_w - int(10 * mm)) // cell)
    rows = max(1, (page_h - int(25 * mm)) // cell)
    dictionary = cv2.aruco.getPredefinedDictionary(ARUCO_DICT)
    gap, thick = size // 6, max(1, int(0.4 * mm))
    tick = max(int(3 * mm), int(15 * mm) - gap - int(1 * mm))   # vach khong cham vach cua marker ben canh
    per_page = cols * rows
    out_files = []
    for p in range(0, len(ids), per_page):
        page = np.full((page_h, page_w), 255, np.uint8)
        x0 = (page_w - cols * cell) // 2
        for k, mid in enumerate(ids[p:p + per_page]):
            r, c = divmod(k, cols)
            cx, cy = x0 + c * cell + cell // 2, int(10 * mm) + r * cell + cell // 2
            h = size // 2
            page[cy - h:cy - h + size, cx - h:cx - h + size] = cv2.aruco.generateImageMarker(dictionary, mid, size)
            for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):      # vach chi tam: noi dai 2 vach doi dien
                a = (cx + dx * (h + gap), cy + dy * (h + gap))
                b = (cx + dx * (h + gap + tick), cy + dy * (h + gap + tick))
                cv2.line(page, a, b, 0, thick)
            cv2.putText(page, f"ID {mid}", (cx - h, cy - h - gap - int(2 * mm)), cv2.FONT_HERSHEY_SIMPLEX,
                        args.dpi / 300, 0, max(1, thick))
        cv2.putText(page, f"ArUco 4x4_50, {args.size:g} mm - in 100%, dan len tam phang cung. Diem cham = giao "
                    "cac vach chi tam.", (int(10 * mm), page_h - int(10 * mm)), cv2.FONT_HERSHEY_SIMPLEX,
                    args.dpi / 600, 0, max(1, thick // 2))
        out = Path(args.out)
        if len(ids) > per_page:
            out = out.with_name(f"{out.stem}_{p // per_page + 1}{out.suffix}")
        cv2.imwrite(str(out), page)
        out_files.append(out)
    print(f"Da tao {', '.join(str(f) for f in out_files)}: marker {ids[0]}..{ids[-1]}, {args.size:g} mm, "
          f"{per_page} marker/trang, {args.dpi} dpi.")


def cmd_belt(args):
    session = load_session(args.session)
    print("DO BANG TAI (huong + mm/xung). Dung 1 diem danh dau mong tren mat bang tai (vd. dau + tren bang dinh).")
    print("Toa do X Y Z doc tren GUI khi dau cong cu cham diem; so xung doc tren GX Works (thanh ghi cong don).")
    while True:
        a = ask_xyz("\n  Diem A - X Y Z robot: ")
        ea = ask_int("  Diem A - so xung encoder: ")
        print("  -> Nang dau cong cu, chay bang tai >= 300 mm (cung chieu khi lam viec), DUNG, cham lai diem do.")
        b = ask_xyz("  Diem B - X Y Z robot: ")
        eb = ask_int("  Diem B - so xung encoder: ")
        d = float(np.linalg.norm(np.subtract(b, a)))
        if eb == ea:
            print("  E_b = E_a: bo lan do nay (bang tai chua chay / thanh ghi khong cong don?).")
        else:
            session["belt"].append({"a": a, "ea": ea, "b": b, "eb": eb, "time": time.strftime("%Y-%m-%d %H:%M:%S")})
            save_session(session, args.session)
            print(f"  Quang duong {d:.1f} mm / {eb - ea} xung = {d / abs(eb - ea):.5f} mm/xung"
                  + ("  (CANH BAO: < 300 mm)" if d < 300 else ""))
        if not ask_yes("  Do them (nen co trai / giua / phai bang tai)?"):
            break
    v, info = belt_vector(session["belt"])
    print_belt(v, info)


def print_belt(v, info):
    print(f"\nBang tai: {len(info['k_each'])} lan do | {np.linalg.norm(v):.5f} mm/xung"
          f" (tung lan: {', '.join(f'{k:.5f}' for k in info['k_each'])}, lech {100 * info['k_spread']:.2f} %)")
    print(f"  Huong chay toi (he robot): ({info['travel_dir'][0]:+.4f}, {info['travel_dir'][1]:+.4f}, "
          f"{info['travel_dir'][2]:+.4f}), lech truc Y {info['axis_deg']:.2f} deg | Z mat bang tai ~ {info['belt_z']:.1f} mm")
    if info["k_spread"] > K_SPREAD_WARN:
        print("  CANH BAO: mm/xung giua cac lan do lech > 0.5 % - kiem tra truot banh encoder / doc sai so xung.")
    if info["axis_deg"] > BELT_AXIS_WARN_DEG:
        print("  CANH BAO: huong bang tai lech truc Y nhieu - kiem tra lai cac diem cham.")
    if abs(info["travel_dir"][2]) > 0.02:
        print("  CANH BAO: Z cua 2 diem cham khac nhau - mat bang tai nghieng / cham khong deu tay.")


def cmd_camera(args):
    session = load_session(args.session)
    old = session["sets"].get(args.name)
    if old and not ask_yes(f"Lan dat '{args.name}' da co - chup lai (xoa du lieu robot cua lan nay)?"):
        return
    print("Bang tai DUNG, tam marker trong vung camera (nen dat o vung vat se di qua).")
    cam = Camera(args.json)
    try:
        intr = intr_to_dict(cam.intr)
        res = capture_markers(cam)
    finally:
        cam.close()
    if not res:
        print("Khong chup / khong co marker nao.")
        return
    print(f"\n{'ID':>4} {'X cam':>9} {'Y cam':>9} {'Z cam':>9} {'dao dong':>9}  (mm)")
    for mid, (p, std) in res.items():
        print(f"{mid:>4} {p[0]:9.1f} {p[1]:9.1f} {p[2]:9.1f} {std:9.2f}" + ("  CANH BAO" if std > MAX_POINT_STD_MM else ""))
    if session["intrinsics"] and session["intrinsics"] != intr:
        print("CANH BAO: intrinsics khac lan truoc (doi camera / do phan giai?) - khong tron du lieu cu.")
    e_cam = ask_int("So xung encoder luc chup (bang tai van DUNG): ")
    session["intrinsics"] = intr
    session["sets"][args.name] = {"e_cam": e_cam, "cam": {str(m): list(map(float, p)) for m, (p, _) in res.items()},
                                  "cam_std": {str(m): s for m, (_, s) in res.items()},
                                  "e_rob": None, "rob": {}, "time": time.strftime("%Y-%m-%d %H:%M:%S")}
    save_session(session, args.session)
    print(f"Da luu lan dat '{args.name}' ({len(res)} marker). Tiep: chay bang tai dua tam vao vung robot, DUNG, "
          f"roi chay 'robot {args.name}'.")


def cmd_robot(args):
    session = load_session(args.session)
    s = session["sets"].get(args.name)
    if s is None:
        raise SystemExit(f"Chua co lan dat '{args.name}' - chay 'camera {args.name}' truoc.")
    print(f"Lan dat '{args.name}': marker {', '.join(sorted(s['cam'], key=int))}. Bang tai DUNG, tam trong vung robot.")
    s["e_rob"] = ask_int("So xung encoder bay gio: ")
    print("Cham dau cong cu vao giao cac vach chi tam cua tung marker, nhap X Y Z tren GUI (Enter = bo qua/giu cu).")
    for mid in sorted(s["cam"], key=int):
        old = s["rob"].get(mid)
        p = ask_xyz(f"  Marker {mid}" + (f" [cu {old[0]:.2f} {old[1]:.2f} {old[2]:.2f}]" if old else "") + ": ",
                    allow_blank=True)
        if p is not None:
            s["rob"][mid] = p
        save_session(session, args.session)
    print(f"Da luu {len(s['rob'])} diem robot. Them lan dat khac (trai/giua/phai, xoay) hoac chay 'fit'.")


def cmd_fit(args):
    session = load_session(args.session)
    r = fit_session(session)
    print_belt(r["v"], r["belt"])
    print(f"\n{'Lan dat':<12} {'ID':>3} {'sai so':>8}  (mm)")
    for (name, mid), e in zip(r["labels"], r["res"]):
        print(f"{name:<12} {mid:>3} {e:8.2f}" + ("  <- nghi sai (cham lech / doc nham?)" if e > OUTLIER_MM else ""))
    print(f"\nKhop {len(r['res'])} diem / {len({n for n, _ in r['labels']})} lan dat: RMS {r['rms']:.2f} mm, "
          f"lon nhat {r['max']:.2f} mm (muc tieu RMS <= {RMS_TARGET_MM:.1f})")
    if r["loo"]:
        print("Kiem tra cheo (khop bang cac lan dat khac, sai so tren lan nay): "
              + ", ".join(f"{n} {e:.2f}" for n, e in r["loo"].items()) + " mm")
    else:
        print("Chi co 1 lan dat - nen them lan dat de kiem tra cheo.")
    if r["rms"] > RMS_TARGET_MM:
        print("CANH BAO: RMS vuot muc tieu - xem cac diem nghi sai, cham lai hoac bo lan dat do.")
    out = {
        "T_cam_to_robot": r["T"].round(6).tolist(),
        "note": "p_robot(E) = T @ [x_cam, y_cam, z_cam, 1] + belt_mm_per_count * (E - E_capture); mm, "
                "he camera = luong mau RealSense 640x480 (x phai, y xuong, z ra truoc)",
        "belt_mm_per_count": [round(float(x), 7) for x in r["v"]],
        "mm_per_count": round(float(np.linalg.norm(r["v"])), 7),
        "belt_dir": [round(float(x), 6) for x in r["belt"]["travel_dir"]],
        "belt_z_mm": round(r["belt"]["belt_z"], 2),
        "rms_mm": round(r["rms"], 3), "max_mm": round(r["max"], 3),
        "loo_rms_mm": {n: round(e, 3) for n, e in r["loo"].items()},
        "n_points": len(r["res"]), "sets": sorted({n for n, _ in r["labels"]}),
        "intrinsics": session["intrinsics"], "stream": [WIDTH, HEIGHT],
        "time": time.strftime("%Y-%m-%d %H:%M:%S"),
    }
    Path(args.out).write_text(json.dumps(out, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"\nDa luu {args.out}")
    print("T (camera -> robot):\n" + np.array2string(r["T"], precision=4, suppress_small=True))


def cmd_check(args):
    cal = json.loads(Path(args.cal).read_text(encoding="utf-8"))
    T, v = np.array(cal["T_cam_to_robot"]), np.array(cal["belt_mm_per_count"])
    print("Kiem tra: dat marker (vi tri MOI) trong vung camera, bang tai DUNG.")
    cam = Camera(args.json)
    try:
        res = capture_markers(cam)
    finally:
        cam.close()
    if not res:
        return
    e_cam = ask_int("So xung encoder luc chup: ")
    while True:
        text = ask("\nSo xung encoder bay gio (Enter = thoat): ")
        if not text:
            break
        try:
            e_now = int(text)
        except ValueError:
            continue
        for mid, (p, _) in res.items():
            q = T[:3, :3] @ p + T[:3, 3] + v * (e_now - e_cam)
            print(f"  Marker {mid}: du doan X {q[0]:8.2f}  Y {q[1]:8.2f}  Z {q[2]:8.2f}  -> cham thu, so voi GUI")


def cmd_status(args):
    session = load_session(args.session)
    if session["belt"]:
        v, info = belt_vector(session["belt"])
        print_belt(v, info)
    else:
        print("Bang tai: chua do.")
    print(f"\nLan dat: {len(session['sets'])}")
    for name, s in session["sets"].items():
        print(f"  {name:<12} {len(s['cam'])} marker camera (E {s['e_cam']}), "
              f"{len(s['rob'])} diem robot" + ("" if s["e_rob"] is not None else " - CHUA nhap robot"))


def main():
    ap = argparse.ArgumentParser(description="Hieu chinh camera D455 -> robot (ma tran 4x4).")
    ap.add_argument("--session", type=Path, default=SESSION_FILE, help="file luu du lieu cac buoc")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("target", help="tao anh marker ArUco de in")
    p.add_argument("--ids", default="0-5", help="vd 0-5 hoac 0-3,10")
    p.add_argument("--size", type=float, default=50.0, help="canh marker (mm), >= 40 o do cao 400 mm")
    p.add_argument("--dpi", type=int, default=300)
    p.add_argument("--out", default="cam2robot_target.png")
    p.set_defaults(func=cmd_target)

    p = sub.add_parser("belt", help="do huong bang tai + mm/xung encoder")
    p.set_defaults(func=cmd_belt)

    for name, func, text in (("camera", cmd_camera, "chup marker bang camera (bang tai dung)"),
                             ("robot", cmd_robot, "nhap toa do robot khi cham cac marker")):
        p = sub.add_parser(name, help=text)
        p.add_argument("name", help="ten lan dat tam marker, vd trai / giua / phai / xoay45")
        p.add_argument("--json", type=Path, default=D455_JSON)
        p.set_defaults(func=func)

    p = sub.add_parser("fit", help="tinh T, sai so, luu cam2robot.json")
    p.add_argument("--out", type=Path, default=OUTPUT_FILE)
    p.set_defaults(func=cmd_fit)

    p = sub.add_parser("check", help="kiem tra T voi vi tri marker moi")
    p.add_argument("--cal", type=Path, default=OUTPUT_FILE)
    p.add_argument("--json", type=Path, default=D455_JSON)
    p.set_defaults(func=cmd_check)

    p = sub.add_parser("status", help="xem du lieu da thu")
    p.set_defaults(func=cmd_status)

    args = ap.parse_args()
    try:
        args.func(args)
    except KeyboardInterrupt:
        print("\nHuy.")


if __name__ == "__main__":
    main()
