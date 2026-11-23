"""Do chieu cao vat tren bang tai THOI GIAN THUC voi Intel RealSense D455.

Dung chung bo xu ly voi max_height.py (dat 2 file cung thu muc). Mat phang bang tai
duoc hieu chinh truc tiep tu camera va luu vao plane_live.json (rieng voi plane.json
cua video, vi cai dat camera khac nhau).

Cach chay:
  python live_max_height.py                      (doc d455.json o thu muc hien tai)
  python live_max_height.py --json D:/cfg/d455.json

Lan dau (hoac khi camera bi xe dich): de bang tai TRONG (khong vat, khong tay) roi nhan C.

Phim:
  C        hieu chinh mat phang (bang tai phai trong)
  S        luu anh chup: depth 16-bit (da align) + RGB vao thu muc snapshots/
           (phan tich lai offline: python max_height.py snapshots/depth --plane plane_live.json)
  SPACE/P  tam dung hien thi | Q/ESC thoat
"""

import argparse
import json
import time
from pathlib import Path

import cv2
import numpy as np
import pyrealsense2 as rs

import max_height as mh

# ====================== CAU HINH ======================
COLOR_W, COLOR_H, COLOR_FPS = 640, 480, 30  # Depth duoc align ve anh mau nay -> ROI tinh theo anh mau
PLANE_LIVE = Path("plane_live.json")
CALIB_FRAMES = 30                           # So frame dung khi nhan C
DEPTH_EXPOSURE_US = None                    # None = giu theo JSON. Vd 6000: tat auto, 6 ms (giam nhoe khi bang tai chay)
PRINT_FRAMES = False                        # True: in moi frame. False: chi in 1 dong tong ket / vat
# ======================================================


def find_d455(ctx):
    for dev in ctx.query_devices():
        if "D455" in dev.get_info(rs.camera_info.name):
            return dev
    return None


def apply_json(dev, json_path):
    """Nap file cau hinh (xuat tu RealSense Viewer) vao camera. Tra ve (device, cau hinh stream)."""
    cfg = json.loads(json_path.read_text())
    serial = dev.get_info(rs.camera_info.serial_number)
    adv = rs.rs400_advanced_mode(dev)
    if not adv.is_enabled():
        print("Bat advanced mode (camera se khoi dong lai vai giay)...")
        adv.toggle_advanced_mode(True)
        dev = None
        for _ in range(30):  # Doi camera ket noi lai
            time.sleep(1)
            for d in rs.context().query_devices():
                if d.get_info(rs.camera_info.serial_number) == serial:
                    dev = d
                    break
            if dev is not None:
                break
        if dev is None:
            raise RuntimeError("Camera khong ket noi lai sau khi bat advanced mode.")
        adv = rs.rs400_advanced_mode(dev)
    adv.load_json(json.dumps(cfg))
    viewer = cfg.get("viewer", {})
    stream = (int(viewer.get("stream-width", 848)), int(viewer.get("stream-height", 480)),
              int(viewer.get("stream-fps", 30)))
    print(f"Da nap {json_path.name} (fw {cfg.get('device', {}).get('fw version', '?')})")
    return dev, stream


