using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using HpVictusControl.Bios;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using MessageBox = System.Windows.MessageBox;
using MessageBoxOptions = System.Windows.MessageBoxOptions;

namespace HpVictusControl;

/// <summary>
/// The app's interface language. Text is keyed by its English wording, so anything without a
/// translation simply stays in English — that's also how technical terms (CPU, GPU, RAM, BIOS,
/// NVIDIA, Hz, FPS…) are kept as they are inside Arabic sentences.
/// </summary>
public static partial class Loc {

    public const string English = "en";
    public const string ArabicCode = "ar";

    public static bool IsArabic { get; private set; }

    /// <summary>Picks the language for this run. Called once at start, before any window is built.</summary>
    public static void Use(string? language) => IsArabic = language == ArabicCode;

    /// <summary>Alexandria (bundled) for Arabic, falling back to Segoe UI, which has Arabic glyphs too.</summary>
    public static FontFamily ArabicFont { get; } =
        new(new Uri("pack://application:,,,/HpVictusControl;component/"), "./Fonts/#Alexandria, Segoe UI");

    public static FlowDirection Direction => IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>Translates a fixed piece of text.</summary>
    public static string T(string english) =>
        IsArabic && Arabic.TryGetValue(english, out string? arabic) ? arabic : english;

    /// <summary>Translates a format string, then fills it in: F("Battery {0}%", 72).</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    /// <summary>A performance mode's display name.</summary>
    public static string Mode(HpFanMode mode) => T(mode.ToString());

    /// <summary>
    /// Translates the text written straight into the XAML — labels, buttons, tooltips — by walking
    /// the logical tree once after the window is built. Text set from code goes through T/F instead.
    /// </summary>
    public static void TranslateTree(DependencyObject root) {
        if (!IsArabic) return;
        TranslateElement(root);
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject element) TranslateTree(element);
    }

    private static void TranslateElement(DependencyObject element) {
        switch (element) {
            case TextBlock text when BindingOperations.GetBindingExpression(text, TextBlock.TextProperty) == null:
                string translated = T(text.Text);
                // Only when there's a translation: setting Text would flatten any formatted inlines.
                if (!ReferenceEquals(translated, text.Text) && translated != text.Text) text.Text = translated;
                break;
            case ContentControl control when control.Content is string content:
                control.Content = T(content);
                break;
        }
        if (element is FrameworkElement framework && framework.ToolTip is string tip) framework.ToolTip = T(tip);
    }

    /// <summary>MessageBox that reads right-to-left when the interface does.</summary>
    public static MessageBoxResult Message(Window? owner, string text, string caption,
            MessageBoxButton buttons, MessageBoxImage icon) {
        MessageBoxOptions options = IsArabic ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None;
        // No owner before any window exists, e.g. while uninstalling from Windows' installed apps.
        return owner == null
            ? MessageBox.Show(text, caption, buttons, icon, MessageBoxResult.None, options)
            : MessageBox.Show(owner, text, caption, buttons, icon, MessageBoxResult.None, options);
    }
}
