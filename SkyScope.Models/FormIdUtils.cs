using System.Globalization;

namespace SkyScope.Models;

internal static class FormIdUtils
{
    // Normalizes a hex FormId string: strips a leading "0x" and leading zeros, returns uppercase
    // hex. Falls back to uppercasing the raw string if parsing fails.
    public static string NormalizeFormId(string formId)
    {
        var hex = formId.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) ? formId[2..] : formId;
        if (uint.TryParse(hex, NumberStyles.HexNumber, null, out var val))
            return val.ToString("X");
        return formId.ToUpperInvariant();
    }
}
