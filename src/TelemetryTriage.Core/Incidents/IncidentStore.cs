using System.Collections.Concurrent;
using TelemetryTriage.Core.Triage;

namespace TelemetryTriage.Core.Incidents;

public interface IIncidentStore
{
    /// <summary>Insert or replace by incident id; replaying the same frames is therefore idempotent.</summary>
    void Upsert(TriagedIncident incident);

    TriagedIncident? Get(string id);

    IReadOnlyList<TriagedIncident> List();
}

/// <summary>Process-local store. Fine for the demo; production would use SQL Server or similar.</summary>
public sealed class InMemoryIncidentStore : IIncidentStore
{
    private readonly ConcurrentDictionary<string, TriagedIncident> _incidents = new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(TriagedIncident incident) => _incidents[incident.Incident.Id] = incident;

    public TriagedIncident? Get(string id) => _incidents.GetValueOrDefault(id);

    public IReadOnlyList<TriagedIncident> List() =>
        _incidents.Values.OrderBy(i => i.Incident.OpenedAt).ThenBy(i => i.Incident.Id, StringComparer.Ordinal).ToList();
}
