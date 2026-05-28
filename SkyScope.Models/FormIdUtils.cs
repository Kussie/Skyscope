using System.Globalization;

namespace SkyScope.Models;

internal static class FormIdUtils
{
    // Normalizes a hex FormId string: strips leading zeros and returns uppercase hex.
    // Falls back to uppercasing the raw string if parsing fails.
    public static string NormalizeFormId(string formId)
    {
        if (uint.TryParse(formId, NumberStyles.HexNumber, null, out var val))
            return val.ToString("X");
        return formId.ToUpperInvariant();
    }
}
