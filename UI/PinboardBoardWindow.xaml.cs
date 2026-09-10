using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Pinboard.Data;
using Pinboard.Events;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Pinboard.UI
{
    /// <summary>
    /// The floating board window. Each card shows a schedule's live data as
    /// a read only grid, and can be dragged anywhere on the canvas by its
    /// header or resized from the bottom right corner.
    ///
    /// Navigation matches Revit's own 3D view controls, middle mouse drag
    /// pans, scroll wheel zooms centered on the cursor, and a middle mouse
    /// double click zooms out to fit every card on screen.
    ///
    /// The board also listens for model changes. A short debounce timer
    /// means a burst of edits collapses into a single refresh instead of
    /// one per change, and only cards whose schedule category was actually
    /// touched get reread.
    ///
    /// This is a full revert to the last confirmed working structure, no
    /// pen, eraser, or print, and the RenderTransform sits directly on the
    /// ItemsControl again rather than on a separate wrapping Grid.
    /// </summary>
    public partial class PinboardBoardWindow : Window
    {
        public Document Document { get; }
        public string BatchName { get; }

        private readonly ObservableCollection<ScheduleCardViewModel> _cards;
        private readonly string _batchName;
        private bool _isPanning;
        private Point _lastPanPoint;

        private readonly HashSet<ElementId> _pendingChangedIds = new HashSet<ElementId>();
        private readonly DispatcherTimer _refreshDebounceTimer;
        private readonly DispatcherTimer _markupSaveDebounceTimer;

        private enum MarkupMode { None, Pen, Eraser, Select }
        private MarkupMode _markupMode = MarkupMode.None;
        private Polyline _currentStroke;
        private Color _currentPenColor = Color.FromRgb(0xE7, 0x4C, 0x3C);
        private double _currentPenThickness = 3;
        private readonly List<Polyline> _selectedStrokes = new List<Polyline>();
        private readonly List<Rectangle> _selectionOutlines = new List<Rectangle>();
        private readonly List<ScheduleCardViewModel> _selectedCards = new List<ScheduleCardViewModel>();
        private readonly List<Rectangle> _cardSelectionOutlines = new List<Rectangle>();
        private Rectangle _dragSelectionBox;
        private Point _dragSelectionStart;
        private bool _isDragSelecting;
        private Point _lastDragPoint;
        private HwndSource _hwndSource;

        public PinboardBoardWindow(Document doc, string batchName, List<ScheduleCardViewModel> cards, Color backgroundColor, byte[] initialMarkup)
        {
            InitializeComponent();
            Document = doc;
            BatchName = batchName;
            _batchName = batchName;
            BatchNameText.Text = $"Batch: {batchName}";
            _cards = new ObservableCollection<ScheduleCardViewModel>(cards);
            CardList.ItemsSource = _cards;

            ApplyTheme(backgroundColor);

            if (initialMarkup != null && initialMarkup.Length > 0)
            {
                try
                {
                    LoadMarkupStrokes(initialMarkup);
                }
                catch
                {
                    // A saved markup that fails to load is treated as if
                    // there were none, rather than blocking the board.
                }
            }

            _refreshDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            _refreshDebounceTimer.Tick += RefreshDebounceTimer_Tick;

            _markupSaveDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _markupSaveDebounceTimer.Tick += MarkupSaveDebounceTimer_Tick;

            BoardRegistry.Register(this);
            Closed += (s, e) =>
            {
                // If Escape or anything else closes this window while a
                // save was still waiting on its debounce timer, that
                // change needs to go out right now rather than being lost
                // along with the timer that never got to fire.
                if (_markupSaveDebounceTimer.IsEnabled)
                {
                    _markupSaveDebounceTimer.Stop();
                    byte[] inkBytes = SerializeMarkupStrokes();
                    PinboardApp.RequestSaveMarkup(Document, _batchName, inkBytes);
                }

                _refreshDebounceTimer.Stop();
                _markupSaveDebounceTimer.Stop();
                BoardRegistry.Unregister(this);

                // The window message hook must come off before the window
                // itself is gone, a hook left attached to a disposed
                // window is exactly the kind of thing that can bring down
                // the whole host process rather than just this window.
                _hwndSource?.RemoveHook(WndProc);
                _hwndSource = null;
                Dispatcher.UnhandledException -= Dispatcher_UnhandledException;
            };

            SourceInitialized += Window_SourceInitialized;

            // Emergency safety net. This window runs on Revit's own shared
            // UI thread with no Application object of its own to catch
            // exceptions the normal WPF way, so an unhandled exception in
            // any event handler here could otherwise crash the whole host
            // process. This turns that into a plain message box instead.
            Dispatcher.UnhandledException += Dispatcher_UnhandledException;

            Loaded += (s, e) => ZoomToFit();
        }

        private void Dispatcher_UnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"Pinboard ran into a problem and recovered:\n\n{e.Exception}",
                "Pinboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        /// <summary>
        /// A plain WPF PreviewKeyDown was not enough to stop Escape from
        /// closing the window, meaning something outside WPF, almost
        /// certainly Revit's own "cancel whatever is happening" shortcut,
        /// was intercepting it first. Hooking the raw window message lets
        /// this catch Escape before that ever gets a chance to see it.
        /// </summary>
        private void Window_SourceInitialized(object sender, EventArgs e)
        {
            _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            _hwndSource?.AddHook(WndProc);
        }

        /// <summary>
        /// This runs inside Windows' own message loop, outside the normal
        /// safety net managed code has. An exception escaping from here
        /// does not just fail this one action, it can crash the entire
        /// host process, so absolutely nothing inside this method is
        /// allowed to throw past this boundary, ever.
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            try
            {
                const int WM_KEYDOWN = 0x0100;
                const int VK_ESCAPE = 0x1B;

                if (msg == WM_KEYDOWN && wParam.ToInt32() == VK_ESCAPE)
                {
                    Keyboard.ClearFocus();
                    PenButton.IsChecked = false;
                    EraserButton.IsChecked = false;
                    SelectButton.IsChecked = false;
                    _markupMode = MarkupMode.None;
                    MarkupCanvas.IsHitTestVisible = false;
                    PenOptionsPanel.Visibility = System.Windows.Visibility.Collapsed;
                    DeselectAll();
                    handled = true;
                }
            }
            catch
            {
                // Swallowed on purpose, see the note above, this must
                // never let an exception continue past here.
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Turning the pen on shows the color and thickness panel, turning
        /// it off (either by clicking again or by starting a stroke) hides
        /// it. Pen and eraser are mutually exclusive.
        /// </summary>
        private void PenButton_Click(object sender, RoutedEventArgs e)
        {
            if (PenButton.IsChecked == true)
            {
                EraserButton.IsChecked = false;
                SelectButton.IsChecked = false;
                DeselectAll();
                _markupMode = MarkupMode.Pen;
                MarkupCanvas.IsHitTestVisible = true;
                PenOptionsPanel.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                _markupMode = MarkupMode.None;
                MarkupCanvas.IsHitTestVisible = false;
                PenOptionsPanel.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void EraserButton_Click(object sender, RoutedEventArgs e)
        {
            if (EraserButton.IsChecked == true)
            {
                PenButton.IsChecked = false;
                SelectButton.IsChecked = false;
                DeselectAll();
                PenOptionsPanel.Visibility = System.Windows.Visibility.Collapsed;
                _markupMode = MarkupMode.Eraser;
                MarkupCanvas.IsHitTestVisible = true;
            }
            else
            {
                _markupMode = MarkupMode.None;
                MarkupCanvas.IsHitTestVisible = false;
            }
        }

        /// <summary>
        /// Select mode. Clicking directly on a stroke selects just that one,
        /// for when you need precision. Dragging from empty space instead
        /// draws a box, and anything the box touches gets selected
        /// together, since lines are thin and easy to miss otherwise.
        /// </summary>
        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectButton.IsChecked == true)
            {
                PenButton.IsChecked = false;
                EraserButton.IsChecked = false;
                PenOptionsPanel.Visibility = System.Windows.Visibility.Collapsed;
                _markupMode = MarkupMode.Select;
                MarkupCanvas.IsHitTestVisible = true;
            }
            else
            {
                _markupMode = MarkupMode.None;
                MarkupCanvas.IsHitTestVisible = false;
                DeselectAll();
            }
        }

        /// <summary>
        /// Picks the color new strokes are drawn with. Strokes already on
        /// the board keep whatever color they were drawn in.
        /// </summary>
        private void ColorSwatch_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Background is SolidColorBrush brush)
            {
                _currentPenColor = brush.Color;
            }
        }

        private void ThicknessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _currentPenThickness = e.NewValue;
        }

        /// <summary>
        /// Starts a stroke in pen mode, hiding the options panel the moment
        /// drawing actually begins, or erases whatever is directly under
        /// the cursor in eraser mode. Mouse capture keeps the drag working
        /// smoothly even if the cursor briefly leaves the canvas.
        /// </summary>
        private void MarkupCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Point position = e.GetPosition(MarkupCanvas);

            if (_markupMode == MarkupMode.Pen)
            {
                PenOptionsPanel.Visibility = System.Windows.Visibility.Collapsed;

                _currentStroke = new Polyline
                {
                    Stroke = new SolidColorBrush(_currentPenColor),
                    StrokeThickness = _currentPenThickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };
                _currentStroke.Points.Add(position);
                MarkupCanvas.Children.Add(_currentStroke);
                MarkupCanvas.CaptureMouse();
            }
            else if (_markupMode == MarkupMode.Eraser)
            {
                EraseAt(position);
                MarkupCanvas.CaptureMouse();
            }
            else if (_markupMode == MarkupMode.Select)
            {
                bool shiftHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

                HitTestResult result = VisualTreeHelper.HitTest(MarkupCanvas, position);
                Polyline hitPolyline = result?.VisualHit as Polyline;
                ScheduleCardViewModel hitCard = hitPolyline == null
                    ? _cards.FirstOrDefault(c => GetCardBounds(c).Contains(position))
                    : null;

                // Shift click on something already selected removes just
                // that one item and leaves the rest of the selection
                // alone, same as Revit's own selection behaviour. Shift
                // click on something new adds it instead of replacing.
                if (shiftHeld)
                {
                    if (hitPolyline != null)
                    {
                        if (_selectedStrokes.Contains(hitPolyline))
                        {
                            RemoveStrokeFromSelection(hitPolyline);
                        }
                        else
                        {
                            AddStrokeToSelection(hitPolyline);
                        }
                        return;
                    }

                    if (hitCard != null)
                    {
                        if (_selectedCards.Contains(hitCard))
                        {
                            RemoveCardFromSelection(hitCard);
                        }
                        else
                        {
                            AddCardToSelection(hitCard);
                        }
                        return;
                    }
                }

                // If a selection already exists and the click lands
                // anywhere inside its bounding box, drag the whole group,
                // no need to land exactly on a thin line or a card border.
                if ((_selectedStrokes.Count > 0 || _selectedCards.Count > 0) && IsInsideAnySelectionBounds(position))
                {
                    _lastDragPoint = position;
                    MarkupCanvas.CaptureMouse();
                    return;
                }

                if (hitPolyline != null)
                {
                    if (!_selectedStrokes.Contains(hitPolyline))
                    {
                        SelectOnlyStroke(hitPolyline);
                    }
                    _lastDragPoint = position;
                    MarkupCanvas.CaptureMouse();
                    return;
                }

                // Cards live in their own separate layer underneath this
                // one, so they never show up in a hit test against
                // MarkupCanvas, their bounds have to be checked directly
                // in the same world coordinates instead.
                if (hitCard != null)
                {
                    SelectOnlyCard(hitCard);
                    _lastDragPoint = position;
                    MarkupCanvas.CaptureMouse();
                    return;
                }

                // A plain click on empty space clears the selection, a
                // shift click on empty space just starts an additive box
                // instead of wiping out what is already selected.
                if (!shiftHeld)
                {
                    DeselectAll();
                }

                _dragSelectionStart = position;
                _dragSelectionBox = new Rectangle
                {
                    Width = 1,
                    Height = 1,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x34, 0x98, 0xDB)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 2 },
                    Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x34, 0x98, 0xDB)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(_dragSelectionBox, position.X);
                Canvas.SetTop(_dragSelectionBox, position.Y);
                MarkupCanvas.Children.Add(_dragSelectionBox);

                // The box is now fully built and in the tree, only now
                // is it safe to say a drag selection is in progress,
                // since that is exactly the flag MouseMove checks
                // before touching this box.
                _isDragSelecting = true;
                MarkupCanvas.CaptureMouse();
            }
        }

        private void MarkupCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }

            Point position = e.GetPosition(MarkupCanvas);

            if (_markupMode == MarkupMode.Pen && _currentStroke != null)
            {
                _currentStroke.Points.Add(position);
            }
            else if (_markupMode == MarkupMode.Eraser)
            {
                EraseAt(position);
            }
            else if (_markupMode == MarkupMode.Select)
            {
                if (_isDragSelecting)
                {
                    double left = Math.Min(_dragSelectionStart.X, position.X);
                    double top = Math.Min(_dragSelectionStart.Y, position.Y);
                    double width = Math.Abs(position.X - _dragSelectionStart.X);
                    double height = Math.Abs(position.Y - _dragSelectionStart.Y);
                    Canvas.SetLeft(_dragSelectionBox, left);
                    Canvas.SetTop(_dragSelectionBox, top);
                    _dragSelectionBox.Width = width;
                    _dragSelectionBox.Height = height;
                }
                else if (_selectedStrokes.Count > 0 || _selectedCards.Count > 0)
                {
                    double dx = position.X - _lastDragPoint.X;
                    double dy = position.Y - _lastDragPoint.Y;
                    foreach (Polyline stroke in _selectedStrokes)
                    {
                        MoveStroke(stroke, dx, dy);
                    }
                    foreach (ScheduleCardViewModel card in _selectedCards)
                    {
                        card.X += dx;
                        card.Y += dy;
                    }
                    UpdateAllSelectionOutlines();
                    UpdateAllCardSelectionOutlines();
                    _lastDragPoint = position;
                }
            }
        }

        private void MarkupCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_markupMode == MarkupMode.None)
            {
                return;
            }

            MarkupCanvas.ReleaseMouseCapture();

            if (_isDragSelecting)
            {
                Rect box = new Rect(
                    Canvas.GetLeft(_dragSelectionBox), Canvas.GetTop(_dragSelectionBox),
                    _dragSelectionBox.Width, _dragSelectionBox.Height);

                MarkupCanvas.Children.Remove(_dragSelectionBox);
                _dragSelectionBox = null;
                _isDragSelecting = false;

                foreach (UIElement child in MarkupCanvas.Children.OfType<Polyline>().ToList())
                {
                    Polyline polyline = (Polyline)child;
                    Rect strokeBounds = GetStrokeBounds(polyline);
                    if (box.IntersectsWith(strokeBounds))
                    {
                        AddStrokeToSelection(polyline);
                    }
                }

                foreach (ScheduleCardViewModel card in _cards)
                {
                    if (box.IntersectsWith(GetCardBounds(card)))
                    {
                        AddCardToSelection(card);
                    }
                }

                return;
            }

            bool madeAChange = _currentStroke != null || _markupMode == MarkupMode.Eraser
                || (_markupMode == MarkupMode.Select && (_selectedStrokes.Count > 0 || _selectedCards.Count > 0));
            _currentStroke = null;

            if (madeAChange)
            {
                // Cards dragged this way still snap to the grid on
                // release, same as dragging one by its own header does.
                foreach (ScheduleCardViewModel card in _selectedCards)
                {
                    card.X = Math.Round(card.X / GridSize) * GridSize;
                    card.Y = Math.Round(card.Y / GridSize) * GridSize;
                }
                UpdateAllCardSelectionOutlines();

                _markupSaveDebounceTimer.Stop();
                _markupSaveDebounceTimer.Start();
            }
        }

        private static Rect GetStrokeBounds(Polyline stroke)
        {
            double minX = stroke.Points.Min(p => p.X);
            double minY = stroke.Points.Min(p => p.Y);
            double maxX = stroke.Points.Max(p => p.X);
            double maxY = stroke.Points.Max(p => p.Y);
            return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
        }

        private static Rect GetCardBounds(ScheduleCardViewModel card)
        {
            return new Rect(card.X, card.Y, card.Width, card.Height);
        }

        /// <summary>
        /// Same padding as the dashed outline itself, so "inside the box"
        /// matches what the box actually looks like on screen. Cards use
        /// no extra padding since their own border already reads clearly.
        /// </summary>
        private bool IsInsideAnySelectionBounds(Point position)
        {
            const double padding = 4;
            foreach (Polyline stroke in _selectedStrokes)
            {
                Rect bounds = GetStrokeBounds(stroke);
                bounds.Inflate(padding, padding);
                if (bounds.Contains(position))
                {
                    return true;
                }
            }
            foreach (ScheduleCardViewModel card in _selectedCards)
            {
                if (GetCardBounds(card).Contains(position))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Clears the current selection and selects just this one stroke.
        /// </summary>
        private void SelectOnlyStroke(Polyline stroke)
        {
            DeselectAll();
            AddStrokeToSelection(stroke);
        }

        /// <summary>
        /// Clears the current selection and selects just this one card.
        /// </summary>
        private void SelectOnlyCard(ScheduleCardViewModel card)
        {
            DeselectAll();
            AddCardToSelection(card);
        }

        /// <summary>
        /// Adds a stroke to the current selection with its own dashed
        /// outline, without disturbing anything already selected, so a box
        /// that touches several strokes selects all of them together.
        /// </summary>
        private void AddStrokeToSelection(Polyline stroke)
        {
            if (_selectedStrokes.Contains(stroke))
            {
                return;
            }

            _selectedStrokes.Add(stroke);

            Rectangle outline = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x34, 0x98, 0xDB)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };
            _selectionOutlines.Add(outline);
            MarkupCanvas.Children.Add(outline);
            UpdateSelectionOutline(stroke, outline);
        }

        /// <summary>
        /// Same idea as a stroke, a dashed outline drawn on the markup
        /// layer at the card's own bounds, since the card itself lives in
        /// a separate layer underneath.
        /// </summary>
        private void AddCardToSelection(ScheduleCardViewModel card)
        {
            if (_selectedCards.Contains(card))
            {
                return;
            }

            _selectedCards.Add(card);

            Rectangle outline = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x34, 0x98, 0xDB)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };
            _cardSelectionOutlines.Add(outline);
            MarkupCanvas.Children.Add(outline);
            UpdateCardSelectionOutline(card, outline);
        }

        private void DeselectAll()
        {
            foreach (Rectangle outline in _selectionOutlines)
            {
                MarkupCanvas.Children.Remove(outline);
            }
            _selectionOutlines.Clear();
            _selectedStrokes.Clear();

            foreach (Rectangle outline in _cardSelectionOutlines)
            {
                MarkupCanvas.Children.Remove(outline);
            }
            _cardSelectionOutlines.Clear();
            _selectedCards.Clear();
        }

        /// <summary>
        /// Removes just one stroke from the selection, used for a shift
        /// click on something already selected.
        /// </summary>
        private void RemoveStrokeFromSelection(Polyline stroke)
        {
            int index = _selectedStrokes.IndexOf(stroke);
            if (index < 0)
            {
                return;
            }

            MarkupCanvas.Children.Remove(_selectionOutlines[index]);
            _selectedStrokes.RemoveAt(index);
            _selectionOutlines.RemoveAt(index);
        }

        /// <summary>
        /// Removes just one card from the selection, used for a shift
        /// click on something already selected.
        /// </summary>
        private void RemoveCardFromSelection(ScheduleCardViewModel card)
        {
            int index = _selectedCards.IndexOf(card);
            if (index < 0)
            {
                return;
            }

            MarkupCanvas.Children.Remove(_cardSelectionOutlines[index]);
            _selectedCards.RemoveAt(index);
            _cardSelectionOutlines.RemoveAt(index);
        }

        private void UpdateAllSelectionOutlines()
        {
            for (int i = 0; i < _selectedStrokes.Count; i++)
            {
                UpdateSelectionOutline(_selectedStrokes[i], _selectionOutlines[i]);
            }
        }

        private void UpdateAllCardSelectionOutlines()
        {
            for (int i = 0; i < _selectedCards.Count; i++)
            {
                UpdateCardSelectionOutline(_selectedCards[i], _cardSelectionOutlines[i]);
            }
        }

        private static void UpdateSelectionOutline(Polyline stroke, Rectangle outline)
        {
            const double padding = 4;
            double minX = stroke.Points.Min(p => p.X) - padding;
            double minY = stroke.Points.Min(p => p.Y) - padding;
            double maxX = stroke.Points.Max(p => p.X) + padding;
            double maxY = stroke.Points.Max(p => p.Y) + padding;

            Canvas.SetLeft(outline, minX);
            Canvas.SetTop(outline, minY);
            outline.Width = Math.Max(1, maxX - minX);
            outline.Height = Math.Max(1, maxY - minY);
        }

        private static void UpdateCardSelectionOutline(ScheduleCardViewModel card, Rectangle outline)
        {
            const double padding = 4;
            Canvas.SetLeft(outline, card.X - padding);
            Canvas.SetTop(outline, card.Y - padding);
            outline.Width = card.Width + padding * 2;
            outline.Height = card.Height + padding * 2;
        }

        /// <summary>
        /// Shifts every point of a stroke by the same amount, keeping its
        /// shape exactly as drawn, just relocated.
        /// </summary>
        private static void MoveStroke(Polyline stroke, double dx, double dy)
        {
            for (int i = 0; i < stroke.Points.Count; i++)
            {
                stroke.Points[i] = new Point(stroke.Points[i].X + dx, stroke.Points[i].Y + dy);
            }
        }

        /// <summary>
        /// Removes whichever stroke is directly under the given point, if
        /// any, one stroke per pass, so a drag across several overlapping
        /// strokes erases them one at a time as the cursor crosses each.
        /// </summary>
        private void EraseAt(Point position)
        {
            HitTestResult result = VisualTreeHelper.HitTest(MarkupCanvas, position);
            if (result?.VisualHit is Polyline polyline)
            {
                MarkupCanvas.Children.Remove(polyline);
                _markupSaveDebounceTimer.Stop();
                _markupSaveDebounceTimer.Start();
            }
        }

        private void MarkupSaveDebounceTimer_Tick(object sender, EventArgs e)
        {
            _markupSaveDebounceTimer.Stop();
            byte[] inkBytes = SerializeMarkupStrokes();
            PinboardApp.RequestSaveMarkup(Document, _batchName, inkBytes);
        }

        /// <summary>
        /// One saved stroke, its points plus the color and thickness it was
        /// drawn with, so reopening a batch shows strokes exactly as they
        /// were left.
        /// </summary>
        private class MarkupStrokeData
        {
            public List<double> Points { get; set; } = new List<double>();
            public byte R { get; set; }
            public byte G { get; set; }
            public byte B { get; set; }
            public double Thickness { get; set; } = 3;
        }

        private byte[] SerializeMarkupStrokes()
        {
            List<MarkupStrokeData> strokes = new List<MarkupStrokeData>();

            foreach (UIElement child in MarkupCanvas.Children)
            {
                if (child is Polyline polyline && polyline.Stroke is SolidColorBrush brush)
                {
                    List<double> flat = new List<double>();
                    foreach (Point p in polyline.Points)
                    {
                        flat.Add(p.X);
                        flat.Add(p.Y);
                    }

                    strokes.Add(new MarkupStrokeData
                    {
                        Points = flat,
                        R = brush.Color.R,
                        G = brush.Color.G,
                        B = brush.Color.B,
                        Thickness = polyline.StrokeThickness
                    });
                }
            }

            string json = JsonSerializer.Serialize(strokes);
            return Encoding.UTF8.GetBytes(json);
        }

        private void LoadMarkupStrokes(byte[] data)
        {
            string json = Encoding.UTF8.GetString(data);
            List<MarkupStrokeData> strokes = JsonSerializer.Deserialize<List<MarkupStrokeData>>(json);
            if (strokes == null)
            {
                return;
            }

            foreach (MarkupStrokeData strokeData in strokes)
            {
                Polyline polyline = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(strokeData.R, strokeData.G, strokeData.B)),
                    StrokeThickness = strokeData.Thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                };

                for (int i = 0; i + 1 < strokeData.Points.Count; i += 2)
                {
                    polyline.Points.Add(new Point(strokeData.Points[i], strokeData.Points[i + 1]));
                }

                MarkupCanvas.Children.Add(polyline);
            }
        }

        /// <summary>
        /// Builds the whole color set from a single real background color,
        /// normally read straight from Revit's own Options, Colors,
        /// Background setting, so the board matches whatever the user
        /// actually picked there rather than a fixed light or dark guess.
        /// </summary>
        private void ApplyTheme(Color backgroundColor)
        {
            Resources["BoardBackgroundBrush"] = new SolidColorBrush(backgroundColor);

            double luminance = 0.299 * backgroundColor.R + 0.587 * backgroundColor.G + 0.114 * backgroundColor.B;
            bool isDark = luminance < 128;

            if (isDark)
            {
                Resources["CardBackgroundBrush"] = new SolidColorBrush(Lighten(backgroundColor, 20));
                Resources["CardBorderBrush"] = new SolidColorBrush(Lighten(backgroundColor, 51));
                Resources["ResizeHandleBrush"] = new SolidColorBrush(Lighten(backgroundColor, 85));
                Resources["GridDotBrush"] = new SolidColorBrush(Lighten(backgroundColor, 56));
                Resources["BatchLabelBrush"] = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
                SelectIcon.Source = LoadToolIcon("tool_select_dark.png");
                PenIcon.Source = LoadToolIcon("tool_pen_dark.png");
                EraserIcon.Source = LoadToolIcon("tool_eraser_dark.png");
                PrintIcon.Source = LoadToolIcon("tool_print_dark.png");
                EditIcon.Source = LoadToolIcon("tool_edit_dark.png");
            }
            else
            {
                Resources["CardBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
                Resources["ResizeHandleBrush"] = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0));
                Resources["GridDotBrush"] = new SolidColorBrush(Color.FromRgb(0xB5, 0xB5, 0xB5));
                Resources["BatchLabelBrush"] = new SolidColorBrush(Color.FromRgb(0x2C, 0x3E, 0x50));
                SelectIcon.Source = LoadToolIcon("tool_select.png");
                PenIcon.Source = LoadToolIcon("tool_pen.png");
                EraserIcon.Source = LoadToolIcon("tool_eraser.png");
                PrintIcon.Source = LoadToolIcon("tool_print.png");
                EditIcon.Source = LoadToolIcon("tool_edit.png");
            }
        }

        private static BitmapImage LoadToolIcon(string filename)
        {
            try
            {
                Uri uri = new Uri($"pack://application:,,,/Pinboard;component/Resources/{filename}");
                return new BitmapImage(uri);
            }
            catch
            {
                return null;
            }
        }

        private static Color Lighten(Color color, int amount)
        {
            byte r = (byte)Math.Min(255, color.R + amount);
            byte g = (byte)Math.Min(255, color.G + amount);
            byte b = (byte)Math.Min(255, color.B + amount);
            return Color.FromRgb(r, g, b);
        }

        /// <summary>
        /// Cheap check run from Revit's own DocumentChanged callback, so it
        /// is safe to read element categories here directly. A deleted
        /// element cannot be looked up anymore, those are treated as
        /// possibly relevant rather than silently ignored.
        /// </summary>
        public bool IsRelevantChange(Document doc, List<ElementId> changedIds)
        {
            foreach (ScheduleCardViewModel card in _cards)
            {
                if (card.CategoryId == null || card.CategoryId == ElementId.InvalidElementId)
                {
                    return true;
                }

                foreach (ElementId changedId in changedIds)
                {
                    Element changedElement = doc.GetElement(changedId);
                    if (changedElement == null)
                    {
                        return true;
                    }

                    if (changedElement.Category != null && changedElement.Category.Id == card.CategoryId)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Called from the safe DocumentChanged context, this only resets a
        /// timer, it does not touch schedule data itself, that happens
        /// later once things settle down.
        /// </summary>
        public void NotifyPossibleChange()
        {
            _refreshDebounceTimer.Stop();
            _refreshDebounceTimer.Start();
        }

        private void RefreshDebounceTimer_Tick(object sender, EventArgs e)
        {
            _refreshDebounceTimer.Stop();
            PinboardApp.RequestRefresh(this);
        }

        /// <summary>
        /// Rereads every card's schedule. Called back through the
        /// ExternalEvent doorbell, so this runs in a Revit sanctioned
        /// moment and it is safe to touch the API here.
        /// </summary>
        public void PerformRefresh(Document doc)
        {
            foreach (ScheduleCardViewModel card in _cards)
            {
                ViewSchedule schedule = doc.GetElement(card.ScheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    continue;
                }

                try
                {
                    ScheduleExtractResult result = ScheduleDataExtractor.Extract(schedule, doc);
                    card.DataView = result.Table.DefaultView;
                }
                catch
                {
                    // Leave the card showing its last good data rather than
                    // blank it out over one failed reread.
                }
            }
        }

        /// <summary>
        /// Kicks off the whole edit flow through the doorbell, since
        /// listing schedules and touching storage both need a safe Revit
        /// context this modeless window does not have on its own.
        /// </summary>
        private void EditSchedulesButton_Click(object sender, RoutedEventArgs e)
        {
            PinboardApp.RequestEditSchedules(this);
        }

        /// <summary>
        /// The current set of schedule ids on the board, used by the edit
        /// picker to pre check what is already here.
        /// </summary>
        public HashSet<ElementId> GetCurrentScheduleIds()
        {
            return new HashSet<ElementId>(_cards.Select(c => c.ScheduleId));
        }

        /// <summary>
        /// Reconciles the board's cards against a fresh full list of
        /// schedule ids, removing cards for anything unchecked and adding
        /// a card for anything newly checked. Called back through the
        /// ExternalEvent doorbell, so this runs in a Revit sanctioned
        /// moment and it is safe to touch the API here. The card list is
        /// an ObservableCollection specifically so the board updates
        /// itself the moment cards are added or removed here, no manual
        /// refresh needed.
        /// </summary>
        public void ApplyScheduleSelection(Document doc, List<ElementId> newScheduleIds)
        {
            List<ScheduleCardViewModel> toRemove = _cards
                .Where(c => !newScheduleIds.Contains(c.ScheduleId))
                .ToList();
            foreach (ScheduleCardViewModel card in toRemove)
            {
                _cards.Remove(card);
            }

            HashSet<ElementId> existingIds = new HashSet<ElementId>(_cards.Select(c => c.ScheduleId));
            List<ElementId> idsToAdd = newScheduleIds.Where(id => !existingIds.Contains(id)).ToList();

            int index = _cards.Count;
            foreach (ElementId id in idsToAdd)
            {
                if (doc.GetElement(id) is ViewSchedule schedule)
                {
                    _cards.Add(BuildCardForSchedule(schedule, index));
                    index++;
                }
            }

            if (idsToAdd.Count > 0 || toRemove.Count > 0)
            {
                ZoomToFit();
            }
        }

        /// <summary>
        /// Same grid position and extraction logic OpenBoardCommand uses
        /// when the board first opens, kept here too since a card can now
        /// also get added later while the board is already open.
        /// </summary>
        private ScheduleCardViewModel BuildCardForSchedule(ViewSchedule schedule, int index)
        {
            const double baseX = 5000;
            const double baseY = 3000;

            double rawX = baseX + (index % 3) * 360;
            double rawY = baseY + (index / 3) * 340;
            double startX = Math.Round(rawX / GridSize) * GridSize;
            double startY = Math.Round(rawY / GridSize) * GridSize;

            try
            {
                ScheduleExtractResult extractResult = ScheduleDataExtractor.Extract(schedule, schedule.Document);
                return new ScheduleCardViewModel
                {
                    Name = schedule.Name,
                    ScheduleId = schedule.Id,
                    CategoryId = schedule.Definition.CategoryId,
                    DataView = extractResult.Table.DefaultView,
                    X = startX,
                    Y = startY
                };
            }
            catch (Exception ex)
            {
                DataTable errorTable = new DataTable();
                errorTable.Columns.Add("Error", typeof(string));
                errorTable.Rows.Add($"Could not read this schedule: {ex.Message}");

                return new ScheduleCardViewModel
                {
                    Name = schedule.Name,
                    ScheduleId = schedule.Id,
                    CategoryId = schedule.Definition.CategoryId,
                    DataView = errorTable.DefaultView,
                    X = startX,
                    Y = startY
                };
            }
        }

        private void HeaderThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ScheduleCardViewModel card)
            {
                card.X += e.HorizontalChange;
                card.Y += e.VerticalChange;
            }
        }

        /// <summary>
        /// Double clicking the header opens the real schedule directly, no
        /// row or element needed for this, so it always works regardless
        /// of whether row to element mapping is trustworthy for it.
        /// </summary>
        private void HeaderThumb_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2)
            {
                return;
            }

            if (((FrameworkElement)sender).DataContext is ScheduleCardViewModel card)
            {
                PinboardApp.RequestOpenSchedule(card.ScheduleId);
            }

            e.Handled = true;
        }

        // Must match the dot spacing set in the DrawingBrush Viewport in
        // PinboardBoardWindow.xaml, both are in pixels, roughly 5mm at
        // standard 96 DPI Windows scaling.
        private const double GridSize = 19;

        /// <summary>
        /// Snaps a card to the nearest grid point once you let go, rather
        /// than while dragging, so the drag itself still feels smooth and
        /// only clicks into place at the end.
        /// </summary>
        private void HeaderThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ScheduleCardViewModel card)
            {
                card.X = Math.Round(card.X / GridSize) * GridSize;
                card.Y = Math.Round(card.Y / GridSize) * GridSize;
            }
        }

        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ScheduleCardViewModel card)
            {
                card.Width = Math.Max(220, card.Width + e.HorizontalChange);
                card.Height = Math.Max(160, card.Height + e.VerticalChange);
            }
        }

        /// <summary>
        /// Snaps a card's final size to the grid once the resize handle is
        /// released.
        /// </summary>
        private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ScheduleCardViewModel card)
            {
                card.Width = Math.Max(220, Math.Round(card.Width / GridSize) * GridSize);
                card.Height = Math.Max(160, Math.Round(card.Height / GridSize) * GridSize);
            }
        }

        /// <summary>
        /// Right edge handle, widens or narrows the card without touching
        /// its height.
        /// </summary>
        private void RightEdgeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ScheduleCardViewModel card)
            {
                card.Width = Math.Max(220, card.Width + e.HorizontalChange);
            }
        }

        private void RightEdgeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ScheduleCardViewModel card)
            {
                card.Width = Math.Max(220, Math.Round(card.Width / GridSize) * GridSize);
            }
        }

        /// <summary>
        /// Bottom edge handle, makes the card taller or shorter without
        /// touching its width.
        /// </summary>
        private void BottomEdgeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ScheduleCardViewModel card)
            {
                card.Height = Math.Max(160, card.Height + e.VerticalChange);
            }
        }

        private void BottomEdgeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ScheduleCardViewModel card)
            {
                card.Height = Math.Max(160, Math.Round(card.Height / GridSize) * GridSize);
            }
        }

        private void ViewportBorder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle)
            {
                return;
            }

            if (e.ClickCount == 2)
            {
                ZoomToFit();
                return;
            }

            _isPanning = true;
            _lastPanPoint = e.GetPosition(ViewportBorder);
            ViewportBorder.CaptureMouse();
        }

        private void ViewportBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning)
            {
                return;
            }

            Point current = e.GetPosition(ViewportBorder);
            BoardTranslateTransform.X += current.X - _lastPanPoint.X;
            BoardTranslateTransform.Y += current.Y - _lastPanPoint.Y;
            MarkupTranslateTransform.X = BoardTranslateTransform.X;
            MarkupTranslateTransform.Y = BoardTranslateTransform.Y;
            _lastPanPoint = current;
            UpdateGridBrush();
        }

        private void ViewportBorder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && _isPanning)
            {
                _isPanning = false;
                ViewportBorder.ReleaseMouseCapture();
            }
        }

        private void ViewportBorder_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double zoomFactor = e.Delta > 0 ? 1.1 : 1 / 1.1;
            Point cursor = e.GetPosition(ViewportBorder);

            double oldScale = BoardScaleTransform.ScaleX;
            double newScale = Math.Clamp(oldScale * zoomFactor, 0.2, 3.0);

            double offsetX = BoardTranslateTransform.X;
            double offsetY = BoardTranslateTransform.Y;

            double canvasX = (cursor.X - offsetX) / oldScale;
            double canvasY = (cursor.Y - offsetY) / oldScale;

            BoardScaleTransform.ScaleX = newScale;
            BoardScaleTransform.ScaleY = newScale;

            BoardTranslateTransform.X = cursor.X - canvasX * newScale;
            BoardTranslateTransform.Y = cursor.Y - canvasY * newScale;

            MarkupScaleTransform.ScaleX = newScale;
            MarkupScaleTransform.ScaleY = newScale;
            MarkupTranslateTransform.X = BoardTranslateTransform.X;
            MarkupTranslateTransform.Y = BoardTranslateTransform.Y;
            UpdateGridBrush();

            e.Handled = true;
        }

        /// <summary>
        /// Fits every card on screen at once, used on load and on a middle
        /// mouse double click.
        /// </summary>
        private void ZoomToFit()
        {
            if (_cards == null || _cards.Count == 0)
            {
                return;
            }

            double minX = _cards.Min(c => c.X);
            double minY = _cards.Min(c => c.Y);
            double maxX = _cards.Max(c => c.X + c.Width);
            double maxY = _cards.Max(c => c.Y + c.Height);

            double contentWidth = maxX - minX;
            double contentHeight = maxY - minY;

            double viewportWidth = ViewportBorder.ActualWidth;
            double viewportHeight = ViewportBorder.ActualHeight;

            if (contentWidth <= 0 || contentHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
            {
                return;
            }

            double scaleToFitWidth = viewportWidth / contentWidth;
            double scaleToFitHeight = viewportHeight / contentHeight;
            double newScale = Math.Clamp(Math.Min(scaleToFitWidth, scaleToFitHeight) * 0.9, 0.1, 3.0);

            double contentCenterX = minX + contentWidth / 2;
            double contentCenterY = minY + contentHeight / 2;

            BoardScaleTransform.ScaleX = newScale;
            BoardScaleTransform.ScaleY = newScale;
            BoardTranslateTransform.X = viewportWidth / 2 - contentCenterX * newScale;
            BoardTranslateTransform.Y = viewportHeight / 2 - contentCenterY * newScale;
            MarkupScaleTransform.ScaleX = newScale;
            MarkupScaleTransform.ScaleY = newScale;
            MarkupTranslateTransform.X = BoardTranslateTransform.X;
            MarkupTranslateTransform.Y = BoardTranslateTransform.Y;
            UpdateGridBrush();
        }

        /// <summary>
        /// Keeps the dot pattern's Viewport rectangle in sync with the
        /// current pan and zoom, so it always looks like one continuous
        /// infinite field rather than a pattern tied to the canvas bounds.
        /// </summary>
        private void UpdateGridBrush()
        {
            double scale = BoardScaleTransform.ScaleX;
            double tileSize = GridSize * scale;
            if (tileSize <= 0)
            {
                return;
            }

            double offsetX = Wrap(BoardTranslateTransform.X, tileSize);
            double offsetY = Wrap(BoardTranslateTransform.Y, tileSize);

            GridBrush.Viewport = new Rect(offsetX, offsetY, tileSize, tileSize);
        }

        private static double Wrap(double value, double modulus)
        {
            double result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        /// <summary>
        /// Prints or saves as PDF whatever is actually on the board, cards
        /// and markup together, framed to just their own bounds rather
        /// than the whole fifteen thousand by ten thousand canvas.
        /// Windows already ships a Microsoft Print to PDF option in the
        /// standard print dialog, so this one button covers both, pick a
        /// real printer to print, pick that option to save a PDF instead.
        /// Capture briefly resets both the cards' and the markup layer's
        /// transforms to a plain 1:1 view of just the content area, then
        /// puts them back exactly as they were, it does not touch either
        /// one's normal behaviour.
        /// </summary>
        private void PrintButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cards == null || _cards.Count == 0)
            {
                MessageBox.Show("There is nothing on this board to print yet.", "Pinboard");
                return;
            }

            const double margin = 20;
            double minX = _cards.Min(c => c.X) - margin;
            double minY = _cards.Min(c => c.Y) - margin;
            double maxX = _cards.Max(c => c.X + c.Width) + margin;
            double maxY = _cards.Max(c => c.Y + c.Height) + margin;

            double contentWidth = maxX - minX;
            double contentHeight = maxY - minY;
            if (contentWidth <= 0 || contentHeight <= 0)
            {
                return;
            }

            double savedBoardScale = BoardScaleTransform.ScaleX;
            double savedBoardX = BoardTranslateTransform.X;
            double savedBoardY = BoardTranslateTransform.Y;
            double savedMarkupScale = MarkupScaleTransform.ScaleX;
            double savedMarkupX = MarkupTranslateTransform.X;
            double savedMarkupY = MarkupTranslateTransform.Y;

            BoardScaleTransform.ScaleX = 1;
            BoardScaleTransform.ScaleY = 1;
            BoardTranslateTransform.X = -minX;
            BoardTranslateTransform.Y = -minY;
            MarkupScaleTransform.ScaleX = 1;
            MarkupScaleTransform.ScaleY = 1;
            MarkupTranslateTransform.X = -minX;
            MarkupTranslateTransform.Y = -minY;
            BoardContentHost.UpdateLayout();

            const double dpi = 150;
            double dpiScale = dpi / 96.0;
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)(contentWidth * dpiScale), (int)(contentHeight * dpiScale), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(BoardContentHost);

            BoardScaleTransform.ScaleX = savedBoardScale;
            BoardScaleTransform.ScaleY = savedBoardScale;
            BoardTranslateTransform.X = savedBoardX;
            BoardTranslateTransform.Y = savedBoardY;
            MarkupScaleTransform.ScaleX = savedMarkupScale;
            MarkupScaleTransform.ScaleY = savedMarkupScale;
            MarkupTranslateTransform.X = savedMarkupX;
            MarkupTranslateTransform.Y = savedMarkupY;
            BoardContentHost.UpdateLayout();
            UpdateGridBrush();

            PrintDialog printDialog = new PrintDialog();
            if (printDialog.ShowDialog() != true)
            {
                return;
            }

            double scaleToFit = Math.Min(
                printDialog.PrintableAreaWidth / contentWidth,
                printDialog.PrintableAreaHeight / contentHeight);

            Image printImage = new Image
            {
                Source = bitmap,
                Width = contentWidth * scaleToFit,
                Height = contentHeight * scaleToFit
            };
            printImage.Measure(new Size(printImage.Width, printImage.Height));
            printImage.Arrange(new Rect(0, 0, printImage.Width, printImage.Height));

            printDialog.PrintVisual(printImage, $"Pinboard - {_batchName}");
        }
    }

    /// <summary>
    /// A card's position, size, and data are mutable and drive the UI
    /// through data binding, so INotifyPropertyChanged is required on all
    /// of them, without it neither dragging nor a background refresh would
    /// show up visually.
    /// </summary>
    public class ScheduleCardViewModel : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public ElementId ScheduleId { get; set; }
        public ElementId CategoryId { get; set; }

        private DataView _dataView;
        public DataView DataView
        {
            get => _dataView;
            set { _dataView = value; OnPropertyChanged(nameof(DataView)); }
        }

        private double _x;
        public double X
        {
            get => _x;
            set { _x = value; OnPropertyChanged(nameof(X)); }
        }

        private double _y;
        public double Y
        {
            get => _y;
            set { _y = value; OnPropertyChanged(nameof(Y)); }
        }

        private double _width = 342;
        public double Width
        {
            get => _width;
            set { _width = value; OnPropertyChanged(nameof(Width)); }
        }

        private double _height = 323;
        public double Height
        {
            get => _height;
            set { _height = value; OnPropertyChanged(nameof(Height)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
