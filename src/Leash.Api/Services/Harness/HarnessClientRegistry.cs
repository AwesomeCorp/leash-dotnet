namespace Leash.Api.Services.Harness;

/// <summary>
/// Registry of all available harness clients. Provides lookup by name and enumeration.
/// Register new clients here to extend support for additional AI coding assistants.
/// </summary>
public class HarnessClientRegistry
{
    private readonly Dictionary<string, IHarnessClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public HarnessClientRegistry(IEnumerable<IHarnessClient> clients)
    {
        foreach (var client in clients)
            _clients[client.Name] = client;
    }

    /// <summary>Gets a client by name (e.g. "claude", "copilot"). Returns null if not found.</summary>
    public IHarnessClient? Get(string name)
        => _clients.TryGetValue(name, out var client) ? client : null;

    /// <summary>Gets a client by name, throwing if not found.</summary>
    public IHarnessClient GetRequired(string name)
        => _clients.TryGetValue(name, out var client)
            ? client
            : throw new ArgumentException($"Unknown harness client: {name}");

    /// <summary>Returns all registered clients.</summary>
    public IReadOnlyCollection<IHarnessClient> GetAll() => _clients.Values;

    /// <summary>Returns all registered client names.</summary>
    public IReadOnlyCollection<string> GetNames() => _clients.Keys;
}
