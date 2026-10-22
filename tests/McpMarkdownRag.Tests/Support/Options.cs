using Microsoft.Extensions.Options;

namespace McpMarkdownRag.Tests.Support;

/// <summary>
/// Small helper mirroring how the production code consumes settings: every service takes
/// <c>IOptions&lt;T&gt;</c>, and tests can synthesize an options wrapper around a preset value.
/// Named <c>TestOptions</c> to avoid the ambiguity with
/// <c>Microsoft.Extensions.Options.Options</c> under ImplicitUsings.
/// </summary>
public static class TestOptions
{
    public static IOptions<T> Create<T>(T value)
        where T : class
        => new FakeOptions<T>(value);
}

/// <summary>Test double for <see cref="Microsoft.Extensions.Options.IOptions{T}"/>.</summary>
public sealed class FakeOptions<T> : IOptions<T>
    where T : class
{
    public FakeOptions(T value) => Value = value;

    public T Value { get; }

    public T ValueOrDefault => Value;
}
