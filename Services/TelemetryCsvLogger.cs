using System.Globalization;
using System.IO;
using System.Text;
using DUC_GUI.Models;

namespace DUC_GUI.Services
{
    /// <summary>
    /// Ghi TOÀN BỘ gói phản hồi (PDO vào/ra của 4 driver + trạng thái + toạ độ) ra file CSV.
    /// Mỗi phiên kết nối IPC tạo 1 file Log/log_data_yyyyMMdd_HHmmss.csv.
    /// Các cột PDO giữ nguyên tên/thứ tự như file log cũ; cột mới được thêm ở cuối.
    /// </summary>
    public sealed class TelemetryCsvLogger : IDisposable
    {
        private readonly object _lock = new();
        private StreamWriter?   _writer;
        private DateTime        _lastFlush;

        public string? CurrentFile { get; private set; }

        public void Start()
        {
            lock (_lock)
            {
                if (_writer != null) return;
                string dir = LogDirectory();
                Directory.CreateDirectory(dir);
                CurrentFile = Path.Combine(dir, $"log_data_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                _writer     = new StreamWriter(CurrentFile, false, new UTF8Encoding(true));
                _writer.WriteLine(Header());
                _lastFlush  = DateTime.Now;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }

        public void Write(Telemetry t)
        {
            lock (_lock)
            {
                if (_writer == null) return;
                var ci  = CultureInfo.InvariantCulture;
                var row = new StringBuilder(512);
                row.Append(DateTime.Now.ToString("HH:mm:ss.fff", ci));
                for (int i = 0; i < RobotParameters.NUM_DRIVERS; i++)
                    AppendFields(row, t.In, i, 9);
                for (int i = 0; i < RobotParameters.NUM_DRIVERS; i++)
                    AppendFields(row, t.Out, i, 8);
                row.Append(',').Append(t.Ms.ToString("F0", ci))
                   .Append(',').Append(t.Mode)
                   .Append(',').Append(t.Halt ? 1 : 0)
                   .Append(',').Append(t.Prog)
                   .Append(',').Append(t.Line);
                foreach (double v in t.Q) row.Append(',').Append(v.ToString("F3", ci));
                foreach (double v in t.P) row.Append(',').Append(v.ToString("F2", ci));
                row.Append(',').Append(t.Lost).Append(',').Append(t.Ovr);
                _writer.WriteLine(row.ToString());

                if ((DateTime.Now - _lastFlush).TotalSeconds >= 1)
                {
                    _writer.Flush();
                    _lastFlush = DateTime.Now;
                }
            }
        }

        private static void AppendFields(StringBuilder row, long[][] table, int drive, int count)
        {
            long[]? values = drive < table.Length ? table[drive] : null;
            for (int k = 0; k < count; k++)
                row.Append(',').Append(values != null && k < values.Length ? values[k] : 0);
        }

        private static string Header()
        {
            var h = new StringBuilder("Timestamp");
            for (int i = 1; i <= RobotParameters.NUM_DRIVERS; i++)
                h.Append($",In_Statusword_D{i},In_ModeDisplay_D{i},In_TorqueAct_D{i},In_PosAct_D{i},In_TorqueDemand_D{i}")
                 .Append($",In_AnalogIn1_D{i},In_DigitalInputs_D{i},In_PosExtCmd_D{i},In_FollowingErr_D{i}");
            for (int i = 1; i <= RobotParameters.NUM_DRIVERS; i++)
                h.Append($",Out_Controlword_D{i},Out_Mode_D{i},Out_TargetPos_D{i},Out_ProfileVel_D{i}")
                 .Append($",Out_TargetVel_D{i},Out_TargetTorque_D{i},Out_DigitalOutputs_D{i},Out_TorqueOffset_D{i}");
            h.Append(",IpcTimeMs,Mode,Halt,ProgramId,Line,Q1_deg,Q2_deg,Q3_mm,Q4_deg,X_mm,Y_mm,Z_mm,Theta_deg,LostFrames,Overruns");
            return h.ToString();
        }

        /// <summary>Khi chạy Debug: ghi vào thư mục Log của project (giống bản cũ); Release: cạnh file exe.</summary>
        private static string LogDirectory()
        {
            string baseDir = AppContext.BaseDirectory;
#if DEBUG
            var dir = new DirectoryInfo(baseDir);
            if (dir.Parent?.Parent?.Parent != null)
                return Path.Combine(dir.Parent.Parent.Parent.FullName, "Log");
#endif
            return Path.Combine(baseDir, "Log");
        }

        public void Dispose() => Stop();
    }
}
