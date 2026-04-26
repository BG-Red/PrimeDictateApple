using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MediaColor = System.Windows.Media.Color;

namespace PrimeDictate;

internal partial class TranscriptionOverlayWindow : Window
{
    private const int MaxDisplayedTranscriptChars = 900;
    private const int WaveformBarCount = 60;
    private readonly Random random = new Random();
    private readonly System.Windows.Shapes.Rectangle[] leftBars = new System.Windows.Shapes.Rectangle[WaveformBarCount];
    private readonly System.Windows.Shapes.Rectangle[] rightBars = new System.Windows.Shapes.Rectangle[WaveformBarCount];
    private readonly double[] barTargets = new double[WaveformBarCount];
    private readonly double[] barCurrents = new double[WaveformBarCount];
    private DispatcherTimer timer;
    private DispatcherTimer visualizerTimer;
    private DateTime startTime;
    private double currentSmoothedRms;
    private double targetRms;

    public TranscriptionOverlayWindow()
    {
        InitializeComponent();
        this.SourceInitialized += this.OnSourceInitialized;
        
        this.timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        this.timer.Tick += this.OnTimerTick;

        this.visualizerTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        this.visualizerTimer.Tick += this.OnVisualizerTick;

        this.InitializeWaveform();
    }

    private void InitializeWaveform()
    {
        var gradientRight = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 0)
        };
        gradientRight.GradientStops.Add(new GradientStop(MediaColor.FromRgb(0, 122, 204), 0.0));
        gradientRight.GradientStops.Add(new GradientStop(MediaColor.FromRgb(163, 62, 255), 1.0));
        
        var gradientLeft = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(1, 0),
            EndPoint = new System.Windows.Point(0, 0)
        };
        gradientLeft.GradientStops.Add(new GradientStop(MediaColor.FromRgb(0, 122, 204), 0.0));
        gradientLeft.GradientStops.Add(new GradientStop(MediaColor.FromRgb(163, 62, 255), 1.0));

        for (int i = 0; i < WaveformBarCount; i++)
        {
            double opacity = 1.0 - ((double)i / WaveformBarCount);
            
            var leftBar = new System.Windows.Shapes.Rectangle
            {
                Width = 3,
                Height = 2,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = gradientLeft,
                Margin = new Thickness(0, 0, 4, 0),
                Opacity = opacity,
                VerticalAlignment = VerticalAlignment.Center
            };
            this.leftBars[i] = leftBar;
            this.LeftWaveform.Children.Add(leftBar);

            var rightBar = new System.Windows.Shapes.Rectangle
            {
                Width = 3,
                Height = 2,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = gradientRight,
                Margin = new Thickness(0, 0, 4, 0),
                Opacity = opacity,
                VerticalAlignment = VerticalAlignment.Center
            };
            this.rightBars[i] = rightBar;
            this.RightWaveform.Children.Add(rightBar);
        }
    }

    public void SetSticky(bool isSticky)
    {
        this.PinButton.IsChecked = isSticky;
    }

    public void SetReadyState()
    {
        this.HeaderText.Text = "Ready";
        this.StateDot.Fill = new SolidColorBrush(MediaColor.FromRgb(32, 164, 112));
        this.TranscriptText.Text = "Waiting for hotkey...";
        this.TimerText.Text = "00:00";
        this.timer.Stop();
        this.visualizerTimer.Stop();
        this.UpdateAudioLevel(0);
        this.OnVisualizerTick(null, EventArgs.Empty);
    }

    public void UpdateAudioLevel(double rms)
    {
        this.targetRms = rms;
    }

    private void OnVisualizerTick(object? sender, EventArgs e)
    {
        // Decay target so it naturally zeroes out
        this.targetRms *= 0.8;
        
        // Smooth out the animation
        this.currentSmoothedRms = (this.currentSmoothedRms * 0.7) + (this.targetRms * 0.3);
        
        // Base scales when silent
        double minScale = 1.0;
        double targetScale1 = minScale + (this.currentSmoothedRms * 4.0);
        double targetScale2 = minScale + (this.currentSmoothedRms * 8.0);
        
        double targetOpacity1 = this.currentSmoothedRms > 0.01 ? 0.6 : 0.0;
        double targetOpacity2 = this.currentSmoothedRms > 0.05 ? 0.4 : 0.0;

        this.Ring1Scale.ScaleX = targetScale1;
        this.Ring1Scale.ScaleY = targetScale1;
        this.Ring1.Opacity = targetOpacity1;

        this.Ring2Scale.ScaleX = targetScale2;
        this.Ring2Scale.ScaleY = targetScale2;
        this.Ring2.Opacity = targetOpacity2;

        this.UpdateWaveformBounds();
    }

    private void UpdateWaveformBounds()
    {
        for (int i = WaveformBarCount - 1; i > 0; i--)
        {
            this.barTargets[i] = this.barTargets[i - 1];
        }

        double dbIntensity = this.currentSmoothedRms * 350.0;
        this.barTargets[0] = 3.0 + (dbIntensity * (0.6 + (this.random.NextDouble() * 0.8)));

        for (int i = 0; i < WaveformBarCount; i++)
        {
            double fadeDist = 1.0 - ((double)i / WaveformBarCount);
            double maxDistHeight = 80.0 * fadeDist;
            
            this.barCurrents[i] = (this.barCurrents[i] * 0.7) + (this.barTargets[i] * 0.3);
            
            double finalHeight = this.barCurrents[i] * fadeDist;
            if (finalHeight > maxDistHeight) finalHeight = maxDistHeight;
            if (finalHeight < 3.0) finalHeight = 3.0;

            this.leftBars[i].Height = finalHeight;
            this.rightBars[i].Height = finalHeight;
        }
    }

    public void UpdateTranscript(string transcript, bool isProcessing)
    {
        this.HeaderText.Text = isProcessing ? "Processing transcript" : "Listening";
        this.StateDot.Fill = isProcessing
            ? new SolidColorBrush(MediaColor.FromRgb(32, 164, 112))
            : new SolidColorBrush(MediaColor.FromRgb(220, 53, 69));
        var displayTranscript = transcript.Trim();
        if (displayTranscript.Length > MaxDisplayedTranscriptChars)
        {
            displayTranscript = "..." + displayTranscript[^MaxDisplayedTranscriptChars..];
        }

        this.TranscriptText.Text = string.IsNullOrWhiteSpace(displayTranscript)
            ? "Listening..."
            : displayTranscript;
            
        if (!isProcessing && !this.timer.IsEnabled)
        {
            this.startTime = DateTime.Now;
            this.timer.Start();
            this.visualizerTimer.Start();
        }
        else if (isProcessing && this.timer.IsEnabled)
        {
            this.timer.Stop();
            this.visualizerTimer.Stop();
            UpdateAudioLevel(0); // Zero out visualizer
            this.OnVisualizerTick(null, EventArgs.Empty);
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle);
        // Excluded: NativeMethods.WsExTransparent -> to allow hit testing (mouse clicks/drag)
        var newStyle = new IntPtr(style.ToInt64() |
            NativeMethods.WsExNoActivate |
            NativeMethods.WsExToolWindow);
        _ = NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, newStyle);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.Now - this.startTime;
        this.TimerText.Text = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
    }

    private void OnBorderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        this.DragMove();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        bool isPinned = this.PinButton.IsChecked == true;
        if (System.Windows.Application.Current is App app)
        {
            app.SaveStickyState(isPinned);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.ShowSettings();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        this.Hide();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(this.TranscriptText.Text);
        }
        catch
        {
            // Ignore clipboard errors
        }
    }
}
