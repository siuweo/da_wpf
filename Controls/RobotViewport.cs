using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using DUC_GUI.Models;
using Silk.NET.OpenGL;

namespace DUC_GUI.Controls
{
    /// <summary>
    /// Khung nhìn 3D dựng bằng OpenGL (thay cho HelixToolkit.Wpf.SharpDX chỉ chạy trên DirectX/Windows).
    /// Avalonia cấp ngữ cảnh GL trên cả Windows (ANGLE) và Linux (GLX/EGL).
    ///   Chuột: trái/phải kéo = xoay, giữa hoặc Shift+kéo = dời, lăn = phóng to/thu nhỏ, nhấp đúp = về góc nhìn ban đầu.
    /// </summary>
    public sealed class RobotViewport : OpenGlControlBase, ICustomHitTest
    {
        public static readonly StyledProperty<IReadOnlyList<StlMesh?>?> MeshesProperty =
            AvaloniaProperty.Register<RobotViewport, IReadOnlyList<StlMesh?>?>(nameof(Meshes));
        public static readonly StyledProperty<IReadOnlyList<Color>?> ColorsProperty =
            AvaloniaProperty.Register<RobotViewport, IReadOnlyList<Color>?>(nameof(Colors));
        public static readonly StyledProperty<IReadOnlyList<Matrix4x4>?> TransformsProperty =
            AvaloniaProperty.Register<RobotViewport, IReadOnlyList<Matrix4x4>?>(nameof(Transforms));
        public static readonly StyledProperty<Color> ClearColorProperty =
            AvaloniaProperty.Register<RobotViewport, Color>(nameof(ClearColor), Color.Parse(RobotParameters.ViewportBackground));

        public IReadOnlyList<StlMesh?>?  Meshes     { get => GetValue(MeshesProperty);     set => SetValue(MeshesProperty, value); }
        public IReadOnlyList<Color>?     Colors     { get => GetValue(ColorsProperty);     set => SetValue(ColorsProperty, value); }
        public IReadOnlyList<Matrix4x4>? Transforms { get => GetValue(TransformsProperty); set => SetValue(TransformsProperty, value); }
        public Color                     ClearColor { get => GetValue(ClearColorProperty); set => SetValue(ClearColorProperty, value); }

        // ===== Camera quỹ đạo quanh _target (trục Z hướng lên), mặc định giống bản WPF: đặt tại (800, 800, 600) nhìn về gốc =====
        private const float Fov = MathF.PI / 4;
        private static readonly Vector3 HomeEye = new(800, 800, 600);
        private Vector3 _target;
        private float   _distance, _yaw, _pitch;

        private enum Drag { None, Rotate, Pan }
        private Drag  _drag;
        private Point _lastPos;

        // ===== Tài nguyên GL =====
        private GL?  _gl;
        private uint _program, _vao, _gridVbo, _axisVbo;
        private int  _gridCount, _axisCount;
        private int  _uViewProj, _uModel, _uColor, _uUnlit, _uEye;
        private readonly Dictionary<int, (StlMesh Mesh, uint Vbo)> _meshVbo = new();

        public RobotViewport()
        {
            ResetCamera();
            Focusable = true;
        }

        private void ResetCamera()
        {
            _target   = Vector3.Zero;
            _distance = HomeEye.Length();
            _yaw      = MathF.Atan2(HomeEye.Y, HomeEye.X);
            _pitch    = MathF.Asin(HomeEye.Z / _distance);
        }

