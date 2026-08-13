using System.Drawing.Drawing2D;

namespace IStripperQuickPlayer.BLL;

internal static class QuickPlayerCardOverlays
{
    private const int Width = 192;
    private const int Height = 288;

    internal static readonly CardOverlayChoice[] Choices =
    [
        new("quickplayer-new", "QuickPlayer · New", "quickPlayerNew",
            "{\"color0\":\"#37F3FF\",\"color1\":\"#8B5CFF\"," +
            "\"color2\":\"#E8FFFF\"}"),
        new("quickplayer-recent", "QuickPlayer · Recent", "quickPlayerRecent",
            "{\"color0\":\"#FFB52E\",\"color1\":\"#FF3F81\"," +
            "\"color2\":\"#FFF1B8\"}"),
        new("quickplayer-favourite", "QuickPlayer · Favourite",
            "quickPlayerFavourite",
            "{\"color0\":\"#FF3D8D\",\"color1\":\"#9B4DFF\"," +
            "\"color2\":\"#FFE4F1\"}")
    ];

    internal static bool Contains(string id) =>
        Choices.Any(choice => choice.Id.Equals(
            id, StringComparison.OrdinalIgnoreCase));

    internal static CardOverlayLoader.CardOverlay? Create(
        CardOverlayChoice choice)
    {
        bool favourite = choice.Id.Equals(
            "quickplayer-favourite", StringComparison.OrdinalIgnoreCase);
        Bitmap? sheet = GlslOverlayRenderer.Render(
            choice.File, favourite ? FavouriteShader : BorderShader,
            choice.Parameters, [null, null, null, null, null],
            null, null, 0, false,
            out int frameCount, out int frameDuration, Width, Height);
        if (sheet == null)
            return null;
        if (favourite)
            DrawHeart(sheet, frameCount);
        else
            DrawLabel(sheet, frameCount,
                choice.Id.EndsWith("new", StringComparison.OrdinalIgnoreCase)
                    ? "NEW" : "RECENT",
                choice.Id.EndsWith("new", StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(55, 242, 255)
                    : Color.FromArgb(255, 181, 46));
        return new(sheet, null, Width, Height, frameCount, frameDuration);
    }

    private static void DrawHeart(Bitmap sheet, int frameCount)
    {
        using Graphics graphics = Graphics.FromImage(sheet);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        for (int frame = 0; frame < frameCount; frame++)
        {
            Rectangle frameBounds = FrameBounds(sheet, frame);
            double phase = frame / (double)frameCount * 5 % 1;
            double beat = Heartbeat(phase);
            float size = 40 * (1 + (float)beat * .11f);
            RectangleF bounds = new(
                frameBounds.Left + Width - 36 - size / 2,
                frameBounds.Top + 38 - size / 2, size, size);
            using GraphicsPath heart = HeartPath(bounds);
            using SolidBrush fill = new(Mix(
                Color.FromArgb(255, 91, 151),
                Color.FromArgb(255, 238, 230),
                Math.Min(1, (float)beat) * .28f));
            using Pen edge = new(Color.FromArgb(235, 255, 210, 232), 2f)
            {
                LineJoin = LineJoin.Round
            };
            graphics.FillPath(fill, heart);
            graphics.DrawPath(edge, heart);
        }
    }

    private static double Pulse(double phase, double centre, double width)
    {
        double distance = Math.Abs(phase - centre);
        double x = Math.Min(distance, 1 - distance) / width;
        return Math.Exp(-x * x);
    }

    private static double Heartbeat(double phase) =>
        Pulse(phase, .18, .10) + .58 * Pulse(phase, .40, .105);

    internal static bool VerifyHeartbeat()
    {
        double[] samples = Enumerable.Range(0, 154)
            .Select(frame => Heartbeat(frame / 154d * 5 % 1))
            .ToArray();
        return samples[6] > .9 && samples[12] > .5 &&
            samples[23] < .01 &&
            Math.Abs(Heartbeat(1) - Heartbeat(0)) < .001 &&
            samples.Zip(samples.Skip(1), (first, second) =>
                Math.Abs(second - first)).Max() < .6;
    }

    private static GraphicsPath HeartPath(RectangleF bounds)
    {
        PointF Point(float x, float y) => new(
            bounds.Left + bounds.Width * x,
            bounds.Top + bounds.Height * y);
        GraphicsPath heart = new();
        heart.StartFigure();
        heart.AddBezier(Point(.5f, .95f), Point(.44f, .86f),
            Point(.05f, .62f), Point(.05f, .34f));
        heart.AddBezier(Point(.05f, .34f), Point(.05f, .10f),
            Point(.34f, -.02f), Point(.5f, .20f));
        heart.AddBezier(Point(.5f, .20f), Point(.66f, -.02f),
            Point(.95f, .10f), Point(.95f, .34f));
        heart.AddBezier(Point(.95f, .34f), Point(.95f, .62f),
            Point(.56f, .86f), Point(.5f, .95f));
        heart.CloseFigure();
        return heart;
    }

    private static Color Mix(Color first, Color second, float amount) =>
        Color.FromArgb(
            (int)Math.Round(first.R + (second.R - first.R) * amount),
            (int)Math.Round(first.G + (second.G - first.G) * amount),
            (int)Math.Round(first.B + (second.B - first.B) * amount));

    private static void DrawLabel(
        Bitmap sheet, int frameCount, string text, Color accent)
    {
        using Graphics graphics = Graphics.FromImage(sheet);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint =
            System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using Font font = new(
            "Segoe UI Semibold", text == "NEW" ? 18 : 14,
            FontStyle.Bold, GraphicsUnit.Pixel);
        using StringFormat format = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        int width = text == "NEW" ? 68 : 94;
        for (int frame = 0; frame < frameCount; frame++)
        {
            Rectangle frameBounds = FrameBounds(sheet, frame);
            float pulse = .72f + .28f * (float)Math.Sin(
                frame / (double)frameCount * Math.PI * 4);
            Rectangle bounds = new(frameBounds.Left + 14,
                frameBounds.Top + 14, width, 32);
            using GraphicsPath badge = RoundedRectangle(bounds, 10);
            using LinearGradientBrush background = new(
                bounds, Color.FromArgb(235, 48, 53, 66),
                Color.FromArgb(235, 7, 8, 14),
                LinearGradientMode.Vertical);
            background.InterpolationColors = new ColorBlend
            {
                Colors =
                [
                    Color.FromArgb(235, 62, 68, 82),
                    Color.FromArgb(235, 9, 10, 18),
                    Color.FromArgb(235, 34, 38, 49)
                ],
                Positions = [0, .55f, 1]
            };
            using Pen edge = new(Color.FromArgb(
                180 + (int)(60 * pulse), accent), 2f);
            using LinearGradientBrush foreground = new(
                bounds, Color.FromArgb(218, 226, 232),
                Color.FromArgb(120, 136, 150),
                LinearGradientMode.Vertical);
            using GraphicsPath letters = new();
            letters.AddString(text, font.FontFamily, (int)font.Style,
                font.Size, bounds, format);
            graphics.FillPath(background, badge);
            graphics.DrawPath(edge, badge);
            graphics.FillPath(foreground, letters);
            DrawTextSpark(graphics, letters, bounds,
                frame / (double)frameCount * Math.Tau);
        }
    }

    private static Rectangle FrameBounds(Bitmap sheet, int frame)
    {
        int columns = Math.Max(1, sheet.Width / Width);
        return new Rectangle(frame % columns * Width,
            frame / columns * Height, Width, Height);
    }

    private static void DrawTextSpark(
        Graphics graphics, GraphicsPath letters, Rectangle bounds,
        double phase)
    {
        PointF centre = TextSparkPosition(bounds, phase);
        float radius = 7 + (float)Math.Sin(phase * 2 + .4);
        using GraphicsPath glow = new();
        glow.AddEllipse(centre.X - radius, centre.Y - radius,
            radius * 2, radius * 2);
        using PathGradientBrush spark = new(glow)
        {
            CenterColor = Color.White,
            SurroundColors = [Color.Transparent]
        };
        GraphicsState state = graphics.Save();
        graphics.SetClip(letters, CombineMode.Intersect);
        graphics.FillPath(spark, glow);
        graphics.Restore(state);
    }

    private static PointF TextSparkPosition(Rectangle bounds, double phase) =>
        new(
            bounds.Left + bounds.Width * (float)(.5 +
                .38 * Math.Sin(phase) + .06 * Math.Sin(phase * 3 + .65)),
            bounds.Top + bounds.Height * (float)(.5 +
                .08 * Math.Sin(phase * 2 + 1.1) +
                .04 * Math.Sin(phase * 5 + .3)));

    internal static bool VerifyTextSpark()
    {
        Rectangle bounds = new(0, 0, 94, 32);
        PointF start = TextSparkPosition(bounds, 0);
        PointF end = TextSparkPosition(bounds, Math.Tau);
        PointF quarter = TextSparkPosition(bounds, Math.Tau / 4);
        return Math.Abs(start.X - end.X) < .001 &&
            Math.Abs(start.Y - end.Y) < .001 &&
            Math.Abs(quarter.Y - start.Y) > 1;
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();
        int diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top,
            diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter,
            diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter,
            diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private const string BorderShader = """
        uniform vec3 color0;
        uniform vec3 color1;
        uniform vec3 color2;

        float roundedBox(vec2 p, vec2 halfSize, float radius) {
            vec2 q = abs(p) - halfSize + radius;
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
        }

        float edgePosition(vec2 p, vec2 resolution) {
            float top = p.y;
            float right = resolution.x - p.x;
            float bottom = resolution.y - p.y;
            float left = p.x;
            float nearest = min(min(top, right), min(bottom, left));
            float along = nearest == top ? p.x :
                nearest == right ? resolution.x + p.y :
                nearest == bottom ? resolution.x + resolution.y +
                    resolution.x - p.x :
                    2.0 * resolution.x + resolution.y + resolution.y - p.y;
            return along / (2.0 * (resolution.x + resolution.y));
        }

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            float scale = iResolution.x / 96.0;
            vec2 center = fragCoord - iResolution.xy * 0.5;
            float edge = roundedBox(center,
                iResolution.xy * 0.5 - 1.25 * scale, 6.0 * scale);
            float distanceToEdge = abs(edge);
            float border = 1.0 - smoothstep(
                0.6 * scale, 2.0 * scale, distanceToEdge);
            float glow = exp(-max(distanceToEdge - 1.0, 0.0) * 0.68) * 0.22;
            float loopPhase = fract(iTime / 11.55);
            float orbit = atan(center.y, center.x) * 3.0 +
                length(center) * 0.055 / scale -
                loopPhase * 25.132741;
            float ribbon = 0.5 + 0.5 * sin(orbit);
            float position = edgePosition(fragCoord, iResolution.xy);
            float cometPosition = fract(loopPhase * 2.0);
            float separation = abs(position - cometPosition);
            float headDistance = min(separation, 1.0 - separation);
            float cometHead = exp(-headDistance * headDistance * 5000.0);
            float behind = fract(cometPosition - position + 1.0);
            float cometTrail = exp(-behind * 20.0) *
                (1.0 - step(0.16, behind));
            vec3 colour = mix(color0, color1, ribbon);
            colour = mix(colour, color2,
                clamp(cometHead + cometTrail * 0.68, 0.0, 1.0));
            float alpha = max(border * (0.62 + ribbon * 0.38),
                glow * (0.42 + cometHead * 3.5 + cometTrail * 1.6));
            fragColor = vec4(colour * alpha, alpha);
        }
        """;

    private const string FavouriteShader = """
        uniform vec3 color0;
        uniform vec3 color1;
        uniform vec3 color2;

        float pulse(float phase, float centre, float width) {
            float distance = abs(phase - centre);
            float x = min(distance, 1.0 - distance) / width;
            return exp(-x * x);
        }

        float roundedBox(vec2 p, vec2 halfSize, float radius) {
            vec2 q = abs(p) - halfSize + radius;
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
        }

        float edgePosition(vec2 p, vec2 resolution) {
            float top = p.y;
            float right = resolution.x - p.x;
            float bottom = resolution.y - p.y;
            float left = p.x;
            float nearest = min(min(top, right), min(bottom, left));
            float along = nearest == top ? p.x :
                nearest == right ? resolution.x + p.y :
                nearest == bottom ? resolution.x + resolution.y +
                    resolution.x - p.x :
                    2.0 * resolution.x + resolution.y + resolution.y - p.y;
            return along / (2.0 * (resolution.x + resolution.y));
        }

        void mainImage(out vec4 fragColor, in vec2 fragCoord) {
            float scale = iResolution.x / 96.0;
            float loopPhase = fract(iTime / 11.55);
            float phase = fract(loopPhase * 5.0);
            float beat = pulse(phase, 0.18, 0.10) +
                0.58 * pulse(phase, 0.40, 0.105);
            vec2 center = fragCoord - iResolution.xy * 0.5;
            float edge = roundedBox(center,
                iResolution.xy * 0.5 - 1.25 * scale, 6.0 * scale);
            float distanceToEdge = abs(edge);
            float border = 1.0 - smoothstep(
                0.6 * scale, 2.0 * scale, distanceToEdge);
            float glow = exp(-max(distanceToEdge - 1.0, 0.0) * 0.31) *
                (0.20 + beat * 0.10);
            float travel = 0.5 + 0.5 * sin(
                atan(center.y, center.x) * 4.0 -
                loopPhase * 18.849556);
            vec3 borderColour = mix(color0, color1, travel);
            float position = edgePosition(fragCoord, iResolution.xy);
            float cometPosition = fract(loopPhase * 2.0);
            float separation = abs(position - cometPosition);
            float headDistance = min(separation, 1.0 - separation);
            float cometHead = exp(-headDistance * headDistance * 5000.0);
            float behind = fract(cometPosition - position + 1.0);
            float cometTrail = exp(-behind * 20.0) *
                (1.0 - step(0.16, behind));
            borderColour = mix(borderColour, color2,
                clamp(cometHead + cometTrail * 0.68, 0.0, 1.0));
            float alpha = max(border * (0.65 + travel * 0.35),
                glow * (1.0 + cometHead * 3.5 + cometTrail * 1.6));
            fragColor = vec4(borderColour * alpha, alpha);
        }
        """;
}
