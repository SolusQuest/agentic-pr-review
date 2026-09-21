using System.Numerics;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;

internal static class ComparisonNumbers
{
    private static (BigInteger Coefficient, int Scale) Parts(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient = (BigInteger)(uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        return ((bits[3] & int.MinValue) == 0 ? coefficient : -coefficient, (bits[3] >> 16) & 255);
    }

    internal static decimal Difference(decimal right, decimal left)
    {
        var r = Parts(right); var l = Parts(left);
        var scale = Math.Max(r.Scale, l.Scale);
        return Exact(r.Coefficient * BigInteger.Pow(10, scale - r.Scale) -
            l.Coefficient * BigInteger.Pow(10, scale - l.Scale), scale);
    }

    internal static decimal PerReview(decimal amount, int count, int scale)
    {
        if (amount < 0 || count <= 0 || scale is < 0 or > 12) throw new ArgumentException("comparison_ratio_invalid");
        var value = Parts(amount);
        var numerator = value.Coefficient * BigInteger.Pow(10, scale);
        var denominator = count * BigInteger.Pow(10, value.Scale);
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var tie = (remainder * 2).CompareTo(denominator);
        if (tie > 0 || tie == 0 && !quotient.IsEven) quotient++;
        return Exact(quotient, scale);
    }

    private static decimal Exact(BigInteger value, int scale)
    {
        while (scale > 0 && value % 10 == 0) { value /= 10; scale--; }
        var negative = value.Sign < 0;
        value = BigInteger.Abs(value);
        if (scale > 28 || value > (BigInteger.One << 96) - 1) throw new OverflowException();
        return new((int)(uint)(value & uint.MaxValue), (int)(uint)((value >> 32) & uint.MaxValue),
            (int)(uint)((value >> 64) & uint.MaxValue), negative, (byte)scale);
    }
}