        private Vector3 Eye() => _target + _distance * new Vector3(
            MathF.Cos(_pitch) * MathF.Cos(_yaw), MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch));

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == MeshesProperty || change.Property == ColorsProperty ||
                change.Property == TransformsProperty || change.Property == ClearColorProperty)
                RequestNextFrameRendering();
        }

        public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

        // ===================================================================================
        // CHUỘT
        // ===================================================================================
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var props = e.GetCurrentPoint(this).Properties;
            if (e.ClickCount == 2 && props.IsLeftButtonPressed)
            {
                ResetCamera();
                RequestNextFrameRendering();
                e.Handled = true;
                return;
            }
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _drag = props.IsMiddleButtonPressed || (shift && (props.IsLeftButtonPressed || props.IsRightButtonPressed)) ? Drag.Pan
                  : props.IsLeftButtonPressed || props.IsRightButtonPressed ? Drag.Rotate
                  : Drag.None;
            if (_drag == Drag.None) return;
            _lastPos = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_drag == Drag.None) return;
            var pos = e.GetPosition(this);
            float dx = (float)(pos.X - _lastPos.X), dy = (float)(pos.Y - _lastPos.Y);
            _lastPos = pos;

            if (_drag == Drag.Rotate)
            {
                _yaw  -= dx * 0.01f;
                _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.55f, 1.55f);
            }
            else
            {
                // dời tâm nhìn theo mặt phẳng màn hình, tỉ lệ để điểm tại tâm đi theo con trỏ
                var fwd   = Vector3.Normalize(_target - Eye());
                var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitZ));
                var up    = Vector3.Cross(right, fwd);
                float mmPerPx = 2f * _distance * MathF.Tan(Fov / 2) / (float)Math.Max(1, Bounds.Height);
                _target += (-dx * right + dy * up) * mmPerPx;
            }
            RequestNextFrameRendering();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_drag == Drag.None) return;
            _drag = Drag.None;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            _distance = Math.Clamp(_distance * MathF.Pow(0.9f, (float)e.Delta.Y), 50f, 20000f);
            RequestNextFrameRendering();
            e.Handled = true;
        }

        // ===================================================================================
        // OPENGL
        // ===================================================================================
        private const string VertexSrc = @"
attribute vec3 aPos;
attribute vec3 aNormal;
uniform mat4 uViewProj;
uniform mat4 uModel;
varying vec3 vNormal;
varying vec3 vWorld;
void main()
{
    vec4 w  = uModel * vec4(aPos, 1.0);
    vWorld  = w.xyz;
    vNormal = (uModel * vec4(aNormal, 0.0)).xyz;
    gl_Position = uViewProj * w;
}";

        // Phong giống vật liệu Helix cũ: ambient = màu*0.25, specular 0.35, độ bóng 40;
        // đèn: ambient #606060, trắng hướng (-0.5,-1,-0.5), #A0A0A0 hướng (0.5,1,0.5). Chiếu sáng 2 mặt.
        private const string FragmentSrc = @"
