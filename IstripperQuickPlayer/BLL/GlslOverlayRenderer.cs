using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;
using GlPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace IStripperQuickPlayer.BLL;

internal static class GlslOverlayRenderer
{
    private const int Width = 96;
    private const int Height = 144;
    private const int FrameDuration = 100;
    private static readonly object renderLock = new();
    internal static string? LastError { get; private set; }

    internal static DrawingBitmap? Render(
        string name, string pixelShader, string parameters,
        string?[] defaultColors, DrawingBitmap? channel0,
        DrawingBitmap? channel1, int rotation, bool mirrorX,
        out int frameCount, out int frameDuration)
        => Render(name, pixelShader, parameters, defaultColors,
            channel0, channel1, rotation, mirrorX,
            out frameCount, out frameDuration, Width, Height);

    internal static DrawingBitmap? Render(
        string name, string pixelShader, string parameters,
        string?[] defaultColors, DrawingBitmap? channel0,
        DrawingBitmap? channel1, int rotation, bool mirrorX,
        out int frameCount, out int frameDuration,
        int width, int height)
    {
        bool quickPlayer = name.StartsWith(
            "quickPlayer", StringComparison.OrdinalIgnoreCase);
        frameDuration = quickPlayer ? 75 : FrameDuration;
        frameCount = name.Equals(
                "borderGlow", StringComparison.OrdinalIgnoreCase) ? 50 :
            name.Equals(
                "heartsFireworks", StringComparison.OrdinalIgnoreCase) ? 60 :
            quickPlayer ? 154 :
            name is "flux" or "wave" ? 63 : 100;
        lock (renderLock)
        {
            LastError = null;
            try
            {
                return RenderCore(
                    pixelShader, ParseColors(parameters, defaultColors),
                    channel0, channel1, rotation, mirrorX,
                    frameCount, frameDuration, width, height);
            }
            catch (Exception exception)
            {
                LastError = exception.ToString();
                Debug.WriteLine(
                    $"GLSL card overlay '{name}' could not be rendered: " +
                    exception);
                return null;
            }
        }
    }

    private static DrawingBitmap RenderCore(
        string source, Vector3[] colors,
        DrawingBitmap? channel0, DrawingBitmap? channel1,
        int rotation, bool mirrorX, int frameCount, int frameDuration,
        int width, int height)
    {
        GLFWProvider.CheckForMainThread = false;
        NativeWindowSettings settings = new()
        {
            ClientSize = new Vector2i(width, height),
            StartVisible = false,
            StartFocused = false,
            WindowBorder = WindowBorder.Hidden,
            API = ContextAPI.OpenGL,
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core
        };
        using OpenTK.Windowing.Desktop.NativeWindow window = new(settings);
        window.Context.MakeCurrent();

        int vertexShader = Compile(
            ShaderType.VertexShader, VertexShader);
        int fragmentShader = Compile(
            ShaderType.FragmentShader, FragmentHeader + source +
            FragmentFooter);
        int program = GL.CreateProgram();
        GL.AttachShader(program, vertexShader);
        GL.AttachShader(program, fragmentShader);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus,
            out int linked);
        if (linked == 0)
            throw new InvalidOperationException(
                GL.GetProgramInfoLog(program));

        int vertexArray = GL.GenVertexArray();
        int texture0 = CreateTexture(channel0);
        int texture1 = CreateTexture(channel1);
        DrawingBitmap sheet = new(
            width * frameCount, height,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        BitmapData target = sheet.LockBits(
            new Rectangle(Point.Empty, sheet.Size),
            ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            GL.BindVertexArray(vertexArray);
            GL.UseProgram(program);
            SetUniform(program, "iResolution",
                new Vector3(width, height, 0));
            SetUniform(program, "qt_Opacity", 1f);
            SetUniform(program, "rotate180",
                rotation % 360 == 180 ? 1 : 0);
            SetUniform(program, "mirrorX", mirrorX ? 1 : 0);
            for (int index = 0; index < colors.Length; index++)
                SetUniform(program, $"color{index}", colors[index]);
            BindTexture(program, "iChannel0", texture0, 0);
            BindTexture(program, "iChannel1", texture1, 1);

            byte[] pixels = new byte[width * height * 4];
            byte[] row = new byte[width * 4];
            GL.Viewport(0, 0, width, height);
            GL.Disable(EnableCap.Blend);
            for (int frame = 0; frame < frameCount; frame++)
            {
                SetUniform(program, "iTime",
                    frame * frameDuration / 1000f);
                GL.ClearColor(0, 0, 0, 0);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
                GL.ReadPixels(0, 0, width, height,
                    GlPixelFormat.Bgra, PixelType.UnsignedByte, pixels);
                CopyFrame(pixels, row, target, frame, width, height);
            }
        }
        catch
        {
            sheet.UnlockBits(target);
            sheet.Dispose();
            throw;
        }
        sheet.UnlockBits(target);
        if (texture0 != 0)
            GL.DeleteTexture(texture0);
        if (texture1 != 0)
            GL.DeleteTexture(texture1);
        GL.DeleteVertexArray(vertexArray);
        GL.DeleteProgram(program);
        GL.DeleteShader(fragmentShader);
        GL.DeleteShader(vertexShader);
        return sheet;
    }

