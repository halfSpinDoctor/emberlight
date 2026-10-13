using System.Globalization;

namespace Emberlight.Core;

public static class FileSizeFormatter
{
    private static readonly string[] Units = { "KB", "MB", "GB", "TB" };

    /// <summary>
    /// Port of FileSize2String: "812 bytes", "1.5 KB", "3.0 MB"...
    /// (The original ran out of units after three divisions, so sizes of 1 TB and up printed a
    /// GB-scaled number labelled "TB"; this version divides once per unit and is correct there.)
    /// </summary>
    public static string Format(ulong fileSize, IFormatProvider? provider = null)
    {
        provider ??= CultureInfo.CurrentCulture;
        if (fileSize < 1024) return string.Create(provider, $"{fileSize} bytes");

        double n = fileSize;
        int i = 0;
        while (true)
        {
            n /= 1024.0;
            if (n < 1024 || i == Units.Length - 1) break;
            i++;
        }
        return string.Create(provider, $"{n:F1} {Units[i]}");
    }
}
