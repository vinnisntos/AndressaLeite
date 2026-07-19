namespace AndressaLeite.Services
{
    /// <summary>
    /// Corrige um bug sistêmico descoberto testando o fluxo de e-mail
    /// (readme.txt, achado da rodada de e-mail transacional): valores
    /// DateTime lidos de volta do Supabase via postgrest-csharp chegam
    /// convertidos pro fuso LOCAL da máquina, mas com Kind=Unspecified em
    /// vez de Kind=Local — qualquer .ToUniversalTime()/.ToLocalTime() ou
    /// comparação com DateTime.UtcNow aplicada depois soma/subtrai o
    /// offset de novo, dobrando o erro (ex.: token de reset marcado como
    /// "expirado" 3h antes da hora real, num servidor em UTC-3).
    ///
    /// Em produção (container Docker rodando em UTC, ver Dockerfile) isso
    /// passa despercebido porque o offset local é zero ali — só aparece
    /// em ambiente de desenvolvimento fora de UTC (ex.: Brasília,
    /// UTC-3). Provavelmente afeta outros campos DateTime lidos do
    /// Supabase em código já existente antes desta rodada (ex.:
    /// StartTime/EndTime de appointments) — não corrigido globalmente
    /// aqui de propósito, só nos pontos tocados pelo e-mail transacional;
    /// ver readme.txt pra registro do achado como item separado.
    /// </summary>
    public static class PostgrestTime
    {
        public static DateTime? ToTrueUtc(DateTime? value)
        {
            if (value is null) return null;
            return ToTrueUtc(value.Value);
        }

        public static DateTime ToTrueUtc(DateTime value)
            => value.Kind == DateTimeKind.Utc
                ? value
                : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
    }
}
