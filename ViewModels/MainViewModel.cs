using DUC_GUI.Models;
using DUC_GUI.Services;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Assimp;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.SharpDX.Model.Scene;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
// Alias để tránh xung đột kiểu giữa WPF và HelixToolkit
using HxCamera     = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using HxGeometry3D = HelixToolkit.SharpDX.Geometry3D;

namespace DUC_GUI.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        // ===================================================================================
        // 1. HẠ TẦNG MVVM
        // ===================================================================================
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name!);
            return true;
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

        // ===================================================================================
        // 2. DỊCH VỤ
        // ===================================================================================
        private readonly IpcClient          _ipc;
        private readonly CameraServer       _camera;
        private readonly TelemetryCsvLogger _csv;
        private readonly Dispatcher         _ui;

        // ===================================================================================
        // 3. HIỂN THỊ 3D (HelixToolkit SharpDX) – mỗi khâu 1 hình học, 1 vật liệu, 1 phép biến đổi DH
        // ===================================================================================
        public EffectsManager EffectsManager { get; }
        public HxCamera       Camera         { get; }
        public LineGeometry3D GridGeometry   { get; }

        private readonly HxGeometry3D?[] _linkGeometry = new HxGeometry3D?[RobotParameters.Links.Length];
        private readonly PhongMaterial[]  _linkMaterial = new PhongMaterial[RobotParameters.Links.Length];
        private readonly Transform3D[]    _linkTransform = new Transform3D[RobotParameters.Links.Length];

        public HxGeometry3D? Link0Geometry => _linkGeometry[0];
        public HxGeometry3D? Link1Geometry => _linkGeometry[1];
        public HxGeometry3D? Link2Geometry => _linkGeometry[2];
        public HxGeometry3D? Link3Geometry => _linkGeometry[3];
        public HxGeometry3D? Link4Geometry => _linkGeometry[4];
        public PhongMaterial Link0Material => _linkMaterial[0];
        public PhongMaterial Link1Material => _linkMaterial[1];
        public PhongMaterial Link2Material => _linkMaterial[2];
        public PhongMaterial Link3Material => _linkMaterial[3];
        public PhongMaterial Link4Material => _linkMaterial[4];
        public Transform3D   Link0Transform => _linkTransform[0];
        public Transform3D   Link1Transform => _linkTransform[1];
        public Transform3D   Link2Transform => _linkTransform[2];
        public Transform3D   Link3Transform => _linkTransform[3];
        public Transform3D   Link4Transform => _linkTransform[4];
        public string        LinkColorLegend { get; } =
            string.Join("   ", RobotParameters.Links.Select(l => $"{l.File}: {l.Ral}"));

        // ===================================================================================
        // 4. TOẠ ĐỘ KHỚP & ĐỀ-CÁC (lấy thẳng từ IPC – IPC là nơi duy nhất tính động học)
        // ===================================================================================
        private double _j1, _j2, _j3, _j4, _x, _y, _z, _theta;
        public double J1 { get => _j1; private set => Set(ref _j1, value); }
        public double J2 { get => _j2; private set => Set(ref _j2, value); }
        public double J3 { get => _j3; private set => Set(ref _j3, value); }
        public double J4 { get => _j4; private set => Set(ref _j4, value); }
        public double RobotX     { get => _x;     private set => Set(ref _x, value); }
        public double RobotY     { get => _y;     private set => Set(ref _y, value); }
        public double RobotZ     { get => _z;     private set => Set(ref _z, value); }   // Z thực tế
        public double RobotTheta { get => _theta; private set => Set(ref _theta, value); }

        // ===================================================================================
        // 5. TRẠNG THÁI KẾT NỐI / IPC
        // ===================================================================================
        private string _ipcAddress = RobotParameters.IpcIpAddress;
        public string IpcAddress { get => _ipcAddress; set => Set(ref _ipcAddress, value); }

        private bool _isIpcConnected;
        public bool IsIpcConnected { get => _isIpcConnected; private set => Set(ref _isIpcConnected, value); }

        /// <summary>IPC đã trả ACK cho CON (EtherCAT sẵn sàng) – chỉ khi đó mới hiện các nút điều khiển.</summary>
        private bool _isConnectionConfirmed;
        public bool IsConnectionConfirmed
        {
            get => _isConnectionConfirmed;
            private set { if (Set(ref _isConnectionConfirmed, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        private string _ipcMode = "OFFLINE";
        public string IpcMode
        {
            get => _ipcMode;
            private set { if (Set(ref _ipcMode, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        private bool _isHalted;
        public bool IsHalted { get => _isHalted; private set => Set(ref _isHalted, value); }

        private int _jogMask;
        public int JogMask
        {
            get => _jogMask;
            private set { if (Set(ref _jogMask, value)) CommandManager.InvalidateRequerySuggested(); }
        }

        private string _statusText = "Chưa kết nối IPC";
        public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

        private string _driveStatus = "";
        public string DriveStatus { get => _driveStatus; private set => Set(ref _driveStatus, value); }

        private string _stepText = "";
        public string StepText { get => _stepText; private set => Set(ref _stepText, value); }

        private string _cameraStatus = "Camera: chưa có kết nối";
        public string CameraStatus { get => _cameraStatus; private set => Set(ref _cameraStatus, value); }

        private int _cameraDetectId;
        public int CameraDetectId { get => _cameraDetectId; private set => Set(ref _cameraDetectId, value); }

        private string _lastTxJson = "";
        public string LastTxJson { get => _lastTxJson; private set => Set(ref _lastTxJson, value); }

        private string _lastCameraJson = "";
        public string LastCameraJson { get => _lastCameraJson; private set => Set(ref _lastCameraJson, value); }

        private bool _isProgramRunning;
        public bool IsProgramRunning
        {
            get => _isProgramRunning;
            private set { if (Set(ref _isProgramRunning, value) && !value) EngineStatus = ""; }
        }

        private string _engineStatus = "";
        public string EngineStatus { get => _engineStatus; private set => Set(ref _engineStatus, value); }

        // ===================================================================================
        // 6. CHẾ ĐỘ VẬN HÀNH
        // ===================================================================================
        private bool _isOperatingMode = true;
        public bool IsOperatingMode { get => _isOperatingMode; set => Set(ref _isOperatingMode, value); }

        private bool _isTeachingMode;
        public bool IsTeachingMode
        {
            get => _isTeachingMode;
            set
            {
                if (!Set(ref _isTeachingMode, value) || !value) return;
                CancelProgram();
                if (IsJoggingMode) SendSimple("JOG");
                else if (IsHandingMode) SendSimple("HAN");
                else IsHandingMode = true;   // mặc định vào Teaching là Handing (nhả khớp)
            }
        }

        private bool _isHandingMode;
        public bool IsHandingMode
        {
            get => _isHandingMode;
            set { if (Set(ref _isHandingMode, value) && value) SendSimple("HAN"); }
        }

        private bool _isJoggingMode;
        public bool IsJoggingMode
        {
            get => _isJoggingMode;
            set
            {
                if (!Set(ref _isJoggingMode, value)) return;
                if (value) SendSimple("JOG");
                CommandManager.InvalidateRequerySuggested();
            }
        }

        // Kine/P2P là chế độ duy nhất đã có; Dyna/Trajectory để trống (IPC sẽ từ chối).
        private bool _isKineMode = true, _isDynaMode, _isP2PTask = true, _isTrajectoryTask, _isOnceMode = true, _isLoopMode;
        public bool IsKineMode       { get => _isKineMode;       set => Set(ref _isKineMode, value); }
        public bool IsDynaMode       { get => _isDynaMode;       set => Set(ref _isDynaMode, value); }
        public bool IsP2PTask        { get => _isP2PTask;        set => Set(ref _isP2PTask, value); }
        public bool IsTrajectoryTask { get => _isTrajectoryTask; set => Set(ref _isTrajectoryTask, value); }
        public bool IsOnceMode       { get => _isOnceMode;       set => Set(ref _isOnceMode, value); }
        public bool IsLoopMode       { get => _isLoopMode;       set => Set(ref _isLoopMode, value); }

        // ===================================================================================
        // 7. NHẬP LIỆU CHO CÁC KHỐI LỆNH
        // ===================================================================================
        public string InputX     { get; set; } = "400";
        public string InputY     { get; set; } = "0";
        public string InputZ     { get; set; } = "150";
        public string InputTheta { get; set; } = "0";
        public string InputTime  { get; set; } = "1000";

        private string _inputDelayMs = "500";
        public string InputDelayMs { get => _inputDelayMs; set => Set(ref _inputDelayMs, value); }

        public int[] GpioPinOptions { get; } = [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];

        private int _inputGpioPin = 2;
        public int InputGpioPin { get => _inputGpioPin; set => Set(ref _inputGpioPin, value); }

        private bool _isGpioHigh;
        public bool IsGpioHigh
        {
            get => _isGpioHigh;
            set { if (Set(ref _isGpioHigh, value)) OnPropertyChanged(nameof(IsGpioLow)); }
        }
        public bool IsGpioLow { get => !_isGpioHigh; set => IsGpioHigh = !value; }

        private string _inputIfConditionId = "1";
        public string InputIfConditionId { get => _inputIfConditionId; set => Set(ref _inputIfConditionId, value); }

        private string _inputCameraJson = "{\"camera_detect_id\":1,\"x\":150,\"y\":-250,\"z\":40,\"theta\":20}";
        public string InputCameraJson { get => _inputCameraJson; set => Set(ref _inputCameraJson, value); }

        // Cài đặt bám & gắp vật trên băng tải
        public string ConveyorVx      { get; set; } = RobotParameters.ConveyorVx.ToString(Inv);
        public string ConveyorVy      { get; set; } = RobotParameters.ConveyorVy.ToString(Inv);
        public string PickApproachDz  { get; set; } = RobotParameters.PickApproachDz.ToString(Inv);
        public string PickDownMs      { get; set; } = RobotParameters.PickDownMs.ToString(Inv);
        public string PickGripMs      { get; set; } = RobotParameters.PickGripMs.ToString(Inv);
        public string PickUpMs        { get; set; } = RobotParameters.PickUpMs.ToString(Inv);
        public bool   PickUseGpio     { get; set; } = true;
        public int    PickGripPin     { get; set; } = RobotParameters.PickGripPin;
        public bool   PickGripHigh    { get; set; } = RobotParameters.PickGripValue != 0;

        // Jog
        public string JogStep { get; set; } = RobotParameters.JogStepDefault.ToString(Inv);

        // ===================================================================================
        // 8. CHƯƠNG TRÌNH
        // ===================================================================================
        public ObservableCollection<ProgramBlock> ProgramBlocks { get; } = [];

        private ProgramBlock? _selectedProgramBlock;
        public ProgramBlock? SelectedProgramBlock { get => _selectedProgramBlock; set => Set(ref _selectedProgramBlock, value); }

        private CancellationTokenSource? _executionCts;
        private TaskCompletionSource<CameraDetection>? _cameraWaiter;
        private CameraDetection? _testDetection;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<IpcEvent>> _pendingPrograms = new();
        private int _programId;

        private enum StaResult { Done, Aborted, Rejected, Cancelled }

        // ===================================================================================
        // 9. LỆNH (COMMANDS)
        // ===================================================================================
        public ICommand SendCommandToIpcCommand   { get; }
        public ICommand ImportProgCommand         { get; }
        public ICommand SaveProgCommand           { get; }
        public ICommand JogXPlusCommand           { get; }
        public ICommand JogXMinusCommand          { get; }
        public ICommand JogYPlusCommand           { get; }
        public ICommand JogYMinusCommand          { get; }
        public ICommand JogZPlusCommand           { get; }
        public ICommand JogZMinusCommand          { get; }
        public ICommand JogThetaPlusCommand       { get; }
        public ICommand JogThetaMinusCommand      { get; }
        public ICommand AddTeachPointCommand      { get; }
        public ICommand AddMoveBlockCommand       { get; }
        public ICommand AddDelayBlockCommand      { get; }
        public ICommand AddGpioBlockCommand       { get; }
        public ICommand AddIfBranchCommand        { get; }
        public ICommand AddElseBranchCommand      { get; }
        public ICommand AddEndIfCommand           { get; }
        public ICommand DeleteProgramBlockCommand { get; }
        public ICommand ClearProgramBlocksCommand { get; }
        public ICommand ApplyCameraJsonCommand    { get; }

        /// <summary>Log hiển thị lên cửa sổ Console (đã gắn giờ).</summary>
        public event Action<string>? LogLine;

        // ===================================================================================
        // 10. KHỞI TẠO
        // ===================================================================================
        public MainViewModel(IpcClient ipc, CameraServer camera, TelemetryCsvLogger csv)
        {
            _ipc    = ipc;
            _camera = camera;
            _csv    = csv;
            _ui     = Application.Current.Dispatcher;

            EffectsManager = new DefaultEffectsManager();
            Camera = new HxCamera
            {
                Position          = new Point3D(800, 800, 600),
                LookDirection     = new Vector3D(-800, -800, -600),
                UpDirection       = new Vector3D(0, 0, 1),
                NearPlaneDistance = 0.1,
                FarPlaneDistance  = 100000.0
            };
            GridGeometry = BuildGridGeometry();
            for (int i = 0; i < RobotParameters.Links.Length; i++)
                _linkMaterial[i] = MakeMaterial(RobotParameters.Links[i].Hex);
            LoadRobotModels();
            UpdateRobotPose();

            SendCommandToIpcCommand = new RobotCommand(p => _ = ExecuteSendCommandAsync(p as string));
            ImportProgCommand       = new RobotCommand(_ => ExecuteImportProg());
            SaveProgCommand         = new RobotCommand(_ => ExecuteSaveProg());

            JogXPlusCommand      = new RelayCommand(() => Jog("X", +1),     () => CanJog(0));
            JogXMinusCommand     = new RelayCommand(() => Jog("X", -1),     () => CanJog(1));
            JogYPlusCommand      = new RelayCommand(() => Jog("Y", +1),     () => CanJog(2));
            JogYMinusCommand     = new RelayCommand(() => Jog("Y", -1),     () => CanJog(3));
            JogZPlusCommand      = new RelayCommand(() => Jog("Z", +1),     () => CanJog(4));
            JogZMinusCommand     = new RelayCommand(() => Jog("Z", -1),     () => CanJog(5));
            JogThetaPlusCommand  = new RelayCommand(() => Jog("Theta", +1), () => CanJog(6));
            JogThetaMinusCommand = new RelayCommand(() => Jog("Theta", -1), () => CanJog(7));
            AddTeachPointCommand = new RelayCommand(ExecuteAddTeachPoint, () => IsConnectionConfirmed);

            AddMoveBlockCommand       = new RobotCommand(_ => ExecuteAddMoveBlock());
            AddDelayBlockCommand      = new RobotCommand(_ => ExecuteAddDelayBlock());
            AddGpioBlockCommand       = new RobotCommand(_ => ProgramBlocks.Add(new GpioBlock { Pin = InputGpioPin, Value = IsGpioHigh ? 1 : 0 }));
            AddIfBranchCommand        = new RobotCommand(_ => ExecuteAddIfBranch());
            AddElseBranchCommand      = new RobotCommand(_ => ProgramBlocks.Add(new ElseBranchBlock()));
            AddEndIfCommand           = new RobotCommand(_ => ProgramBlocks.Add(new EndIfBlock()));
            DeleteProgramBlockCommand = new RobotCommand(_ => { if (SelectedProgramBlock != null) ProgramBlocks.Remove(SelectedProgramBlock); });
            ClearProgramBlocksCommand = new RobotCommand(_ => ProgramBlocks.Clear());
            ApplyCameraJsonCommand    = new RobotCommand(_ => ExecuteApplyCameraJson());

            _ipc.LogLine            += Log;
            _ipc.ConnectionChanged  += up => _ui.InvokeAsync(() => OnConnectionChanged(up));
            _ipc.TelemetryReceived  += OnTelemetry;
            _ipc.EventReceived      += OnIpcEvent;
            _camera.LogLine         += Log;
            _camera.DetectionReceived += OnCameraDetection;
            _camera.ClientCountChanged += n => _ui.InvokeAsync(() =>
                CameraStatus = n > 0 ? $"Camera: {n} kết nối" : "Camera: chưa có kết nối");
        }

        private void Log(string line) => LogLine?.Invoke(line);
        private void LogUi(string text) => Log($"[{DateTime.Now:HH:mm:ss}] [GUI] {text}\r\n");

        // ===================================================================================
        // 11. SỰ KIỆN TỪ IPC / CAMERA
        // ===================================================================================
        private void OnConnectionChanged(bool up)
        {
            IsIpcConnected = up;
            if (up) return;
            CancelProgram();
            IsConnectionConfirmed = false;
            IpcMode    = "OFFLINE";
            JogMask    = 0;
            StatusText = "Mất kết nối IPC";
            _csv.Stop();
        }

        // Phản hồi 50 Hz: ghi CSV ngay trên luồng nhận, cập nhật giao diện gộp (không dồn hàng đợi UI)
        private Telemetry? _latestTelemetry;
        private int _telemetryQueued;

        private void OnTelemetry(Telemetry t)
        {
            _csv.Write(t);
            Volatile.Write(ref _latestTelemetry, t);
            if (Interlocked.Exchange(ref _telemetryQueued, 1) == 0)
                _ui.InvokeAsync(ApplyLatestTelemetry, DispatcherPriority.Render);
        }

        private void ApplyLatestTelemetry()
        {
            Interlocked.Exchange(ref _telemetryQueued, 0);
            var t = Volatile.Read(ref _latestTelemetry);
            if (t == null || t.Q.Length < 4 || t.P.Length < 4) return;

            J1 = t.Q[0]; J2 = t.Q[1]; J3 = t.Q[2]; J4 = t.Q[3];
            RobotX = t.P[0]; RobotY = t.P[1]; RobotZ = t.P[2]; RobotTheta = t.P[3];
            IpcMode  = t.Mode;
            IsHalted = t.Halt;
            JogMask  = t.Jog;
            StepText = t.Steps > 0 ? $"Chương trình {t.Prog}: bước {t.Step}/{t.Steps} (dòng {t.Line})" : "";
            DriveStatus = string.Join("  ", Enumerable.Range(0, Math.Min(t.Drv, t.Ds.Length))
                                                      .Select(i => $"D{i + 1}:{Cia402.Name(t.Ds[i])}")) +
                          (t.Lost > 0 ? $"  | mất frame: {t.Lost}" : "");
            UpdateRobotPose();
        }

        private void OnIpcEvent(IpcEvent evt)
        {
            if (_pendingPrograms.TryGetValue(evt.Id, out var tcs))
                tcs.TrySetResult(evt);
        }

        private void OnCameraDetection(CameraDetection det)
        {
            _ui.InvokeAsync(() => { LastCameraJson = det.Raw; CameraDetectId = det.Id; });
            if (_cameraWaiter?.TrySetResult(det) == true)
                LogUi($"Camera: vật ID={det.Id} tại ({det.X:F1}, {det.Y:F1}, {det.Z:F1}) → bắt đầu gắp");
            else
                LogUi($"Camera: bỏ qua vật ID={det.Id} (robot đang bận hoặc chưa Start)");
        }

        // ===================================================================================
        // 12. LỆNH IPC
        // ===================================================================================
        private async Task ExecuteSendCommandAsync(string? command)
        {
            switch (command)
            {
                case "CON": await ConnectAsync(); break;
                case "DIS": await DisconnectAsync(); break;
                case "STA": await ExecuteProgramAsync(); break;
                case "STO":
                    CancelProgram();
                    SendSimple("STO");
                    break;
                case "HAL":
                case "RES":
                    SendSimple(IsHalted ? "RES" : "HAL");
                    break;
                case "HOM":
                    CancelProgram();
                    SendSimple("HOM");
                    break;
            }
        }

        private bool _connecting;

        private async Task ConnectAsync()
        {
            if (_connecting) return;
            _connecting = true;
            try
            {
                if (!_ipc.IsConnected)
                {
                    StatusText = $"Đang kết nối {IpcAddress}:{RobotParameters.IpcPort} ...";
                    if (!await _ipc.ConnectAsync(IpcAddress.Trim(), RobotParameters.IpcPort))
                    {
                        StatusText = "Không kết nối được IPC";
                        return;
                    }
                }
                StatusText = "Đang khởi tạo EtherCAT trên IPC ...";
                var ack = await _ipc.RequestAsync(new { Cmd = "CON" }, "CON", TimeSpan.FromSeconds(20));
                IsConnectionConfirmed = ack?.Ok == true;
                StatusText = ack == null ? "IPC không phản hồi lệnh CON" : ack.Msg;
                if (!IsConnectionConfirmed) return;

                _csv.Start();
                LogUi($"Ghi dữ liệu phản hồi vào {_csv.CurrentFile}");
                if (IsTeachingMode) SendSimple(IsJoggingMode ? "JOG" : "HAN");
            }
            finally
            {
                _connecting = false;
            }
        }

        private async Task DisconnectAsync()
        {
            CancelProgram();
            if (_ipc.IsConnected)
                await _ipc.RequestAsync(new { Cmd = "DIS" }, "DIS", TimeSpan.FromSeconds(3));
            _ipc.Disconnect();
            StatusText = "Đã ngắt kết nối";
        }

        /// <summary>Gọi khi đóng ứng dụng: gửi DIS để IPC dừng robot và đóng EtherCAT.</summary>
        public Task ShutdownAsync() => DisconnectAsync();

        private void SendSimple(string cmd)
        {
            if (IsConnectionConfirmed)
                _ = _ipc.SendAsync(new { Cmd = cmd });
        }

        // ===================================================================================
        // 13. TEACHING: JOG + ĐIỂM DẠY
        // ===================================================================================
        // Nút +/- bị khoá khi IPC báo đi thêm 1 mm/1° theo hướng đó sẽ vượt giới hạn khớp,
        // biên vùng làm việc hoặc chạm lồng kính (bit tương ứng trong JogMask).
        private bool CanJog(int bit) =>
            IsConnectionConfirmed && IsJoggingMode && IpcMode is "HOLD" or "RUN" && (JogMask & (1 << bit)) != 0;

        private void Jog(string axis, int direction)
        {
            if (!double.TryParse(JogStep, NumberStyles.Float, Inv, out double step) || step <= 0)
                step = RobotParameters.JogStepDefault;
            step = Math.Min(step, RobotParameters.JogStepMax);
            _ = _ipc.SendAsync(new { Cmd = "JOG", Axis = axis, Step = direction * step });
        }

        /// <summary>Handing/Jogging: lấy toạ độ điểm công tác hiện tại (Z thực tế) thành 1 khối MOVE.</summary>
        private void ExecuteAddTeachPoint()
        {
            string time = double.TryParse(InputTime, NumberStyles.Float, Inv, out double t) && t >= 100 ? InputTime : "1000";
            ProgramBlocks.Add(new MoveBlock
            {
                X = RobotX.ToString("F2", Inv), Y = RobotY.ToString("F2", Inv),
                Z = RobotZ.ToString("F2", Inv), Theta = RobotTheta.ToString("F2", Inv),
                Time = time
            });
        }

        // ===================================================================================
        // 14. THÊM KHỐI LỆNH
        // ===================================================================================
        private static string SanitizeNumber(string raw)
        {
            var sb = new StringBuilder();
            foreach (char c in raw.Trim().Replace(',', '.'))
                if (c == '-' || c == '.' || char.IsAsciiDigit(c))
                    sb.Append(c);
            return sb.Length > 0 ? sb.ToString() : "0";
        }

        private void ExecuteAddMoveBlock()
        {
            if (!double.TryParse(InputTime, NumberStyles.Float, Inv, out double tMs) || tMs < 100) tMs = 100;
            ProgramBlocks.Add(new MoveBlock
            {
                X = SanitizeNumber(InputX), Y = SanitizeNumber(InputY), Z = SanitizeNumber(InputZ),
                Theta = SanitizeNumber(InputTheta), Time = ((int)tMs).ToString(Inv)
            });
        }

        private void ExecuteAddDelayBlock()
        {
            if (!double.TryParse(InputDelayMs, NumberStyles.Float, Inv, out double d) || d < 0) d = 0;
            ProgramBlocks.Add(new DelayBlock { DelayMs = ((int)d).ToString(Inv) });
        }

        private void ExecuteAddIfBranch()
        {
            if (!int.TryParse(InputIfConditionId, out int id) || id < 0) id = 1;
            bool isFirst = !ProgramBlocks.OfType<IfBranchBlock>().Any();
            ProgramBlocks.Add(new IfBranchBlock { ConditionId = id, IsFirstIf = isFirst });
        }

        // ===================================================================================
        // 15. ENGINE THỰC THI CHƯƠNG TRÌNH
        // ===================================================================================
        //  - Không có IF: gửi toàn bộ khối thành 1 gói STA (Once/Loop theo lựa chọn), chờ DONE.
        //  - Có IF..END (phân loại bằng camera, luôn coi là Once):
        //      1. Gửi phần HEAD (trước IF) và chờ IPC báo DONE.
        //      2. Lặp: chờ camera phát hiện vật -> chọn nhánh theo ID (không khớp thì ELSE)
        //         -> gửi [PICK vật] + [thân nhánh] + [phần sau END] -> chờ DONE/ABORT
        //         -> robot đứng yên tại chỗ chờ vật tiếp theo. Dừng khi bấm Stop.
        private async Task ExecuteProgramAsync()
        {
            if (IsProgramRunning) return;
            if (!IsConnectionConfirmed)
            {
                LogUi("Chưa kết nối IPC.");
                return;
            }
            var blocks = ProgramBlocks.ToList();
            if (blocks.Count == 0)
            {
                LogUi("Chưa có khối lệnh nào trong chương trình.");
                return;
            }

            IsProgramRunning = true;
            _executionCts    = new CancellationTokenSource();
            try
            {
                await RunBlocksAsync(blocks, _executionCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (FormatException ex)
            {
                LogUi($"Khối lệnh không hợp lệ: {ex.Message}");
                MessageBox.Show(ex.Message, "Chương trình không hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                IsProgramRunning = false;
                _cameraWaiter    = null;
                _executionCts?.Dispose();
                _executionCts = null;
            }
        }

        private void CancelProgram()
        {
            _executionCts?.Cancel();
            _testDetection = null;
        }

        private async Task RunBlocksAsync(List<ProgramBlock> blocks, CancellationToken token)
        {
            int firstIf = blocks.FindIndex(b => b is IfBranchBlock { IsFirstIf: true });
            if (firstIf < 0)
            {
                await SendStaAndWaitAsync(ToLines(blocks), IsLoopMode, token);
                return;
            }

            int endIf = FindEndIfBlockIndex(blocks, firstIf);
            if (endIf < 0)
                throw new FormatException("Khối IF chưa có END tương ứng.");
            var tail = blocks.Skip(endIf + 1).ToList();

            var head = ToLines(blocks.Take(firstIf));
            if (head.Count > 0 && await SendStaAndWaitAsync(head, false, token) != StaResult.Done)
                return;

            while (!token.IsCancellationRequested)
            {
                EngineStatus = "Đang chờ camera phát hiện vật ...";
                CameraDetection det = await WaitCameraAsync(token);

                var (bodyStart, bodyEnd) = FindBranchByDetectId(blocks, firstIf, endIf, det.Id);
                if (bodyStart < 0)
                {
                    LogUi($"ID={det.Id} không khớp nhánh nào và không có ELSE → bỏ qua vật này.");
                    continue;
                }
                var lines = new List<object> { PickLine(det) };
                lines.AddRange(ToLines(blocks.Skip(bodyStart).Take(bodyEnd - bodyStart), lines.Count));
                lines.AddRange(ToLines(tail, lines.Count));

                EngineStatus = $"Đang gắp vật ID={det.Id}";
                var result = await SendStaAndWaitAsync(lines, false, token);
                if (result is StaResult.Rejected or StaResult.Cancelled) return;
                if (result == StaResult.Aborted) LogUi("Lần gắp này thất bại – robot giữ vị trí, chờ vật tiếp theo.");
            }
        }

        private async Task<CameraDetection> WaitCameraAsync(CancellationToken token)
        {
            var pending = Interlocked.Exchange(ref _testDetection, null);
            if (pending != null) return pending;

            var tcs = new TaskCompletionSource<CameraDetection>(TaskCreationOptions.RunContinuationsAsynchronously);
            _cameraWaiter = tcs;
            try { return await tcs.Task.WaitAsync(token); }
            finally { _cameraWaiter = null; }
        }

        private async Task<StaResult> SendStaAndWaitAsync(List<object> lines, bool loop, CancellationToken token)
        {
            if (lines.Count == 0 || token.IsCancellationRequested) return StaResult.Cancelled;

            int id = Interlocked.Increment(ref _programId);
            var packet = new
            {
                Cmd     = "STA",
                Id      = id,
                Mode    = IsKineMode ? "Kine" : "Dyna",
                Task    = IsP2PTask ? "P2P" : "Trajectory",
                Exec    = loop ? "Loop" : "Once",
                Program = lines
            };
            LastTxJson = JsonSerializer.Serialize(packet, PrettyJson);

            var done = new TaskCompletionSource<IpcEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingPrograms[id] = done;
            try
            {
                var ack = await _ipc.RequestAsync(packet, "STA", TimeSpan.FromSeconds(3));
                if (ack?.Ok != true)
                {
                    EngineStatus = $"IPC từ chối chương trình: {ack?.Msg ?? "không phản hồi"}";
                    return StaResult.Rejected;
                }
                var evt = await done.Task.WaitAsync(token);
                return evt.E == "DONE" ? StaResult.Done : StaResult.Aborted;
            }
            catch (OperationCanceledException)
            {
                return StaResult.Cancelled;
            }
            finally
            {
                _pendingPrograms.TryRemove(id, out _);
            }
        }

        /// <summary>Lệnh PICK: IPC tự tính điểm gặp vật đang trôi, hạ xuống, kẹp, nhấc lên (luôn bám theo vật).</summary>
        private object PickLine(CameraDetection d)
        {
            double dz = ParseSetting(PickApproachDz, "Cao độ tiếp cận");
            return new
            {
                Line  = 1,
                Type  = "Pick",
                X     = Math.Round(d.X, 2),
                Y     = Math.Round(d.Y, 2),
                Z     = Math.Round(d.Z, 2),
                Theta = Math.Round(d.Theta, 2),
                Vx    = d.Vx ?? ParseSetting(ConveyorVx, "Vx băng tải"),
                Vy    = d.Vy ?? ParseSetting(ConveyorVy, "Vy băng tải"),
                Age   = Math.Round(d.AgeMs),
                ZApp  = Math.Round(Math.Min(d.Z + dz, RobotParameters.Z_MAX), 2),
                Pin   = PickUseGpio ? PickGripPin : -1,
                Value = PickGripHigh ? 1 : 0,
                TDown = ParseSetting(PickDownMs, "Thời gian hạ"),
                TGrip = ParseSetting(PickGripMs, "Thời gian kẹp"),
                TUp   = ParseSetting(PickUpMs, "Thời gian nhấc")
            };
        }

        private static double ParseSetting(string text, string what) =>
            double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, Inv, out double v)
                ? v
                : throw new FormatException($"{what} = '{text}' không phải là số.");

        /// <summary>Chuyển khối Move/Delay/GPIO thành các dòng trong "Program" của gói STA (bỏ qua IF/ELSE/END).</summary>
        private static List<object> ToLines(IEnumerable<ProgramBlock> blocks, int startLine = 0)
        {
            var lines = new List<object>();
            foreach (var block in blocks)
            {
                int line = startLine + lines.Count + 1;
                switch (block)
                {
                    case MoveBlock mb:
                        lines.Add(new
                        {
                            Line  = line, Type = "Move",
                            X     = ParseSetting(mb.X, $"Dòng {line}: X"),
                            Y     = ParseSetting(mb.Y, $"Dòng {line}: Y"),
                            Z     = ParseSetting(mb.Z, $"Dòng {line}: Z"),
                            Theta = ParseSetting(mb.Theta, $"Dòng {line}: Theta"),
                            Time  = ParseSetting(mb.Time, $"Dòng {line}: Time")
                        });
                        break;
                    case DelayBlock db:
                        lines.Add(new { Line = line, Type = "Delay", Ms = ParseSetting(db.DelayMs, $"Dòng {line}: Delay") });
                        break;
                    case GpioBlock gb:
                        lines.Add(new { Line = line, Type = "Gpio", gb.Pin, gb.Value });
                        break;
                }
            }
            return lines;
        }

        private static int FindEndIfBlockIndex(List<ProgramBlock> blocks, int firstIfIdx)
        {
            for (int i = firstIfIdx + 1; i < blocks.Count; i++)
                if (blocks[i] is EndIfBlock) return i;
            return -1;
        }

        /// <summary>Thân nhánh [start, end) khớp ID; không khớp thì nhánh ELSE; không có ELSE trả (-1,-1).</summary>
        private static (int start, int end) FindBranchByDetectId(List<ProgramBlock> blocks, int firstIf, int endIf, int id)
        {
            int match = -1, elseStart = -1;
            for (int i = firstIf; i < endIf; i++)
            {
                if (blocks[i] is IfBranchBlock ifb && ifb.ConditionId == id && match < 0) match = i + 1;
                else if (blocks[i] is ElseBranchBlock && elseStart < 0) elseStart = i + 1;
            }
            int start = match >= 0 ? match : elseStart;
            if (start < 0) return (-1, -1);
            int end = start;
            while (end < endIf && blocks[end] is not (IfBranchBlock or ElseBranchBlock or EndIfBlock)) end++;
            return (start, end);
        }

        // Thử nhanh bằng JSON dán tay (không cần Python): nếu engine chưa chạy thì chạy luôn
        private void ExecuteApplyCameraJson()
        {
            if (!CameraServer.TryParse(InputCameraJson, out var det))
            {
                LogUi("JSON camera không hợp lệ (cần camera_detect_id/ID, x, y, z, theta).");
                return;
            }
            LastCameraJson = det.Raw;
            CameraDetectId = det.Id;
            if (_cameraWaiter?.TrySetResult(det) == true) return;

            _testDetection = det;   // engine sẽ dùng khi tới lượt chờ camera
            if (!IsProgramRunning)
            {
                if (!ProgramBlocks.OfType<IfBranchBlock>().Any())
                {
                    ProgramBlocks.Add(new IfBranchBlock { ConditionId = det.Id, IsFirstIf = true });
                    ProgramBlocks.Add(new ElseBranchBlock());
                    ProgramBlocks.Add(new EndIfBlock());
                    LogUi("Tạo chương trình thử: IF [ID] / ELSE / END.");
                }
                _ = ExecuteProgramAsync();
            }
        }

        // ===================================================================================
        // 16. LƯU / NẠP CHƯƠNG TRÌNH (.txt: BlockType|Param1|Param2|...)
        // ===================================================================================
        private static string GetProgramDirectoryPath()
        {
            string baseDir = AppContext.BaseDirectory;
            string dir     = Path.Combine(baseDir, "Program");
#if DEBUG
            var info = new DirectoryInfo(baseDir);
            if (info.Parent?.Parent?.Parent != null)
                dir = Path.Combine(info.Parent.Parent.Parent.FullName, "Program");
#endif
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void ExecuteSaveProg()
        {
            string dir = GetProgramDirectoryPath();
            int n = 1;
            while (File.Exists(Path.Combine(dir, $"program{n}.txt"))) n++;
            var dialog = new SaveFileDialog { Filter = "Text files (*.txt)|*.txt", InitialDirectory = dir, FileName = $"program{n}.txt" };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Robot Program File");
                sb.AppendLine($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine("# Format: Move|X|Y|Z|Theta|Time  Delay|ms  Gpio|Pin|Value  If|ID|IsFirst  Else  End");
                sb.AppendLine("#");
                foreach (var block in ProgramBlocks)
                {
                    string? line = block switch
                    {
                        MoveBlock mb       => $"Move|{mb.X}|{mb.Y}|{mb.Z}|{mb.Theta}|{mb.Time}",
                        DelayBlock db      => $"Delay|{db.DelayMs}",
                        GpioBlock gb       => $"Gpio|{gb.Pin}|{gb.Value}",
                        IfBranchBlock ifb  => $"If|{ifb.ConditionId}|{(ifb.IsFirstIf ? 1 : 0)}",
                        ElseBranchBlock    => "Else",
                        EndIfBlock         => "End",
                        _                  => null
                    };
                    if (line != null) sb.AppendLine(line);
                }
                File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show($"Đã lưu {ProgramBlocks.Count} khối lệnh vào:\n{Path.GetFileName(dialog.FileName)}",
                                "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi lưu chương trình: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExecuteImportProg()
        {
            var dialog = new OpenFileDialog { Filter = "Text files (*.txt)|*.txt", InitialDirectory = GetProgramDirectoryPath() };
            if (dialog.ShowDialog() != true) return;

            try
            {
                ProgramBlocks.Clear();
                foreach (string raw in File.ReadAllLines(dialog.FileName))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    var block = DeserializeProgramBlock(line.Split('|'));
                    if (block != null) ProgramBlocks.Add(block);
                }
                MessageBox.Show($"Đã tải chương trình: {ProgramBlocks.Count} khối lệnh.", "Thành công",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi tải chương trình: {ex.Message}", "Lỗi Import", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static ProgramBlock? DeserializeProgramBlock(string[] p)
        {
            string Arg(int i, string def) => p.Length > i ? p[i].Trim() : def;
            int IntArg(int i, int def) => int.TryParse(Arg(i, ""), out int v) ? v : def;

            return Arg(0, "") switch
            {
                "Move"  => new MoveBlock { X = Arg(1, "0"), Y = Arg(2, "0"), Z = Arg(3, "0"), Theta = Arg(4, "0"), Time = Arg(5, "1000") },
                "Delay" => new DelayBlock { DelayMs = Arg(1, "500") },
                "Gpio"  => new GpioBlock { Pin = IntArg(1, 2), Value = IntArg(2, 0) != 0 ? 1 : 0 },   // "OUT" cũ -> 0
                "If"    => new IfBranchBlock { ConditionId = IntArg(1, 1), IsFirstIf = Arg(2, "0") == "1" },
                "Else"  => new ElseBranchBlock(),
                "End"   => new EndIfBlock(),
                _       => null
            };
        }

        // ===================================================================================
        // 17. MÔ HÌNH 3D
        // ===================================================================================
        private static LineGeometry3D BuildGridGeometry()
        {
            var lb = new LineBuilder();
            const int half = 500, step = 50;
            for (int v = -half; v <= half; v += step)
            {
                lb.AddLine(new System.Numerics.Vector3(v, -half, 0), new System.Numerics.Vector3(v, half, 0));
                lb.AddLine(new System.Numerics.Vector3(-half, v, 0), new System.Numerics.Vector3(half, v, 0));
            }
            return lb.ToLineGeometry3D();
        }

        private static PhongMaterial MakeMaterial(string hex)
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            return new PhongMaterial
            {
                DiffuseColor      = new HelixToolkit.Maths.Color4(r, g, b, 1f),
                AmbientColor      = new HelixToolkit.Maths.Color4(r * 0.25f, g * 0.25f, b * 0.25f, 1f),
                SpecularColor     = new HelixToolkit.Maths.Color4(0.35f, 0.35f, 0.35f, 1f),
                SpecularShininess = 40f
            };
        }

        private void LoadRobotModels()
        {
            try
            {
                var dir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "Resources", "Models3D"));
                if (!dir.Exists) return;
                using var importer = new Importer();
                for (int i = 0; i < RobotParameters.Links.Length; i++)
                {
                    var file = dir.GetFiles($"{RobotParameters.Links[i].File}.*").FirstOrDefault();
                    if (file == null) continue;   // vd. Link4.STL chưa có
                    var scene = importer.Load(file.FullName);
                    _linkGeometry[i] = scene?.Root is MeshNode mesh
                        ? mesh.Geometry
                        : scene?.Root.Traverse().OfType<MeshNode>().FirstOrDefault()?.Geometry;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi nạp mô hình STL: {ex.Message}", "Model Load Error");
            }
        }

        /// <summary>Đặt vị trí từng khâu theo bảng DH và giá trị khớp hiện tại.</summary>
        private void UpdateRobotPose()
        {
            var frames = DhKinematics.Frames(RobotParameters.DhTable, [J1, J2, J3, J4]);
            for (int i = 0; i < RobotParameters.Links.Length; i++)
            {
                var transform = new MatrixTransform3D(frames[RobotParameters.Links[i].Frame]);
                transform.Freeze();
                _linkTransform[i] = transform;
                OnPropertyChanged($"Link{i}Transform");
            }
        }
    }
}
