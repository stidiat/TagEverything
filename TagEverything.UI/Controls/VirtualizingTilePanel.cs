using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TagEverything.UI.Controls;

/// <summary>Fixed-size tiles, realizing only the visible rows and one buffer row.</summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    public double ItemWidth { get; set; } = 236;
    public double ItemHeight { get; set; } = 296;
    private int _columns = 1;
    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth { get; private set; }
    public double ExtentHeight { get; private set; }
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }
    public double HorizontalOffset => 0;
    public double VerticalOffset { get; private set; }

    protected override Size MeasureOverride(Size available)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner == null) return new();
        ViewportWidth = double.IsInfinity(available.Width) ? ItemWidth : Math.Max(1, available.Width);
        ViewportHeight = double.IsInfinity(available.Height) ? 600 : Math.Max(0, available.Height);
        _columns = Math.Max(1, (int)(ViewportWidth / ItemWidth));
        ExtentWidth = ViewportWidth;
        ExtentHeight = Math.Ceiling(owner.Items.Count / (double)_columns) * ItemHeight;
        VerticalOffset = Math.Clamp(VerticalOffset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        var first = Math.Max(0, ((int)(VerticalOffset / ItemHeight) - 1) * _columns);
        var last = Math.Min(owner.Items.Count - 1, ((int)Math.Ceiling((VerticalOffset + ViewportHeight) / ItemHeight) + 1) * _columns - 1);
        var generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var index = owner.ItemContainerGenerator.IndexFromContainer(InternalChildren[i]);
            if (index < first || index > last)
            {
                var position = new GeneratorPosition(i, 0);
                if (generator is IRecyclingItemContainerGenerator recycling) recycling.Recycle(position, 1);
                else generator.Remove(position, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
        if (last >= first)
        {
            var position = generator.GeneratorPositionFromIndex(first);
            var childIndex = position.Offset == 0 ? position.Index : position.Index + 1;
            using (generator.StartAt(position, GeneratorDirection.Forward, true))
            {
                for (var index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var newlyRealized);
                    if (newlyRealized)
                    {
                        if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new(ItemWidth, ItemHeight));
                }
            }
        }
        ScrollOwner?.InvalidateScrollInfo();
        return new(ViewportWidth, ViewportHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        foreach (UIElement child in InternalChildren)
        {
            var index = owner.ItemContainerGenerator.IndexFromContainer(child);
            child.Arrange(new Rect(index % _columns * ItemWidth, index / _columns * ItemHeight - VerticalOffset, ItemWidth, ItemHeight));
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        if (InternalChildren.Count > 0) RemoveInternalChildRange(0, InternalChildren.Count);
        ItemContainerGenerator.RemoveAll();
        InvalidateMeasure();
    }
    protected override void BringIndexIntoView(int index) => SetVerticalOffset(index / _columns * ItemHeight);
    public void SetVerticalOffset(double offset)
    {
        var next = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (Math.Abs(next - VerticalOffset) < .1) return;
        VerticalOffset = next;
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }
    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => SetVerticalOffset(VerticalOffset - 40);
    public void LineDown() => SetVerticalOffset(VerticalOffset + 40);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - 120);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + 120);
    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        var item = ItemsControl.ContainerFromElement(owner, visual);
        if (item != null)
        {
            var index = owner.ItemContainerGenerator.IndexFromContainer(item);
            var top = index / _columns * ItemHeight;
            if (top < VerticalOffset) SetVerticalOffset(top);
            else if (top + ItemHeight > VerticalOffset + ViewportHeight) SetVerticalOffset(top + ItemHeight - ViewportHeight);
        }
        return rectangle;
    }
}
