using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Emberlight.Core;

namespace Emberlight.App.Controls;

/// <summary>
/// A virtualizing wrap panel for equally sized cells: only the rows on (or next to) the screen have
/// containers; everything else is just an index. Scrolling is in pixels and it implements IScrollInfo
/// so the ListBox's ScrollViewer drives it (set ScrollViewer.CanContentScroll="True").
///
/// All geometry (column count, extent, which items are visible, where a row must scroll to be seen) comes
/// from <see cref="ThumbnailGridMath"/> in the core library, where it is unit tested. This class only does
/// the WPF plumbing: creating and recycling containers through the ItemContainerGenerator and positioning them.
/// </summary>
public sealed class VirtualizingGridPanel : VirtualizingPanel, IScrollInfo
{
    private const double LineStep = 24;

    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingGridPanel),
        new FrameworkPropertyMetadata(128.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        value => value is double d && double.IsFinite(d) && d > 0);

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingGridPanel),
        new FrameworkPropertyMetadata(104.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange),
        value => value is double d && double.IsFinite(d) && d > 0);

    /// <summary>Width of one cell including its margin.</summary>
    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    /// <summary>Height of one cell including its margin.</summary>
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    private IItemContainerGenerator? _generator;
    private int _columns = 1;
    private int _itemCount;
    private double _offset;
    private Size _extent, _viewport;

    // ============================================================================================
    // Layout
    // ============================================================================================

    protected override Size MeasureOverride(Size availableSize)
    {
        double vw = double.IsInfinity(availableSize.Width) ? ItemWidth * 5 : availableSize.Width;
        double vh = double.IsInfinity(availableSize.Height) ? ItemHeight * 4 : availableSize.Height;

        var owner = ItemsControl.GetItemsOwner(this);
        if (owner == null)
        {
            _itemCount = 0;
            return new Size(vw, vh);
        }
        EnsureGenerator();

        _itemCount = owner.Items.Count;
        UpdateScrollInfo(vw, vh);

        var (first, endExclusive) = ThumbnailGridMath.VisibleRange(
            _itemCount, vw, vh, _offset, ItemWidth, ItemHeight, overscanRows: 1);
        CleanUp(first, endExclusive - 1);
        if (endExclusive > first) Realize(first, endExclusive - 1);

        return new Size(vw, vh);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_generator == null) return finalSize;
        var children = InternalChildren;
        for (int i = 0; i < children.Count; i++)
        {
            int index = _generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;
            var (x, y) = ThumbnailGridMath.CellOrigin(index, _columns, ItemWidth, ItemHeight);
            children[i].Arrange(new Rect(x, y - _offset, ItemWidth, ItemHeight));
        }
        return finalSize;
    }

    private void EnsureGenerator()
    {
        if (_generator != null) return;
        _ = InternalChildren;                 // touching this is what creates the generator for an items host
        _generator = ItemContainerGenerator;
    }

    private void UpdateScrollInfo(double viewportWidth, double viewportHeight)
    {
        _columns = ThumbnailGridMath.Columns(viewportWidth, ItemWidth);
        var extent = new Size(viewportWidth, ThumbnailGridMath.ExtentHeight(_itemCount, _columns, ItemHeight));
        var viewport = new Size(viewportWidth, viewportHeight);
        double clamped = ThumbnailGridMath.ClampOffset(_offset, extent.Height, viewport.Height);

        bool changed = extent != _extent || viewport != _viewport || clamped != _offset;
        _extent = extent;
        _viewport = viewport;
        _offset = clamped;
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    /// <summary>Creates containers for items first..last (inclusive) that don't have one yet, and measures them.</summary>
    private void Realize(int first, int last)
    {
        var generator = _generator!;
        var children = InternalChildren;

        GeneratorPosition start = generator.GeneratorPositionFromIndex(first);
        int childIndex = start.Offset == 0 ? start.Index : start.Index + 1;

        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (int item = first; item <= last; item++, childIndex++)
            {
                if (generator.GenerateNext(out bool isNew) is not UIElement child) break;
                if (isNew)
                {
                    if (childIndex >= children.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }
    }

    /// <summary>Discards containers for items outside first..last so memory stays proportional to the screen, not the folder.</summary>
    private void CleanUp(int first, int last)
    {
        if (_generator == null) return;
        var children = InternalChildren;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int index = _generator.IndexFromGeneratorPosition(position);
            if (index < first || index > last)
            {
                _generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    // ============================================================================================
    // Items changing. Panel's own handling covers Reset (it clears everything) but deliberately does
    // nothing for Add/Remove/Replace/Move, so a virtualizing panel must drop affected containers itself.
    // ============================================================================================

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                if (_offset != 0)
                {
                    _offset = 0;           // a new list (new folder, re-sort) starts at the top
                    ScrollOwner?.InvalidateScrollInfo();   // the ScrollViewer must hear about it even if the extent is unchanged
                }
                break;
        }
        base.OnItemsChanged(sender, args);
    }

    /// <summary>Called by ListBox.ScrollIntoView for an item that may not have a container yet.</summary>
    protected override void BringIndexIntoView(int index) => RevealIndex(index);

    private void RevealIndex(int index)
    {
        if (index < 0 || index >= _itemCount) return;
        SetVerticalOffset(ThumbnailGridMath.OffsetToReveal(index, _columns, ItemHeight, _offset, _viewport.Height));
    }

    // ============================================================================================
    // IScrollInfo (vertical, pixel based; there is never a horizontal scrollbar)
    // ============================================================================================

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }

    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        double clamped = ThumbnailGridMath.ClampOffset(offset, _extent.Height, _viewport.Height);
        if (clamped == _offset) return;
        _offset = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void LineUp() => SetVerticalOffset(_offset - LineStep);
    public void LineDown() => SetVerticalOffset(_offset + LineStep);
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset - 3 * LineStep);
    public void MouseWheelDown() => SetVerticalOffset(_offset + 3 * LineStep);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    /// <summary>Called when a child (or something inside it) takes keyboard focus or asks to be brought into view.</summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (_generator == null) return rectangle;
        var children = InternalChildren;

        DependencyObject? current = visual;
        while (current != null && !ReferenceEquals(current, this))
        {
            if (current is UIElement element)
            {
                int childIndex = children.IndexOf(element);
                if (childIndex >= 0)
                {
                    int index = _generator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
                    if (index >= 0) RevealIndex(index);
                    break;
                }
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return rectangle;
    }
}
