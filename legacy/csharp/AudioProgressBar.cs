using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace MusicPower3
{
    /// <summary>
    /// Lightweight Windows 11 style slider / progress bar powered by a high-performance C++ native engine (NativeAudioProgress.dll).
    /// </summary>
    public sealed class AudioProgressBar : UserControl
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ProgressBarLayoutResult
        {
            public double TrackThickness;
            public double TrackCornerRadius;
            public double FillLength;
            public double ThumbOffsetX;
            public double ThumbOffsetY;
            public double ThumbCoreSize;
            public double ThumbOuterSize;
            public double ThumbOpacity;
            [MarshalAs(UnmanagedType.I1)]
            public bool IsThumbVisible;
        }

        [DllImport("NativeAudioProgress.dll", CallingConvention = CallingConvention.Cdecl)]
        [SuppressGCTransition]
        private static extern void NativeProgressBar_CalculateLayout(
            double value,
            double minimum,
            double maximum,
            double availableWidth,
            double availableHeight,
            int orientation,
            [MarshalAs(UnmanagedType.I1)] bool isHovered,
            [MarshalAs(UnmanagedType.I1)] bool isScrubbing,
            [MarshalAs(UnmanagedType.I1)] bool isPressed,
            [MarshalAs(UnmanagedType.I1)] bool isThumbAlwaysVisible,
            out ProgressBarLayoutResult outResult
        );

        [DllImport("NativeAudioProgress.dll", CallingConvention = CallingConvention.Cdecl)]
        [SuppressGCTransition]
        private static extern double NativeProgressBar_CalculateValueFromPointer(
            double pointerX,
            double pointerY,
            double availableWidth,
            double availableHeight,
            int orientation,
            double minimum,
            double maximum,
            double stepFrequency
        );

        // Win11 slider metrics: 4 px track that grows on hover, 20 px thumb with an accent core.
        private const double RestThickness = 4.0;
        private const double HoverThickness = 6.0;
        private const double ThumbSize = 20.0;
        private const double ThumbCoreRest = 12.0;
        private const double ThumbCoreHover = 14.0;
        private const double ThumbCorePressed = 10.0;

        private readonly Grid _rootGrid;
        private readonly Border _trackBorder;
        private readonly Border _fillBorder;
        private readonly Grid _thumbContainer;
        private readonly Ellipse _thumbOuter;
        private readonly Ellipse _thumbCore;
        private readonly TranslateTransform _thumbTransform;

        private bool _isScrubbing = false;
        private bool _isHovered = false;

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
            if (d is not AudioProgressBar bar) return;
            if (!bar._isScrubbing) bar.UpdateVisuals();

            if (e.Property == ValueProperty && AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged)
                && FrameworkElementAutomationPeer.FromElement(bar) is AudioProgressBarAutomationPeer peer)
            {
                peer.RaiseValueChanged((double)e.OldValue, (double)e.NewValue);
            }
        }

        private static void OnOrientationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AudioProgressBar bar) bar.UpdateOrientationLayout();
        }

        private static void OnThumbVisibilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AudioProgressBar bar) bar.UpdateInteractionState();
        }

        private static void OnAccentColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AudioProgressBar bar && e.NewValue is Color col)
            {
                var solid = new SolidColorBrush(Color.FromArgb(255, col.R, col.G, col.B));
                bar._fillBorder.Background = solid;
                bar._thumbCore.Fill = solid;
            }
        }

        #endregion

        public AudioProgressBar()
        {
            this.IsHitTestVisible = true;
            this.IsTabStop = true;
            this.UseSystemFocusVisuals = true;

            _rootGrid = new Grid { Background = new SolidColorBrush(Colors.Transparent) };

            _trackBorder = new Border { CornerRadius = new CornerRadius(RestThickness / 2) };

            Color initialAccent = Color.FromArgb(255, AccentColor.R, AccentColor.G, AccentColor.B);
            _fillBorder = new Border
            {
                CornerRadius = new CornerRadius(RestThickness / 2),
                Background = new SolidColorBrush(initialAccent)
            };

            _thumbTransform = new TranslateTransform();
            _thumbOuter = new Ellipse { Width = ThumbSize, Height = ThumbSize, StrokeThickness = 1 };
            _thumbCore = new Ellipse
            {
                Width = ThumbCoreRest,
                Height = ThumbCoreRest,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(initialAccent)
            };

            _thumbContainer = new Grid
            {
                Width = ThumbSize,
                Height = ThumbSize,
                RenderTransform = _thumbTransform,
                Opacity = 0.0,
                IsHitTestVisible = false
            };
            _thumbContainer.Children.Add(_thumbOuter);
            _thumbContainer.Children.Add(_thumbCore);

            _rootGrid.Children.Add(_trackBorder);
            _rootGrid.Children.Add(_fillBorder);
            _rootGrid.Children.Add(_thumbContainer);

            this.Content = _rootGrid;

            ApplyThemeColors();
            UpdateOrientationLayout();

            this.SizeChanged += (s, e) => UpdateVisuals();
            this.ActualThemeChanged += (s, e) => ApplyThemeColors();
            this.PointerEntered += OnPointerEntered;
            this.PointerExited += OnPointerExited;
            this.PointerPressed += OnPointerPressed;
            this.PointerMoved += OnPointerMoved;
            this.PointerReleased += OnPointerReleased;
            this.PointerCaptureLost += OnPointerCaptureLost;
        }

        // Neutral track/thumb colours that follow the system light/dark theme (values match the Windows 11 slider).
        private void ApplyThemeColors()
        {
            bool light = ActualTheme == ElementTheme.Light;
            _trackBorder.Background = new SolidColorBrush(light ? Color.FromArgb(0x72, 0x00, 0x00, 0x00) : Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF));
            _thumbOuter.Fill = new SolidColorBrush(light ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x45, 0x45, 0x45));
            _thumbOuter.Stroke = new SolidColorBrush(light ? Color.FromArgb(0x29, 0x00, 0x00, 0x00) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        }

        private void UpdateOrientationLayout()
        {
            if (Orientation == Orientation.Horizontal)
            {
                this.Height = ThumbSize + 4; this.Width = double.NaN;
                _rootGrid.Height = ThumbSize + 4; _rootGrid.Width = double.NaN;

                _trackBorder.HorizontalAlignment = HorizontalAlignment.Stretch; _trackBorder.VerticalAlignment = VerticalAlignment.Center;
                _fillBorder.HorizontalAlignment = HorizontalAlignment.Left; _fillBorder.VerticalAlignment = VerticalAlignment.Center;
                _thumbContainer.HorizontalAlignment = HorizontalAlignment.Left; _thumbContainer.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                this.Width = ThumbSize + 4; this.Height = double.NaN;
                _rootGrid.Width = ThumbSize + 4; _rootGrid.Height = double.NaN;

                _trackBorder.HorizontalAlignment = HorizontalAlignment.Center; _trackBorder.VerticalAlignment = VerticalAlignment.Stretch;
                _fillBorder.HorizontalAlignment = HorizontalAlignment.Center; _fillBorder.VerticalAlignment = VerticalAlignment.Bottom;
                _thumbContainer.HorizontalAlignment = HorizontalAlignment.Center; _thumbContainer.VerticalAlignment = VerticalAlignment.Top;
            }
            UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 && height <= 0) return;

            try
            {
                NativeProgressBar_CalculateLayout(
                    Value,
                    Minimum,
                    Maximum,
                    width,
                    height,
                    Orientation == Orientation.Horizontal ? 0 : 1,
                    _isHovered,
                    _isScrubbing,
                    _isScrubbing,
                    IsThumbAlwaysVisible,
                    out ProgressBarLayoutResult layout
                );

                var radius = new CornerRadius(layout.TrackCornerRadius);
                _trackBorder.CornerRadius = radius;
                _fillBorder.CornerRadius = radius;

                if (Orientation == Orientation.Horizontal)
                {
                    _trackBorder.Height = layout.TrackThickness;
                    _fillBorder.Height = layout.TrackThickness;
                    _fillBorder.Width = layout.FillLength;
                }
                else
                {
                    _trackBorder.Width = layout.TrackThickness;
                    _fillBorder.Width = layout.TrackThickness;
                    _fillBorder.Height = layout.FillLength;
                }

                _thumbTransform.X = layout.ThumbOffsetX;
                _thumbTransform.Y = layout.ThumbOffsetY;
                _thumbContainer.Opacity = layout.ThumbOpacity;

                _thumbCore.Width = layout.ThumbCoreSize;
                _thumbCore.Height = layout.ThumbCoreSize;
                _thumbOuter.Width = layout.ThumbOuterSize;
                _thumbOuter.Height = layout.ThumbOuterSize;
            }
            catch
            {
                UpdateVisualsManagedFallback();
            }
        }

        private void UpdateVisualsManagedFallback()
        {
            double range = Maximum - Minimum;
            if (range <= 0) return;
            double percentage = Math.Clamp((Value - Minimum) / range, 0.0, 1.0);
            double half = ThumbSize / 2;
            bool active = _isHovered || _isScrubbing;
            double thickness = active ? HoverThickness : RestThickness;
            var radius = new CornerRadius(thickness / 2);

            _trackBorder.CornerRadius = radius;
            _fillBorder.CornerRadius = radius;

            if (Orientation == Orientation.Horizontal)
            {
                double width = ActualWidth;
                if (width <= 0) return;
                _trackBorder.Height = thickness;
                _fillBorder.Height = thickness;
                _fillBorder.Width = percentage * width;
                _thumbTransform.X = Math.Clamp((percentage * width) - half, -half, Math.Max(-half, width - half));
                _thumbTransform.Y = 0;
            }
            else
            {
                double height = ActualHeight;
                if (height <= 0) return;
                _trackBorder.Width = thickness;
                _fillBorder.Width = thickness;
                _fillBorder.Height = percentage * height;
                double thumbY = height - (percentage * height) - half;
                _thumbTransform.Y = Math.Clamp(thumbY, -half, Math.Max(-half, height - half));
                _thumbTransform.X = 0;
            }

            double core = _isScrubbing ? ThumbCorePressed : (_isHovered ? ThumbCoreHover : ThumbCoreRest);
            _thumbCore.Width = core;
            _thumbCore.Height = core;
            _thumbContainer.Opacity = (IsThumbAlwaysVisible || _isHovered || _isScrubbing) ? 1.0 : 0.0;
        }

        private void UpdateInteractionState() => UpdateVisuals();

        // Sets a new value and raises ValueChanged once. Returns false when nothing changed.
        private bool CommitValue(double rawValue)
        {
            double range = Maximum - Minimum;
            if (range <= 0) return false;

            if (StepFrequency > 0)
                rawValue = Math.Round((rawValue - Minimum) / StepFrequency) * StepFrequency + Minimum;

            double newValue = Math.Clamp(rawValue, Minimum, Maximum);
            if (Math.Abs(newValue - Value) < 1e-9) return false;

            Value = newValue;
            UpdateVisuals();
            ValueChanged?.Invoke(this, newValue);
            return true;
        }

        private void UpdateValueFromPointer(PointerRoutedEventArgs e)
        {
            Point pos = e.GetCurrentPoint(this).Position;
            double val;
            try
            {
                val = NativeProgressBar_CalculateValueFromPointer(
                    pos.X,
                    pos.Y,
                    ActualWidth,
                    ActualHeight,
                    Orientation == Orientation.Horizontal ? 0 : 1,
                    Minimum,
                    Maximum,
                    StepFrequency
                );
            }
            catch
            {
                double range = Maximum - Minimum;
                if (range <= 0) return;
                double percentage = (Orientation == Orientation.Horizontal)
                    ? (ActualWidth > 0 ? Math.Clamp(pos.X / ActualWidth, 0.0, 1.0) : 0.0)
                    : (ActualHeight > 0 ? Math.Clamp(1.0 - (pos.Y / ActualHeight), 0.0, 1.0) : 0.0);
                val = Minimum + (percentage * range);
                if (StepFrequency > 0)
                    val = Math.Round((val - Minimum) / StepFrequency) * StepFrequency + Minimum;
            }

            CommitValue(val);
        }

        internal void SetValueFromAutomation(double value) => CommitValue(value);

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isHovered = true;
            UpdateVisuals();
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isHovered = false;
            UpdateVisuals();
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            _isScrubbing = true;
            this.CapturePointer(e.Pointer);
            UpdateVisuals();
            ScrubbingStarted?.Invoke(this, Value);
            UpdateValueFromPointer(e);
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isScrubbing) UpdateValueFromPointer(e);
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isScrubbing) return;

            UpdateValueFromPointer(e);
            EndScrubbing();
            this.ReleasePointerCapture(e.Pointer);
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_isScrubbing) EndScrubbing();
        }

        private void EndScrubbing()
        {
            _isScrubbing = false;
            UpdateVisuals();
            ScrubbingEnded?.Invoke(this, Value);
        }

        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            double range = Maximum - Minimum;
            double step = StepFrequency > 0 ? StepFrequency : range * 0.01;
            double? target = e.Key switch
            {
                VirtualKey.Right or VirtualKey.Up => Value + step,
                VirtualKey.Left or VirtualKey.Down => Value - step,
                VirtualKey.PageUp => Value + step * 10,
                VirtualKey.PageDown => Value - step * 10,
                VirtualKey.Home => Minimum,
                VirtualKey.End => Maximum,
                _ => null
            };

            if (target == null) { base.OnKeyDown(e); return; }

            e.Handled = true;
            if (CommitValue(target.Value)) ScrubbingEnded?.Invoke(this, Value);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new AudioProgressBarAutomationPeer(this);

        /// <summary>Exposes the control to screen readers and UI Automation as a range/slider.</summary>
        public sealed partial class AudioProgressBarAutomationPeer : FrameworkElementAutomationPeer, IRangeValueProvider
        {
            private readonly AudioProgressBar _owner;

            public AudioProgressBarAutomationPeer(AudioProgressBar owner) : base(owner) { _owner = owner; }

            protected override string GetClassNameCore() => nameof(AudioProgressBar);
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

            protected override object GetPatternCore(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);

            public bool IsReadOnly => false;
            public double LargeChange => (_owner.Maximum - _owner.Minimum) * 0.1;
            public double SmallChange => _owner.StepFrequency > 0 ? _owner.StepFrequency : (_owner.Maximum - _owner.Minimum) * 0.01;
            public double Maximum => _owner.Maximum;
            public double Minimum => _owner.Minimum;
            public double Value => _owner.Value;

            public void SetValue(double value) => _owner.SetValueFromAutomation(value);

            internal void RaiseValueChanged(double oldValue, double newValue) =>
                RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, newValue);
        }
    }
}