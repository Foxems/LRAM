using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lantern.Core;

namespace Lantern.UI
{
    /// <summary>Putting the accounts in your own order: drag a row, the Sort menu, and Move in the row menu.</summary>
    public partial class MainWindow
    {
        const string AccountDragFormat = "Lantern.Account";

        Point _dragOrigin;
        AccountItem _dragCandidate;
        AccountItem _dragging;

        void WireReorder()
        {
            AccountsList.PreviewMouseLeftButtonDown += OnRowMouseDown;
            AccountsList.PreviewMouseMove += OnRowMouseMove;
            AccountsList.PreviewMouseLeftButtonUp += (s, e) => _dragCandidate = null;
            AccountsHost.DragOver += OnAccountDragOver;
            AccountsHost.DragLeave += (s, e) => HideDropLine();
            AccountsHost.Drop += OnAccountDrop;
            SortButton.Click += (s, e) => ShowSortMenu();
        }

        static DependencyObject ParentOf(DependencyObject d) =>
            d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

        UIElement ContainerOf(AccountItem item) => AccountsList.ItemContainerGenerator.ContainerFromItem(item) as UIElement;

        // starting a drag
        void OnRowMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragCandidate = null;
            if (DialogLayer.Visibility == Visibility.Visible) return;

            AccountItem item = null;
            for (var d = e.OriginalSource as DependencyObject; d != null && d != AccountsList; d = ParentOf(d))
            {
                // clicks on the tick box, the Launch/⋯ buttons and the like stay clicks
                if (d is ButtonBase || d is TextBoxBase || d is ScrollBar) return;
                if (item == null && d is FrameworkElement fe && fe.DataContext is AccountItem a) item = a;
            }
            if (item == null) return;
            _dragCandidate = item;
            _dragOrigin = e.GetPosition(AccountsHost);
        }

        void OnRowMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragCandidate == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragCandidate = null; return; }

            var p = e.GetPosition(AccountsHost);
            if (Math.Abs(p.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            var item = _dragCandidate;
            _dragCandidate = null;
            _dragging = item;
            var row = ContainerOf(item);
            if (row != null) row.Opacity = 0.45;
            try { DragDrop.DoDragDrop(AccountsList, new DataObject(AccountDragFormat, item.Data.Id), DragDropEffects.Move); }
            catch (Exception ex) { Logger.Write("Drag: " + ex.Message); }
            finally
            {
                if (row != null) row.Opacity = 1;
                _dragging = null;
                HideDropLine();
            }
        }

        // where would it land?
        /// <summary>
        /// For a pointer at height <paramref name="y"/> (relative to the list area): the row it would go next to, whether
        /// before or after it, and where to draw the line. False when dropping there would change nothing.
        /// </summary>
        bool InsertionAt(AccountItem dragged, double y, out AccountItem anchor, out bool before, out double lineY)
        {
            anchor = null; before = true; lineY = 0;
            var rows = _view.Cast<AccountItem>().ToList();
            if (rows.Count == 0) return false;

            foreach (var it in rows)
            {
                var c = ContainerOf(it);
                if (c == null) continue;
                double top = c.TransformToAncestor(AccountsHost).Transform(new Point(0, 0)).Y;
                double h = c.RenderSize.Height;
                if (y < top + h / 2) { anchor = it; before = true; lineY = top; break; }
            }
            if (anchor == null)
            {
                anchor = rows[rows.Count - 1]; before = false;
                var c = ContainerOf(anchor);
                if (c == null) return false;
                lineY = c.TransformToAncestor(AccountsHost).Transform(new Point(0, 0)).Y + c.RenderSize.Height;
            }

            int di = rows.IndexOf(dragged), ai = rows.IndexOf(anchor);
            if (di < 0 || ai == di) return false;
            if (before && ai == di + 1) return false;
            if (!before && ai == di - 1) return false;
            return true;
        }

        void ShowDropLine(double y)
        {
            var g = LayoutState.Current.Gutter;
            y = Math.Max(1.5, Math.Min(AccountsHost.ActualHeight - 1.5, y));
            DropLine.Margin = new Thickness(g.Left, y - 1.5, g.Right, 0);
            DropLine.Visibility = Visibility.Visible;
        }

        void HideDropLine() => DropLine.Visibility = Visibility.Collapsed;

        AccountItem DraggedFrom(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(AccountDragFormat)) return null;
            var id = e.Data.GetData(AccountDragFormat) as string;
            return _mgr.Accounts.FirstOrDefault(a => a.Data.Id == id);
        }

        void OnAccountDragOver(object sender, DragEventArgs e)
        {
            var dragged = DraggedFrom(e);
            if (dragged == null) return;          // cookies / files dropped on the list are handled by the window
            e.Handled = true;
            e.Effects = DragDropEffects.Move;

            var p = e.GetPosition(AccountsHost);
            const double edge = 36;
            if (p.Y < edge) AccountsScroll.ScrollToVerticalOffset(AccountsScroll.VerticalOffset - 14);
            else if (p.Y > AccountsHost.ActualHeight - edge) AccountsScroll.ScrollToVerticalOffset(AccountsScroll.VerticalOffset + 14);

            if (InsertionAt(dragged, p.Y, out _, out _, out double lineY)) ShowDropLine(lineY);
            else HideDropLine();
        }

        void OnAccountDrop(object sender, DragEventArgs e)
        {
            var dragged = DraggedFrom(e);
            if (dragged == null) return;
            e.Handled = true;
            HideDropLine();
            DropAccountAt(dragged, e.GetPosition(AccountsHost).Y);
        }

        /// <summary>Moves <paramref name="dragged"/> to where a pointer at <paramref name="y"/> (list-area coordinates) would drop it.</summary>
        internal bool DropAccountAt(AccountItem dragged, double y)
        {
            if (!InsertionAt(dragged, y, out var anchor, out bool before, out _)) return false;
            return _mgr.MoveAccount(dragged, anchor, before);
        }

        // sort menu + row-menu moves
        void ShowSortMenu()
        {
            var menu = new ContextMenu { PlacementTarget = SortButton, Placement = PlacementMode.Bottom };
            var head = MenuEntry("", "Sort the list once — then drag rows to fine-tune", null);
            head.IsEnabled = false;
            menu.Items.Add(head);
            menu.Items.Add(new Separator());
            foreach (var (key, label) in new[]
            {
                ("name", "Name, A to Z"), ("recent", "Last played first"), ("running", "Running first"),
                ("group", "By group"), ("uptime", "Most online time (last 7 days)")
            })
            {
                var k = key; var l = label;
                menu.Items.Add(MenuEntry("", l, (s, e) => { _mgr.Sort(k); ShowToast("Sorted: " + l.ToLowerInvariant() + ". Drag rows to adjust.", false); }));
            }
            menu.IsOpen = true;
        }

        MenuItem MoveMenu(AccountItem item)
        {
            var move = MenuEntry("", "Move", null);
            List<AccountItem> shown() => _view.Cast<AccountItem>().ToList();
            move.Items.Add(MenuEntry("", "To the top", (s, e) => _mgr.MoveToEdge(item, true)));
            move.Items.Add(MenuEntry("", "Up one", (s, e) => _mgr.Nudge(item, -1, shown())));
            move.Items.Add(MenuEntry("", "Down one", (s, e) => _mgr.Nudge(item, +1, shown())));
            move.Items.Add(MenuEntry("", "To the bottom", (s, e) => _mgr.MoveToEdge(item, false)));
            return move;
        }
    }
}
