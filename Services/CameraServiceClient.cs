using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DUC_GUI.Services
{
    // Trạng thái tách vật từ camera/camera_service.py (GET /status). Toạ độ vẫn là PIXEL ảnh màu
    // (depth đã align theo RGB) + chiều cao so với mặt băng tải; quy đổi sang hệ robot là bước sau.

    /// <summary>Một vật đang trong ROI, đã theo dõi qua nhiều frame (mh.Tracker trong max_height.py).</summary>
    public sealed class IsolatedObject
    {
        public int    Id       { get; set; }   // id theo dõi, giữ nguyên khi vật trôi qua ROI
        public double U        { get; set; }   // tâm vật đã lọc (pixel)
        public double V        { get; set; }
        public double AngleDeg { get; set; }   // góc cạnh dài so với trục u của ảnh, (-90, 90]
        public double LenPx    { get; set; }
        public double WidPx    { get; set; }
        public int    AreaPx   { get; set; }
        public double HMm      { get; set; }   // chiều cao lớn nhất so với băng tải (mm), trung vị N frame gần nhất
        public double HRawMm   { get; set; }   // chiều cao đo ở frame này
        [JsonPropertyName("h_p99_mm")]
        public double HP99Mm   { get; set; }
        public int    PeakU    { get; set; }   // pixel cao nhất
        public int    PeakV    { get; set; }
        public double ZMm      { get; set; }   // khoảng cách camera tại đỉnh (mm)
        public bool   Edge     { get; set; }   // chạm biên ROI: vật chưa lọt hẳn vào vùng xét
        public double VxPx     { get; set; }   // vận tốc tâm (pixel / frame)
        public double VyPx     { get; set; }
        public int    Frames   { get; set; }   // số frame đã thấy vật
    }

    /// <summary>Tổng kết một vật đã đi qua ROI.</summary>
    public sealed class PassedObject
    {
        public int    Id      { get; set; }
        public double HMm     { get; set; }   // trung vị chiều cao (ưu tiên frame vật nằm trọn trong ROI)
        public double P10Mm   { get; set; }
        public double P90Mm   { get; set; }
        public int    N       { get; set; }   // số frame dùng để tính
        public bool   Partial { get; set; }   // chỉ thấy một phần (luôn chạm biên ROI)
        public long   First   { get; set; }
        public long   Last    { get; set; }
    }

    public sealed class CalibProgress
    {
        public int Done  { get; set; }
        public int Total { get; set; }
    }

    public sealed class PlaneInfo
    {
        public string File       { get; set; } = "";
        public string Time       { get; set; } = "";
        public double DistanceMm { get; set; }
        public double TiltDeg    { get; set; }
        public double RmsMm      { get; set; }
    }

    public sealed class IsolationStatus
    {
        public string         Source      { get; set; } = "";
        public long           Frame       { get; set; }
        public double         Fps         { get; set; }
        public double         ProcMs      { get; set; }
        public bool           Calibrated  { get; set; }
        public CalibProgress? Calibrating { get; set; }
        public PlaneInfo?     Plane       { get; set; }
        public double         DriftMm     { get; set; }
        public bool           DriftWarn   { get; set; }
        public double[]       BeltVPx     { get; set; } = [0, 0];   // vận tốc băng tải học từ các vật (pixel / frame)
        public string         Error       { get; set; } = "";
        public List<IsolatedObject> Objects { get; set; } = [];   // vật đang trong ROI
        public List<PassedObject>   Passed  { get; set; } = [];   // vật vừa đi qua, mới nhất trước
    }

    public sealed class CalibrateReply
    {
        public bool   Ok  { get; set; }
        public string Msg { get; set; } = "";
    }

    /// <summary>Gọi các API HTTP của camera service (ngoài các luồng MJPEG).</summary>
    public static class CameraServiceClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>null nếu camera service không trả lời.</summary>
        public static async Task<IsolationStatus?> GetStatusAsync(string baseUrl, CancellationToken ct = default)
        {
            try
            {
                await using var s = await Http.GetStreamAsync($"{baseUrl}/status", ct).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<IsolationStatus>(s, Json, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }

        /// <summary>Yêu cầu hiệu chỉnh lại mặt phẳng băng tải (băng tải phải TRỐNG).</summary>
        public static async Task<CalibrateReply> CalibrateAsync(string baseUrl)
        {
            try
            {
                using var resp = await Http.PostAsync($"{baseUrl}/calibrate", null).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonSerializer.Deserialize<CalibrateReply>(body, Json)
                       ?? new CalibrateReply { Msg = $"HTTP {(int)resp.StatusCode}" };
            }
            catch (Exception ex)
            {
                return new CalibrateReply { Msg = $"Không gọi được camera service: {ex.Message}" };
            }
        }
    }
}
