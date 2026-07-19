namespace AgenticPrReview.Runtime.Canonical;

/// <summary>
/// A structured diagnostic path segment. Only segments produced at an actual
/// array position may carry <see cref="IsIndex"/> = true; property names are
/// never reinterpreted as indices (a numeric property name is still a name).
/// </summary>
internal readonly record struct CanonicalPathSegment(string Name, bool IsIndex)
{
    internal static CanonicalPathSegment Property(string name) => new(name, false);

    internal static CanonicalPathSegment Index(int index) =>
        new(index.ToString(System.Globalization.CultureInfo.InvariantCulture), true);

    internal static CanonicalPathSegment Index(string indexText) => new(indexText, true);

    public override string ToString() => Name;
}
