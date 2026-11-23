# DUC_GUI – bản Avalonia (Windows + Linux)

Giao diện điều khiển robot SCARA. Chuyển từ WPF sang **Avalonia** để chạy được trên cả Windows và Linux.

## Chạy

```
dotnet run
```

Cần .NET 8 SDK.

## Đóng gói

```
dotnet publish -c Release -r win-x64 --self-contained     # Windows
dotnet publish -c Release -r linux-x64 --self-contained   # Linux
```

Gửi thư mục `bin/Release/net8.0/<rid>/publish/`. Nếu đã có bản build và chạy thì máy không cần cài .NET nữa 

## Cấu trúc

### Khởi động
| File | Mục đích |
|---|---|
| `Program.cs` | Điểm bắt đầu, tự nhận hệ điều hành khi chạy |
| `App.axaml(.cs)` | Theme, style chung, mở cửa sổ chính |
| `DUC_GUI.csproj` | Cấu hình project, thư viện |
| `app.manifest.xml` | Thông tin cho Windows (Linux bỏ qua) |

### Robot
| File | Mục đích |
|---|---|
| `Models/RobotParameters.cs` | Thông số: kích thước, bảng DH, IP/cổng, màu khâu, giá trị mặc định |
| `Models/DhKinematics.cs` | Tính vị trí từng khâu từ bảng DH |
| `Models/ProgramBlock.cs` | Các khối lệnh: Move, Delay, GPIO, IF/ELSE/END |

### Kết nối
| File | Mục đích |
|---|---|
| `Services/IpcClient.cs` | Kết nối TCP tới IPC: gửi lệnh, nhận ACK / sự kiện / phản hồi 50 Hz |
| `Services/IpcMessages.cs` | Định dạng các gói tin từ IPC |
| `Services/CameraServer.cs` | Nhận kết quả camera (TCP cổng 7500) |
| `Services/MjpegStream.cs` | Đọc luồng hình MJPEG từ `camera/camera_service.py` (cổng 7501) |
| `Services/CameraServiceClient.cs` | Tách vật: đọc `/status` (vật đã tách, hiệu chỉnh, độ trôi), gửi `/calibrate` |
| `Services/TelemetryCsvLogger.cs` | Ghi phản hồi ra `Log/*.csv` |

### Hiển thị 3D
| File | Mục đích |
|---|---|
| `Controls/RobotViewport.cs` | Vẽ robot 3D bằng OpenGL, xoay / dời / phóng bằng chuột |
| `Models/StlMesh.cs` | Đọc file STL |
| `Resources/Models3D/*.STL` | Mô hình các khâu (thêm `Link4.STL` sẽ tự hiện) |

### Giao diện (UI)
| File | Mục đích |
|---|---|
| `MainWindow.axaml(.cs)` | Cửa sổ chính: bảng điều khiển, 3D, chương trình |
| `ConsoleLogWindow.axaml(.cs)` | Cửa sổ log (Window > Console) |
| `Controls/GroupBox.cs` | Khung có tiêu đề (Avalonia không có sẵn) |
| `Dialogs.cs` | Hộp thông báo, chọn file Import/Save |

### Xử lý logic
| File | Mục đích |
|---|---|
| `ViewModels/MainViewModel.cs` | Nối giao diện với robot: lệnh, trạng thái, chạy chương trình |
| `Models/RelayCommand.cs`, `RobotCommand.cs` | Lệnh gắn vào nút bấm |
| `Models/CommandManager.cs` | Cập nhật bật/tắt nút (vd. nút Jog) |

## Xem file .axaml

Là file XML, mở bằng editor bất kỳ. Muốn xem trước giao diện: cài extension **Avalonia for VSCode** hoặc **Avalonia for Visual Studio**, build một lần rồi bấm nút preview.
