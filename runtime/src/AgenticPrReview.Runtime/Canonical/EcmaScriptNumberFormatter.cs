using System.Globalization;
using System.Text;

namespace AgenticPrReview.Runtime.Canonical;

/// <summary>
/// Formats a finite IEEE-754 binary64 value exactly as ECMAScript
/// Number::toString(x, 10) (the RFC 8785 reference algorithm). Negative zero
/// serializes as "0". Non-finite values are rejected by the caller contract.
/// </summary>
internal static class EcmaScriptNumberFormatter
{
    internal static string Format(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new Rfc8785CanonicalizationException(
                Rfc8785RejectionReason.NonFiniteNumber, "Non-finite number is not a JSON number.");
        }

        if (value == 0.0)
        {
            // Covers both +0 and -0 (RFC 8785: -0 serializes as 0).
            return "0";
        }

        var negative = value < 0;
        var abs = negative ? -value : value;
        var (digits, exp) = GetShortestDigits(abs);
        var layout = FormatLayout(digits, exp + 1);
        return negative ? "-" + layout : layout;
    }

    /// <summary>
    /// Returns the shortest correctly-rounded decimal digit string that
    /// round-trips to <paramref name="value"/>, plus the decimal exponent of
    /// its first digit. .NET's default double formatting is shortest
    /// round-trip; we parse it back into (digits, exponent) and re-emit per
    /// the ECMAScript layout rules.
    /// </summary>
    private static (string Digits, int Exp) GetShortestDigits(double value)
    {
        var s = value.ToString(CultureInfo.InvariantCulture);
        var eIndex = s.IndexOf('E');
        string mantissa;
        var sciExp = 0;
        if (eIndex >= 0)
        {
            mantissa = s[..eIndex];
            sciExp = int.Parse(s[(eIndex + 1)..], CultureInfo.InvariantCulture);
        }
        else
        {
            mantissa = s;
        }

        string intPart;
        string fracPart;
        var dot = mantissa.IndexOf('.');
        if (dot >= 0)
        {
            intPart = mantissa[..dot];
            fracPart = mantissa[(dot + 1)..];
        }
        else
        {
            intPart = mantissa;
            fracPart = string.Empty;
        }

        string digits;
        int exp;
        if (eIndex >= 0)
        {
            // Scientific mantissa is a single integer digit.
            digits = (intPart + fracPart).TrimEnd('0');
            exp = sciExp;
        }
        else if (intPart != "0")
        {
            digits = (intPart + fracPart).TrimEnd('0');
            exp = intPart.Length - 1;
        }
        else
        {
            var j = 0;
            while (j < fracPart.Length && fracPart[j] == '0')
            {
                j++;
            }

            digits = fracPart[j..].TrimEnd('0');
            exp = -(j + 1);
        }

        if (digits.Length == 0)
        {
            // Unreachable for a non-zero input; defensive.
            digits = "0";
        }

        return (digits, exp);
    }

    /// <summary>
    /// ECMA-262 Number::toString layout. <paramref name="n"/> is the decimal
    /// exponent such that value ≈ digits × 10^(n - k), k = digits.Length.
    /// </summary>
    private static string FormatLayout(string digits, int n)
    {
        var k = digits.Length;
        if (k <= n && n <= 21)
        {
            return digits + new string('0', n - k);
        }

        if (0 < n && n <= 21)
        {
            return digits[..n] + "." + digits[n..];
        }

        if (-6 < n && n <= 0)
        {
            return "0." + new string('0', -n) + digits;
        }

        var sb = new StringBuilder(k + 8);
        sb.Append(digits[0]);
        if (k > 1)
        {
            sb.Append('.');
            sb.Append(digits.AsSpan(1));
        }

        sb.Append('e');
        var e = n - 1;
        sb.Append(e >= 0 ? '+' : '-');
        sb.Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
