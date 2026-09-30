using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace DUC_GUI.Models
{
    /// <summary>
    /// Lưới tam giác đọc từ file STL (nhị phân hoặc ASCII), đơn vị giữ nguyên như file (mm).
    /// Mỗi đỉnh 6 float liên tiếp: x, y, z, nx, ny, nz – pháp tuyến tính lại từ 3 đỉnh (đổ bóng phẳng).
    /// </summary>
    public sealed class StlMesh
    {
        public float[] Vertices    { get; }
        public int     VertexCount => Vertices.Length / 6;

        private StlMesh(float[] vertices) => Vertices = vertices;

        public static StlMesh Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            var tris = IsBinary(data) ? ReadBinary(data) : ReadAscii(Encoding.ASCII.GetString(data));

            var v = new float[tris.Count * 18];
            for (int t = 0; t < tris.Count; t++)
            {
                var (a, b, c) = tris[t];
                var n = Vector3.Cross(b - a, c - a);
                n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
                int o = t * 18;
                Put(v, o,      a, n);
                Put(v, o + 6,  b, n);
                Put(v, o + 12, c, n);
            }
            return new StlMesh(v);
        }

        private static void Put(float[] v, int o, Vector3 p, Vector3 n)
        {
            v[o] = p.X; v[o + 1] = p.Y; v[o + 2] = p.Z;
            v[o + 3] = n.X; v[o + 4] = n.Y; v[o + 5] = n.Z;
        }

        // Nhiều file nhị phân (SolidWorks) cũng bắt đầu bằng "solid" → xác định bằng kích thước file
        private static bool IsBinary(byte[] data)
        {
            if (data.Length < 84) return false;
            uint count = BitConverter.ToUInt32(data, 80);
            return 84 + (long)count * 50 == data.Length;
        }

        private static List<(Vector3, Vector3, Vector3)> ReadBinary(byte[] data)
        {
            int count = (int)BitConverter.ToUInt32(data, 80);
            var tris = new List<(Vector3, Vector3, Vector3)>(count);
            for (int i = 0; i < count; i++)
            {
                int o = 84 + i * 50 + 12;   // bỏ qua pháp tuyến trong file
                tris.Add((ReadVec(data, o), ReadVec(data, o + 12), ReadVec(data, o + 24)));
            }
            return tris;
        }

        private static Vector3 ReadVec(byte[] d, int o) =>
            new(BitConverter.ToSingle(d, o), BitConverter.ToSingle(d, o + 4), BitConverter.ToSingle(d, o + 8));

        private static List<(Vector3, Vector3, Vector3)> ReadAscii(string text)
        {
            var tris = new List<(Vector3, Vector3, Vector3)>();
            var pts  = new List<Vector3>(3);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
                var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4) continue;
                pts.Add(new Vector3(float.Parse(p[1], CultureInfo.InvariantCulture),
                                    float.Parse(p[2], CultureInfo.InvariantCulture),
                                    float.Parse(p[3], CultureInfo.InvariantCulture)));
                if (pts.Count == 3)
                {
                    tris.Add((pts[0], pts[1], pts[2]));
                    pts.Clear();
                }
            }
            return tris;
        }
    }
}
