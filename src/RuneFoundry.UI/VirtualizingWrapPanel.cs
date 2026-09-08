using System.Windows.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RuneFoundry.UI;

/// <summary>
/// A wrap panel that only builds the tiles you can see.
///
/// WPF ships no virtualising wrap panel, and a plain <see cref="WrapPanel"/> inside a list
/// silently defeats the list's virtualisation: every <c>IsVirtualizing</c> flag stays true
/// and all 196 icons are realised anyway. That cost about 300 ms of frozen window every time
/// the Icons tab was opened — measured, after moving all the file and image work off the UI
/// thread made no difference to it.
///
/// The tiles here are uniform, which is what makes this tractable: one child is measured, and
/// the rest of the layout is arithmetic. That assumption is the whole design — a panel that
/// had to cope with ragged sizes could not know a row's height without building it.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    private Size _itemSize = new(72, 72);
    private bool _measured;
    private Size _extent;
    private Size _viewport;
    private Point _offset;

    /// <summary>
    /// Rows kept either side of the view. One screen's worth means a fast scroll usually
    /// finds its tiles already built, without paying to build the whole sheet.
    /// </summary>
    public static readonly DependencyProperty CacheRowsProperty =
        DependencyProperty.Register(nameof(CacheRows), typeof(int), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int CacheRows
    {
        get => (int)GetValue(CacheRowsProperty);
        set => SetValue(CacheRowsProperty, value);
    }

    /// <summary>The size to lay tiles out on, when the first one has not been measured yet.</summary>
    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(72d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public static readonly DependencyProperty ItemHeightProperty =
        DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(72d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private int Columns(double width)
        => Math.Max(1, (int)Math.Floor(width / Math.Max(1, _itemSize.Width)));

    protected override Size MeasureOverride(Size available)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        var count = owner?.Items.Count ?? 0;

        // The generator only exists once the panel is attached to an ItemsControl.
        var generator = ItemContainerGenerator;
        if (generator is null || count == 0)
        {
            _extent = new Size(0, 0);
            _viewport = available;
            return new Size(0, 0);
        }

        // Measure one tile for real rather than trusting a number written in the XAML: the
        // template's own padding, border and margin all count towards how many fit in a row,
        // and getting that wrong by a pixel puts one fewer tile on every line.
        _itemSize = MeasuredItemSize(generator, count);

        var width = double.IsInfinity(available.Width) ? _itemSize.Width * count : available.Width;
        var columns = Columns(width);
        var rows = (int)Math.Ceiling((double)count / columns);

        _extent = new Size(width, rows * _itemSize.Height);
        _viewport = new Size(width, double.IsInfinity(available.Height) ? _extent.Height : available.Height);
        ClampOffset();

        var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / _itemSize.Height) - CacheRows);
        var lastRow = Math.Min(rows - 1,
            (int)Math.Ceiling((_offset.Y + _viewport.Height) / _itemSize.Height) + CacheRows);

        var first = firstRow * columns;
        var last = Math.Min(count - 1, (lastRow + 1) * columns - 1);

        RealiseRange(first, last, count);

        ScrollOwner?.InvalidateScrollInfo();
        return new Size(
            double.IsInfinity(available.Width) ? _extent.Width : available.Width,
            double.IsInfinity(available.Height) ? _extent.Height : available.Height);
    }

    /// <summary>
    /// The size of one tile, including everything around it. Measured once from a real
    /// container and then remembered; the declared ItemWidth/ItemHeight are the fallback for
    /// the first pass, before anything has been generated.
    /// </summary>
    private Size MeasuredItemSize(IItemContainerGenerator generator, int count)
    {
        if (_measured) return _itemSize;
        if (count == 0) return new Size(ItemWidth, ItemHeight);

        var position = generator.GeneratorPositionFromIndex(0);
        using (generator.StartAt(position, GeneratorDirection.Forward, true))
        {
            if (generator.GenerateNext(out var isNew) is not UIElement child)
                return new Size(ItemWidth, ItemHeight);

            if (isNew)
            {
                AddInternalChild(child);
                generator.PrepareItemContainer(child);
            }

            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var margin = child is FrameworkElement fe ? fe.Margin : new Thickness(0);
            var size = new Size(
                child.DesiredSize.Width + margin.Left + margin.Right,
                child.DesiredSize.Height + margin.Top + margin.Bottom);

            if (size.Width > 1 && size.Height > 1)
            {
                _itemSize = size;
                _measured = true;
            }
        }

        return _itemSize;
    }

    /// <summary>
    /// Builds the children in view and throws away the ones that are not, which is the whole
    /// point: a container that is never made costs nothing to lay out or draw.
    /// </summary>
    private void RealiseRange(int first, int last, int count)
    {
        var generator = ItemContainerGenerator;
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;

        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (var i = first; i <= last; i++, childIndex++)
            {
                var child = (UIElement?)generator.GenerateNext(out var isNew);
                if (child is null) break;

                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }

                child.Measure(_itemSize);
            }
        }

        CleanUpItems(first, last);
    }

    private void CleanUpItems(int first, int last)
    {
        var generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;

            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        var columns = Columns(finalSize.Width);

        foreach (UIElement child in InternalChildren)
        {
            var index = generator.IndexFromGeneratorPosition(
                new GeneratorPosition(InternalChildren.IndexOf(child), 0));
            if (index < 0) continue;

            var row = index / columns;
            var column = index % columns;

            child.Arrange(new Rect(
                column * _itemSize.Width,
                row * _itemSize.Height - _offset.Y,
                _itemSize.Width,
                _itemSize.Height));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        // The indices every realised child was placed at have moved; start again.
        if (args.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Remove
            or System.Collections.Specialized.NotifyCollectionChangedAction.Replace
            or System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            RemoveInternalChildRange(0, InternalChildren.Count);
        }

        _offset = new Point(0, 0);
        _measured = false;
        InvalidateMeasure();
    }

    // ---- IScrollInfo ------------------------------------------------------
    //
    // Implemented rather than inherited because the panel scrolls by pixel over an extent it
    // computes itself; the ScrollViewer only ever knows what it is told here.

    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    private void ClampOffset()
    {
        var maximum = Math.Max(0, _extent.Height - _viewport.Height);
        _offset.Y = Math.Min(Math.Max(0, _offset.Y), maximum);
        _offset.X = 0;
    }

    public void SetVerticalOffset(double offset)
    {
        var maximum = Math.Max(0, _extent.Height - _viewport.Height);
        offset = Math.Min(Math.Max(0, offset), maximum);
        if (Math.Abs(offset - _offset.Y) < 0.5) return;

        _offset.Y = offset;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void SetHorizontalOffset(double offset) { }

    // A line is a row of tiles; a page is what fits on screen. Both in the panel's own units,
    // so the scrollbar and the wheel agree with what is drawn.
    public void LineUp() => SetVerticalOffset(_offset.Y - _itemSize.Height);
    public void LineDown() => SetVerticalOffset(_offset.Y + _itemSize.Height);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - _itemSize.Height);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + _itemSize.Height);

    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    /// <summary>Keeps a tile picked with the keyboard on screen.</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var child = visual as UIElement;
        if (child is null) return rectangle;

        var index = ItemContainerGenerator.IndexFromGeneratorPosition(
            new GeneratorPosition(InternalChildren.IndexOf(child), 0));
        if (index < 0) return rectangle;

        var columns = Columns(_viewport.Width);
        var top = index / columns * _itemSize.Height;
        var bottom = top + _itemSize.Height;

        if (top < _offset.Y) SetVerticalOffset(top);
        else if (bottom > _offset.Y + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);

        return rectangle;
    }
}
