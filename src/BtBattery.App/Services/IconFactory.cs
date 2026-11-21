using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace BtBattery.App;

/// <summary>
/// 画托盘图标：设备轮廓尽量铺满格子，大小接近系统音量、以太网图标。
/// 电池竖着叠在右下角、盖在设备图标之上，用电量填充表示还剩多少。颜色跟随任务栏主题。低于 20% 时改为空心电池加感叹号，并用红色提醒。
/// </summary>
internal static class IconFactory
{
    public static Icon CreateDeviceIcon(DeviceKind kind, int? percent, bool lightGlyph, int pixels) =>
        ToIcon(Render(kind, percent, lightGlyph, pixels, drawMeter: true));

    public static Icon CreateAppIcon(bool lightGlyph, int pixels) =>
        ToIcon(Render(null, null, lightGlyph, pixels, drawMeter: false));

    public static BitmapSource CreateBitmapSource(DeviceKind kind, int? percent, bool lightGlyph, int pixels)
    {
        using var bitmap = Render(kind, percent, lightGlyph, pixels, drawMeter: true);
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                handle,
                IntPtr.Zero,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            NativeMethods.DeleteObject(handle);
        }
    }

    public static int TrayPixels(int dpi)
    {
        if (dpi <= 0)
            dpi = 96;

        var raw = (int)Math.Round(16.0 * dpi / 96.0);
        int[] steps = { 16, 20, 24, 32, 40, 48 };
        var best = 16;
        var distance = int.MaxValue;
        foreach (var step in steps)
        {
            var gap = Math.Abs(step - raw);
            if (gap < distance)
            {
                distance = gap;
                best = step;
            }
        }

        return best;
    }

    private static Bitmap Render(DeviceKind? kind, int? percent, bool lightGlyph, int pixels, bool drawMeter)
    {
        var bitmap = new Bitmap(pixels, pixels, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.Clear(Color.Transparent);

        var glyph = lightGlyph ? Hex("#F3F3F3") : Hex("#1A1A1A");
        var low = lightGlyph ? Hex("#FF6F6F") : Hex("#C42B1C");
        var fill = BatteryMath.Band(percent) == BatteryBand.Low ? low : glyph;

        // 系统托盘里音量、以太网几乎铺满 16 像素格子，这里同样只留很窄的边距。
        var inset = Math.Max(0.2f, pixels * 0.01f);
        var glyphArea = new RectangleF(inset, inset, pixels - inset * 2f, pixels - inset * 2f);

        if (kind is null)
            DrawBluetooth(graphics, glyphArea, glyph);
        else
            DrawGlyph(graphics, kind.Value, glyphArea, glyph);

        if (!drawMeter)
            return bitmap;

        // 电池要足够大，填充高低在 16–20 像素下也能看出大概电量。
        var batteryWidth = Math.Max(5.4f, pixels * 0.40f);
        var batteryHeight = Math.Max(8.8f, pixels * 0.66f);
        var margin = Math.Max(0.35f, pixels * 0.03f);
        var battery = new RectangleF(
            pixels - margin - batteryWidth,
            pixels - margin - batteryHeight,
            batteryWidth,
            batteryHeight);

        PunchHalo(graphics, battery, Math.Max(0.5f, pixels * 0.035f));
        var warn = percent is int level && BatteryMath.Band(level) == BatteryBand.Low;
        DrawWindowsBattery(graphics, battery, pixels, percent, warn ? fill : glyph, fill, warn);
        return bitmap;
    }

    /// <summary>在电池周围挖一圈透明空隙，避免和底下的设备轮廓糊在一起。</summary>
    private static void PunchHalo(Graphics graphics, RectangleF battery, float pad)
    {
        var halo = RectangleF.Inflate(battery, pad, pad);
        using var path = Rounded(halo, Math.Max(0.8f, Math.Min(halo.Width, halo.Height) * 0.16f));
        var previous = graphics.CompositingMode;
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.FromArgb(0, 0, 0, 0)))
            graphics.FillPath(clear, path);
        graphics.CompositingMode = previous;
    }

    private static void DrawWindowsBattery(Graphics graphics, RectangleF area, int pixels, int? percent, Color outlineColor, Color fillColor, bool warn)
    {
        var nubHeight = Math.Max(0.9f, area.Height * 0.09f);
        var nubWidth = Math.Max(1.2f, area.Width * 0.38f);
        var body = new RectangleF(
            area.X,
            area.Y + nubHeight * 0.70f,
            area.Width,
            Math.Max(3f, area.Height - nubHeight * 0.70f));
        var nub = new RectangleF(
            area.X + (area.Width - nubWidth) / 2f,
            area.Y,
            nubWidth,
            nubHeight + Math.Max(0.3f, area.Height * 0.03f));

        var stroke = StrokeWidth(pixels);
        using (var bodyPath = Rounded(body, Math.Max(0.5f, area.Width * 0.14f)))
        using (var pen = new Pen(outlineColor, stroke)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        })
        {
            graphics.DrawPath(pen, bodyPath);
        }

        using (var nubPath = Rounded(nub, Math.Max(0.3f, nubWidth * 0.28f)))
        using (var nubBrush = new SolidBrush(outlineColor))
        {
            graphics.FillPath(nubBrush, nubPath);
        }

        var inset = stroke + Math.Max(0.45f, StrokeWidth(pixels) * 0.35f);
        var inner = RectangleF.Inflate(body, -inset, -inset);
        if (inner.Width < 0.6f || inner.Height < 0.6f)
            return;

        // 低于 20% 时不画填充，改在空心电池里画感叹号。
        if (warn)
        {
            DrawExclamation(graphics, inner, outlineColor, pixels);
            return;
        }

        if (percent is not int value || value <= 0)
            return;

        var fillHeight = inner.Height * Math.Min(value, 100) / 100f;
        if (fillHeight < 0.45f)
            return;

        var fillRect = new RectangleF(inner.X, inner.Bottom - fillHeight, inner.Width, fillHeight);
        using var fillPath = Rounded(fillRect, Math.Max(0.3f, inner.Width * 0.18f));
        using var fillBrush = new SolidBrush(fillColor);
        graphics.FillPath(fillBrush, fillPath);
    }

    private static void DrawExclamation(Graphics graphics, RectangleF inner, Color color, int pixels)
    {
        if (inner.Width < 1.1f || inner.Height < 3.2f)
            return;

        var stroke = StrokeWidth(pixels);
        var stemWidth = Math.Max(stroke, inner.Width * 0.30f);
        var dot = Math.Max(stroke, inner.Width * 0.30f);
        var gap = Math.Max(0.55f, inner.Height * 0.09f);
        var stemHeight = inner.Height * 0.52f;
        var total = stemHeight + gap + dot;
        if (total > inner.Height)
        {
            var scale = inner.Height / total;
            stemHeight *= scale;
            gap *= scale;
            dot *= scale;
            total = stemHeight + gap + dot;
        }

        var cx = inner.X + inner.Width / 2f;
        var top = inner.Y + (inner.Height - total) / 2f;
        var stem = new RectangleF(cx - stemWidth / 2f, top, stemWidth, stemHeight);
        using (var path = Rounded(stem, stemWidth / 2f))
        using (var brush = new SolidBrush(color))
            graphics.FillPath(brush, path);

        var dotRect = new RectangleF(cx - dot / 2f, top + stemHeight + gap, dot, dot);
        using var dotBrush = new SolidBrush(color);
        graphics.FillEllipse(dotBrush, dotRect);
    }

    private static void DrawGlyph(Graphics graphics, DeviceKind kind, RectangleF area, Color color)
    {
        switch (kind)
        {
            case DeviceKind.Keyboard:
                DrawKeyboard(graphics, area, color);
                break;
            case DeviceKind.Headset:
                DrawHeadset(graphics, area, color);
                break;
            case DeviceKind.Gamepad:
                DrawGamepad(graphics, area, color);
                break;
            case DeviceKind.Combo:
                DrawCombo(graphics, area, color);
                break;
            default:
                DrawMouse(graphics, area, color);
                break;
        }
    }

    private static void DrawMouse(Graphics graphics, RectangleF area, Color color)
    {
        using var pen = PenOf(color, area);
        var body = new RectangleF(area.X + area.Width * 0.22f, area.Y + area.Height * 0.01f, area.Width * 0.52f, area.Height * 0.96f);
        graphics.DrawEllipse(pen, body);
        var center = body.X + body.Width / 2f;
        graphics.DrawLine(pen, center, body.Y + body.Height * 0.08f, center, body.Y + body.Height * 0.34f);
    }

    private static void DrawKeyboard(Graphics graphics, RectangleF area, Color color)
    {
        using var pen = PenOf(color, area);
        var body = Inset(area, 0.02f, 0.10f, 0.02f, 0.10f);
        using var path = Rounded(body, Math.Max(1.0f, Math.Min(body.Width, body.Height) * 0.12f));
        graphics.DrawPath(pen, path);
        using var brush = new SolidBrush(color);
        var key = Math.Max(0.9f, area.Width * 0.055f);
        for (var row = 0; row < 2; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                var x = body.X + body.Width * (0.17f + column * 0.19f) - key / 2f;
                var y = body.Y + body.Height * (0.34f + row * 0.34f) - key / 2f;
                graphics.FillRectangle(brush, x, y, key, key);
            }
        }
    }

    private static void DrawHeadset(Graphics graphics, RectangleF area, Color color)
    {
        using var pen = PenOf(color, area);
        var band = new RectangleF(area.X + area.Width * 0.10f, area.Y + area.Height * 0.00f, area.Width * 0.80f, area.Height * 0.76f);
        graphics.DrawArc(pen, band, 200, 140);
        using var brush = new SolidBrush(color);
        var cupW = area.Width * 0.18f;
        var cupH = area.Height * 0.28f;
        graphics.FillEllipse(brush, area.X + area.Width * 0.08f, area.Y + area.Height * 0.52f, cupW, cupH);
        graphics.FillEllipse(brush, area.X + area.Width * 0.74f, area.Y + area.Height * 0.52f, cupW, cupH);
    }

    private static void DrawGamepad(Graphics graphics, RectangleF area, Color color)
    {
        using var pen = PenOf(color, area);
        var body = Inset(area, 0.00f, 0.12f, 0.00f, 0.10f);
        using var path = Rounded(body, Math.Max(2f, body.Height * 0.46f));
        graphics.DrawPath(pen, path);
        using var brush = new SolidBrush(color);
        var thickness = Math.Max(1.2f, area.Width * 0.07f);
        var centerX = body.X + body.Width * 0.32f;
        var centerY = body.Y + body.Height * 0.50f;
        graphics.FillRectangle(brush, centerX - thickness / 2f, centerY - thickness * 1.55f, thickness, thickness * 3.1f);
        graphics.FillRectangle(brush, centerX - thickness * 1.55f, centerY - thickness / 2f, thickness * 3.1f, thickness);
        var dot = Math.Max(1.4f, area.Width * 0.08f);
        graphics.FillEllipse(brush, body.X + body.Width * 0.62f, body.Y + body.Height * 0.22f, dot, dot);
        graphics.FillEllipse(brush, body.X + body.Width * 0.76f, body.Y + body.Height * 0.48f, dot, dot);
    }

    private static void DrawCombo(Graphics graphics, RectangleF area, Color color)
    {
        var keyboard = new RectangleF(area.X, area.Y + area.Height * 0.08f, area.Width * 0.62f, area.Height * 0.78f);
        DrawKeyboard(graphics, keyboard, color);
        var mouse = new RectangleF(area.X + area.Width * 0.58f, area.Y, area.Width * 0.42f, area.Height);
        DrawMouse(graphics, mouse, color);
    }

    private static void DrawBluetooth(Graphics graphics, RectangleF area, Color color)
    {
        using var pen = PenOf(color, area);
        var left = area.X + area.Width * 0.10f;
        var right = area.X + area.Width * 0.90f;
        var center = area.X + area.Width * 0.50f;
        var top = area.Y + area.Height * 0.00f;
        var upper = area.Y + area.Height * 0.27f;
        var mid = area.Y + area.Height * 0.50f;
        var lower = area.Y + area.Height * 0.73f;
        var bottom = area.Y + area.Height * 1.00f;
        graphics.DrawLines(pen, new[]
        {
            new PointF(center, top),
            new PointF(right, upper),
            new PointF(center, mid),
            new PointF(right, lower),
            new PointF(center, bottom),
            new PointF(center, top),
        });
        graphics.DrawLine(pen, left, upper, right, lower);
        graphics.DrawLine(pen, left, lower, right, upper);
    }

    private static float StrokeWidth(int pixels) => Math.Max(1.0f, pixels / 16f);

    private static Pen PenOf(Color color, RectangleF area)
    {
        var stroke = Math.Max(1.0f, Math.Min(area.Width, area.Height) / 16f);
        return new Pen(color, stroke)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
    }

    private static RectangleF Inset(RectangleF area, float left, float top, float right, float bottom) =>
        new(area.X + area.Width * left, area.Y + area.Height * top, area.Width * (1f - left - right), area.Height * (1f - top - bottom));

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        if (diameter < 1.5f || rect.Width < 2f || rect.Height < 2f)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Hex(string hex)
    {
        var value = int.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
            bitmap.Dispose();
        }
    }
}
