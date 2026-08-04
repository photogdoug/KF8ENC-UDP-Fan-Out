using System.Drawing.Drawing2D;

namespace WsjtxUdpFanout;

internal sealed record TrafficLegendItem(string Name, Color Color, double PacketsPerSecond);

internal sealed class DestinationTrafficChart : Control
{
    private const int MaximumSamples = 60;
    private static readonly Color TotalTrafficBlue = Color.FromArgb(30, 144, 255);
    private readonly Queue<double> _samples = new();
    private AppTheme _theme = AppThemes.Light;
    private ulong _lastPackets;
    private bool _hasBaseline;

    public DestinationTrafficChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.White;
        ForeColor = Color.FromArgb(55, 66, 78);
        AccessibleName = "Total packet traffic over the last 60 seconds";
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    public TrafficLegendItem LegendItem => new("All packets", TotalTrafficBlue, _samples.LastOrDefault());

    public void ApplyTheme(AppTheme theme)
    {
        _theme = theme;
        BackColor = theme.Surface;
        ForeColor = theme.Text;
        Invalidate();
    }

    public void Sample(ulong totalPackets)
    {
        ulong packetDelta = _hasBaseline && totalPackets >= _lastPackets
            ? totalPackets - _lastPackets
            : 0;
        _lastPackets = totalPackets;
        _hasBaseline = true;
        _samples.Enqueue(packetDelta);
        while (_samples.Count > MaximumSamples)
            _samples.Dequeue();

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        const int left = 45;
        const int top = 8;
        const int right = 12;
        const int bottom = 27;
        Rectangle plot = new(left, top, Math.Max(1, ClientSize.Width - left - right), Math.Max(1, ClientSize.Height - top - bottom));
        if (plot.Width < 20 || plot.Height < 20)
            return;

        var plottedValues = new double[MaximumSamples];
        double[] samples = _samples.ToArray();
        Array.Copy(samples, 0, plottedValues, MaximumSamples - samples.Length, samples.Length);
        double yMaximum = NiceMaximum(Math.Max(5, plottedValues.Max()));

        using var gridPen = new Pen(_theme.ChartGrid, 1);
        using var axisPen = new Pen(_theme.ChartAxis, 1);
        using var labelBrush = new SolidBrush(_theme.MutedText);
        using var emptyBrush = new SolidBrush(_theme.MutedText);
        using var labelFont = new Font("Segoe UI", 7.5F);
        using var emptyFont = new Font("Segoe UI", 9F);

        for (int line = 0; line <= 4; line++)
        {
            float y = plot.Top + (plot.Height * line / 4f);
            e.Graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            double value = yMaximum * (4 - line) / 4d;
            string text = value.ToString(value >= 10 ? "N0" : "N1");
            SizeF size = e.Graphics.MeasureString(text, labelFont);
            e.Graphics.DrawString(text, labelFont, labelBrush, plot.Left - size.Width - 7, y - size.Height / 2);
        }

        string[] timeLabels = ["−60s", "−45s", "−30s", "−15s", "now"];
        for (int tick = 0; tick < timeLabels.Length; tick++)
        {
            float x = plot.Left + (plot.Width * tick / 4f);
            e.Graphics.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
            SizeF size = e.Graphics.MeasureString(timeLabels[tick], labelFont);
            float labelX = Math.Clamp(x - size.Width / 2, plot.Left, plot.Right - size.Width);
            e.Graphics.DrawString(timeLabels[tick], labelFont, labelBrush, labelX, plot.Bottom + 5);
        }

        e.Graphics.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        e.Graphics.DrawLine(axisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);

        if (_samples.Count < 2)
        {
            const string message = "Traffic history will appear here as packets arrive.";
            SizeF size = e.Graphics.MeasureString(message, emptyFont);
            e.Graphics.DrawString(message, emptyFont, emptyBrush,
                plot.Left + (plot.Width - size.Width) / 2,
                plot.Top + (plot.Height - size.Height) / 2);
            return;
        }

        var points = new PointF[MaximumSamples];
        for (int index = 0; index < MaximumSamples; index++)
        {
            float x = plot.Left + plot.Width * index / (MaximumSamples - 1f);
            points[index] = new PointF(
                x,
                plot.Bottom - (float)(Math.Min(plottedValues[index], yMaximum) / yMaximum * plot.Height));
        }
        using var linePen = new Pen(TotalTrafficBlue, 2.2F) { LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(linePen, points);
        PointF lastPoint = points[^1];
        using var markerBrush = new SolidBrush(TotalTrafficBlue);
        e.Graphics.FillEllipse(markerBrush, lastPoint.X - 3.5F, lastPoint.Y - 3.5F, 7, 7);
    }

    private static double NiceMaximum(double maximum)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(maximum)));
        double normalized = maximum / magnitude;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }
}
