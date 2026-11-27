using System.Globalization;

namespace Pim.Client.Core.Services.Notes;

public static class LocalInkPalette
{
    public const string LegacyColor = "#263b85";
    public const string InkColor = "#252b35";
    public const string RedColor = "#b8373c";
    public const string BlueColor = "#245fba";
    public const string GreenColor = "#216e4e";

    public const double LegacyWidth = 2.5;
    public const double ThinWidth = 1.8;
    public const double MediumWidth = 3.4;
    public const double ThickWidth = 6;
    public const double MinimumWidth = 0.5;
    public const double MaximumWidth = 24;
    public const double EraserRadius = 12;

    public static IReadOnlyList<string> Colors { get; } =
        [InkColor, RedColor, BlueColor, GreenColor];

    public static IReadOnlyList<double> Widths { get; } =
        [ThinWidth, MediumWidth, ThickWidth];

    public static bool IsBrushColor(string? color)
        => color is not null
            && Colors.Any(option => string.Equals(option, color, StringComparison.OrdinalIgnoreCase));

    public static bool IsBrushWidth(double width)
        => Widths.Any(option => Math.Abs(option - width) <= 1e-9);
}

public sealed record LocalInkStyle
{
    public LocalInkStyle(string color, double width)
    {
        Color = NormalizeColor(color);
        if (!double.IsFinite(width)
            || width < LocalInkPalette.MinimumWidth
            || width > LocalInkPalette.MaximumWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "手写笔宽超出支持范围。");
        }

        Width = width;
    }

    public string Color { get; }
    public double Width { get; }

    public static LocalInkStyle Legacy { get; } =
        new(LocalInkPalette.LegacyColor, LocalInkPalette.LegacyWidth);

    public static LocalInkStyle Default { get; } =
        new(LocalInkPalette.InkColor, LocalInkPalette.MediumWidth);

    public static string NormalizeColor(string color)
    {
        ArgumentNullException.ThrowIfNull(color);
        var trimmed = color.Trim();
        if (trimmed.Length != 7
            || trimmed[0] != '#'
            || !trimmed[1..].All(Uri.IsHexDigit))
        {
            throw new ArgumentException("手写颜色必须是 #RRGGBB。", nameof(color));
        }

        return trimmed.ToLowerInvariant();
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Color} {Width}");
}