def main():
    ap = argparse.ArgumentParser(description="Do chieu cao vat tren bang tai, thoi gian thuc (D455).")
    ap.add_argument("--json", type=Path, default=Path("d455.json"), help="cau hinh tu RealSense Viewer")
    args = ap.parse_args()

    ctx = rs.context()
    dev = find_d455(ctx)
    if dev is None:
        raise SystemExit("KHONG TIM THAY Intel RealSense D455. Kiem tra cap USB 3, nguon va driver.")
    if not args.json.exists():
        raise SystemExit(f"Khong thay file cau hinh {args.json}")
    dev, (dw, dh, dfps) = apply_json(dev, args.json)
    serial = dev.get_info(rs.camera_info.serial_number)

    depth_sensor = dev.first_depth_sensor()
    if DEPTH_EXPOSURE_US is not None:
        depth_sensor.set_option(rs.option.enable_auto_exposure, 0)
        depth_sensor.set_option(rs.option.exposure, float(DEPTH_EXPOSURE_US))
        print(f"Exposure depth: thu cong {DEPTH_EXPOSURE_US} us")

    config = rs.config()
    config.enable_device(serial)
    config.enable_stream(rs.stream.depth, dw, dh, rs.format.z16, dfps)
    config.enable_stream(rs.stream.color, COLOR_W, COLOR_H, rs.format.bgr8, COLOR_FPS)
    pipeline = rs.pipeline()
    profile = pipeline.start(config)
    depth_scale = profile.get_device().first_depth_sensor().get_depth_scale()
    to_mm = np.float32(depth_scale * 1000.0)
    align = rs.align(rs.stream.color)
    shape = (COLOR_H, COLOR_W)
    sl = mh.roi_slices(shape)
    print(f"D455 S/N {serial}: depth {dw}x{dh}@{dfps} -> align ve mau {COLOR_W}x{COLOR_H}"
          f" | depth scale {depth_scale} m | ROI {mh.ROI}")

    det = None
    tracker = mh.Tracker()
    parts = []
    if PLANE_LIVE.exists():
        plane = mh.load_plane(shape, PLANE_LIVE)
        det = mh.Detector(plane, shape)
        print(f"Dung mat phang da luu {PLANE_LIVE} ({plane['time']}, cach {plane['distance_mm']:.0f} mm,"
              f" nghieng {plane['tilt_deg']:.2f} deg). Nhan C de hieu chinh lai.")
    else:
        print(f"Chua co {PLANE_LIVE}: de bang tai TRONG roi nhan C de hieu chinh.")
    print("Phim: C hieu chinh | S luu anh | SPACE tam dung | Q thoat\n")

    snap_dir = Path("snapshots")
    calib = None          # Danh sach frame dang gom khi hieu chinh
    paused = False
    warned = False
    n_frames, n_obj = 0, 0
    t_last, fps = time.perf_counter(), 0.0
    t_proc_avg = 0.0
    try:
        while True:
            frames = pipeline.wait_for_frames(timeout_ms=5000)
            t0 = time.perf_counter()
            aligned = align.process(frames)
            depth_frame = aligned.get_depth_frame()
            color_frame = aligned.get_color_frame()
            if not depth_frame or not color_frame:
                continue
            z16 = np.asanyarray(depth_frame.get_data())
            rgb = np.asanyarray(color_frame.get_data())
            z16_roi = z16[sl]
            z = z16_roi.astype(np.float32) * to_mm
            z[z16_roi == 0] = np.nan
            n_frames += 1

            # --- Hieu chinh ---
            if calib is not None:
                calib.append(z)
                if len(calib) >= CALIB_FRAMES:
                    try:
                        plane = mh.fit_and_save_plane(calib, shape, f"live S/N {serial}",
                                                      [n_frames - len(calib), n_frames - 1], PLANE_LIVE)
                        det = mh.Detector(plane, shape)
                        tracker = mh.Tracker()
                        warned = False
                    except RuntimeError as exc:
                        print(f"Hieu chinh that bai: {exc}")
                    calib = None

            # --- Do ---
            r = det.process(z) if det is not None and calib is None else None
            if r is not None:
                for summ in tracker.update(r["objects"], n_frames):
                    print(mh.format_summary(summ))
                    parts.append(summ)
            t_proc = time.perf_counter() - t0
            t_proc_avg = 0.95 * t_proc_avg + 0.05 * t_proc if n_frames > 1 else t_proc
            now = time.perf_counter()
            fps = 0.9 * fps + 0.1 / max(now - t_last, 1e-6) if n_frames > 1 else 0.0
            t_last = now

            vis = tracker.visible() if r is not None else []
            if r is not None:
                off = r["surf_offset"]
                if not warned and abs(off) > mh.RECAL_WARN_MM:
                    print(f"CANH BAO: bang tai troi {off:+.1f} mm so voi mat phang -> camera xe dich?"
                          " De bang tai trong va nhan C.")
                    warned = True
                if vis:
                    n_obj += 1
                if PRINT_FRAMES:
                    for tr in vis:
                        o = tr.obj
                        print(f"#{tr.id} cao {o['h']:6.1f} mm (muot {tr.height_now():5.1f})"
                              f" tai (x={o['x']:3d}, y={o['y']:3d}) | Z {o['z']:6.1f} mm"
                              f" | vat {o['px']} px | troi {off:+.2f} mm")

            # --- Hien thi ---
            if not paused:
                ref = rgb.copy()
                if mh.ROI is not None:
                    cv2.rectangle(ref, mh.ROI[:2], mh.ROI[2:], (0, 255, 0), 1)
                mh.draw_tracks(ref, vis)
                if calib is not None:
                    status = f"HIEU CHINH {len(calib)}/{CALIB_FRAMES} - giu bang tai trong"
                elif det is None:
                    status = "CHUA HIEU CHINH - bang tai trong roi nhan C"
                else:
                    status = f"{fps:4.1f} fps | xu ly {1000 * t_proc_avg:4.1f} ms"
                cv2.putText(ref, status, (10, 25), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)
                cv2.imshow("RGB (tham chieu)", ref)
                if r is not None:
                    hmap = mh.height_to_colormap(r["height"], r["valid"])
                    mh.draw_tracks(hmap, vis, (det.x0, det.y0))
                    cv2.imshow("Chieu cao (ROI)", hmap)
                    cv2.imshow("Vat | Canh depth (ROI) - tho, chua lam muot",
                               np.hstack([r["obj"], r["edges"]]).astype(np.uint8) * 255)

            key = cv2.waitKey(1) & 0xFF
            if key in (ord("q"), 27):
                break
            if key in (ord(" "), ord("p")):
                paused = not paused
                print("|| Tam dung hien thi" if paused else "> Tiep tuc")
            elif key == ord("c") and calib is None:
                print(f"Hieu chinh: gom {CALIB_FRAMES} frame, giu bang tai trong...")
                calib = []
            elif key == ord("s"):
                stamp = time.strftime("%Y%m%d_%H%M%S")
                (snap_dir / "depth").mkdir(parents=True, exist_ok=True)
                (snap_dir / "rgb").mkdir(parents=True, exist_ok=True)
                cv2.imwrite(str(snap_dir / "depth" / f"{stamp}.png"), z16)
                cv2.imwrite(str(snap_dir / "rgb" / f"{stamp}.png"), rgb)
                print(f"Da luu anh chup {stamp} (depth 16-bit, scale {depth_scale} m) vao {snap_dir}/")
    except KeyboardInterrupt:
        print("\nDa nhan Ctrl+C.")
    finally:
        pipeline.stop()
        cv2.destroyAllWindows()
        for summ in tracker.flush():
            print(mh.format_summary(summ))
            parts.append(summ)
        print(f"Da dung. {n_frames} frame, {n_obj} frame co vat, {len(parts)} vat da di qua.")


if __name__ == "__main__":
    main()
