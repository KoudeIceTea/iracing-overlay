using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace IracingOverlay.App.Settings;

public sealed class ReorderableFields : StackPanel
{
    private readonly Border _line = new() { Height = 2, Background = Brushes.DodgerBlue, IsHitTestVisible = false };
    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private ScrollViewer? _scroll;
    private Grid? _dragged;
    private Point _start;
    private bool _moved;
    private int _slot = -1;

    internal event Action? OrderChanged;
    internal string[] Order => Children.Cast<Grid>().Select(row => (string)row.Tag).ToArray();

    public ReorderableFields() => _scrollTimer.Tick += (_, _) => UpdateTarget(scroll: true);

    internal void ResetFields()
    {
        _scrollTimer.Stop();
        ClearLine();
        _dragged = null;
        _scroll = null;
        OrderChanged = null;
        Children.Clear();
    }

    internal void AddField(string id, CheckBox toggle)
    {
        var row = new Grid { Tag = id };
        var body = new DockPanel();
        var grip = new Thumb();
        grip.SetResourceReference(StyleProperty, "FieldGrip");
        AutomationProperties.SetName(grip, "Move " + toggle.Content);
        grip.DragStarted += (_, _) =>
        {
            grip.Focus();
            _dragged = row;
            _start = Mouse.GetPosition(this);
            _moved = false;
            _scroll = FindScrollViewer();
            _scrollTimer.Start();
        };
        grip.DragDelta += (_, _) => UpdateTarget(scroll: false);
        grip.DragCompleted += (_, e) =>
        {
            if (!e.Canceled) UpdateTarget(scroll: false);
            int index = _slot;
            _scrollTimer.Stop();
            ClearLine();
            _dragged = null;
            _scroll = null;
            if (!e.Canceled && index >= 0) Move(row, index);
        };
        grip.PreviewKeyDown += (_, e) =>
        {
            if (grip.IsDragging && e.Key == Key.Escape) { grip.CancelDrag(); e.Handled = true; }
            else if (!grip.IsDragging && e.Key is Key.Up or Key.Down)
            {
                int i = Children.IndexOf(row);
                Move(row, Math.Clamp(e.Key == Key.Up ? i - 1 : i + 2, 0, Children.Count));
                grip.Focus();
                grip.BringIntoView();
                e.Handled = true;
            }
        };
        body.Children.Add(grip);
        body.Children.Add(toggle);
        row.Children.Add(body);
        Children.Add(row);
    }

    private ScrollViewer? FindScrollViewer()
    {
        for (DependencyObject? p = this; p != null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer viewer) return viewer;
        return null;
    }

    private void UpdateTarget(bool scroll)
    {
        if (_dragged == null || _scroll == null) return;
        var point = Mouse.GetPosition(this);
        _moved |= Math.Abs(point.Y - _start.Y) >= SystemParameters.MinimumVerticalDragDistance;
        if (!_moved) return;
        var view = Mouse.GetPosition(_scroll);
        if (view.X < 0 || view.X > _scroll.ActualWidth || view.Y < 0 || view.Y > _scroll.ActualHeight)
        {
            ClearLine();
            return;
        }
        if (scroll && view.Y < 24) _scroll.LineUp();
        else if (scroll && view.Y > _scroll.ActualHeight - 24) _scroll.LineDown();

        int slot = 0;
        foreach (Grid row in Children)
        {
            if (point.Y < row.TranslatePoint(new Point(0, row.ActualHeight / 2), this).Y) break;
            slot++;
        }
        if (_slot == slot) return;
        ClearLine();
        _slot = slot;
        var target = (Grid)Children[Math.Min(slot, Children.Count - 1)];
        _line.VerticalAlignment = slot == Children.Count ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        target.Children.Add(_line);
    }

    private void ClearLine()
    {
        if (_line.Parent is Grid row) row.Children.Remove(_line);
        _slot = -1;
    }

    private void Move(Grid row, int slot)
    {
        int from = Children.IndexOf(row);
        int to = slot > from ? slot - 1 : slot;
        if (to == from) return;
        Children.RemoveAt(from);
        Children.Insert(to, row);
        OrderChanged?.Invoke();
    }
}
