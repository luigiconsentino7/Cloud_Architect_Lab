namespace Lab.Ir.Scene;

/// <summary>Inferência e validação do tipo de interação de uma conexão a partir dos arquétipos das pontas.</summary>
public static class InteractionRules
{
    public static Interaction Infer(string from, string to)
    {
        if (Archetypes.IsData(from) && to == Archetypes.Stream) return Interaction.Cdc;
        if (Archetypes.IsData(from) && Archetypes.IsData(to)) return Interaction.Replication;
        if (Archetypes.IsMessaging(from) && !Archetypes.IsMessaging(to)) return Interaction.AsyncConsume;
        if (Archetypes.IsMessaging(to)) return Interaction.AsyncPublish;
        if (Archetypes.IsData(to)) return Interaction.DataAccess;
        return Interaction.SyncRequest;
    }

    public static string? Problem(Interaction interaction, string from, string to) => interaction switch
    {
        Interaction.SyncRequest when Archetypes.IsData(to) || Archetypes.IsMessaging(to) || Archetypes.IsClient(to) =>
            "uma chamada síncrona precisa ir para computação, borda ou um sistema externo",
        Interaction.SyncRequest when Archetypes.IsData(from) || Archetypes.IsMessaging(from) =>
            "armazenamentos e filas não fazem chamadas síncronas",
        Interaction.AsyncPublish when !Archetypes.IsMessaging(to) =>
            "uma publicação assíncrona precisa de uma fila, tópico ou stream como destino",
        Interaction.AsyncConsume when !Archetypes.IsMessaging(from) =>
            "um consumo assíncrono precisa partir de uma fila, tópico ou stream",
        Interaction.AsyncConsume when !(Archetypes.IsCompute(to) || Archetypes.IsExternal(to)) =>
            "quem consome mensagens precisa ser um componente de computação",
        Interaction.DataAccess when !Archetypes.IsData(to) =>
            "um acesso a dados precisa de um armazenamento como destino",
        Interaction.Replication when !(Archetypes.IsData(from) && Archetypes.IsData(to)) =>
            "replicação só acontece entre armazenamentos",
        Interaction.Cdc when !(Archetypes.IsData(from) && to == Archetypes.Stream) =>
            "CDC liga um armazenamento a um stream",
        _ => null,
    };

    public static Interaction? Parse(string? value) => value switch
    {
        "sync-request" => Interaction.SyncRequest,
        "async-publish" => Interaction.AsyncPublish,
        "async-consume" => Interaction.AsyncConsume,
        "data-access" => Interaction.DataAccess,
        "replication" => Interaction.Replication,
        "cdc" => Interaction.Cdc,
        _ => null,
    };

    public static string Describe(Interaction interaction) => interaction switch
    {
        Interaction.SyncRequest => "chamada síncrona",
        Interaction.AsyncPublish => "publicação assíncrona",
        Interaction.AsyncConsume => "consumo assíncrono",
        Interaction.DataAccess => "acesso a dados",
        Interaction.Replication => "replicação",
        Interaction.Cdc => "CDC",
        _ => interaction.ToString(),
    };
}
