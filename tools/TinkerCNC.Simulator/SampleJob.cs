using System.Globalization;
using System.Text;
using TinkerCNC.GCode;

namespace TinkerCNC.Simulator;

/// <summary>A generated demo program with three tools and an <c>;@MKR</c> metadata header.</summary>
public static class SampleJob
{
    public const string Path = "/sd/gcodes/demo_part.nc";

    public static string Generate()
    {
        var g = new StringBuilder();
        g.AppendLine(";@MKR|BEGIN");
        g.AppendLine(";@MKR|SCHEMA|v=1.0.0");
        g.AppendLine(";@MKR|CAM|id=TinkerCNC.Simulator|name=TinkerCNC Simulator|v=simulated");
        g.AppendLine(";@MKR|UNIT|value=mm");
        g.AppendLine(";@MKR|MAXFEEDRATE|value=3000");
        g.AppendLine(";@MKR|TOOL|number=1|name=Flat End 3.175|type=Flat End|diameter=3.175");
        g.AppendLine(";@MKR|TOOL|number=2|name=Ball Nose 2.0|type=Ball Nose|diameter=2");
        g.AppendLine(";@MKR|TOOL|number=3|name=V-Bit 30°|type=Engraving|diameter=3.175");
        g.AppendLine(";@MKR|TOOLPATH|number=1|tool_number=1|name=Pocket roughing");
        g.AppendLine(";@MKR|TOOLPATH|number=2|tool_number=2|name=3D finishing");
        g.AppendLine(";@MKR|TOOLPATH|number=3|tool_number=3|name=Engrave outline");
        g.AppendLine(";@MKR|END");
        g.AppendLine();                                                   // not counted by the firmware
        g.AppendLine("; " + new string('-', MachineLineMap.MaxLineBytes)); // too long: skipped by the firmware
        g.AppendLine("G21 G90 G17");

        Toolpath(g, 1, 1, () => Pocket(g, 120, 80, 80, 50, depth: -3, step: 2.5));
        Toolpath(g, 2, 2, () => Raster(g, 110, 70, 100, 60, step: 1.2));
        Toolpath(g, 3, 3, () => Circles(g, 160, 100, 6));

        g.AppendLine("G0 Z10");
        g.AppendLine("M5");
        g.AppendLine("G0 X0 Y0");
        g.AppendLine("M30");
        return g.ToString();
    }

    private static void Toolpath(StringBuilder g, int number, int tool, Action body)
    {
        g.AppendLine($";@MKR|TOOLPATH_START|toolpath_number={number}");
        g.AppendLine($"M6 T{tool}");
        g.AppendLine("M3 S12000");
        g.AppendLine("G0 Z5");
        body();
        g.AppendLine("G0 Z10");
    }

    /// <summary>Concentric rectangles from the outside in, at two depths.</summary>
    private static void Pocket(StringBuilder g, double cx, double cy, double w, double h, double depth, double step)
    {
        for (var z = -1.5; z >= depth; z -= 1.5)
        {
            for (var inset = 0.0; inset < Math.Min(w, h) / 2; inset += step)
            {
                double x0 = cx - w / 2 + inset, x1 = cx + w / 2 - inset, y0 = cy - h / 2 + inset, y1 = cy + h / 2 - inset;
                g.AppendLine(F($"G0 X{x0} Y{y0}"));
                g.AppendLine(F($"G1 Z{z} F300"));
                g.AppendLine(F($"G1 X{x1} F1200"));
                g.AppendLine(F($"G1 Y{y1}"));
                g.AppendLine(F($"G1 X{x0}"));
                g.AppendLine(F($"G1 Y{y0}"));
                g.AppendLine("G0 Z2");
            }
        }
    }

    /// <summary>Zig-zag raster with a wavy Z, like a 3D finishing pass.</summary>
    private static void Raster(StringBuilder g, double cx, double cy, double w, double h, double step)
    {
        var forward = true;
        g.AppendLine(F($"G0 X{cx - w / 2} Y{cy - h / 2}"));
        g.AppendLine("G1 Z-1 F400");
        for (var y = cy - h / 2; y <= cy + h / 2; y += step)
        {
            for (var i = 0; i <= 10; i++)
            {
                var t = forward ? i / 10.0 : 1 - i / 10.0;
                var x = cx - w / 2 + t * w;
                var z = -1 - 0.8 * Math.Sin(t * Math.PI) * Math.Cos((y - cy) / h * Math.PI);
                g.AppendLine(F($"G1 X{x} Y{y} Z{z} F1800"));
            }
            forward = !forward;
        }
    }

    /// <summary>Concentric circles with G2 arcs.</summary>
    private static void Circles(StringBuilder g, double cx, double cy, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            var r = i * 3.0;
            g.AppendLine(F($"G0 X{cx + r} Y{cy}"));
            g.AppendLine("G1 Z-0.6 F200");
            g.AppendLine(F($"G2 X{cx + r} Y{cy} I{-r} J0 F600"));
            g.AppendLine("G0 Z2");
        }
    }

    /// <summary>Invariant formatting with coordinates rounded to 0.001 mm.</summary>
    private static string F(FormattableString s) =>
        string.Format(CultureInfo.InvariantCulture, s.Format, s.GetArguments().Select(a => a is double d ? Math.Round(d, 3) : a).ToArray());
}
