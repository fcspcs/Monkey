using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Monkey.Ui;

/// <summary>Ein Wert im Diagramm.</summary>
public sealed class ChartPoint
{
    /// <summary>Kurze Beschriftung an der Achse ("Mo", "14.8.").</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Ganzer Text fuer den Kurzhinweis unter dem Zeiger.</summary>
    public string Detail { get; init; } = string.Empty;

    public double Value { get; init; }

    /// <summary>Hervorgehoben - in der Regel der heutige Tag.</summary>
    public bool Emphasised { get; init; }
}

/// <summary>
/// Gemeinsamer Unterbau der Diagramme: Achsen, Gitter, Skalierung und der
/// Kurzhinweis unter dem Zeiger.
///
/// Gezeichnet wird von Hand, weil das Projekt bewusst ohne Fremdbibliotheken
/// auskommt - eine Diagrammbibliothek waere hier die groesste Abhaengigkeit im
/// ganzen Programm. Die Masse folgen den Regeln aus der Gestaltungsvorlage:
/// duenne Marken, haarfeines Gitter, Beschriftung nur an den Stellen, die etwas
/// aussagen.
/// </summary>
public abstract class ChartBase : FrameworkElement
{
    // Platz fuer die Achsen. Links die Werte, unten die Beschriftungen, oben
    // Luft fuer die wenigen direkt angeschriebenen Werte.
    protected const double AxisGutter = 48;
    protected const double AxisBand = 22;
    protected const double TopPad = 22;
    protected const double RightPad = 10;

    protected const double LabelSize = 10.5;

