using System.Net;

namespace EgressProxy.Client;

internal sealed class NoProxyWebProxy : IWebProxy
{
    private readonly Uri _proxyUri;
    private readonly bool _bypassAll;
    private readonly IReadOnlyList<NoProxyRule> _rules;

    public NoProxyWebProxy(Uri proxyUri, string? noProxy)
    {
        _proxyUri = proxyUri ?? throw new ArgumentNullException(nameof(proxyUri));

        var rules = new List<NoProxyRule>();
        var bypassAll = false;

        foreach (var entry in ParseEntries(noProxy))
        {
            if (entry == "*")
            {
                bypassAll = true;
                break;
            }

            if (NoProxyRule.TryCreate(entry, out var rule))
            {
                rules.Add(rule);
            }
        }

        _bypassAll = bypassAll;
        _rules = rules;
    }

    public ICredentials? Credentials { get; set; }

    public Uri GetProxy(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return _proxyUri;
    }

    public bool IsBypassed(Uri host)
    {
        ArgumentNullException.ThrowIfNull(host);

        if (_bypassAll)
        {
            return true;
        }

        var hostName = host.Host;
        var port = host.IsDefaultPort ? (int?)null : host.Port;
        return _rules.Any(rule => rule.IsMatch(hostName, port));
    }

    private static IEnumerable<string> ParseEntries(string? noProxy)
    {
        if (string.IsNullOrWhiteSpace(noProxy))
        {
            return [];
        }

        return noProxy
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(static item => item.Trim())
            .Where(static item => !string.IsNullOrWhiteSpace(item));
    }

    private sealed record NoProxyRule(string Host, int? Port, bool MatchSubdomains)
    {
        public bool IsMatch(string host, int? port)
        {
            if (Port.HasValue && Port.Value != port)
            {
                return false;
            }

            if (MatchSubdomains)
            {
                return host.Equals(Host, StringComparison.OrdinalIgnoreCase) ||
                       host.EndsWith($".{Host}", StringComparison.OrdinalIgnoreCase);
            }

            return host.Equals(Host, StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryCreate(string rawEntry, out NoProxyRule rule)
        {
            if (Uri.TryCreate(rawEntry, UriKind.Absolute, out var uri))
            {
                var uriPort = uri.IsDefaultPort ? (int?)null : uri.Port;
                rule = new NoProxyRule(uri.Host, uriPort, rawEntry.Contains("://.") || uri.Host.StartsWith(".", StringComparison.Ordinal));
                return true;
            }

            var entry = rawEntry;
            var matchSubdomains = entry.StartsWith(".", StringComparison.Ordinal);
            if (matchSubdomains)
            {
                entry = entry[1..];
            }

            if (string.IsNullOrWhiteSpace(entry))
            {
                rule = null!;
                return false;
            }

            var port = default(int?);
            var separatorIndex = entry.LastIndexOf(':');
            if (separatorIndex > 0 && separatorIndex < entry.Length - 1 && int.TryParse(entry[(separatorIndex + 1)..], out var parsedPort))
            {
                port = parsedPort;
                entry = entry[..separatorIndex];
            }

            if (string.IsNullOrWhiteSpace(entry))
            {
                rule = null!;
                return false;
            }

            rule = new NoProxyRule(entry, port, matchSubdomains);
            return true;
        }
    }
}
