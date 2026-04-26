using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace PrimeDictate;

internal partial class TranscriptionOverlayWindow : Window
{
    private const double EdgeMargin = 18;
    private const int MaxDisplayedTranscriptChars = 900;

    private DictationOverlayPlacement placement = DictationOverlayPlacement.LowerRight;

    public TranscriptionOverlayWindow()
    {
        InitializeComponent();
        this.SourceInitialized += this.OnSourceInitialized;
    }

    public void SetPlacement(DictationOverlayPlacement overlayPlacement)
    {
        this.placement = overlayPlacement;
        this.Reposition();
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

        this.UpdateLayout();
        this.Reposition();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        this.Reposition();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle);
        var newStyle = new IntPtr(style.ToInt64() |
            NativeMethods.WsExNoActivate |
            NativeMethods.WsExTransparent |
            NativeMethods.WsExToolWindow);
        _ = NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, newStyle);
    }

    private void Reposition()
    {
        if (!this.IsLoaded)
        {
            return;
        }

        var area = SystemParameters.WorkArea;
        var targetWidth = this.ActualWidth > 0 ? this.ActualWidth : this.Width;
        var targetHeight = this.ActualHeight > 0 ? this.ActualHeight : this.Height;
        if (double.IsNaN(targetWidth) || targetWidth <= 0)
        {
            targetWidth = 430;
        }

        if (double.IsNaN(targetHeight) || targetHeight <= 0)
        {
            targetHeight = 140;
        }

        this.Left = this.placement is DictationOverlayPlacement.LowerLeft or DictationOverlayPlacement.UpperLeft
            ? area.Left + EdgeMargin
            : area.Right - targetWidth - EdgeMargin;
        this.Top = this.placement is DictationOverlayPlacement.UpperLeft or DictationOverlayPlacement.UpperRight
            ? area.Top + EdgeMargin
            : area.Bottom - targetHeight - EdgeMargin;
    }
}
