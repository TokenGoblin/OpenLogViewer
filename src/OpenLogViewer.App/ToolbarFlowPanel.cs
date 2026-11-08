using System.Windows;
using System.Windows.Controls;

namespace OpenLogViewer.App;

/// <summary>
/// The toolbar's left-hand run: one line while it fits, and a second line
/// rather than hidden controls when it does not.
///
/// The toolbar used to be a DockPanel, which at a narrow window simply clipped
/// whatever was declared last. That was tolerable while the window was the only
/// thing that could shrink it; with the interface zoom (see
/// <see cref="InterfaceScale"/>) a large size on a laptop screen hid the view
/// chips completely. A WrapPanel would wrap, but it would also push the status
/// text onto a line of its own the moment that text was longer than the space
/// left — which on a wide window at normal size is always — so the toolbar would
/// grow a second row it never had before.
///
/// So the last child is the fill, as it was in the DockPanel: it takes whatever
/// width is left on the current line and trims to it. Only the controls before
/// it wrap, and only when they themselves do not fit. When they do, this lays
/// out exactly as the DockPanel did.
/// </summary>
public sealed class ToolbarFlowPanel : Panel
{
    /// <summary>Where each child went in the last measure, and the height of its line.</summary>
    private readonly List<(Rect Slot, int Line)> _slots = [];

    private readonly List<double> _lineHeights = [];

    protected override Size MeasureOverride(Size available)
    {
        _slots.Clear();
        _lineHeights.Clear();

        double width = double.IsInfinity(available.Width) ? double.MaxValue : available.Width;
        double x = 0, y = 0, lineHeight = 0, widest = 0;
        int line = 0;
        int last = InternalChildren.Count - 1;

        for (int i = 0; i <= last; i++)
        {
            UIElement child = InternalChildren[i];

            if (i == last)
            {
                // The fill: whatever this line has left, or a fresh line when
                // nothing is left at all.
                if (width - x <= 0 && x > 0) NewLine();

                child.Measure(new Size(Math.Max(0, width - x), double.PositiveInfinity));
                Place(child, Math.Max(0, Math.Min(child.DesiredSize.Width, width - x)));
                break;
            }

            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size desired = child.DesiredSize;

            // Collapsed controls take no room and must not start a line.
            if (desired.Width <= 0) { Place(child, 0); continue; }

            if (x > 0 && x + desired.Width > width) NewLine();

            Place(child, desired.Width);
        }

        _lineHeights.Add(lineHeight);

        return new Size(
            double.IsInfinity(available.Width) ? widest : Math.Min(widest, available.Width),
            y + lineHeight);

        void NewLine()
        {
            _lineHeights.Add(lineHeight);
            y += lineHeight;
            x = 0;
            lineHeight = 0;
            line++;
        }

        void Place(UIElement child, double childWidth)
        {
            _slots.Add((new Rect(x, y, childWidth, child.DesiredSize.Height), line));
            x += childWidth;
            lineHeight = Math.Max(lineHeight, child.DesiredSize.Height);
            widest = Math.Max(widest, x);
        }
    }

    protected override Size ArrangeOverride(Size final)
    {
        int last = InternalChildren.Count - 1;

        for (int i = 0; i < InternalChildren.Count && i < _slots.Count; i++)
        {
            (Rect slot, int line) = _slots[i];
            double height = _lineHeights[line];

            // A single line takes the full height, as the DockPanel gave it, so
            // the dividers and the centred chips sit exactly where they did.
            if (_lineHeights.Count == 1) height = final.Height;

            // The fill stretches to the end of its line, which is what lets a
            // long status trim instead of overrunning.
            double width = i == last ? Math.Max(slot.Width, final.Width - slot.X) : slot.Width;

            InternalChildren[i].Arrange(new Rect(slot.X, slot.Y, width, height));
        }

        return final;
    }
}