    public static readonly DependencyProperty PointsProperty =
        DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<ChartPoint>), typeof(ChartBase),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Feste Obergrenze der Werteachse; NaN laesst sie mitwachsen.</summary>
    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(ChartBase),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Waagerechte Bezugslinie, etwa das Tagesbudget. NaN blendet sie aus.</summary>
    public static readonly DependencyProperty ReferenceValueProperty =
        DependencyProperty.Register(nameof(ReferenceValue), typeof(double), typeof(ChartBase),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ReferenceLabelProperty =
        DependencyProperty.Register(nameof(ReferenceLabel), typeof(string), typeof(ChartBase),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty =
        DependencyProperty.Register(nameof(EmptyText), typeof(string), typeof(ChartBase),
            new FrameworkPropertyMetadata("No data yet.", FrameworkPropertyMetadataOptions.AffectsRender));

    // Die Farben stehen hier als Vorgaben und nicht als Stil in Theme.xaml:
    // diese Datei wird nur ins Steuerpult eingebunden, Theme.xaml dagegen auch
    // in den Installer - ein Stil, der ColumnChart nennt, liesse den Installer
    // nicht mehr bauen. Es sind dieselben Werte wie dort, aus assets/monkey.png:
    // #802D0E das Fell, #261B10 Pupille und Nase.
    public static readonly DependencyProperty InkProperty =
        DependencyProperty.Register(nameof(Ink), typeof(Brush), typeof(ChartBase),
            new FrameworkPropertyMetadata(Frozen("#FF802D0E"), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridLineBrushProperty =
        DependencyProperty.Register(nameof(GridLineBrush), typeof(Brush), typeof(ChartBase),
            new FrameworkPropertyMetadata(Frozen("#FFF0EAE3"), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AxisBrushProperty =
        DependencyProperty.Register(nameof(AxisBrush), typeof(Brush), typeof(ChartBase),
            new FrameworkPropertyMetadata(Frozen("#FF6E6055"), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty =
        DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(ChartBase),
            new FrameworkPropertyMetadata(Frozen("#FF261B10"), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ReferenceBrushProperty =
        DependencyProperty.Register(nameof(ReferenceBrush), typeof(Brush), typeof(ChartBase),
            new FrameworkPropertyMetadata(Frozen("#FF807164"), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Die Flaechenfarbe des Blattes. Sie trennt beruehrende Marken und umringt
    /// Punkte - deshalb muss sie zur Karte passen, auf der das Diagramm liegt.
    /// </summary>
    public static readonly DependencyProperty SurfaceBrushProperty =
        DependencyProperty.Register(nameof(SurfaceBrush), typeof(Brush), typeof(ChartBase),
            new FrameworkPropertyMetadata(Frozen("#FFFFFFFF"), FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartPoint>? Points
    {
        get => (IReadOnlyList<ChartPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double ReferenceValue
    {
        get => (double)GetValue(ReferenceValueProperty);
        set => SetValue(ReferenceValueProperty, value);
    }

    public string ReferenceLabel
    {
        get => (string)GetValue(ReferenceLabelProperty);
        set => SetValue(ReferenceLabelProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public Brush Ink
    {
        get => (Brush)GetValue(InkProperty);
        set => SetValue(InkProperty, value);
    }

    public Brush GridLineBrush
    {
        get => (Brush)GetValue(GridLineBrushProperty);
        set => SetValue(GridLineBrushProperty, value);
    }

    public Brush AxisBrush
    {
        get => (Brush)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public Brush ReferenceBrush
    {
        get => (Brush)GetValue(ReferenceBrushProperty);
        set => SetValue(ReferenceBrushProperty, value);
    }

    public Brush SurfaceBrush
    {
        get => (Brush)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    /// <summary>
    /// Wie ein Wert als Text erscheint - in Achsenwerten, Beschriftungen und
    /// Kurzhinweisen. Wird von der Statistikseite gesetzt.
    /// </summary>
    public Func<double, string> ValueFormat { get; set; } =
        value => value.ToString("0", CultureInfo.CurrentCulture);

    /// <summary>Index unter dem Zeiger, sonst -1.</summary>
    protected int Hover { get; private set; } = -1;

    private Point _pointer;

    protected ChartBase()
    {
        MinHeight = 160;
    }

    // ------------------------------------------------------------ Zeigerspur

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);

        _pointer = e.GetPosition(this);
        var index = IndexAt(_pointer);

        // Auch bei gleichem Balken neu zeichnen: der Hinweis folgt dem Zeiger.
        if (index >= 0 || Hover >= 0)
        {
            Hover = index;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (Hover < 0) return;

        Hover = -1;
        InvalidateVisual();
    }

    /// <summary>Welcher Wert liegt unter diesem Punkt? -1, wenn keiner.</summary>
    protected virtual int IndexAt(Point position)
    {
        var points = Points;
        if (points is null || points.Count == 0) return -1;

        var plot = PlotArea();
        if (!plot.Contains(position)) return -1;

        var band = plot.Width / points.Count;
        if (band <= 0) return -1;

        return Math.Clamp((int)((position.X - plot.Left) / band), 0, points.Count - 1);
    }

    // ------------------------------------------------------------ Geruest

    protected Rect PlotArea()
    {
        var width = Math.Max(0, ActualWidth - AxisGutter - RightPad);
        var height = Math.Max(0, ActualHeight - TopPad - AxisBand);
        return new Rect(AxisGutter, TopPad, width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        // Ohne gezeichnete Flaeche bekommt das Element keine Mausereignisse.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var plot = PlotArea();
        if (plot.Width <= 4 || plot.Height <= 4) return;

        var points = Points;
        if (points is null || points.Count == 0)
        {
            var empty = Label(EmptyText, LabelSize + 1, AxisBrush);
            dc.DrawText(empty, new Point(
                plot.Left + (plot.Width - empty.Width) / 2,
                plot.Top + (plot.Height - empty.Height) / 2));
            return;
        }

        var max = ScaleMaximum(points);
        DrawGrid(dc, plot, max);
        DrawSeries(dc, plot, points, max);
        DrawReference(dc, plot, max);
        DrawTooltip(dc, plot, points);
    }

    /// <summary>Obergrenze der Achse: rund, und immer mindestens der groesste Wert.</summary>
    protected double ScaleMaximum(IReadOnlyList<ChartPoint> points)
    {
        if (!double.IsNaN(Maximum) && Maximum > 0) return Maximum;

        var largest = 0.0;
        foreach (var point in points) largest = Math.Max(largest, point.Value);
        if (!double.IsNaN(ReferenceValue)) largest = Math.Max(largest, ReferenceValue);

        return NiceCeiling(largest);
    }

    /// <summary>
    /// Naechstgroessere runde Zahl. Die Stufen sind so gewaehlt, dass auch die
    /// Haelfte glatt bleibt - die Gitterlinie in der Mitte traegt sie.
    /// </summary>
    protected static double NiceCeiling(double value)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return 1;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1.0, 2, 4, 6, 8, 10 })
            if (value <= step * magnitude + 1e-9)
                return step * magnitude;

        return 10 * magnitude;
    }

    private void DrawGrid(DrawingContext dc, Rect plot, double max)
    {
        // Haarfein und einfarbig - nie gestrichelt, das laese sich als Schwelle.
        var pen = new Pen(GridLineBrush, 1);
        pen.Freeze();

        foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
        {
            var y = Snap(plot.Bottom - plot.Height * fraction);
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));

            var text = Label(ValueFormat(max * fraction), LabelSize, AxisBrush);
            dc.DrawText(text, new Point(
                plot.Left - 10 - text.Width,
                y - text.Height / 2));
        }
    }

    private void DrawReference(DrawingContext dc, Rect plot, double max)
    {
        if (double.IsNaN(ReferenceValue) || ReferenceValue <= 0 || ReferenceValue > max) return;

        var pen = new Pen(ReferenceBrush, 1);
        pen.Freeze();

        var y = Snap(plot.Bottom - plot.Height * (ReferenceValue / max));
        dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));

        if (string.IsNullOrEmpty(ReferenceLabel)) return;

        // Die Beschriftung sitzt ueber der Zeichenflaeche, nicht auf den Marken -
        // ein kurzes Linienstueck daneben stellt den Bezug her. Direkt an der
        // Linie laege sie sonst quer ueber den Saeulen.
        var text = Label(ReferenceLabel, LabelSize, AxisBrush);
        var left = plot.Right - text.Width;
        var top = Math.Max(0, plot.Top - text.Height - 5);

        var swatch = Snap(top + text.Height / 2);
        dc.DrawLine(pen, new Point(left - 20, swatch), new Point(left - 6, swatch));
        dc.DrawText(text, new Point(left, top));
    }

    protected abstract void DrawSeries(DrawingContext dc, Rect plot, IReadOnlyList<ChartPoint> points, double max);

    /// <summary>Wo sitzt der Hinweis? Balken zeigen ihn ueber dem Balken.</summary>
    protected abstract Point TooltipAnchor(Rect plot, IReadOnlyList<ChartPoint> points, int index, double max);

    private void DrawTooltip(DrawingContext dc, Rect plot, IReadOnlyList<ChartPoint> points)
    {
        if (Hover < 0 || Hover >= points.Count) return;

        var detail = points[Hover].Detail;
        if (string.IsNullOrEmpty(detail)) return;

        var text = Label(detail, LabelSize + 0.5, Frozen("#FFF7F1EA"));
        var anchor = TooltipAnchor(plot, points, Hover, ScaleMaximum(points));

        const double padX = 9, padY = 6;
        var box = new Rect(
            anchor.X - text.Width / 2 - padX,
            anchor.Y - text.Height - 2 * padY - 8,
            text.Width + 2 * padX,
            text.Height + 2 * padY);

        // Innerhalb der Zeichenflaeche halten.
        if (box.Left < 0) box.X = 0;
        if (box.Right > ActualWidth) box.X = ActualWidth - box.Width;
        if (box.Top < 0) box.Y = anchor.Y + 12;

        dc.DrawRoundedRectangle(Frozen("#FF241A11"), null, box, 12, 12);
        dc.DrawText(text, new Point(box.Left + padX, box.Top + padY));
    }

    // ------------------------------------------------------------ Werkzeug

    protected FormattedText Label(string text, double size, Brush brush, bool semiBold = false)
    {
        var typeface = new Typeface(
            new FontFamily("Segoe UI"),
            FontStyles.Normal,
            semiBold ? FontWeights.SemiBold : FontWeights.Normal,
            FontStretches.Normal);

        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    /// <summary>Auf die halbe Pixelgrenze, damit Haarlinien scharf bleiben.</summary>
    protected static double Snap(double value) => Math.Round(value) + 0.5;

    protected static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>Dieselbe Farbe als Schleier - fuer Flaechen unter einer Linie.</summary>
    protected Brush Wash(double opacity)
    {
        if (Ink is not SolidColorBrush solid) return Ink;

        var brush = new SolidColorBrush(solid.Color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Saeulen fuer Vergleiche: Verbrauch je Tag, Verbrauch je Wochentag. Eine Reihe,
/// eine Farbe - die Laenge traegt die Aussage, die Farbe muss nichts dazutun.
/// </summary>
public sealed class ColumnChart : ChartBase
{
    private const double MaxBarWidth = 24;

    /// <summary>Abstand in Flaechenfarbe zwischen zwei Saeulen.</summary>
    private const double BarGap = 2;

    protected override void DrawSeries(DrawingContext dc, Rect plot, IReadOnlyList<ChartPoint> points, double max)
    {
        var band = plot.Width / points.Count;
        var width = Math.Min(MaxBarWidth, Math.Max(2, band - BarGap));

        // Angeschrieben werden nur der groesste Wert und der hervorgehobene Tag -
        // eine Zahl an jeder Saeule liest ohnehin niemand.
        var peak = 0;
        for (var i = 1; i < points.Count; i++)
            if (points[i].Value > points[peak].Value) peak = i;

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var centre = plot.Left + band * (i + 0.5);

            if (i == Hover)
            {
                // Die Spur unter dem Zeiger, damit klar ist, welcher Tag gemeint ist.
                dc.DrawRectangle(Frozen("#14802D0E"), null,
                    new Rect(centre - band / 2, plot.Top, band, plot.Height));
            }

            var height = max <= 0 ? 0 : plot.Height * (point.Value / max);

            if (height < 1)
            {
                // Ein Tag ohne Verbrauch ist eine Aussage, keine Luecke: ein
                // flacher Stummel zeigt, dass gemessen wurde.
                dc.DrawRectangle(GridLineBrush, null,
                    new Rect(centre - width / 2, plot.Bottom - 2, width, 2));
            }
            else
            {
                // Voll gerundete Kuppe: der Radius ist die halbe Saeulenbreite.
                var bar = new Rect(centre - width / 2, plot.Bottom - height, width, height);
                dc.DrawGeometry(Ink, null, Column(bar, width / 2));
            }

        }

        DrawValueLabels(dc, plot, points, band, max, peak);
        DrawAxisLabels(dc, plot, points, band);
    }

    /// <summary>
    /// Der groesste Wert und der hervorgehobene Tag werden angeschrieben, sonst
    /// nichts - eine Zahl ueber jeder Saeule liest niemand. Die Beschriftung darf
    /// dabei breiter sein als ihr Band; nur ueberlappen duerfen sich die wenigen
    /// Beschriftungen nicht.
    /// </summary>
    private void DrawValueLabels(DrawingContext dc, Rect plot, IReadOnlyList<ChartPoint> points,
                                 double band, double max, int peak)
    {
        var marked = new List<int>();
        for (var i = 0; i < points.Count; i++)
            if (points[i].Emphasised && points[i].Value > 0)
                marked.Add(i);

        if (points[peak].Value > 0 && !marked.Contains(peak)) marked.Insert(0, peak);

        var taken = new List<(double Left, double Right)>();

        foreach (var i in marked)
        {
            var text = Label(ValueFormat(points[i].Value), LabelSize, LabelBrush, semiBold: true);

            var centre = plot.Left + band * (i + 0.5);
            var left = Math.Clamp(centre - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width));
            var right = left + text.Width;

            var collides = false;
            foreach (var slot in taken)
                if (left < slot.Right + 8 && right > slot.Left - 8) { collides = true; break; }

            if (collides) continue;

            var height = max <= 0 ? 0 : plot.Height * (points[i].Value / max);
            dc.DrawText(text, new Point(left, Math.Max(0, plot.Bottom - height - text.Height - 4)));
            taken.Add((left, right));
        }
    }

    /// <summary>
    /// Beschriftungen nur so dicht, wie sie sich nicht beruehren - lieber jede
    /// dritte lesbar als alle uebereinander.
    /// </summary>
    private void DrawAxisLabels(DrawingContext dc, Rect plot, IReadOnlyList<ChartPoint> points, double band)
    {
        var lastRight = double.NegativeInfinity;

        for (var i = 0; i < points.Count; i++)
        {
            if (string.IsNullOrEmpty(points[i].Label)) continue;

            var emphasised = points[i].Emphasised;
            var text = Label(points[i].Label, LabelSize, emphasised ? LabelBrush : AxisBrush, emphasised);
            var left = plot.Left + band * (i + 0.5) - text.Width / 2;

            if (left < lastRight + 6) continue;
            if (left + text.Width > plot.Right + RightPad) continue;

            dc.DrawText(text, new Point(left, plot.Bottom + 5));
            lastRight = left + text.Width;
        }
    }

    protected override Point TooltipAnchor(Rect plot, IReadOnlyList<ChartPoint> points, int index, double max)
    {
        var band = plot.Width / points.Count;
        var height = max <= 0 ? 0 : plot.Height * (points[index].Value / max);
        return new Point(plot.Left + band * (index + 0.5), plot.Bottom - height);
    }

    /// <summary>
    /// Oben gerundet, unten kantig: die Saeule waechst sichtbar aus der Grundlinie
    /// heraus, statt wie eine Kapsel darauf zu schweben.
    /// </summary>
    private static Geometry Column(Rect r, double radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width / 2, r.Height));

        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(new Point(r.Left, r.Bottom), true, true);
            c.LineTo(new Point(r.Left, r.Top + radius), true, false);
            c.ArcTo(new Point(r.Left + radius, r.Top), new Size(radius, radius), 0, false,
                SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(r.Right - radius, r.Top), true, false);
            c.ArcTo(new Point(r.Right, r.Top + radius), new Size(radius, radius), 0, false,
                SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(r.Right, r.Bottom), true, false);
        }

        geometry.Freeze();
        return geometry;
    }
}

/// <summary>
/// Verlauf einer einzelnen Reihe: das Guthaben ueber die Zeit. Linie mit einem
/// Schleier darunter, ein Punkt am Ende - dort steht der Wert, der zaehlt.
/// </summary>
public sealed class TrendChart : ChartBase
{
    protected override void DrawSeries(DrawingContext dc, Rect plot, IReadOnlyList<ChartPoint> points, double max)
    {
        var step = points.Count > 1 ? plot.Width / (points.Count - 1) : 0;

        Point At(int i)
        {
            var x = points.Count > 1 ? plot.Left + step * i : plot.Left + plot.Width / 2;
            var y = max <= 0 ? plot.Bottom : plot.Bottom - plot.Height * (points[i].Value / max);
            return new Point(x, y);
        }

        if (points.Count > 1)
        {
            var area = new StreamGeometry();
            using (var c = area.Open())
            {
                c.BeginFigure(new Point(At(0).X, plot.Bottom), true, true);
                c.LineTo(At(0), true, false);
                for (var i = 1; i < points.Count; i++) c.LineTo(At(i), true, false);
                c.LineTo(new Point(At(points.Count - 1).X, plot.Bottom), true, false);
            }
            area.Freeze();

            // Ein Schleier, kein satter Block.
            dc.DrawGeometry(Wash(0.10), null, area);

            var line = new StreamGeometry();
            using (var c = line.Open())
            {
                c.BeginFigure(At(0), false, false);
                for (var i = 1; i < points.Count; i++) c.LineTo(At(i), true, false);
            }
            line.Freeze();

            var pen = new Pen(Ink, 2)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, line);
        }

        if (Hover >= 0 && Hover < points.Count)
        {
            var pen = new Pen(ReferenceBrush, 1);
            pen.Freeze();

            var x = Snap(At(Hover).X);
            dc.DrawLine(pen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            Marker(dc, At(Hover));
        }

        // Der Endpunkt traegt den aktuellen Stand - hier lohnt die Beschriftung.
        var last = At(points.Count - 1);
        Marker(dc, last);

        // Neben den Punkt, wenn dort Platz ist - sonst darueber. Einfach nach
        // links schieben ginge nicht: dann laege die Zahl auf ihrem eigenen Punkt.
        var value = Label(ValueFormat(points[^1].Value), LabelSize, LabelBrush, semiBold: true);
        var origin = last.X + 9 + value.Width <= ActualWidth
            ? new Point(last.X + 9, Math.Max(0, last.Y - value.Height / 2))
            : new Point(Math.Max(0, ActualWidth - value.Width),
                        Math.Max(0, last.Y - value.Height - 9));
        dc.DrawText(value, origin);

        DrawAxisLabels(dc, plot, points, step);
    }

    /// <summary>Punkt mit Ring in Flaechenfarbe - so bleibt er auf der Linie lesbar.</summary>
    private void Marker(DrawingContext dc, Point centre)
    {
        var ring = new Pen(SurfaceBrush, 2);
        ring.Freeze();
        dc.DrawEllipse(Ink, ring, centre, 4.5, 4.5);
    }

    private void DrawAxisLabels(DrawingContext dc, Rect plot, IReadOnlyList<ChartPoint> points, double step)
    {
        var lastRight = double.NegativeInfinity;

        for (var i = 0; i < points.Count; i++)
        {
            if (string.IsNullOrEmpty(points[i].Label)) continue;

            var text = Label(points[i].Label, LabelSize, AxisBrush);
            var centre = points.Count > 1 ? plot.Left + step * i : plot.Left + plot.Width / 2;
            var left = centre - text.Width / 2;

            if (left < lastRight + 10) continue;
            if (left < 0 || left + text.Width > ActualWidth) continue;

            dc.DrawText(text, new Point(left, plot.Bottom + 5));
            lastRight = left + text.Width;
        }
    }

    protected override int IndexAt(Point position)
    {
        var points = Points;
        if (points is null || points.Count == 0) return -1;

        var plot = PlotArea();
        if (position.X < plot.Left - 8 || position.X > plot.Right + 8) return -1;
        if (position.Y < plot.Top || position.Y > plot.Bottom) return -1;
        if (points.Count == 1) return 0;

        var step = plot.Width / (points.Count - 1);
        return step <= 0 ? 0 : Math.Clamp((int)Math.Round((position.X - plot.Left) / step), 0, points.Count - 1);
    }

    protected override Point TooltipAnchor(Rect plot, IReadOnlyList<ChartPoint> points, int index, double max)
    {
        var step = points.Count > 1 ? plot.Width / (points.Count - 1) : 0;
        var x = points.Count > 1 ? plot.Left + step * index : plot.Left + plot.Width / 2;
        var y = max <= 0 ? plot.Bottom : plot.Bottom - plot.Height * (points[index].Value / max);
        return new Point(x, y);
    }
}
