using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace RfMeterGui;

/// <summary>One chart point: the avg/min/max of the samples that arrived in one UI tick.</summary>
public readonly record struct ChartSample(DateTime Time, double Avg, double Min, double Max);

/// <summary>
/// Grafana-style time-series chart. The traces are colored by signal strength through a
/// vertical gradient mapped to the dBm axis (violet floor → blue → amber → orange → red),
/// with a soft area fill under the average trace.
///
/// Performance: data is decimated to ~2 px columns and everything is drawn in a single
/// OnRender pass (no per-frame UIElement churn), so the render cost is bounded by the
/// control's width — not by how much history has accumulated. This fixes the old
/// "app gets slower the longer it runs" behavior of the Canvas-based chart.
/// </summary>
public sealed class SignalChart : FrameworkElement
{
    private List<ChartSample>? _bins;
    private DateTime _now;
    private int _windowSec = 60;
    private bool _showMax = true;
    private double _peakHoldDbm = double.NegativeInfinity;

    // Pooled decimation buffers, grown on demand — no per-frame allocation.
    private double[] _colSum = Array.Empty<double>();
    private int[] _colCount = Array.Empty<int>();
    private double[] _colMax = Array.Empty<double>();

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6E, 0x5F, 0x96)));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0x26, 0x1C, 0x42), 1);

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double thickness)
    {
        var p = new Pen(Frozen(new SolidColorBrush(c)), thickness);
        p.Freeze();
        return p;
    }

    /// <summary>
    /// Signal-strength color anchors: the same scale colors the time-series traces and the
    /// spectrum bars. Low = violet/blue (near the noise floor), then amber, orange, red hot.
    /// </summary>
    private static readonly (double Dbm, Color Color)[] ValueStops =
    {
        (-95, Color.FromRgb(0x8B, 0x5C, 0xF6)), // violet — noise floor
        (-70, Color.FromRgb(0x3B, 0x82, 0xF6)), // blue
        (-45, Color.FromRgb(0xF5, 0x9E, 0x0B)), // amber
        (-25, Color.FromRgb(0xF9, 0x73, 0x16)), // orange
        (-10, Color.FromRgb(0xEF, 0x44, 0x44)), // red — hot
    };

    /// <summary>Interpolate the strength scale at one dBm value.</summary>
    public static Color ColorForDbm(double dbm)
    {
        if (dbm <= ValueStops[0].Dbm) return ValueStops[0].Color;
        for (int i = 1; i < ValueStops.Length; i++)
        {
            if (dbm <= ValueStops[i].Dbm)
            {
                double t = (dbm - ValueStops[i - 1].Dbm) / (ValueStops[i].Dbm - ValueStops[i - 1].Dbm);
                return LerpColor(ValueStops[i - 1].Color, ValueStops[i].Color, t);
            }
        }
        return ValueStops[^1].Color;
    }

    private static Color LerpColor(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>
    /// Vertical gradient brush whose colors line up with the dBm axis (top = axisHi), so a
    /// stroked trace is automatically colored by its value, Grafana style.
    /// </summary>
    private static LinearGradientBrush BuildAxisBrush(double axisLo, double axisHi, double height, byte alpha)
    {
        double span = axisHi - axisLo;
        var stops = new GradientStopCollection();
        void Add(double offset, Color c) => stops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), offset));
        Add(0, ColorForDbm(axisHi));
        foreach (var (dbm, color) in ValueStops)
            if (dbm > axisLo && dbm < axisHi)
                Add((axisHi - dbm) / span, color);
        Add(1, ColorForDbm(axisLo));
        var brush = new LinearGradientBrush(stops, new Point(0, 0), new Point(0, height))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Feed the chart the current state and schedule a repaint.</summary>
    public void Update(List<ChartSample> bins, DateTime now, int windowSeconds, bool showMax, double peakHoldDbm)
    {
        _bins = bins;
        _now = now;
        _windowSec = windowSeconds;
        _showMax = showMax;
        _peakHoldDbm = peakHoldDbm;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width < 60 || height < 40) return;

        // Transparent hit-test rect so the element has a size even with no strokes.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var bins = _bins;
        if (_now == default) _now = DateTime.UtcNow;   // first render happens before any Update
        var cutoff = _now.AddSeconds(-_windowSec);

        // Find the first bin inside the visible window (bins are time-ordered).
        int firstVisible = 0, visibleCount = 0;
        if (bins != null)
        {
            firstVisible = bins.Count;
            for (int i = bins.Count - 1; i >= 0; i--)
            {
                if (bins[i].Time < cutoff) break;
                firstVisible = i;
            }
            visibleCount = bins.Count - firstVisible;
        }

        // Auto-range the Y axis to the visible data, snapped outward to 5 dB steps.
        double dataMin = double.PositiveInfinity, dataMax = double.NegativeInfinity;
        for (int i = firstVisible; i < (bins?.Count ?? 0); i++)
        {
            if (bins![i].Min < dataMin) dataMin = bins[i].Min;
            if (bins[i].Max > dataMax) dataMax = bins[i].Max;
        }
        if (visibleCount == 0) { dataMin = -90; dataMax = 0; }

        double axisLo = Math.Floor((dataMin - 2) / 5.0) * 5.0;
        double axisHi = Math.Ceiling((dataMax + 2) / 5.0) * 5.0;
        if (axisHi - axisLo < 10) { var center = (axisHi + axisLo) / 2.0; axisLo = center - 5; axisHi = center + 5; }
        axisLo = Math.Max(axisLo, -110);
        axisHi = Math.Min(axisHi, 30);

        double Y(double dbm) => height - (dbm - axisLo) / (axisHi - axisLo) * height;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Horizontal (dBm) gridlines with labels, at a step that yields at most 9 lines.
        double span = axisHi - axisLo;
        double step = new[] { 1.0, 2.0, 5.0, 10.0, 20.0 }.First(s => span / s <= 9);
        for (double v = Math.Ceiling(axisLo / step) * step; v <= axisHi + 0.001; v += step)
        {
            double y = Y(v);
            dc.DrawLine(GridPen, new Point(0, y), new Point(width, y));
            var label = new FormattedText(v.ToString("0", Inv), Inv, FlowDirection.LeftToRight,
                LabelTypeface, 10, LabelBrush, pixelsPerDip);
            dc.DrawText(label, new Point(3, y - 13));
        }

        // Vertical (time) gridlines with seconds-ago labels.
        for (int k = 1; k < 5; k++)
        {
            double secondsBack = _windowSec * k / 5.0;
            double x = width - secondsBack / _windowSec * width;
            dc.DrawLine(GridPen, new Point(x, 0), new Point(x, height));
            var label = new FormattedText($"-{secondsBack:0}s", Inv, FlowDirection.LeftToRight,
                LabelTypeface, 10, LabelBrush, pixelsPerDip);
            dc.DrawText(label, new Point(x + 3, height - 15));
        }

        if (visibleCount > 0)
        {
            // Decimate to ~2 px columns: one avg + max pair per column regardless of history size.
            const double colW = 2.0;
            int nCols = Math.Max(1, (int)(width / colW));
            if (_colSum.Length < nCols)
            {
                _colSum = new double[nCols];
                _colCount = new int[nCols];
                _colMax = new double[nCols];
            }
            Array.Clear(_colCount, 0, nCols);
            Array.Clear(_colSum, 0, nCols);
            Array.Fill(_colMax, double.NegativeInfinity, 0, nCols);

            for (int i = firstVisible; i < bins!.Count; i++)
            {
                var bin = bins[i];
                double x = width - (_now - bin.Time).TotalSeconds / _windowSec * width;
                int c = (int)(x / colW);
                if (c < 0 || c >= nCols) continue;
                _colSum[c] += bin.Avg;
                _colCount[c]++;
                if (bin.Max > _colMax[c]) _colMax[c] = bin.Max;
            }

            var strokeBrush = BuildAxisBrush(axisLo, axisHi, height, 0xFF);
            var fillBrush = BuildAxisBrush(axisLo, axisHi, height, 0x3A);
            var avgPen = new Pen(strokeBrush, 2.0) { LineJoin = PenLineJoin.Round };
            avgPen.Freeze();

            // Area fill under the average trace (closed down to the chart floor).
            double firstX = -1, lastX = -1;
            var fillGeo = new StreamGeometry();
            using (var ctx = fillGeo.Open())
            {
                bool started = false;
                for (int c = 0; c < nCols; c++)
                {
                    if (_colCount[c] == 0) continue;
                    double x = c * colW + colW / 2;
                    var p = new Point(x, Y(_colSum[c] / _colCount[c]));
                    if (!started)
                    {
                        firstX = x;
                        ctx.BeginFigure(new Point(x, height), isFilled: true, isClosed: true);
                        ctx.LineTo(p, false, false);
                        started = true;
                    }
                    else ctx.LineTo(p, false, false);
                    lastX = x;
                }
                if (started) ctx.LineTo(new Point(lastX, height), false, false);
            }
            fillGeo.Freeze();
            if (firstX >= 0) dc.DrawGeometry(fillBrush, null, fillGeo);

            // Max envelope (thin, translucent, same value-colored gradient).
            if (_showMax)
            {
                var maxPen = new Pen(BuildAxisBrush(axisLo, axisHi, height, 0x90), 1.1);
                maxPen.Freeze();
                var maxGeo = BuildPolyline(nCols, colW, c => _colCount[c] > 0 ? Y(_colMax[c]) : double.NaN);
                dc.DrawGeometry(null, maxPen, maxGeo);
            }

            // Average trace on top.
            var avgGeo = BuildPolyline(nCols, colW, c => _colCount[c] > 0 ? Y(_colSum[c] / _colCount[c]) : double.NaN);
            dc.DrawGeometry(null, avgPen, avgGeo);
        }

        // Dashed peak-hold line, colored by its own strength, when inside the visible range.
        if (!double.IsNegativeInfinity(_peakHoldDbm) && _peakHoldDbm >= axisLo && _peakHoldDbm <= axisHi)
        {
            var peakPen = new Pen(Frozen(new SolidColorBrush(ColorForDbm(_peakHoldDbm))), 1.2)
            {
                DashStyle = new DashStyle(new double[] { 4, 4 }, 0),
            };
            peakPen.Freeze();
            double y = Y(_peakHoldDbm);
            dc.DrawLine(peakPen, new Point(0, y), new Point(width, y));
        }
    }

    /// <summary>Build one open polyline across the decimation columns (NaN = skip column).</summary>
    private static StreamGeometry BuildPolyline(int nCols, double colW, Func<int, double> yAt)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool started = false;
            for (int c = 0; c < nCols; c++)
            {
                double y = yAt(c);
                if (double.IsNaN(y)) continue;
                var p = new Point(c * colW + colW / 2, y);
                if (!started) { ctx.BeginFigure(p, false, false); started = true; }
                else ctx.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        return geo;
    }
}
