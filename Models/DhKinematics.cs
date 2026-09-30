using System.Numerics;

namespace DUC_GUI.Models
{
    /// <summary>
    /// Tính hệ toạ độ các khâu từ bảng DH chuẩn: T_i = Rz(θ_i)·Tz(d_i)·Tx(a_i)·Rx(α_i), T_0i = T_01·...·T_i.
    /// (System.Numerics dùng vector hàng: p' = p·M, nên ma trận được nhân theo thứ tự ngược lại.)
    /// </summary>
    public static class DhKinematics
    {
        private static float Rad(double deg) => (float)(deg * Math.PI / 180.0);

        /// <param name="q">Biến khớp: độ với khớp xoay, mm với khớp tịnh tiến.</param>
        /// <returns>frames[0] = đế, frames[i] = hệ gắn cuối khâu i (toạ độ thế giới, mm).</returns>
        public static Matrix4x4[] Frames(IReadOnlyList<DhRow> table, IReadOnlyList<double> q)
        {
            var frames = new Matrix4x4[table.Count + 1];
            frames[0] = Matrix4x4.Identity;
            for (int i = 0; i < table.Count; i++)
            {
                DhRow  row   = table[i];
                double qi    = i < q.Count ? q[i] : 0.0;
                double theta = row.ThetaDeg + (row.Type == DhJointType.Revolute ? qi : 0.0);
                double d     = row.D + (row.Type == DhJointType.Prismatic ? qi : 0.0);

                var m = Matrix4x4.CreateRotationX(Rad(row.AlphaDeg))                    // Rx(α) – áp dụng đầu tiên
                      * Matrix4x4.CreateTranslation((float)row.A, 0f, (float)d)         // Tx(a)·Tz(d)
                      * Matrix4x4.CreateRotationZ(Rad(theta));                          // Rz(θ)
                frames[i + 1] = m * frames[i];
            }
            return frames;
        }
    }
}
