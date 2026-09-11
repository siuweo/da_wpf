namespace DUC_GUI.Services
{
    // Các gói IPC -> GUI (mỗi gói 1 dòng JSON, trường "T" cho biết loại). Xem src/README.md.

    /// <summary>{"T":"FB",...} – phản hồi trạng thái robot, 50 Hz.</summary>
    public sealed class Telemetry
    {
        public uint     Seq   { get; set; }
        public double   Ms    { get; set; }               // đồng hồ monotonic của IPC
        public string   Mode  { get; set; } = "OFFLINE";  // OFFLINE/IDLE/HOLD/RUN/HAND/CLOSING
        public bool     Halt  { get; set; }
        public int      Drv   { get; set; }               // số driver EtherCAT
        public int      Prog  { get; set; }               // Id chương trình đang chạy
        public int      Step  { get; set; }
        public int      Steps { get; set; }
        public int      Line  { get; set; }
        public double[] Q     { get; set; } = new double[4];   // q1, q2, q4 [độ]; q3 [mm]
        public double[] P     { get; set; } = new double[4];   // X, Y, Z (thực tế) [mm], Theta [độ]
        public int      Jog   { get; set; }               // bit 2k: được +trục k, 2k+1: được -trục k
        public uint     Lost  { get; set; }               // tổng số frame EtherCAT bị mất
        public uint     Ovr   { get; set; }               // số lần trễ chu kỳ 1 ms
        public int[]    Ds    { get; set; } = new int[4]; // trạng thái CiA402 từng driver
        public long[][] In    { get; set; } = [];         // 4 x 9 trường CDHD_In_Type
        public long[][] Out   { get; set; } = [];         // 4 x 8 trường CDHD_Out_Type
    }

    /// <summary>{"T":"ACK","Cmd":"STA","Ok":true,"Msg":"..."} – IPC trả lời từng lệnh.</summary>
    public sealed class IpcAck
    {
        public string Cmd { get; set; } = "";
        public bool   Ok  { get; set; }
        public string Msg { get; set; } = "";
    }

    /// <summary>{"T":"EVT","E":"DONE"|"ABORT","Id":n,"Msg":"..."} – chương trình Id kết thúc.</summary>
    public sealed class IpcEvent
    {
        public string E   { get; set; } = "";
        public int    Id  { get; set; }
        public string Msg { get; set; } = "";
    }

    public static class Cia402
    {
        private static readonly string[] Names =
            ["NotReady", "SwitchOnDisabled", "Ready", "SwitchedOn", "OpEnabled", "QuickStop", "FaultReaction", "Fault"];

        public static string Name(int state) => state >= 0 && state < Names.Length ? Names[state] : "?";
    }
}
