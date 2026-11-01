using System.Globalization;

namespace Orrery.Core.Network;

/// <summary>A hardware (MAC) address, the way a connection stores it for Wake-on-LAN.</summary>
public static class MacAddress
{
    public const int Length = 6;

    /// <summary>
    /// Reads the usual spellings: <c>00:1A:2B:3C:4D:5E</c>, <c>00-1A-2B-3C-4D-5E</c>, <c>001A.2B3C.4D5E</c> and
    /// <c>001A2B3C4D5E</c>, in either case. A pair may drop its leading zero when separated by colons or hyphens
    /// (<c>0:1a:2b:3c:4d:5e</c>, as some systems print them). All zeros and all ones aren't a machine's address.
    /// </summary>
    public static bool TryParse(string? text, out byte[] bytes)
    {
        bytes = [];
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 17)
        {
            return false;
        }

        // The separator decides the shape: how many hex digits each group between separators may have.
        var separators = value.Where(c => c is ':' or '-' or '.').Distinct().ToList();
        if (separators.Count > 1)
        {
            return false;
        }

        var digits = new List<char>(12);
        if (separators.Count == 0)
        {
            if (value.Length != 12)
            {
                return false;
            }

            digits.AddRange(value);
        }
        else
        {
            bool dotted = separators[0] == '.';
            var groups = value.Split(separators[0]);
            if (groups.Length != (dotted ? 3 : Length))
            {
                return false;
            }

            foreach (var group in groups)
            {
                if (dotted ? group.Length != 4 : group.Length is < 1 or > 2)
                {
                    return false;
                }

                digits.AddRange(dotted ? group : group.PadLeft(2, '0'));
            }
        }

        if (digits.Count != 2 * Length || !digits.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        var parsed = new byte[Length];
        for (int i = 0; i < Length; i++)
        {
            parsed[i] = byte.Parse(digits.GetRange(2 * i, 2).ToArray(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        if (parsed.All(b => b == 0) || parsed.All(b => b == 0xFF))
        {
            return false;
        }

        bytes = parsed;
        return true;
    }

    /// <summary>The address as <c>00:1A:2B:3C:4D:5E</c>.</summary>
    public static string Format(byte[] bytes) => string.Join(':', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>The address in the form saved with a connection; false when <paramref name="text"/> isn't one.</summary>
    public static bool TryNormalize(string? text, out string normalized)
    {
        bool ok = TryParse(text, out var bytes);
        normalized = ok ? Format(bytes) : string.Empty;
        return ok;
    }
}
