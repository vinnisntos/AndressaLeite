namespace AndressaLeite.Services
{
    /// <summary>
    /// Tenant (salão) resolvido para a requisição atual, a partir do
    /// subdomínio (ver TenantResolutionMiddleware). Scoped: uma instância
    /// por requisição, populada uma única vez pelo middleware antes de
    /// qualquer PageModel rodar.
    /// </summary>
    public class CurrentTenant
    {
        public string? Id { get; set; }
        public string? Slug { get; set; }
        public string? Name { get; set; }
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Horário de funcionamento do salão, já convertido pra TimeSpan
        /// pelo TenantResolutionMiddleware (o valor persistido é string
        /// "HH:mm:ss" — ver Models/Tenant.cs). Editável pelo admin em
        /// DashAdmin ("Horário de Funcionamento").
        /// </summary>
        public TimeSpan BusinessOpenTime { get; set; } = new(9, 0, 0);
        public TimeSpan BusinessCloseTime { get; set; } = new(19, 0, 0);

        /// <summary>Null = sem intervalo de almoço configurado.</summary>
        public TimeSpan? LunchStartTime { get; set; }
        public TimeSpan? LunchEndTime { get; set; }

        /// <summary>True quando o subdomínio bateu com um tenant real.</summary>
        public bool IsResolved => Id != null;

        /// <summary>
        /// True quando a requisição chegou pelo domínio raiz (ou "www"),
        /// sem nenhum slug de tenant — contexto de marketing/onboarding da
        /// plataforma, não de um salão específico.
        /// </summary>
        public bool IsPlatformContext { get; set; }
    }
}
