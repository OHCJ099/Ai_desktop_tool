using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Automation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AI_desktop_tool
{
    public partial class OcrOverlayWindow : Window
    {
        private Point _startPoint;
        private bool _isDragging;
        private int _physicalWidth;
        private int _physicalHeight;
        private double _scaleX = 1.0;
        private double _scaleY = 1.0;

        public string UiaExtractedText { get; private set; }
        public Rect SelectedRect { get; private set; }

        public OcrOverlayWindow()
        {
            InitializeComponent();
            UiaExtractedText = "";
            SelectedRect = Rect.Empty;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Get DPI scale factor using WPF native composition target
            _scaleX = 1.0;
            _scaleY = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                _scaleX = source.CompositionTarget.TransformToDevice.M11;
                _scaleY = source.CompositionTarget.TransformToDevice.M22;
            }

            // Calculate physical screen resolution
            double logicalWidth = SystemParameters.PrimaryScreenWidth;
            double logicalHeight = SystemParameters.PrimaryScreenHeight;
            _physicalWidth = (int)(logicalWidth * _scaleX);
            _physicalHeight = (int)(logicalHeight * _scaleY);
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _startPoint = e.GetPosition(this);
                _isDragging = true;
                SelectionRect.Visibility = Visibility.Visible;
                Canvas.SetLeft(SelectionRect, _startPoint.X);
                Canvas.SetTop(SelectionRect, _startPoint.Y);
                SelectionRect.Width = 0;
                SelectionRect.Height = 0;
            }
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                Point currentPoint = e.GetPosition(this);

                double x = Math.Min(_startPoint.X, currentPoint.X);
                double y = Math.Min(_startPoint.Y, currentPoint.Y);
                double width = Math.Abs(_startPoint.X - currentPoint.X);
                double height = Math.Abs(_startPoint.Y - currentPoint.Y);

                // Update red selection rect
                Canvas.SetLeft(SelectionRect, x);
                Canvas.SetTop(SelectionRect, y);
                SelectionRect.Width = width;
                SelectionRect.Height = height;

                // Update clipping mask of MaskGrid (hollow out the selected area)
                double screenWidth = SystemParameters.PrimaryScreenWidth;
                double screenHeight = SystemParameters.PrimaryScreenHeight;

                var outerGeometry = new RectangleGeometry(new Rect(0, 0, screenWidth, screenHeight));
                var innerGeometry = new RectangleGeometry(new Rect(x, y, width, height));
                
                var maskGeometry = new CombinedGeometry(GeometryCombineMode.Exclude, outerGeometry, innerGeometry);
                MaskGrid.Clip = maskGeometry;
            }
        }

        private void Window_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && _isDragging)
            {
                _isDragging = false;
                SelectionRect.Visibility = Visibility.Collapsed;
                MaskGrid.Clip = null; // Clear clip

                Point endPoint = e.GetPosition(this);
                double logicalX = Math.Min(_startPoint.X, endPoint.X);
                double logicalY = Math.Min(_startPoint.Y, endPoint.Y);
                double logicalW = Math.Abs(_startPoint.X - endPoint.X);
                double logicalH = Math.Abs(_startPoint.Y - endPoint.Y);

                SelectedRect = new Rect(logicalX, logicalY, logicalW, logicalH);

                CloseWindow();
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }

        private void CloseWindow()
        {
            try
            {
                DialogResult = true;
            }
            catch (InvalidOperationException)
            {
                Close();
            }
        }

        public string ExtractUiaText()
        {
            double logicalW = SelectedRect.Width;
            double logicalH = SelectedRect.Height;
            if (logicalW < 5 && logicalH < 5)
            {
                return ExtractTextFromPoint(new Point(SelectedRect.X, SelectedRect.Y));
            }
            else
            {
                return ExtractTextFromRect(SelectedRect.X, SelectedRect.Y, logicalW, logicalH);
            }
        }

        private string ExtractTextFromPoint(Point pt)
        {
            try
            {
                var point = new System.Windows.Point(pt.X * _scaleX, pt.Y * _scaleY);
                var element = AutomationElement.FromPoint(point);
                if (element == null) return "";

                object pattern;
                // 1. Try TextPattern
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    var textPattern = (TextPattern)pattern;
                    string text = textPattern.DocumentRange.GetText(-1);
                    if (!string.IsNullOrEmpty(text)) return text.Trim();
                }

                // 2. Try ValuePattern
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                {
                    var valuePattern = (ValuePattern)pattern;
                    string val = valuePattern.Current.Value;
                    if (!string.IsNullOrEmpty(val)) return val.Trim();
                }

                // 3. Try Name fallback
                return element.Current.Name ?? "";
            }
            catch
            {
                return "";
            }
        }

        private string ExtractTextFromRect(double x, double y, double w, double h)
        {
            try
            {
                double physX = x * _scaleX;
                double physY = y * _scaleY;
                double physW = w * _scaleX;
                double physH = h * _scaleY;

                var selectionRect = new Rect(physX, physY, physW, physH);
                var centerPoint = new Point(physX + physW / 2, physY + physH / 2);
                
                AutomationElement searchRoot = null;
                try
                {
                    // Try center point first
                    try
                    {
                        searchRoot = AutomationElement.FromPoint(centerPoint);
                    }
                    catch { }

                    // If center fails, try top-left (slightly offset)
                    if (searchRoot == null)
                    {
                        try
                        {
                            searchRoot = AutomationElement.FromPoint(new Point(physX + 2, physY + 2));
                        }
                        catch { }
                    }

                    if (searchRoot != null)
                    {
                        var walker = TreeWalker.ControlViewWalker;
                        var current = searchRoot;
                        while (current != null && current != AutomationElement.RootElement)
                        {
                            var bounds = current.Current.BoundingRectangle;
                            if (!bounds.IsEmpty && bounds.Contains(selectionRect))
                            {
                                searchRoot = current;
                                break;
                            }
                            current = walker.GetParent(current);
                        }
                    }
                }
                catch { }

                // Fallback: If we couldn't find a proper element containing the selection rect,
                // find which top-level window intersects with selectionRect, to avoid searching the entire desktop.
                if (searchRoot == null || searchRoot == AutomationElement.RootElement)
                {
                    try
                    {
                        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
                        foreach (AutomationElement win in windows)
                        {
                            var bounds = win.Current.BoundingRectangle;
                            if (!bounds.IsEmpty && bounds.IntersectsWith(selectionRect))
                            {
                                searchRoot = win;
                                break;
                            }
                        }
                    }
                    catch { }
                }

                if (searchRoot == null)
                {
                    searchRoot = AutomationElement.RootElement;
                }

                MainWindow.LogDebug($"ExtractTextFromRect: Resolved searchRoot ControlType={searchRoot.Current.ControlType.ProgrammaticName}, Name={searchRoot.Current.Name}");

                var elements = searchRoot.FindAll(TreeScope.Subtree, System.Windows.Automation.Condition.TrueCondition);
                var sb = new System.Text.StringBuilder();
                var seenTexts = new System.Collections.Generic.HashSet<string>();

                MainWindow.LogDebug($"ExtractTextFromRect: Found {elements.Count} elements in subtree.");

                foreach (AutomationElement element in elements)
                {
                    try
                    {
                        var bounds = element.Current.BoundingRectangle;
                        if (!bounds.IsEmpty && bounds.IntersectsWith(selectionRect))
                        {
                            string text = "";
                            if (element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern))
                            {
                                text = ((TextPattern)pattern).DocumentRange.GetText(-1);
                            }
                            else if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                            {
                                text = ((ValuePattern)pattern).Current.Value;
                            }
                            else
                            {
                                text = element.Current.Name;
                            }

                            if (!string.IsNullOrEmpty(text))
                            {
                                text = text.Trim();
                                if (!seenTexts.Contains(text))
                                {
                                    seenTexts.Add(text);
                                    sb.AppendLine(text);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore
                    }
                }
                
                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                MainWindow.LogDebug($"ExtractTextFromRect exception: {ex.Message}");
                return "";
            }
        }
    }
}
