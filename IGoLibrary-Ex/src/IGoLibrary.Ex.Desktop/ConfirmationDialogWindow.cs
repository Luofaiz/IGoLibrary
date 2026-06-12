using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaApplication = Avalonia.Application;

namespace IGoLibrary.Ex.Desktop;

public sealed class ConfirmationDialogWindow : Window
{
    public ConfirmationDialogWindow(string title, string message, string confirmText, string cancelText)
    {
        Title = title;
        Width = 420;
        Height = 220;
        MinWidth = 360;
        MinHeight = 200;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ResolveBrush("AppErrorWindowBackgroundBrush", "#FFF7F8FA");

        var cancelButton = new Button
        {
            Content = cancelText,
            Classes = { "Secondary" },
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        cancelButton.Click += (_, _) => Close(false);

        var confirmButton = new Button
        {
            Content = confirmText,
            Classes = { "Danger" },
            MinWidth = 96,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        confirmButton.Click += (_, _) => Close(true);

        Content = new Border
        {
            Margin = new Thickness(12),
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(18),
            Background = ResolveBrush("AppErrorPanelBackgroundBrush", "#FFFFFFFF"),
            BoxShadow = BoxShadows.Parse("0 10 28 0 #160F172A"),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                RowSpacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 20,
                        FontWeight = FontWeight.Bold,
                        Foreground = ResolveBrush("AppErrorPrimaryTextBrush", "#FF1F2937")
                    },
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14,
                        Foreground = ResolveBrush("AppErrorSecondaryTextBrush", "#FF4B5563"),
                        [Grid.RowProperty] = 1
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 10,
                        Children =
                        {
                            cancelButton,
                            confirmButton
                        },
                        [Grid.RowProperty] = 2
                    }
                }
            }
        };
    }

    private static IBrush ResolveBrush(string resourceKey, string fallbackColor)
    {
        var app = AvaloniaApplication.Current;
        if (app?.TryGetResource(
                resourceKey,
                app.ActualThemeVariant,
                out var resource) == true &&
            resource is IBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.Parse(fallbackColor));
    }
}
