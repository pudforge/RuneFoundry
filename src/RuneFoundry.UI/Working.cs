using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RuneFoundry.UI;

/// <summary>
/// A veil over the window while something slow is happening, with a spinner and a line
/// saying what.
///
/// <see cref="BusyStrip"/> is the quiet version: a line of text beside a list being filled
/// in, where carrying on clicking is harmless. This is for the other kind. Repacking a
/// fifty-megabyte sprite sheet takes seconds, and a second click during it starts a second
/// repack against half-written files, so the window stops taking input until it is done.
///
/// Deliberately light. It began as a dark sheet with a panel in the middle, which read as
/// the window having gone away rather than as the page being busy. The work is happening to
/// the page behind, so the page stays visible: a barely-there veil to catch the mouse, and a
/// pill near the top to say what is going on.
/// </summary>
internal sealed class WorkingOverlay : Adorner
{
    private readonly VisualCollection _visuals;
    private readonly UIElement _panel;

    public WorkingOverlay(UIElement adorned, string message) : base(adorned)
    {
        // The point of the thing: clicks land here and go nowhere.
        IsHitTestVisible = true;

        _panel = Build(message);
        _visuals = new VisualCollection(this) { _panel };
    }

    private static UIElement Build(string message)
    {
        var spinner = new Border
        {
            Width = 18,
            Height = 18,
            BorderThickness = new Thickness(2, 2, 2, 0),
            BorderBrush = Brush("Accent", Colors.SteelBlue),
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        var spin = new RotateTransform();
        spinner.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(0.9),
            RepeatBehavior = RepeatBehavior.Forever,
        });

        var card = new Border
        {
            Background = Brush("PanelRaised", Color.FromRgb(0x2A, 0x2A, 0x30)),
            BorderBrush = Brush("Border", Color.FromRgb(0x44, 0x44, 0x4C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(18, 11, 22, 11),

            // A pill near the top rather than a panel in the middle. The work is happening
            // to the page behind it, and covering that page to say so is the part that felt
            // like the window had gone away.
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 56, 0, 0),
            MaxWidth = 460,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    spinner,
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brush("Text", Colors.White),
                    },
                },
            },
        };

        return new Grid
        {
            // Barely there. It only has to catch the mouse and take the colour down a shade;
            // the page stays readable, which is the point of showing it at all.
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
            Children = { card },
        };
    }

    /// <summary>
    /// A themed brush, falling back to a fixed colour.
    ///
    /// Drawn in code rather than XAML, so the theme's own resources are looked up by name;
    /// the fallback is only reached if a key is ever renamed, and a readable overlay in the
    /// wrong shade beats a crash.
    /// </summary>
    private static Brush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size constraint)
    {
        _panel.Measure(constraint);
        return constraint;
    }

    protected override Size ArrangeOverride(Size size)
    {
        _panel.Arrange(new Rect(size));
        return size;
    }
}

/// <summary>Runs slow work with the window held, so it cannot be started twice.</summary>
public static class Working
{
    /// <summary>
    /// Covers the window, runs <paramref name="work"/> off the drawing thread, and uncovers it.
    ///
    /// Returns what the work returned, or <c>default</c> if it threw — in which case
    /// <paramref name="onError"/> has already been told, on the drawing thread, so it is safe
    /// to put a message box up from it.
    /// </summary>
    public static async Task<T?> While<T>(Window? owner, string message, Func<T> work,
                                          Action<Exception>? onError = null)
    {
        var layer = owner?.Content is UIElement content ? AdornerLayer.GetAdornerLayer(content) : null;
        var overlay = layer is not null && owner?.Content is UIElement root
            ? new WorkingOverlay(root, message)
            : null;

        if (overlay is not null) layer!.Add(overlay);

        // Held as well as covered: the scrim stops the mouse, and this stops the keyboard
        // reaching a button that is about to act on files being rewritten.
        var was = owner?.IsHitTestVisible ?? true;
        if (owner is not null) Keyboard.ClearFocus();

        try
        {
            return await Task.Run(work);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return default;
        }
        finally
        {
            if (owner is not null) owner.IsHitTestVisible = was;
            if (overlay is not null) layer!.Remove(overlay);
        }
    }

    /// <summary>The same, for work that returns nothing.</summary>
    public static async Task<bool> While(Window? owner, string message, Action work,
                                         Action<Exception>? onError = null)
    {
        var done = await While(owner, message, () => { work(); return true; }, onError);
        return done;
    }
}
