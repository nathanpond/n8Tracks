using System.Collections.Immutable;
using System.Globalization;

namespace n8Tracks.Domain.Songs;

/// <summary>
/// A parsed hierarchical Version number, such as <c>1</c>, <c>2.1</c>, or <c>1.3.2</c>: one or more
/// positive 32-bit whole numbers written without leading zeros and separated by dots, at most
/// <see cref="MaximumLength"/> characters in all. Equal when the parts are equal; ordered as the tree
/// is (<c>1</c>, <c>1.1</c>, <c>1.2</c>, <c>1.10</c>, <c>2</c>).
/// </summary>
public sealed class VersionNumber : IEquatable<VersionNumber>, IComparable<VersionNumber>
{
    /// <summary>The longest a number may be written, in characters.</summary>
    public const int MaximumLength = 64;

    private readonly ImmutableArray<int> parts;
    private readonly string text;

    private VersionNumber(ImmutableArray<int> parts)
    {
        this.parts = parts;
        text = string.Join('.', parts.Select(static part => part.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>The number every Song's first Version has.</summary>
    public static VersionNumber Initial { get; } = new([1]);

    /// <summary>The parts, from the top of the tree down; never empty, each from 1.</summary>
    public ImmutableArray<int> Parts => parts;

    /// <summary>How deep in the tree it is: <c>1</c> for a top-level number.</summary>
    public int Depth => parts.Length;

    /// <summary>The last part: its place among its siblings.</summary>
    public int Last => parts[^1];

    /// <summary>The number it branches from (<c>1.3</c> for <c>1.3.2</c>); null for a top-level number.</summary>
    public VersionNumber? Parent => parts.Length == 1 ? null : new VersionNumber(parts[..^1]);

    /// <summary>
    /// A key whose ordinal text order is the tree order: each part zero-padded to
    /// <see cref="VersionNumbers.SortKeyPartWidth"/> digits, joined with dots.
    /// </summary>
    public string SortKey => string.Join(
        '.',
        parts.Select(static part => part.ToString(CultureInfo.InvariantCulture).PadLeft(VersionNumbers.SortKeyPartWidth, '0')));

    /// <summary>
    /// Reads a number exactly as written: no surrounding space and no <c>v</c> prefix (callers strip
    /// them). False for anything else, including a part of 0, a leading zero, an empty part, a part
    /// above <see cref="int.MaxValue"/>, and text over <see cref="MaximumLength"/> characters.
    /// </summary>
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out VersionNumber? number)
    {
        number = null;
        if (string.IsNullOrEmpty(text) || text.Length > MaximumLength)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<int>();
        foreach (var part in text.Split('.'))
        {
            if (part.Length == 0
                || part[0] == '0'
                || !part.All(char.IsAsciiDigit)
                || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            builder.Add(value);
        }

        number = new VersionNumber(builder.ToImmutable());
        return true;
    }

    /// <summary>Reads a number that must be valid, by <see cref="TryParse"/>.</summary>
    public static VersionNumber Parse(string text) =>
        TryParse(text, out var number)
            ? number
            : throw new ArgumentException("A Version number is positive whole numbers separated by dots, at most 64 characters.", nameof(text));

    /// <summary>
    /// The number with its last part replaced by <paramref name="last"/> (<c>1.3.4</c> from
    /// <c>1.3.1</c> and 4); null when that is longer than <see cref="MaximumLength"/>.
    /// </summary>
    public VersionNumber? WithLast(int last)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(last, 1);

        return Bounded(parts.SetItem(parts.Length - 1, last));
    }

    /// <summary>
    /// A child of this number with last part <paramref name="last"/> (<c>1.3.1.1</c> from
    /// <c>1.3.1</c> and 1); null when that is longer than <see cref="MaximumLength"/>.
    /// </summary>
    public VersionNumber? Child(int last)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(last, 1);

        return Bounded(parts.Add(last));
    }

    /// <summary>A top-level number (<c>6</c>).</summary>
    public static VersionNumber TopLevel(int part)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(part, 1);

        return new VersionNumber([part]);
    }

    public int CompareTo(VersionNumber? other)
    {
        if (other is null)
        {
            return 1;
        }

        var shared = Math.Min(parts.Length, other.parts.Length);
        for (var index = 0; index < shared; index++)
        {
            var compared = parts[index].CompareTo(other.parts[index]);
            if (compared != 0)
            {
                return compared;
            }
        }

        // A parent comes before its children.
        return parts.Length.CompareTo(other.parts.Length);
    }

    public bool Equals(VersionNumber? other) => other is not null && parts.SequenceEqual(other.parts);

    public override bool Equals(object? obj) => obj is VersionNumber other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(text);

    /// <summary>The number as it is written and stored, such as <c>1.3.2</c>.</summary>
    public override string ToString() => text;

    public static bool operator ==(VersionNumber? left, VersionNumber? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(VersionNumber? left, VersionNumber? right) => !(left == right);

    public static bool operator <(VersionNumber? left, VersionNumber? right) => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(VersionNumber? left, VersionNumber? right) => left is null || left.CompareTo(right) <= 0;

    public static bool operator >(VersionNumber? left, VersionNumber? right) => left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(VersionNumber? left, VersionNumber? right) => left is null ? right is null : left.CompareTo(right) >= 0;

    private static VersionNumber? Bounded(ImmutableArray<int> parts)
    {
        var number = new VersionNumber(parts);
        return number.text.Length > MaximumLength ? null : number;
    }
}
