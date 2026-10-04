using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace n8Tracks.Api.Logging;

/// <summary>
/// Drops what the framework attaches to events on its own: the properties of ASP.NET Core's log
/// scopes inside a request, and the event ID. That leaves the source and <c>requestId</c> as the only
/// enrichment. A property the message itself uses is kept.
/// </summary>
internal sealed class HostScopeTrimEnricher : ILogEventEnricher
{
    private static readonly string[] HostScopeProperties = ["RequestId", "RequestPath", "ConnectionId", "EventId"];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        foreach (var name in HostScopeProperties)
        {
            if (logEvent.Properties.ContainsKey(name) && !IsUsedByMessage(logEvent, name))
            {
                logEvent.RemovePropertyIfPresent(name);
            }
        }
    }

    private static bool IsUsedByMessage(LogEvent logEvent, string name)
    {
        foreach (var token in logEvent.MessageTemplate.Tokens)
        {
            if (token is PropertyToken property && property.PropertyName == name)
            {
                return true;
            }
        }

        return false;
    }
}
