using System;
using System.Collections.Generic;

namespace SkyScope.Core;

// SPID loads *_DISTR.ini files in plain case-sensitive (ordinal) order, unlike SkyPatcher's
// case-insensitive walk — confirmed from a real SPID log ("AZ_..." loads before "Afterlife_...").
public sealed class SpidLoadOrderComparer : IComparer<string>
{
    public static readonly SpidLoadOrderComparer Instance = new();

    public int Compare(string? a, string? b) => string.CompareOrdinal(a, b);
}
