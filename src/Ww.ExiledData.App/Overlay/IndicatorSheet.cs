using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ww.ExiledData.App.Services;

namespace Ww.ExiledData.App.Overlay;

/// <summary>
/// Every capture outcome as a still <see cref="CaptureIndicator"/>, at its real size, over a stand-in for game imagery
/// (light and dark patches, since the overlay is not themed). For <c>--screenshot</c> (docs/design-ui/screens).
/// </summary>
public sealed class IndicatorSheet : Window
{
    private const double IndicatorSize = 39; // OverlayWindow.SizeDip

    private static readonly (Feedback Feedback, string Label)[] Samples =
    [
        (new(FeedbackKind.Added, ""), "item added"),
        (new(FeedbackKind.SiblingAdded, ""), "same text twice"),
        (new(FeedbackKind.Covered, ""), "item's next cell"),
        (new(FeedbackKind.StackableUpdated, "", 31), "stackable count"),
        (new(FeedbackKind.StackableAdded, "", 12_345), "big stack"),
        (new(FeedbackKind.AlreadyRecorded, ""), "already recorded"),
        (new(FeedbackKind.Added, "", OutsideGrid: true), "outside the grid"),
        (new(FeedbackKind.Nothing, ""), "nothing captured"),
        (new(FeedbackKind.Info, ""), "nothing to do"),
        (new(FeedbackKind.NotInGame, ""), "not in the game"),
        (new(FeedbackKind.Undo, ""), "undo"),
        (new(FeedbackKind.Redo, ""), "redo"),
        (new(FeedbackKind.NewSession, ""), "new session"),
        (new(FeedbackKind.RunStarted, ""), "run started"),
        (new(FeedbackKind.RunEnded, ""), "run ended"),
        (new(FeedbackKind.SweepStarted, ""), "sweep started"),
        (new(FeedbackKind.CellSkipped, ""), "cell skipped"),
        (new(FeedbackKind.SweepEnded, "", 26), "sweep finished"),
        (new(FeedbackKind.SweepCanceled, ""), "sweep canceled"),
    ];

    // Stand-ins for a dark dungeon and a bright outdoor scene.
    private static readonly IBrush[] Scenes =
    [
        new SolidColorBrush(Color.FromRgb(0x2A, 0x26, 0x22)),
        new SolidColorBrush(Color.FromRgb(0xC9, 0xB9, 0x96)),
    ];

    public IndicatorSheet()
    {
        Title = "Capture indicator";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        var rows = new StackPanel();
        foreach (var scene in Scenes)
        {
            var row = new WrapPanel { Background = scene, MaxWidth = 9 * 92 };
            foreach (var (feedback, label) in Samples)
            {
                var indicator = new CaptureIndicator { Width = IndicatorSize, Height = IndicatorSize };
                indicator.ShowStill(feedback);
                row.Children.Add(new StackPanel
                {
                    Width = 92,
                    Margin = new Thickness(0, 12),
                    Spacing = 6,
                    Children =
                    {
                        indicator,
                        new TextBlock
                        {
                            Text = label,
                            FontSize = 11,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Foreground = scene == Scenes[0] ? Brushes.White : Brushes.Black,
                        },
                    },
                });
            }
            rows.Children.Add(row);
        }
        Content = rows;
    }
}
