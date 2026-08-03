using System.Collections.Concurrent;

namespace Portal.Components;

/// <summary>
/// What a surface could not read, collected safely while its reads run in parallel.
///
/// <para>Surfaces fetch their panels concurrently, because the reads are independent and doing
/// them one after another makes first paint the <i>sum</i> of every upstream call rather than the
/// slowest one. That turns a single <c>Error</c> field into shared mutable state written from
/// several tasks at once — benign in practice, since assigning a string reference is atomic, but
/// the result would be whichever failure happened to finish last. An operator reading "denials
/// could not be read" has no way to know that policy failed too.</para>
///
/// <para>So failures are collected rather than overwritten, and reported together. The order is
/// stable so the same two failures always produce the same sentence, rather than one that
/// reshuffles on every refresh.</para>
/// </summary>
public sealed class LoadErrors
{
    private readonly ConcurrentBag<string> _failures = [];

    /// <summary>Records a source that could not be read, named as the operator would name it.</summary>
    public void Record(string what) => _failures.Add($"{what} could not be read");

    /// <summary>Every failure, or <c>null</c> when a surface read everything it asked for.</summary>
    public string? Message => _failures.IsEmpty
        ? null
        : string.Join(" · ", _failures.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}