    private static void CopyFrame(
        byte[] pixels, byte[] row, BitmapData target, int frame,
        int width, int height)
    {
        for (int y = 0; y < height; y++)
        {
            System.Buffer.BlockCopy(
                pixels, (height - y - 1) * row.Length,
                row, 0, row.Length);
            for (int x = 0; x < width; x++)
            {
                float mask = RoundedMask(x, y, width, height);
                int pixel = x * 4;
                if (mask < 1)
                {
                    row[pixel] = (byte)(row[pixel] * mask);
                    row[pixel + 1] = (byte)(row[pixel + 1] * mask);
                    row[pixel + 2] = (byte)(row[pixel + 2] * mask);
                    row[pixel + 3] = (byte)(row[pixel + 3] * mask);
                }
                row[pixel + 3] = Math.Max(row[pixel + 3],
                    Math.Max(row[pixel],
                        Math.Max(row[pixel + 1], row[pixel + 2])));
            }
            Marshal.Copy(row, 0,
                target.Scan0 + y * target.Stride +
                    frame * width * 4,
                row.Length);
        }
    }

    private static float RoundedMask(
        int x, int y, int width, int height)
    {
        float radius = 6f * width / Width;
        float dx = Math.Max(radius - x - .5f,
            x + .5f - (width - radius));
        float dy = Math.Max(radius - y - .5f,
            y + .5f - (height - radius));
        if (dx <= 0 || dy <= 0)
            return 1;
        return Math.Clamp(radius + .5f -
            MathF.Sqrt(dx * dx + dy * dy), 0, 1);
    }

    private static int Compile(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus,
            out int compiled);
        if (compiled == 0)
            throw new InvalidOperationException(
                GL.GetShaderInfoLog(shader));
        return shader;
    }

    private static int CreateTexture(DrawingBitmap? source)
    {
        if (source == null)
            return 0;
        using DrawingBitmap upload = new(
            source.Width, source.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(upload))
            graphics.DrawImageUnscaled(source, 0, 0);
        BitmapData data = upload.LockBits(
            new Rectangle(Point.Empty, upload.Size),
            ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        int texture = GL.GenTexture();
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.Repeat);
            GL.TexImage2D(TextureTarget.Texture2D, 0,
                PixelInternalFormat.Rgba8, upload.Width, upload.Height,
                0, GlPixelFormat.Bgra, PixelType.UnsignedByte,
                data.Scan0);
        }
        finally
        {
            upload.UnlockBits(data);
        }
        return texture;
    }

    private static void BindTexture(
        int program, string uniform, int texture, int unit)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        SetUniform(program, uniform, unit);
    }

    private static void SetUniform(
        int program, string name, int value)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location >= 0)
            GL.Uniform1(location, value);
    }

    private static void SetUniform(
        int program, string name, float value)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location >= 0)
            GL.Uniform1(location, value);
    }

    private static void SetUniform(
        int program, string name, Vector3 value)
    {
        int location = GL.GetUniformLocation(program, name);
        if (location >= 0)
            GL.Uniform3(location, value);
    }

    private static Vector3[] ParseColors(
        string parameters, string?[] defaults)
    {
        Dictionary<string, string>? values = null;
        try
        {
            values =
                System.Text.Json.JsonSerializer.Deserialize<
                    Dictionary<string, string>>(parameters);
        }
        catch (System.Text.Json.JsonException) { }
        return Enumerable.Range(0, 5).Select(index =>
        {
            string? value = values?.GetValueOrDefault($"color{index}")
                ?? defaults[index];
            DrawingColor color = System.Drawing.ColorTranslator.FromHtml(
                string.IsNullOrWhiteSpace(value) ? "transparent" : value);
            return new Vector3(
                color.R / 255f, color.G / 255f, color.B / 255f);
        }).ToArray();
    }

    private const string VertexShader = """
        #version 330 core
        uniform int rotate180;
        uniform int mirrorX;
        out vec2 coord;
        const vec2 positions[4] = vec2[4](
            vec2(-1.0,  1.0), vec2(-1.0, -1.0),
            vec2( 1.0,  1.0), vec2( 1.0, -1.0));
        const vec2 coordinates[4] = vec2[4](
            vec2(0.0, 0.0), vec2(0.0, 1.0),
            vec2(1.0, 0.0), vec2(1.0, 1.0));
        void main() {
            vec2 uv = coordinates[gl_VertexID];
            if (mirrorX != 0) uv.x = 1.0 - uv.x;
            if (rotate180 != 0) uv = 1.0 - uv;
            coord = uv;
            gl_Position = vec4(positions[gl_VertexID], 0.0, 1.0);
        }
        """;

    private const string FragmentHeader = """
        #version 330 core
        uniform float qt_Opacity;
        in vec2 coord;
        out vec4 qtFragColor;
        uniform vec3 iResolution;
        uniform float iTime;
        uniform float iChannelTime[4];
        uniform vec3 iChannelResolution[4];
        uniform vec4 iMouse;
        uniform sampler2D iChannel0;
        uniform sampler2D iChannel1;
        uniform sampler2D iChannel2;
        uniform sampler2D iChannel3;
        uniform vec4 iDate;
        uniform float iSampleRate;
        """;

    private const string FragmentFooter = """

        void main() {
            vec2 localFragCoord = coord * iResolution.xy;
            mainImage(qtFragColor, localFragCoord);
        }
        """;
}
