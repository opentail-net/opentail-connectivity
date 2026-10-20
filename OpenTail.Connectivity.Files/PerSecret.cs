namespace OpenTail.Connectivity.Files;

/// <summary>Keeps one built object per secret value: rebuilt only when the secret changes (rotation), shared otherwise.
/// A replaced object is disposed if it is <see cref="IDisposable"/>.</summary>
public sealed class PerSecret<T>(Func<string, T> build) where T : class
{
    private readonly object _gate = new();
    private string? _secret;
    private T? _value;

    public T Get(string secret)
    {
        lock (_gate)
        {
            if (_value is null || !string.Equals(_secret, secret, StringComparison.Ordinal))
            {
                (_value as IDisposable)?.Dispose();
                _value = build(secret);
                _secret = secret;
            }
            return _value;
        }
    }
}
