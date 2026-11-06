using SkiaSharp;

namespace TinkerCNC.Simulator;

/// <summary>Draws the JPEG frames sent on the simulated camera channels.</summary>
public sealed class FrameRenderer
{
    private const int Width = 640, Height = 360, Quality = 80;
    private static readonly SKRect Bed = new(40, 30, 600, 330); // 400 x 300 mm
    private static readonly SKColor Background = new(18, 22, 28);
    private static readonly SKColor BedFill = SKColor.Parse("2a2f36");
    private static readonly SKColor Grid = SKColor.Parse("39414b");
    private static readonly SKColor Accent = SKColor.Parse("f08a24");

    private static readonly IReadOnlyDictionary<string, SKColor> StateColors = new Dictionary<string, SKColor>
    {
        ["Idle"] = SKColor.Parse("3ccf8e"),
        ["Alarm"] = SKColor.Parse("ff5d5d"),
    };

    /// <summary>Top view of the bed with the tool position; colour reflects the machine state.</summary>
    public byte[] Live(SimMachine machine)
    {
        var p = machine.MachinePosition;
        var color = StateColors.GetValueOrDefault(machine.State, Accent);
        return Render(canvas =>
        {
            DrawBed(canvas);
            // machine coordinates run from the soft limits (negative) to the homed corner at 0
            var t = SimMachine.Travel;
            var x = Bed.Left + (float)(Math.Clamp(p.X + t.X, 0, t.X) / t.X) * Bed.Width;
            var y = Bed.Bottom - (float)(Math.Clamp(p.Y + t.Y, 0, t.Y) / t.Y) * Bed.Height;
            var radius = 8 + (float)Math.Clamp(p.Z + t.Z, 0, t.Z) / 8;
            using var halo = new SKPaint { Color = color.WithAlpha(90), IsAntialias = true };
            using var tool = new SKPaint { Color = color, IsAntialias = true };
            canvas.DrawCircle(x, y, radius * 2, halo);
            canvas.DrawCircle(x, y, radius, tool);

            // Sweeping marker so motion is visible even while idle.
            using var marker = new SKPaint { Color = Accent };
            canvas.DrawRect(Bed.Left + DateTime.UtcNow.Millisecond / 1000f * (Bed.Width - 20), Bed.Bottom + 14, 20, 6, marker);
        });
    }

    /// <summary>Recorded "carving" whose machined area grows with the frame index.</summary>
    public byte[] Timelapse(int frame, int totalFrames)
    {
        var progress = frame / (float)Math.Max(1, totalFrames - 1);
        return Render(canvas =>
        {
            DrawBed(canvas);
            using var cut = new SKPaint { Color = SKColor.Parse("8a5a2b") };
            using var bar = new SKPaint { Color = Accent };
            canvas.DrawRect(Bed.Left + 20, Bed.Top + 20, (Bed.Width - 40) * progress, Bed.Height - 40, cut);
            canvas.DrawRect(Bed.Left, Bed.Bottom + 14, Bed.Width * progress, 6, bar);
        });
    }

    private static void DrawBed(SKCanvas canvas)
    {
        using var fill = new SKPaint { Color = BedFill };
        using var grid = new SKPaint { Color = Grid, StrokeWidth = 1 };
        canvas.DrawRect(Bed, fill);
        for (var mm = 0; mm <= 400; mm += 50)
        {
            var x = Bed.Left + mm * Bed.Width / 400;
            canvas.DrawLine(x, Bed.Top, x, Bed.Bottom, grid);
        }
        for (var mm = 0; mm <= 300; mm += 50)
        {
            var y = Bed.Bottom - mm * Bed.Height / 300;
            canvas.DrawLine(Bed.Left, y, Bed.Right, y, grid);
        }
    }

    private static byte[] Render(Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        surface.Canvas.Clear(Background);
        draw(surface.Canvas);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, Quality);
        return data.ToArray();
    }
}
