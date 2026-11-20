namespace DUC_GUI.Models
{
    public enum DhJointType { Revolute, Prismatic }

    /// <summary>Một hàng bảng DH chuẩn: T = Rz(θ)·Tz(d)·Tx(a)·Rx(α). Biến khớp cộng vào θ (xoay) hoặc d (tịnh tiến).</summary>
    public sealed record DhRow(DhJointType Type, double ThetaDeg, double D, double A, double AlphaDeg);

    /// <summary>Một khâu 3D: file STL, hệ toạ độ DH gắn vào (0 = đế) và màu RAL.</summary>
    public sealed record LinkVisual(string File, int Frame, string Ral, string Hex);

    public static class RobotParameters
    {
        public const int NUM_DRIVERS = 4;

        // ===== MẠNG: 1 kết nối TCP duy nhất tới IPC + 1 cổng nhận kết quả Camera =====
        public const string IpcIpAddress = "192.168.1.19";   // "127.0.0.1" khi chạy scara_sim trong WSL
        public const int    IpcPort      = 8000;
        public const int    CameraPort   = 7500;             // Python Camera -> GUI (TCP)
        // Hình ảnh D455 (RGB + depth, MJPEG) từ camera/camera_service.py - cửa sổ Window > Camera
        public const string CameraViewUrl = "http://127.0.0.1:7501";

        // ===== KÍCH THƯỚC (mm) =====
        public const double A1 = 225.0;        // cánh tay 1
        public const double A2 = 175.0;        // cánh tay 2
        public const double D1 = 190.0;        // đế -> khớp 1
        public const double D2 = 76.33;        // lệch cao khớp 1 -> khớp 2
        public const double D3 = 99.83;        // offset trục Z (khâu 3)
        public const double D4 = 0.0;          // khâu 4 (dụng cụ) - cập nhật khi có bản vẽ

        // Theo DH, q3 = 0 <=> Z = D1 + D2 - D3 = 166.5 mm nhưng Z thực tế là 150 mm.
        // IPC đã xử lý offset này: MỌI toạ độ trên GUI và trong gói tin đều là Z THỰC TẾ.
        public const double Z_OFF = 16.5;
        public const double Z_MAX = 150.0;

        /// <summary>
        /// Bảng DH dùng để dựng mô hình 3D (q1, q2, q4: độ; q3: mm).
        /// Khớp 2 có α = 180° nên trục Z của khâu 3/4 hướng xuống.
        /// </summary>
        public static readonly DhRow[] DhTable =
        [
            new(DhJointType.Revolute,  0, D1, A1, 0),     // khớp 1
            new(DhJointType.Revolute,  0, D2, A2, 180),   // khớp 2
            new(DhJointType.Prismatic, 0, D3, 0,  0),     // khớp 3: d = D3 + q3
            new(DhJointType.Revolute,  0, D4, 0,  0),     // khớp 4
        ];

        /// <summary>
        /// Các khâu 3D và màu sơn theo bảng màu RAL công nghiệp.
        /// Khâu 3 (trục vít-me/spline) vừa tịnh tiến vừa xoay theo q4 nên gắn vào hệ 4.
        /// Link4.STL (dụng cụ) chưa có - khi thêm file vào Resources/Models3D sẽ tự hiển thị.
        /// </summary>
        public static readonly LinkVisual[] Links =
        [
            new("Link0", 0, "RAL 7016 Anthracite grey", "#383E42"),
            new("Link1", 1, "RAL 2004 Pure orange",     "#F44611"),
            new("Link2", 2, "RAL 1021 Colza yellow",    "#EEC900"),
            new("Link3", 4, "RAL 9006 White aluminium", "#A5A5A5"),
            new("Link4", 4, "RAL 3020 Traffic red",     "#CC0605"),
        ];
        public const string ViewportBackground = "#E9EDF1";

        // ===== JOG =====
        public const double JogStepDefault = 10.0;   // mm hoặc độ
        public const double JogStepMax     = 50.0;

        // ===== BÁM & GẮP VẬT (mặc định, chỉnh được trên GUI) =====
        public const double ConveyorVx      = 0.0;    // mm/s (hệ toạ độ robot)
        public const double ConveyorVy      = 0.0;
        public const double PickApproachDz  = 50.0;   // cao độ tiếp cận phía trên vật
        public const int    PickGripPin     = 2;
        public const int    PickGripValue   = 1;
        public const double PickDownMs      = 300;
        public const double PickGripMs      = 250;
        public const double PickUpMs        = 300;

        // ===== CSP / LINE (mặc định, chỉnh được trên GUI; giới hạn kiểm tra ở IPC - Robot_config_param.h) =====
        public const double LineSpeed       = 50.0;    // mm/s
        public const double LineAcc         = 500.0;   // mm/s^2
        public const double LineThetaSpeed  = 60.0;    // độ/s
        public const double LineDtMs        = 10.0;    // khoảng cách thời gian giữa 2 điểm IPC tạo ra
    }
}
