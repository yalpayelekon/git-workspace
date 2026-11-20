using System.Windows;
using System.Windows.Media;
using GitWorkspace.Core;

namespace GitWorkspace;

public sealed class GraphCell : FrameworkElement
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(GraphRow), typeof(GraphCell), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public GraphRow? Row { get => (GraphRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }
    private static readonly Brush[] Colors = [Brushes.DeepSkyBlue, Brushes.MediumSeaGreen, Brushes.Gold, Brushes.Orchid, Brushes.Coral, Brushes.Turquoise];
    private static Brush Color(string key) => Colors[key.Aggregate(0, (value, c) => (value * 31 + c) & 0x7fffffff) % Colors.Length];
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (Row == null) return;
        var spacing = Math.Min(12, (ActualWidth - 12) / Math.Max(1, Row.Width));
        double X(int lane) => 7 + lane * spacing;
        var mid = ActualHeight / 2;
        foreach (var edge in Row.Above) context.DrawLine(new(Color(edge.ColorKey), 1.5), new(X(edge.From), 0), new(X(edge.To), mid));
        foreach (var edge in Row.Below) context.DrawLine(new(Color(edge.ColorKey), 1.5), new(X(edge.From), mid), new(X(edge.To), ActualHeight));
        context.DrawEllipse(Color(Row.Hash), new(Brushes.Black, 1), new(X(Row.Node), mid), 3.5, 3.5);
    }
}
