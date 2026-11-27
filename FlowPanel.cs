using System.Windows;
using System.Windows.Controls;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace ESCaptureSketch;

/// <summary>
/// 단추를 왼쪽에서 오른쪽으로 놓다가 폭이 모자라면 다음 줄로 내리는 패널 (WrapPanel 과 같다).
/// 차이: 오른쪽 위에 겹쳐 놓인 단추 묶음(Reserved)이 실제로 차지한 곳만 비켜 간다. 묶음은 ㄱ자 모양이라
/// 첫 줄에서는 묶음의 전체 폭을, 그 아래(묶음 높이까지)에서는 맨 오른쪽 열(Corner)의 폭만 비우고, 더 아래는 폭 전체를 쓴다.
/// 첫 단추조차 들어갈 자리가 없는 줄은 비워 두고 다음 줄에서 시작한다.
/// </summary>
public sealed class FlowPanel : Panel
{
    /// <summary>오른쪽 위에 따로 놓이는 요소. 이 요소의 폭·높이만큼을 위쪽 줄에서 비운다.</summary>
    public FrameworkElement? Reserved { get; set; }

    /// <summary>Reserved 안에서 두 줄 높이인 맨 오른쪽 열과, 그 열의 윗단추(첫 줄의 높이).</summary>
    public FrameworkElement? Corner { get; set; }
    public FrameworkElement? CornerTop { get; set; }

    private double _lowerExtra;
    /// <summary>둘째 줄부터 Corner 왼쪽에 더 비울 폭 (그 자리에 따로 놓는 단추가 있을 때).</summary>
    public double LowerExtra
    {
        get => _lowerExtra;
        set { if (_lowerExtra == value) return; _lowerExtra = value; InvalidateMeasure(); }
    }

    private readonly List<Point> _pos = new();

    protected override Size MeasureOverride(Size available)
    {
        Reserved?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var rw = Reserved?.DesiredSize.Width ?? 0;
        var rh = Reserved?.DesiredSize.Height ?? 0;
        // Corner 가 없으면 예전처럼 묶음 전체를 사각형으로 비운다.
        var topHeight = CornerTop?.DesiredSize.Height ?? rh;
        var lowerWidth = (Corner?.DesiredSize.Width ?? rw) + LowerExtra;
        var total = available.Width;

        _pos.Clear();
        double x = 0, y = 0, lineH = 0, maxX = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var s = child.DesiredSize;
            double Limit() => double.IsInfinity(total) ? double.PositiveInfinity : total - (y < topHeight ? rw : y < rh ? lowerWidth : 0);
            if (x > 0 && x + s.Width > Limit())
            {
                y += lineH; x = 0; lineH = 0;
            }
            // 오른쪽 묶음 때문에 이 줄에 하나도 못 놓으면 그 줄은 건너뛴다.
            while (x == 0 && s.Height > 0 && y < rh && s.Width > Limit()) y += s.Height;
            _pos.Add(new Point(x, y));
            x += s.Width;
            lineH = Math.Max(lineH, s.Height);
            maxX = Math.Max(maxX, x);
        }
        var height = Math.Max(y + lineH, rh);
        return new Size(double.IsInfinity(total) ? maxX + rw : total, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var i = 0;
        foreach (UIElement child in InternalChildren)
        {
            var p = i < _pos.Count ? _pos[i] : new Point();
            child.Arrange(new Rect(p, child.DesiredSize));
            i++;
        }
        return final;
    }
}
