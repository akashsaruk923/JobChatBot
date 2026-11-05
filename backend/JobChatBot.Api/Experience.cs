using System.Globalization;
using System.Text.RegularExpressions;
namespace JobChatBot.Api;
public static class Experience
{
    public static (double Min, double Max)? Range(string text)
    {
        var m = Regex.Match(text, @"(?<!\d)(\d{1,2}(?:\.\d)?)\s*(?:-|–|to)\s*(\d{1,2}(?:\.\d)?)\s*(?:years?|yrs?)\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!m.Success) return null;
        var min = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var max = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return min <= max ? (min, max) : null;
    }
    public static bool Fits(double years, double? min, double? max) => min.HasValue && max.HasValue && years >= min && years <= max;
}
