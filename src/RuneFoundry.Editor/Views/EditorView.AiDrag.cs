using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RuneFoundry.UI;

namespace RuneFoundry.Editor.Views;

/// <summary>
/// Dragging AI rows into a new order.
///
/// The buttons stay: a drag is quick for a short hop and hopeless for moving a row past a
/// screenful of others. This only adds a second way to reach the same two moves, and it
/// goes through the same guard, so the setup block is no more draggable than it is
/// movable by button.
/// </summary>
public partial class EditorView
{
    /// <summary>Where the pointer went down, to tell a drag from a click.</summary>
    private Point _aiDragFrom;

    /// <summary>The row being dragged, held by identity rather than index.</summary>
    private AiBlock? _aiDragging;

    private void OnAiRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _aiDragFrom = e.GetPosition(null);
        _aiDragging = RowUnder(e.OriginalSource as DependencyObject);
    }

    private void OnAiRowMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _aiDragging is null) return;

        // Far enough to mean it: a few pixels of wobble while clicking a dropdown is not
        // a drag, and treating it as one makes the row editors feel slippery.
        var moved = e.GetPosition(null) - _aiDragFrom;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var block = _aiDragging;
        _aiDragging = null;

        if (_aiBlocks is null) return;

        var at = _aiBlocks.IndexOf(block);
        if (at < 0 || Protected(at, "moved")) return;

        DragDrop.DoDragDrop(AiBlockList, block, DragDropEffects.Move);
    }

    private void OnAiRowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(AiBlock))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnAiRowDrop(object sender, DragEventArgs e)
    {
        if (_aiBlocks is null) return;
        if (e.Data.GetData(typeof(AiBlock)) is not AiBlock dragged) return;

        var from = _aiBlocks.IndexOf(dragged);
        var onto = RowUnder(e.OriginalSource as DependencyObject);
        var to = onto is null ? _aiBlocks.Count - 1 : _aiBlocks.IndexOf(onto);

        if (from < 0 || to < 0 || from == to) return;

        // Both ends are checked: dragging a setup row out, and dropping an ordinary row
        // into the middle of the block, are the same mistake from either direction.
        if (Protected(from, "moved") || Protected(to, "moved")) return;

        _aiBlocks.Move(from, to);
        AiBlockList.SelectedIndex = to;
        MarkAiEdited();
        RegroupAiBlocks();

        SetStatus($"Moved the row to position {to + 1}.");
        e.Handled = true;
    }

    /// <summary>The row a piece of the visual tree belongs to.</summary>
    private static AiBlock? RowUnder(DependencyObject? source)
    {
        while (source is not null and not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);

        return (source as ListBoxItem)?.DataContext as AiBlock;
    }
}
