using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;

namespace MusicPower3
{
    public sealed class AudioProgressBar : UserControl
    {
        private readonly Grid _rootGrid;
        private readonly Border _trackBorder;
        private readonly Border _fillBorder;
        private readonly Grid _thumbContainer;
        private readonly TranslateTransform _thumbTransform;

        private bool _isScrubbing = false;

        public event EventHandler<double>? ValueChanged;
        public event EventHandler<double>? ScrubbingStarted;
        public event EventHandler<double>? ScrubbingEnded;

        #region Dependency Properties

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(AudioProgressBar), new PropertyMetadata(0.0, OnValuePropertyChanged));

        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(AudioProgressBar), new PropertyMetadata(0.0, OnValuePropertyChanged));

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(AudioProgressBar), new PropertyMetadata(100.0, OnValuePropertyChanged));

        public static readonly DependencyProperty StepFrequencyProperty =
            DependencyProperty.Register(nameof(StepFrequency), typeof(double), typeof(AudioProgressBar), new PropertyMetadata(0.0));

        public static readonly DependencyProperty OrientationProperty =
            DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(AudioProgressBar), new PropertyMetadata(Orientation.Horizontal, OnOrientationChanged));

        public static readonly DependencyProperty IsThumbAlwaysVisibleProperty =
            DependencyProperty.Register(nameof(IsThumbAlwaysVisible), typeof(bool), typeof(AudioProgressBar), new PropertyMetadata(false, OnThumbVisibilityChanged));

        public static readonly DependencyProperty AccentColorProperty =
            DependencyProperty.Register(nameof(AccentColor), typeof(Color), typeof(AudioProgressBar), new PropertyMetadata(Color.FromArgb(255, 0, 120, 212), OnAccentColorChanged));

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, Math.Clamp(value, Minimum, Maximum));
        }

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, Math.Max(Minimum + 0.0001, value));
        }

        public double StepFrequency
        {
            get => (double)GetValue(StepFrequencyProperty);
            set => SetValue(StepFrequencyProperty, Math.Max(0.0, value));
        }

        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty);
            set => SetValue(OrientationProperty, value);
        }

        public bool IsThumbAlwaysVisible
        {
            get => (bool)GetValue(IsThumbAlwaysVisibleProperty);
            set => SetValue(IsThumbAlwaysVisibleProperty, value);
        }

        public Color AccentColor
        {
            get => (Color)GetValue(AccentColorProperty);
            set => SetValue(AccentColorProperty, value);
        }

        private static void OnValuePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AudioProgressBar bar && !bar._isScrubbing) bar.UpdateVisuals();
        }

        private static void OnOrientationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AudioProgressBar bar) bar.UpdateOrientationLayout();
        }

        private static void OnThumbVisibilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AudioProgressBar bar && e.NewValue is bool alwaysVisible)
            {
                bar._thumbContainer.Opacity = alwaysVisible ? 1.0 : 0.0;
            }
        }

        private static void OnAccentColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AudioProgressBar bar && e.NewValue is Color col)
            {
                Color solidCol = Color.FromArgb(255, col.R, col.G, col.B);
                bar._fillBorder.Background = new SolidColorBrush(solidCol);
            }
        }

        #endregion

        public AudioProgressBar()
        {
            this.IsHitTestVisible = true;

            _rootGrid = new Grid { Background = new SolidColorBrush(Colors.Transparent) };

            // CRISP TRACK VISIBILITY: 20% white fill with a 33% white border so it never looks invisible
            _trackBorder = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1)
            };

            Color initialAccent = Color.FromArgb(255, AccentColor.R, AccentColor.G, AccentColor.B);
            _fillBorder = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(initialAccent)
            };

            _thumbTransform = new TranslateTransform();
            _thumbContainer = new Grid
            {
                Width = 20,
                Height = 20,
                RenderTransform = _thumbTransform,
                Opacity = IsThumbAlwaysVisible ? 1.0 : 0.0,
                IsHitTestVisible = false
            };

            var thumbImage = new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/thumb.ico")),
                Stretch = Stretch.Uniform,
                Width = 20,
                Height = 20
            };
            _thumbContainer.Children.Add(thumbImage);

            _rootGrid.Children.Add(_trackBorder);
            _rootGrid.Children.Add(_fillBorder);
            _rootGrid.Children.Add(_thumbContainer);

            this.Content = _rootGrid;

            UpdateOrientationLayout();

            this.SizeChanged += (s, e) => UpdateVisuals();
            this.PointerEntered += OnPointerEntered;
            this.PointerExited += OnPointerExited;
            this.PointerPressed += OnPointerPressed;
            this.PointerMoved += OnPointerMoved;
            this.PointerReleased += OnPointerReleased;
        }

        private void UpdateOrientationLayout()
        {
            if (Orientation == Orientation.Horizontal)
            {
                this.Height = 24; this.Width = double.NaN;
                _rootGrid.Height = 24; _rootGrid.Width = double.NaN;
                
                _trackBorder.Height = 6; _trackBorder.Width = double.NaN;
                _trackBorder.HorizontalAlignment = HorizontalAlignment.Stretch; _trackBorder.VerticalAlignment = VerticalAlignment.Center;
                
                _fillBorder.Height = 6; _fillBorder.Width = 0;
                _fillBorder.HorizontalAlignment = HorizontalAlignment.Left; _fillBorder.VerticalAlignment = VerticalAlignment.Center;
                
                _thumbContainer.HorizontalAlignment = HorizontalAlignment.Left; _thumbContainer.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                this.Width = 24; this.Height = double.NaN;
                _rootGrid.Width = 24; _rootGrid.Height = double.NaN;
                
                _trackBorder.Width = 6; _trackBorder.Height = double.NaN;
                _trackBorder.HorizontalAlignment = HorizontalAlignment.Center; _trackBorder.VerticalAlignment = VerticalAlignment.Stretch;
                
                _fillBorder.Width = 6; _fillBorder.Height = 0;
                _fillBorder.HorizontalAlignment = HorizontalAlignment.Center; _fillBorder.VerticalAlignment = VerticalAlignment.Bottom;
                
                _thumbContainer.HorizontalAlignment = HorizontalAlignment.Center; _thumbContainer.VerticalAlignment = VerticalAlignment.Top;
            }
            UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            double range = Maximum - Minimum;
            if (range <= 0) return;
            double percentage = Math.Clamp((Value - Minimum) / range, 0.0, 1.0);

            // CRITICAL FIX: We strictly update only the active dimension (Width for Horizontal, Height for Vertical).
            // Setting the opposite dimension to double.NaN on an empty border caused WinUI 3 to collapse its size to 0px!
            if (Orientation == Orientation.Horizontal)
            {
                double width = ActualWidth;
                if (width <= 0) return;
                _fillBorder.Width = Math.Max(0, percentage * width);
                _thumbTransform.X = Math.Clamp((percentage * width) - 10.0, -10.0, Math.Max(-10.0, width - 10.0));
                _thumbTransform.Y = 0;
            }
            else
            {
                double height = ActualHeight;
                if (height <= 0) return;
                _fillBorder.Height = Math.Max(0, percentage * height);
                double thumbY = height - (percentage * height) - 10.0;
                _thumbTransform.Y = Math.Clamp(thumbY, -10.0, Math.Max(-10.0, height - 10.0));
                _thumbTransform.X = 0;
            }
        }

        private void UpdateValueFromPointer(PointerRoutedEventArgs e)
        {
            double range = Maximum - Minimum;
            if (range <= 0) return;

            Point pos = e.GetCurrentPoint(this).Position;
            double percentage = Orientation == Orientation.Horizontal
                ? Math.Clamp(pos.X / ActualWidth, 0.0, 1.0)
                : Math.Clamp(1.0 - (pos.Y / ActualHeight), 0.0, 1.0);

            double rawValue = Minimum + (percentage * range);
            if (StepFrequency > 0)
            {
                rawValue = Math.Round((rawValue - Minimum) / StepFrequency) * StepFrequency + Minimum;
            }

            Value = Math.Clamp(rawValue, Minimum, Maximum);
            UpdateVisuals();
            ValueChanged?.Invoke(this, Value);
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => _thumbContainer.Opacity = 1.0;

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isScrubbing && !IsThumbAlwaysVisible) _thumbContainer.Opacity = 0.0;
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isScrubbing = true;
            this.CapturePointer(e.Pointer);
            _thumbContainer.Opacity = 1.0;
            UpdateValueFromPointer(e);
            ScrubbingStarted?.Invoke(this, Value);
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isScrubbing) UpdateValueFromPointer(e);
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isScrubbing)
            {
                _isScrubbing = false;
                this.ReleasePointerCapture(e.Pointer);
                UpdateValueFromPointer(e);
                ScrubbingEnded?.Invoke(this, Value);

                Point pos = e.GetCurrentPoint(this).Position;
                if (!IsThumbAlwaysVisible && (pos.X < 0 || pos.X > ActualWidth || pos.Y < 0 || pos.Y > ActualHeight))
                {
                    _thumbContainer.Opacity = 0.0;
                }
            }
        }
    }
}