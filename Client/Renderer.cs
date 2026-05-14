using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Shared;

namespace Client;

public class Renderer(Interpolator interpolator)
{
    private IWindow?      _window;
    private GL?           _gl;
    private uint          _vao, _vbo, _ebo;
    private uint          _shader;
    private int           _mvpLoc, _colorLoc;
    private LiteNetClient? _quicClient;
    private InputHandler? _inputHandler;

    private const string VertSrc = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uMVP;
        void main() { gl_Position = uMVP * vec4(aPos, 1.0); }
        """;

    private const string FragSrc = """
        #version 330 core
        uniform vec3 uColor;
        out vec4 FragColor;
        void main() { FragColor = vec4(uColor, 1.0); }
        """;

    private static readonly float[] CubeVerts =
    [
        -0.5f,-0.5f,-0.5f,  0.5f,-0.5f,-0.5f,  0.5f, 0.5f,-0.5f, -0.5f, 0.5f,-0.5f,
        -0.5f,-0.5f, 0.5f,  0.5f,-0.5f, 0.5f,  0.5f, 0.5f, 0.5f, -0.5f, 0.5f, 0.5f,
    ];

    // CCW winding viewed from outside → normals point outward, backface culling works
    private static readonly uint[] CubeIdx =
    [
        4,5,6, 4,6,7,   // front  (+Z)
        0,2,1, 0,3,2,   // back   (-Z)
        0,7,3, 0,4,7,   // left   (-X)
        1,2,6, 1,6,5,   // right  (+X)
        0,1,5, 0,5,4,   // bottom (-Y)
        3,6,2, 3,7,6,   // top    (+Y)
    ];

    public Task RunAsync(LiteNetClient client)
    {
        _quicClient = client;

        var opts = WindowOptions.Default with
        {
            Size  = new Vector2D<int>(800, 600),
            Title = "MMORPG POC",
            API   = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core,
                                    ContextFlags.Default, new APIVersion(3, 3))
        };

        _window         = Window.Create(opts);
        _window.Load   += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClose;

        _window.Run();
        return Task.CompletedTask;
    }

    private unsafe void OnLoad()
    {
        _gl = _window!.CreateOpenGL();
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);   // cull back faces → proper 3D look
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
        _gl.ClearColor(0.08f, 0.08f, 0.12f, 1f);

        var input = _window.CreateInput();
        _inputHandler = new InputHandler(input, _quicClient!, () => _window!.Close(), interpolator);


        // VAO / VBO / EBO
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();

        _gl.BindVertexArray(_vao);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = CubeVerts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                           (nuint)(CubeVerts.Length * sizeof(float)), p,
                           BufferUsageARB.StaticDraw);

        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (uint* p = CubeIdx)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                           (nuint)(CubeIdx.Length * sizeof(uint)), p,
                           BufferUsageARB.StaticDraw);

        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        _gl.EnableVertexAttribArray(0);

        // Shader
        var vert = _gl.CreateShader(ShaderType.VertexShader);
        _gl.ShaderSource(vert, VertSrc);
        _gl.CompileShader(vert);
        _gl.GetShader(vert, ShaderParameterName.CompileStatus, out int vs);
        if (vs == 0) Console.Error.WriteLine("Vert: " + _gl.GetShaderInfoLog(vert));

        var frag = _gl.CreateShader(ShaderType.FragmentShader);
        _gl.ShaderSource(frag, FragSrc);
        _gl.CompileShader(frag);
        _gl.GetShader(frag, ShaderParameterName.CompileStatus, out int fs);
        if (fs == 0) Console.Error.WriteLine("Frag: " + _gl.GetShaderInfoLog(frag));

        _shader = _gl.CreateProgram();
        _gl.AttachShader(_shader, vert);
        _gl.AttachShader(_shader, frag);
        _gl.LinkProgram(_shader);
        _gl.GetProgram(_shader, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) Console.Error.WriteLine("Link: " + _gl.GetProgramInfoLog(_shader));
        _gl.DeleteShader(vert);
        _gl.DeleteShader(frag);

        _mvpLoc   = _gl.GetUniformLocation(_shader, "uMVP");
        _colorLoc = _gl.GetUniformLocation(_shader, "uColor");
    }

    private double _titleTimer;

    private void OnUpdate(double dt)
    {
        _quicClient?.PollEvents();
        _inputHandler?.Poll(dt);
        _titleTimer += dt;
        if (_titleTimer >= 0.5)
        {
            _titleTimer = 0;
            UpdateTitle();
        }
    }

    private void UpdateTitle()
    {
        if (_inputHandler is null) return;

        float drift = 0;
        if (interpolator.TryGetLatestSelf(out var self))
        {
            float dx = _inputHandler.LocalX - self.X;
            float dz = _inputHandler.LocalZ - self.Z;
            drift = MathF.Sqrt(dx * dx + dz * dz);
        }
        var others = new System.Text.StringBuilder();
        foreach (var p in interpolator.Players)
        {
            if (p.PlayerId != interpolator.MyPlayerId)
                others.Append($" | p{p.PlayerId}=({p.X:F1},{p.Z:F1})");
        }

        double ageMs = interpolator.SnapshotAgeMs;
        _window!.Title = $"MMORPG POC | me=p{interpolator.MyPlayerId} drift={drift:F2}{others} | snap={ageMs:F0}ms";
    }

#if DEBUG
    private int _frameCount;
#endif

    private unsafe void OnRender(double dt)
    {
        var gl = _gl!;
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        int w = _window!.FramebufferSize.X, h = _window.FramebufferSize.Y;
        if (w == 0 || h == 0) return;

        // Debug: print data counts every 60 frames
#if DEBUG
        if (++_frameCount % 60 == 0)
        {
            var ps = interpolator.Players;
            Console.Error.WriteLine($"frame={_frameCount} players={ps.Length} mvpLoc={_mvpLoc} colorLoc={_colorLoc}");
            foreach (var p in ps)
                Console.Error.WriteLine($"  id={p.PlayerId} X={p.X:F2} Z={p.Z:F2} self={p.PlayerId == interpolator.MyPlayerId}");
        }
#endif

        gl.Viewport(0, 0, (uint)w, (uint)h);
        gl.UseProgram(_shader);
        gl.BindVertexArray(_vao);

        float yaw   = _inputHandler?.Yaw    ?? 0f;
        float pitch = _inputHandler?.Pitch  ?? 0f;
        float ex    = _inputHandler?.LocalX ?? 0f;
        float ez    = _inputHandler?.LocalZ ?? 0f;
        float ey    = _inputHandler?.LocalY ?? 0f;
        var eye = new Vector3(ex, 1.6f + ey, ez);
        var look = new Vector3(
            MathF.Sin(yaw) * MathF.Cos(pitch),
            MathF.Sin(pitch),
            MathF.Cos(yaw) * MathF.Cos(pitch));
        var view = Matrix4x4.CreateLookAt(eye, eye + look, Vector3.UnitY);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(
                       MathF.PI / 3f, w / (float)h, 0.1f, 500f);
        var vp = view * proj;

        // Players — own cube uses predicted position so it stays under the camera
        foreach (var p in interpolator.Players)
        {
            if (p.PlayerId == interpolator.MyPlayerId) continue;
            var mvp = Matrix4x4.CreateRotationY(p.Yaw) *
                      Matrix4x4.CreateTranslation(p.X, p.Y + 1f, p.Z) * vp;
            SetMvp(gl, mvp);
            var (r, g, b) = PlayerColor(p.PlayerId);
            gl.Uniform3(_colorLoc, r, g, b);
            gl.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, (void*)0);
        }

        // Particles — color is authoritative from server (ColorId in snapshot)
        foreach (var p in interpolator.GetParticles())
        {
            var mvp = Matrix4x4.CreateScale(0.15f) *
                      Matrix4x4.CreateTranslation(p.X, p.Y, p.Z) * vp;
            SetMvp(gl, mvp);
            if (p.ColorId != 0)
            {
                var (r, g, b) = PlayerColor(p.ColorId);
                gl.Uniform3(_colorLoc, r, g, b);
            }
            else
                gl.Uniform3(_colorLoc, 0.3f, 0.6f, 1.0f);
            gl.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, (void*)0);
        }

        // Game objects — light gray cubes driven by server scripts
        Span<Vector4> frustum = stackalloc Vector4[6];
        ExtractFrustumPlanes(vp, frustum);
        foreach (var go in interpolator.GetGameObjects())
        {
            if (!InFrustum(frustum, go.X, go.Y, go.Z, 0.9f)) continue;
            var mvp = Matrix4x4.CreateRotationY(go.Yaw) *
                      Matrix4x4.CreateTranslation(go.X, go.Y, go.Z) * vp;
            SetMvp(gl, mvp);
            gl.Uniform3(_colorLoc, 0.85f, 0.85f, 0.85f);
            gl.DrawElements(PrimitiveType.Triangles, 36, DrawElementsType.UnsignedInt, (void*)0);
        }
    }

    // Extracts 6 normalized frustum planes from a row-major VP matrix.
    // Plane equation: dot(plane.XYZ, pos) + plane.W >= 0 means inside.
    private static void ExtractFrustumPlanes(Matrix4x4 m, Span<Vector4> p)
    {
        p[0] = NormalizePlane(m.M11+m.M14, m.M21+m.M24, m.M31+m.M34, m.M41+m.M44); // left
        p[1] = NormalizePlane(m.M14-m.M11, m.M24-m.M21, m.M34-m.M31, m.M44-m.M41); // right
        p[2] = NormalizePlane(m.M12+m.M14, m.M22+m.M24, m.M32+m.M34, m.M42+m.M44); // bottom
        p[3] = NormalizePlane(m.M14-m.M12, m.M24-m.M22, m.M34-m.M32, m.M44-m.M42); // top
        p[4] = NormalizePlane(m.M13+m.M14, m.M23+m.M24, m.M33+m.M34, m.M43+m.M44); // near
        p[5] = NormalizePlane(m.M14-m.M13, m.M24-m.M23, m.M34-m.M33, m.M44-m.M43); // far
    }

    private static Vector4 NormalizePlane(float a, float b, float c, float d)
    {
        float len = MathF.Sqrt(a * a + b * b + c * c);
        return new Vector4(a / len, b / len, c / len, d / len);
    }

    private static bool InFrustum(Span<Vector4> planes, float x, float y, float z, float r)
    {
        foreach (var p in planes)
            if (p.X * x + p.Y * y + p.Z * z + p.W < -r)
                return false;
        return true;
    }

    private unsafe void SetMvp(GL gl, Matrix4x4 m)
    {
        float[] arr =
        [
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44,
        ];
        fixed (float* p = arr)
            gl.UniformMatrix4(_mvpLoc, 1, false, p);
    }

    private static (float r, float g, float b) PlayerColor(uint id)
    {
        float hue = (id * 137.508f) % 360f;
        float h   = hue / 60f;
        float x   = 1f - MathF.Abs(h % 2f - 1f);
        return (int)h switch
        {
            0 => (1, x, 0),
            1 => (x, 1, 0),
            2 => (0, 1, x),
            3 => (0, x, 1),
            4 => (x, 0, 1),
            _ => (1, 0, x),
        };
    }

    private void OnClose()
    {
        _gl?.DeleteVertexArray(_vao);
        _gl?.DeleteBuffer(_vbo);
        _gl?.DeleteBuffer(_ebo);
        _gl?.DeleteProgram(_shader);
    }
}
