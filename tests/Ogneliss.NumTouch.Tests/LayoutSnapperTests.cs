using Ogneliss.NumTouch.Core.Geometry;
using Ogneliss.NumTouch.Core.Layouts;

namespace Ogneliss.NumTouch.Tests;

/// <summary>Привязка в редакторе раскладок: к соседям, промежутку, краям, размеру, сетке.</summary>
public class LayoutSnapperTests
{
    private static readonly (string, RectD)[] Neighbor = { ("a", new RectD(0.10, 0.10, 0.20, 0.20)) };
    private static SnapOptions Opt(double grid = 0) => new() { ThresholdX = 0.02, ThresholdY = 0.02, GapX = 0.02, GapY = 0.03, GridX = grid, GridY = grid };

    [Fact]
    public void Move_NextToNeighbor_SnapsToLayoutGap_AndTopEdge()
    {
        // Сосед справа кончается на 0.30; с промежутком 0.02 левый край должен встать на 0.32, верх — на 0.10.
        var r = LayoutSnapper.Move(new RectD(0.335, 0.112, 0.2, 0.2), Neighbor, Opt()).Rect;
        Assert.Equal(0.32, r.X, 6);
        Assert.Equal(0.10, r.Y, 6);
    }

    [Fact]
    public void Move_ProducesGuides_ForAlignedEdgeAndGap()
    {
        var res = LayoutSnapper.Move(new RectD(0.335, 0.112, 0.2, 0.2), Neighbor, Opt());
        Assert.Contains(res.Guides, g => g.Kind == SnapKind.Gap);
        Assert.Contains(res.Guides, g => g.Kind == SnapKind.Edge && !g.Vertical && Math.Abs(g.Position - 0.10) < 1e-9);
    }

    [Fact]
    public void Move_FarFromKeys_UsesGrid()
    {
        var r = LayoutSnapper.Move(new RectD(0.613, 0.71, 0.1, 0.1), Neighbor, Opt(grid: 0.025)).Rect;
        Assert.Equal(0.625, r.X, 6);
        Assert.Equal(0.70, r.Y, 6);
    }

    [Fact]
    public void Move_BeyondThreshold_WithoutGrid_Unchanged()
    {
        var r = LayoutSnapper.Move(new RectD(0.613, 0.71, 0.1, 0.1), Neighbor, Opt()).Rect;
        Assert.Equal(0.613, r.X, 6);
        Assert.Equal(0.71, r.Y, 6);
    }

    [Fact]
    public void Move_StaysInsideArea_AndSnapsToBorder()
    {
        var r = LayoutSnapper.Move(new RectD(0.905, -0.05, 0.1, 0.1), Neighbor, Opt()).Rect;
        Assert.Equal(0.9, r.X, 6);   // правый край прилип к границе 1
        Assert.Equal(0.0, r.Y, 6);
    }

    [Fact]
    public void Resize_RightEdge_MatchesNeighborWidth()
    {
        var res = LayoutSnapper.Resize(new RectD(0.5, 0.5, 0.188, 0.1), ResizeEdges.Right, Neighbor, Opt());
        Assert.Equal(0.20, res.Rect.Width, 6);
        Assert.Contains("a", res.SameWidthIds);
    }

    [Fact]
    public void Resize_BottomEdge_AlignsWithNeighborBottom()
    {
        var res = LayoutSnapper.Resize(new RectD(0.5, 0.15, 0.1, 0.142), ResizeEdges.Bottom, Neighbor, Opt());
        Assert.Equal(0.30, res.Rect.Bottom, 6);
        Assert.Equal(0.5, res.Rect.X, 6); // остальное не двигается
    }

    [Fact]
    public void Resize_LeftEdge_MovesOnlyLeft()
    {
        var res = LayoutSnapper.Resize(RectD.FromEdges(0.315, 0.15, 0.6, 0.25), ResizeEdges.Left, Neighbor, Opt());
        Assert.Equal(0.32, res.Rect.Left, 6);  // промежуток от соседа
        Assert.Equal(0.6, res.Rect.Right, 6);
    }

    [Fact]
    public void Resize_EnforcesMinimumSize()
    {
        var res = LayoutSnapper.Resize(RectD.FromEdges(0.5, 0.5, 0.501, 0.6), ResizeEdges.Right, Array.Empty<(string, RectD)>(), Opt());
        Assert.Equal(LayoutValidator.MinKeySize, res.Rect.Width, 6);
        Assert.Equal(0.5, res.Rect.Left, 6);
    }
}
