using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;

namespace DUC_GUI.Models
{
    public enum ProgramBlockType { Move, Line, Delay, Gpio, IfBranch, ElseBranch, EndIf }

    /// <summary>Lớp cơ sở cho tất cả các khối lệnh trong chương trình robot</summary>
    public abstract class ProgramBlock : INotifyPropertyChanged
    {
        public abstract ProgramBlockType BlockType { get; }
        public abstract string DisplaySummary { get; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ===================================================================================
    // MOVE BLOCK: Di chuyển robot đến tọa độ cụ thể (mm, độ – Z THỰC TẾ, không cộng offset)
    // ===================================================================================
    public class MoveBlock : ProgramBlock
    {
        public override ProgramBlockType BlockType => ProgramBlockType.Move;
        public string X     { get; set; } = "0";
        public string Y     { get; set; } = "0";
        public string Z     { get; set; } = "0";
        public string Theta { get; set; } = "0";
        public string Time  { get; set; } = "1000";
        public override string DisplaySummary =>
            $"X={X}  Y={Y}  Z={Z}  Θ={Theta}  T={Time}ms";
    }

    // ===================================================================================
    // LINE BLOCK: Đi THẲNG tới toạ độ (từ đích của khối trước) với tốc độ Speed mm/s.
    //   IPC tự chia đoạn thẳng thành chuỗi điểm và phát cho driver mỗi 1 ms -> luôn chạy ở CSP.
    // ===================================================================================
    public class LineBlock : ProgramBlock
    {
        public override ProgramBlockType BlockType => ProgramBlockType.Line;
        public string X     { get; set; } = "0";
        public string Y     { get; set; } = "0";
        public string Z     { get; set; } = "0";
        public string Theta { get; set; } = "0";
        public string Speed { get; set; } = "50";    // mm/s
        public string Acc   { get; set; } = "500";   // mm/s^2
        public override string DisplaySummary =>
            $"X={X}  Y={Y}  Z={Z}  Θ={Theta}  v={Speed}mm/s";
    }

    // ===================================================================================
    // DELAY BLOCK: Dừng chương trình trong N ms
    // ===================================================================================
    public class DelayBlock : ProgramBlock
    {
        public override ProgramBlockType BlockType => ProgramBlockType.Delay;
        public string DelayMs { get; set; } = "500";
        public override string DisplaySummary => $"{DelayMs} ms";
    }

    // ===================================================================================
    // GPIO BLOCK: Bật/tắt 1 chân GPIO của PLC (qua USB-Serial trên IPC)
    // ===================================================================================
    public class GpioBlock : ProgramBlock
    {
        public override ProgramBlockType BlockType => ProgramBlockType.Gpio;
        public int Pin   { get; set; } = 2;
        public int Value { get; set; } = 0;   // 0 / 1
        public override string DisplaySummary => $"PIN={Pin}  →  {Value}";
    }

    // ===================================================================================
    // IF BRANCH BLOCK: IF/ELSEIF theo camera_detect_id.
    //   Mỗi nhánh tự động bắt đầu bằng 1 lệnh PICK: robot đuổi theo vật trên băng tải và gắp,
    //   sau đó chạy các khối Move/Delay/GPIO nằm sau dòng này (tới ELSEIF/ELSE/END) = "body".
    // ===================================================================================
    public class IfBranchBlock : ProgramBlock
    {
        public override ProgramBlockType BlockType => ProgramBlockType.IfBranch;

        public int ConditionId { get; set; } = 1;
        public bool IsFirstIf { get; set; } = true;  // true=IF, false=ELSEIF

        public override string DisplaySummary
        {
            get
            {
                string keyword = IsFirstIf ? "IF" : "ELSEIF";
                return $"📷 {keyword}  [ID = {ConditionId}]  →  PICK (bám & gắp)";
            }
        }
    }

    // ===================================================================================
    // ELSE BRANCH BLOCK: Dòng ELSE (fallback khi không match IF/ELSEIF nào) – cũng PICK trước
    // ===================================================================================
    public class ElseBranchBlock : ProgramBlock
    {
        public override ProgramBlockType BlockType => ProgramBlockType.ElseBranch;
        public override string DisplaySummary => "📷 ELSE  →  PICK (bám & gắp)";
    }

    // ===================================================================================
    // END IF BLOCK: Kết thúc khối IF/ELSEIF/ELSE, trở về luồng chính
    // ===================================================================================
    public class EndIfBlock : ProgramBlock
    {
        public override ProgramBlockType BlockType => ProgramBlockType.EndIf;
        public override string DisplaySummary => "📷 END";
    }

}