varying vec3 vNormal;
varying vec3 vWorld;
uniform vec3  uColor;
uniform float uUnlit;
uniform vec3  uEye;
//DECLAREGLFRAG
vec3 shade(vec3 n, vec3 v, vec3 l, vec3 lightColor)
{
    float diff = max(dot(n, l), 0.0);
    float spec = diff > 0.0 ? pow(max(dot(n, normalize(l + v)), 0.0), 40.0) : 0.0;
    return lightColor * (uColor * diff + vec3(0.35) * spec);
}
void main()
{
    if (uUnlit > 0.5) { gl_FragColor = vec4(uColor, 1.0); return; }
    vec3 v = normalize(uEye - vWorld);
    vec3 n = normalize(vNormal);
    if (dot(n, v) < 0.0) n = -n;
    vec3 c = vec3(0.376) * uColor * 0.25
           + shade(n, v, normalize(vec3( 0.5,  1.0,  0.5)), vec3(1.0))
           + shade(n, v, normalize(vec3(-0.5, -1.0, -0.5)), vec3(0.627));
    gl_FragColor = vec4(min(c, vec3(1.0)), 1.0);
}";

        // GLSL cũ (120 / ES 100) chạy được trên hầu hết driver; macOS chỉ có core profile nên đổi sang 150
        private string WithHeader(bool fragment, string src)
        {
            bool es      = GlVersion.Type == GlProfileType.OpenGLES;
            int  version = es ? 100 : OperatingSystem.IsMacOS() ? 150 : 120;
            string head  = $"#version {version}\n";
            if (es) head += "#ifdef GL_FRAGMENT_PRECISION_HIGH\nprecision highp float;\n#else\nprecision mediump float;\n#endif\n";
            if (version >= 150)
            {
                src = src.Replace("attribute", "in");
                src = fragment
                    ? src.Replace("varying", "in").Replace("//DECLAREGLFRAG", "out vec4 outFragColor;").Replace("gl_FragColor", "outFragColor")
                    : src.Replace("varying", "out");
            }
            return head + src;
        }

        private uint Compile(ShaderType type, string src)
        {
            var gl = _gl!;
            uint s = gl.CreateShader(type);
            gl.ShaderSource(s, WithHeader(type == ShaderType.FragmentShader, src));
            gl.CompileShader(s);
            gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"Lỗi biên dịch shader: {gl.GetShaderInfoLog(s)}");
            return s;
        }

        protected override void OnOpenGlInit(GlInterface glInterface)
        {
            base.OnOpenGlInit(glInterface);
            var gl = _gl = GL.GetApi(glInterface.GetProcAddress);

            uint vs = Compile(ShaderType.VertexShader, VertexSrc);
            uint fs = Compile(ShaderType.FragmentShader, FragmentSrc);
            _program = gl.CreateProgram();
            gl.AttachShader(_program, vs);
            gl.AttachShader(_program, fs);
            gl.BindAttribLocation(_program, 0, "aPos");
            gl.BindAttribLocation(_program, 1, "aNormal");
            gl.LinkProgram(_program);
            gl.GetProgram(_program, ProgramPropertyARB.LinkStatus, out int linked);
            if (linked == 0) throw new InvalidOperationException($"Lỗi liên kết shader: {gl.GetProgramInfoLog(_program)}");
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);

            _uViewProj = gl.GetUniformLocation(_program, "uViewProj");
            _uModel    = gl.GetUniformLocation(_program, "uModel");
            _uColor    = gl.GetUniformLocation(_program, "uColor");
            _uUnlit    = gl.GetUniformLocation(_program, "uUnlit");
            _uEye      = gl.GetUniformLocation(_program, "uEye");

            _vao = gl.GenVertexArray();
            gl.BindVertexArray(_vao);

            var grid = BuildGrid();
            _gridVbo   = Upload(grid);
            _gridCount = grid.Length / 6;
            var axis = BuildBox(new Vector3(0, -0.05f, -0.05f), new Vector3(1, 0.05f, 0.05f));
            _axisVbo   = Upload(axis);
            _axisCount = axis.Length / 6;
        }

        protected override void OnOpenGlDeinit(GlInterface glInterface)
        {
            if (_gl is { } gl)
            {
                gl.DeleteBuffer(_gridVbo);
                gl.DeleteBuffer(_axisVbo);
                foreach (var (_, vbo) in _meshVbo.Values) gl.DeleteBuffer(vbo);
                gl.DeleteVertexArray(_vao);
                gl.DeleteProgram(_program);
            }
            _meshVbo.Clear();
            _gl = null;
            base.OnOpenGlDeinit(glInterface);
        }

        private uint Upload(float[] data)
        {
            var gl = _gl!;
            uint vbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.StaticDraw);
            return vbo;
        }

        private unsafe void Draw(uint vbo, int count, PrimitiveType type)
        {
            var gl = _gl!;
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            gl.EnableVertexAttribArray(0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
            gl.DrawArrays(type, 0, (uint)count);
        }

        private void SetMatrix(int location, Matrix4x4 m) =>
            // System.Numerics lưu theo hàng (vector hàng); GL đọc theo cột → shader nhận M^T, dùng M^T·v = (v·M)^T
            _gl!.UniformMatrix4(location, 1, false, MemoryMarshal.Cast<Matrix4x4, float>(new ReadOnlySpan<Matrix4x4>(ref m)));

        private void SetColor(Color c) => _gl!.Uniform3(_uColor, c.R / 255f, c.G / 255f, c.B / 255f);

        protected override void OnOpenGlRender(GlInterface glInterface, int fb)
        {
            if (_gl is not { } gl) return;
            double scaling = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            int w = Math.Max(1, (int)(Bounds.Width * scaling)), h = Math.Max(1, (int)(Bounds.Height * scaling));

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
            gl.Viewport(0, 0, (uint)w, (uint)h);
            var bg = ClearColor;
            gl.ClearColor(bg.R / 255f, bg.G / 255f, bg.B / 255f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            gl.Enable(EnableCap.DepthTest);
            gl.DepthFunc(DepthFunction.Lequal);
            gl.Disable(EnableCap.CullFace);

            gl.UseProgram(_program);
            gl.BindVertexArray(_vao);

            // ----- Cảnh chính -----
            var eye  = Eye();
            float near = Math.Max(1f, _distance * 0.01f), far = _distance * 20f + 5000f;
            var viewProj = Matrix4x4.CreateLookAt(eye, _target, Vector3.UnitZ)
                         * Matrix4x4.CreatePerspectiveFieldOfView(Fov, w / (float)h, near, far);
            SetMatrix(_uViewProj, viewProj);
            gl.Uniform3(_uEye, eye.X, eye.Y, eye.Z);

            SetMatrix(_uModel, Matrix4x4.Identity);
            gl.Uniform1(_uUnlit, 1f);
            SetColor(Color.Parse("#9AA3AD"));
            Draw(_gridVbo, _gridCount, PrimitiveType.Lines);

            gl.Uniform1(_uUnlit, 0f);
            var meshes = Meshes; var colors = Colors; var transforms = Transforms;
            if (meshes != null)
            {
                for (int i = 0; i < meshes.Count; i++)
                {
                    if (meshes[i] is not { } mesh) continue;   // vd. Link4.STL chưa có
                    if (!_meshVbo.TryGetValue(i, out var entry) || entry.Mesh != mesh)
                    {
                        if (entry.Vbo != 0) gl.DeleteBuffer(entry.Vbo);
                        entry = (mesh, Upload(mesh.Vertices));
                        _meshVbo[i] = entry;
                    }
                    SetMatrix(_uModel, transforms != null && i < transforms.Count ? transforms[i] : Matrix4x4.Identity);
                    SetColor(colors != null && i < colors.Count ? colors[i] : Avalonia.Media.Colors.Gray);
                    Draw(entry.Vbo, mesh.VertexCount, PrimitiveType.Triangles);
                }
            }

            // ----- Hệ trục toạ độ nhỏ ở góc trên-trái (X đỏ, Y xanh lá, Z xanh dương), chỉ xoay theo camera -----
            int size = (int)(90 * scaling);
            gl.Clear(ClearBufferMask.DepthBufferBit);
            gl.Viewport(0, h - size, (uint)size, (uint)size);
            var dir = Vector3.Normalize(eye - _target);
            SetMatrix(_uViewProj, Matrix4x4.CreateLookAt(dir * 3f, Vector3.Zero, Vector3.UnitZ)
                                * Matrix4x4.CreateOrthographic(2.8f, 2.8f, 0.1f, 10f));
            gl.Uniform3(_uEye, dir.X * 3f, dir.Y * 3f, dir.Z * 3f);
            DrawAxis(Matrix4x4.Identity,                          Color.Parse("#E53935"));
            DrawAxis(Matrix4x4.CreateRotationZ(MathF.PI / 2),     Color.Parse("#43A047"));
            DrawAxis(Matrix4x4.CreateRotationY(-MathF.PI / 2),    Color.Parse("#1E88E5"));
        }

        private void DrawAxis(Matrix4x4 rot, Color color)
        {
            SetMatrix(_uModel, rot);
            SetColor(color);
            Draw(_axisVbo, _axisCount, PrimitiveType.Triangles);
        }

        // Lưới sàn 1000×1000 mm, ô 50 mm (như bản WPF)
        private static float[] BuildGrid()
        {
            const int half = 500, step = 50;
            var v = new List<float>();
            for (int k = -half; k <= half; k += step)
            {
                v.AddRange([k, -half, 0, 0, 0, 1,  k, half, 0, 0, 0, 1]);
                v.AddRange([-half, k, 0, 0, 0, 1,  half, k, 0, 0, 0, 1]);
            }
            return [.. v];
        }

        private static float[] BuildBox(Vector3 a, Vector3 b)
        {
            var v = new List<float>();
            void Quad(Vector3 n, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
            {
                foreach (var p in new[] { p0, p1, p2, p0, p2, p3 })
                    v.AddRange([p.X, p.Y, p.Z, n.X, n.Y, n.Z]);
            }
            Quad(-Vector3.UnitX, new(a.X, a.Y, a.Z), new(a.X, b.Y, a.Z), new(a.X, b.Y, b.Z), new(a.X, a.Y, b.Z));
            Quad( Vector3.UnitX, new(b.X, a.Y, a.Z), new(b.X, a.Y, b.Z), new(b.X, b.Y, b.Z), new(b.X, b.Y, a.Z));
            Quad(-Vector3.UnitY, new(a.X, a.Y, a.Z), new(a.X, a.Y, b.Z), new(b.X, a.Y, b.Z), new(b.X, a.Y, a.Z));
            Quad( Vector3.UnitY, new(a.X, b.Y, a.Z), new(b.X, b.Y, a.Z), new(b.X, b.Y, b.Z), new(a.X, b.Y, b.Z));
            Quad(-Vector3.UnitZ, new(a.X, a.Y, a.Z), new(b.X, a.Y, a.Z), new(b.X, b.Y, a.Z), new(a.X, b.Y, a.Z));
            Quad( Vector3.UnitZ, new(a.X, a.Y, b.Z), new(a.X, b.Y, b.Z), new(b.X, b.Y, b.Z), new(b.X, a.Y, b.Z));
            return [.. v];
        }
    }
}
