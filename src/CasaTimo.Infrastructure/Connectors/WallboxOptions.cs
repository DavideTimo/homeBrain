namespace CasaTimo.Infrastructure.Connectors;

public class WallboxOptions
{
    /// <summary>
    /// Porta TCP su cui il server OCPP ascolta le connessioni della wallbox.
    /// La wallbox si connette A NOI, non viceversa.
    /// </summary>
    public int Port { get; set; } = 9000;

    /// <summary>
    /// Se impostata, ogni connessione OCPP deve includere questo valore
    /// nell'header Authorization (Basic auth) per essere accettata.
    /// </summary>
    public string? AuthKey { get; set; }

    /// <summary>Timeout in secondi per le connessioni WebSocket inattive.</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 180;
}
