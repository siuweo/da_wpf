using System.Windows.Media.Media3D;

namespace DUC_GUI.Models
{
    /// <summary>
    /// Tính hệ toạ độ các khâu từ bảng DH chuẩn: T_i = Rz(θ_i)·Tz(d_i)·Tx(a_i)·Rx(α_i), T_0i = T_01·...·T_i.
    /// (WPF dùng vector hàng: p' = p·M, nên ma trận được nhân theo thứ tự ngược lại.)
    /// </summary>
    public static class DhKinematics
    {
        private static readonly Vector3D AxisX = new(1, 0, 0);
        private static readonly Vector3D AxisZ = new(0, 0, 1);

        /// <param name="q">Biến khớp: độ với khớp xoay, mm với khớp tịnh tiến.</param>
        /// <returns>frames[0] = đế, frames[i] = hệ gắn cuối khâu i (toạ độ thế giới, mm).</returns>
        public static Matrix3D[] Frames(IReadOnlyList<DhRow> table, IReadOnlyList<double> q)
        {
            var frames = new Matrix3D[table.Count + 1];
            frames[0] = Matrix3D.Identity;
            for (int i = 0; i < table.Count; i++)
            {
                DhRow  row   = table[i];
                double qi    = i < q.Count ? q[i] : 0.0;
                double theta = row.ThetaDeg + (row.Type == DhJointType.Revolute ? qi : 0.0);
                double d     = row.D + (row.Type == DhJointType.Prismatic ? qi : 0.0);

                var m = Matrix3D.Identity;
                m.Rotate(new Quaternion(AxisX, row.AlphaDeg));   // Rx(α) – áp dụng đầu tiên
                m.Translate(new Vector3D(row.A, 0, d));          // Tx(a)·Tz(d)
                m.Rotate(new Quaternion(AxisZ, theta));          // Rz(θ)
                frames[i + 1] = m * frames[i];
            }
            return frames;
        }
    }
}
