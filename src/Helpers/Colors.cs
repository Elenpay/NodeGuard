namespace NodeGuard.Helpers;

using System;

public class Colors
{
    private static readonly List<string> CommonColors = new List<string>
    {
        "#FF6666", // Light Red
        "#66FF66", // Light Green
        "#6666FF", // Light Blue
        "#FFFF66", // Light Yellow
        "#66FFFF", // Light Cyan
        "#FF66FF", // Light Magenta
        "#FFB366", // Light Orange
        "#B366FF", // Light Purple
        "#D2B48C", // Tan (Light Brown)
        "#FFB6C1", // Light Pink
        "#CCFF66", // Light Lime
        "#66CC99", // Light Teal
        "#E6E6FA", // Lavender
        "#D9D9D9", // Light Gray
        "#F0F0F0", // Very Light Gray (Almost White)
        "#CCCCFF", // Light Blue-Gray
        "#FF9999", // Light Maroon
        "#99CCFF", // Light Navy
        "#CCFFCC", // Light Olive
        "#C0C0C0"  // Silver
    };

    public static string GetColor(int index)
    {
        // Ensure the index is within the range of available colors
        int colorIndex = Math.Abs(index) % CommonColors.Count;
        return CommonColors[colorIndex];
    }
}
