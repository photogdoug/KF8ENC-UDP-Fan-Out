using System.Drawing.Drawing2D;

namespace WsjtxUdpFanout;

internal sealed record TrafficLegendItem(string Name, Color Color, double PacketsPerSecond);

internal sealed class DestinationTrafficChart : Control
{
    private const int MaximumSamples = 60;
    private static readonly Color[] SeriesPalette =
    [
        Color.FromArgb(35, 103, 176),
        Color.FromArgb(33, 145, 89),
        Color.FromArgb(222, 132, 34),
        Color.FromArgb(133, 85, 184),
        Color.FromArgb(201, 68, 76),
        Color.FromArgb(24, 139, 147),
        Color.FromArgb(194, 83, 151),
        Color.FromArgb(114, 93, 74)
    ];

    private readonly Dictionary<Guid, TrafficSeries> _series = [];
    private int _nextColorIndex;

    public DestinationTrafficChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.White;
        ForeColor = Color.FromArgb(55, 66, 78);
        AccessibleName = "Destination traffic over the last 60 seconds";
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    public IReadOnlyList<TrafficLegendItem> LegendItems => _series.Values
        .Select(series => new TrafficLegendItem(series.Name, series.Color, series.Samples.LastOrDefault()))
        .ToList();

    public Color GetSeriesColor(Guid id) => _series.TryGetValue(id, out TrafficSeries? series)
        ? series.Color
        : Color.FromArgb(120, 130, 140);

    public void Sample(IReadOnlyList<TargetSnapshot> targets)
    {
        HashSet<Guid> activeIds = targets.Select(target => target.Id).ToHashSet();
        foreach (Guid removedId in _series.Keys.Where(id => !activeIds.Contains(id)).ToList())
            _series.Remove(removedId);

        foreach (TargetSnapshot target in targets)
        {
            if (!_series.TryGetValue(target.Id, out TrafficSeries? series))
            {
                series = new TrafficSeries(target.Name, NextColor(), target.Packets);
                _series.Add(target.Id, series);
            }

            series.Name = target.Name;
            ulong packetDelta = target.Packets >= series.LastPackets
                ? target.Packets - series.LastPackets
                : target.Packets;
            series.LastPackets = target.Packets;
            series.Samples.Enqueue(packetDelta);
            while (series.Samples.Count > MaximumSamples)
                series.Samples.Dequeue();
        }

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

        TrafficSeries[] orderedSeries = _series.Values.ToArray();
        var plottedValues = new double[orderedSeries.Length][];
        var totals = new double[MaximumSamples];
        for (int seriesIndex = 0; seriesIndex < orderedSeries.Length; seriesIndex++)
        {
            plottedValues[seriesIndex] = new double[MaximumSamples];
            double[] samples = orderedSeries[seriesIndex].Samples.ToArray();
            int startIndex = MaximumSamples - samples.Length;
            for (int index = 0; index < samples.Length; index++)
            {
                plottedValues[seriesIndex][startIndex + index] = samples[index];
                totals[startIndex + index] += samples[index];
            }
        }
        double yMaximum = NiceMaximum(Math.Max(5, totals.DefaultIfEmpty(0).Max()));

        using var gridPen = new Pen(Color.FromArgb(224, 230, 236), 1);
        using var axisPen = new Pen(Color.FromArgb(172, 182, 192), 1);
        using var labelBrush = new SolidBrush(Color.FromArgb(105, 116, 128));
        using var emptyBrush = new SolidBrush(Color.FromArgb(125, 136, 148));
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

        if (_series.Count == 0 || _series.Values.All(series => series.Samples.Count < 2))
        {
            const string message = "Traffic history will appear here as packets arrive.";
            SizeF size = e.Graphics.MeasureString(message, emptyFont);
            e.Graphics.DrawString(message, emptyFont, emptyBrush,
                plot.Left + (plot.Width - size.Width) / 2,
                plot.Top + (plot.Height - size.Height) / 2);
            return;
        }

        var cumulative = new double[MaximumSamples];
        for (int seriesIndex = 0; seriesIndex < orderedSeries.Length; seriesIndex++)
        {
            TrafficSeries series = orderedSeries[seriesIndex];
            double[] values = plottedValues[seriesIndex];
            var topPoints = new PointF[MaximumSamples];
            var bottomPoints = new PointF[MaximumSamples];
            for (int index = 0; index < MaximumSamples; index++)
            {
                float x = plot.Left + plot.Width * index / (MaximumSamples - 1f);
                bottomPoints[index] = new PointF(
                    x,
                    plot.Bottom - (float)(Math.Min(cumulative[index], yMaximum) / yMaximum * plot.Height));
                cumulative[index] += values[index];
                topPoints[index] = new PointF(
                    x,
                    plot.Bottom - (float)(Math.Min(cumulative[index], yMaximum) / yMaximum * plot.Height));
            }

            PointF[] areaPoints = [.. topPoints, .. bottomPoints.Reverse()];
            using var areaBrush = new SolidBrush(Color.FromArgb(72, series.Color));
            using var linePen = new Pen(series.Color, 1.8F) { LineJoin = LineJoin.Round };
            e.Graphics.FillPolygon(areaBrush, areaPoints);
            e.Graphics.DrawLines(linePen, topPoints);
            PointF lastPoint = topPoints[^1];
            using var markerBrush = new SolidBrush(series.Color);
            e.Graphics.FillEllipse(markerBrush, lastPoint.X - 3.5F, lastPoint.Y - 3.5F, 7, 7);
        }
    }

    private Color NextColor()
    {
        Color color = SeriesPalette[_nextColorIndex % SeriesPalette.Length];
        _nextColorIndex++;
        return color;
    }

    private static double NiceMaximum(double maximum)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(maximum)));
        double normalized = maximum / magnitude;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private sealed class TrafficSeries(string name, Color color, ulong lastPackets)
    {
        public string Name { get; set; } = name;
        public Color Color { get; } = color;
        public ulong LastPackets { get; set; } = lastPackets;
        public Queue<double> Samples { get; } = new();
    }
}
