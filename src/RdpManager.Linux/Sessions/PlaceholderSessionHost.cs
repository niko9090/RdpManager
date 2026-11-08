using RdpManager.Core;

namespace RdpManager.Linux.Sessions;

/// <summary>Host di sessioni provvisorio: apre la scheda ma non si collega (le sessioni FreeRDP arrivano a parte).</summary>
public sealed class PlaceholderSessionHost : ISessionHost
{
    public bool IsAvailable => false;

    public string? UnavailableReason => "Le sessioni remote non sono ancora disponibili in questa versione per Linux.";

    public ISession Open(RdpConnection connection) => new PlaceholderSession(connection);
}

/// <summary>Contenuto della scheda segnaposto.</summary>
public sealed record PlaceholderSessionContent(string Title, string Target, string Message);

public sealed class PlaceholderSession : ISession
{
    public PlaceholderSession(RdpConnection connection)
    {
        ConnectionId = connection.Id;
        Title = connection.DisplayName;
        Content = new PlaceholderSessionContent(
            connection.DisplayName,
            $"{connection.Host}:{connection.Port}",
            "La sessione remota non è ancora disponibile in questa versione per Linux.");
    }

    public Guid ConnectionId { get; }

    public string Title { get; }

    public object Content { get; }

    public event EventHandler? Closed;

    public void Close() => Closed?.Invoke(this, EventArgs.Empty);
}
