using DeChimex.Application.Common.Interfaces;

namespace DeChimex.Host.Realtime;

/// <summary>
/// In-memory order-workspace presence (see <see cref="IOrderPresenceTracker"/>).
/// A side counts as online while it holds ≥1 SignalR connection to the order's group;
/// the last-seen stamp is the moment its final connection dropped. Nothing is
/// persisted: after a restart every side simply shows offline until it reconnects,
/// which is exactly as much as the server can honestly claim.
/// </summary>
public sealed class OrderPresenceTracker : IOrderPresenceTracker
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, OrderEntry> _orders = new();
    private readonly Dictionary<string, (Guid OrderId, string Role)> _connections = new();

    private sealed class OrderEntry
    {
        public HashSet<string> CustomerConnections { get; } = new(StringComparer.Ordinal);
        public HashSet<string> StaffConnections { get; } = new(StringComparer.Ordinal);
        public DateTime? CustomerLastSeenUtc { get; set; }
        public DateTime? StaffLastSeenUtc { get; set; }
    }

    public void Join(Guid orderId, string connectionId, string role)
    {
        if (role is not ("customer" or "staff")) return;
        lock (_gate)
        {
            PruneLocked();
            if (!_orders.TryGetValue(orderId, out var entry))
            {
                entry = new OrderEntry();
                _orders[orderId] = entry;
            }
            (role == "customer" ? entry.CustomerConnections : entry.StaffConnections).Add(connectionId);
            _connections[connectionId] = (orderId, role);
        }
    }

    public IReadOnlyList<Guid> Leave(string connectionId)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(connectionId, out var watched))
                return [];
            _connections.Remove(connectionId);
            if (!_orders.TryGetValue(watched.OrderId, out var entry))
                return [watched.OrderId];
            var set = watched.Role == "customer" ? entry.CustomerConnections : entry.StaffConnections;
            set.Remove(connectionId);
            if (set.Count == 0)
            {
                if (watched.Role == "customer") entry.CustomerLastSeenUtc = DateTime.UtcNow;
                else entry.StaffLastSeenUtc = DateTime.UtcNow;
            }
            // The entry stays as a tombstone so "last active" survives a full
            // disconnect; ancient tombstones are pruned on Join.
            return [watched.OrderId];
        }
    }

    /// <summary>Drops fully-offline entries nobody has touched in 24h (memory hygiene).</summary>
    private void PruneLocked()
    {
        if (_orders.Count < 1024) return;
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var (orderId, entry) in _orders.ToArray())
        {
            if (entry.CustomerConnections.Count == 0 && entry.StaffConnections.Count == 0 &&
                (entry.CustomerLastSeenUtc ?? DateTime.MinValue) < cutoff &&
                (entry.StaffLastSeenUtc ?? DateTime.MinValue) < cutoff)
                _orders.Remove(orderId);
        }
    }

    public bool IsMember(Guid orderId, string connectionId)
    {
        lock (_gate)
            return _connections.TryGetValue(connectionId, out var watched) && watched.OrderId == orderId;
    }

    public string? RoleOf(string connectionId)
    {
        lock (_gate)
            return _connections.TryGetValue(connectionId, out var watched) ? watched.Role : null;
    }

    public bool IsRecipientConnected(Guid orderId, string senderRole)
    {
        var snapshot = GetSnapshot(orderId);
        return senderRole == "customer" ? snapshot.StaffOnline : snapshot.CustomerOnline;
    }

    public OrderPresenceSnapshot GetSnapshot(Guid orderId)
    {
        lock (_gate)
        {
            if (!_orders.TryGetValue(orderId, out var entry))
                return new OrderPresenceSnapshot(false, false, null, null);
            return new OrderPresenceSnapshot(
                entry.CustomerConnections.Count > 0,
                entry.StaffConnections.Count > 0,
                entry.CustomerLastSeenUtc,
                entry.StaffLastSeenUtc);
        }
    }
}
