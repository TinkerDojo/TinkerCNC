using TinkerCNC.GCode;

namespace TinkerCNC.Tests;

public class ToolpathTests
{
    private static (double X, double Y, double Z) Point(Toolpath path, int k) =>
        (path.Positions[3 * k], path.Positions[3 * k + 1], path.Positions[3 * k + 2]);

    [Fact]
    public void First_move_and_moves_after_a_tool_change_start_unknown()
    {
        var path = GCodeEstimator.Analyze("G0 Z5\nG0 X10\nG1 Z-1 F300\nM6 T2\nG0 X20\nG1 Y5").Toolpath;

        Assert.Equal([ToolpathMove.Jump, ToolpathMove.Rapid, ToolpathMove.Feed, ToolpathMove.Jump, ToolpathMove.Feed], path.Moves);
        Assert.Equal([1, 2, 3, 5, 6], path.Lines);
        Assert.Equal((20, 0, -1), Point(path, 3));
    }

    [Fact]
    public void Arcs_are_split_into_chords_on_the_circle()
    {
        var path = GCodeEstimator.Analyze("G0 X10 Y0\nG1 Z0 F600\nG2 X10 Y0 I-10 J0").Toolpath;

        var arc = Enumerable.Range(0, path.Count).Where(k => path.Lines[k] == 3).ToList();
        Assert.Equal(72, arc.Count); // full circle in 5° chords
        Assert.All(arc, k =>
        {
            var (x, y, _) = Point(path, k);
            Assert.Equal(10, Math.Sqrt(x * x + y * y), 3);
        });
        Assert.True(Point(path, arc[0]).Y < 0); // clockwise from (10, 0) goes down first
        Assert.Equal((10, 0, 0), Point(path, arc[^1]));
    }

    [Fact]
    public void Radius_arcs_find_their_centre()
    {
        var analysis = GCodeEstimator.Analyze("G0 X0 Y0\nG1 Z0 F600\nG2 X10 Y10 R10");
        var path = analysis.Toolpath;

        var arc = Enumerable.Range(0, path.Count).Where(k => path.Lines[k] == 3).ToList();
        Assert.Equal(18, arc.Count); // quarter circle around (10, 0)
        Assert.All(arc, k =>
        {
            var (x, y, _) = Point(path, k);
            Assert.Equal(10, Math.Sqrt((x - 10) * (x - 10) + y * y), 3);
        });
        Assert.Equal(Math.PI * 10 / 2 / 600 * 60, analysis.Times.Seconds[2], 3);
    }

    [Fact]
    public void G92_breaks_the_line()
    {
        var path = GCodeEstimator.Analyze("G0 X0\nG1 X10 F600\nG92 X0\nG1 X5").Toolpath;

        Assert.Equal([ToolpathMove.Jump, ToolpathMove.Feed, ToolpathMove.Jump, ToolpathMove.Feed], path.Moves);
        Assert.Equal((0, 0, 0), Point(path, 2));
        Assert.Equal((5, 0, 0), Point(path, 3));
    }

    [Fact]
    public void Rotary_moves_are_drawn_on_the_part()
    {
        var path = GCodeEstimator.Analyze("G0 X0 Y0 Z10 A0\nG1 A90 F600").Toolpath;

        var (x, y, z) = Point(path, path.Count - 1);
        Assert.Equal(0, x, 3);
        Assert.Equal(10, y, 3);  // the tool above the axis touches the side the part turned away from
        Assert.Equal(0, z, 3);
    }
}
