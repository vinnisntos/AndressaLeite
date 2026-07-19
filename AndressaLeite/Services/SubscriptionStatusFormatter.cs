namespace AndressaLeite.Services
{
    /// <summary>
    /// Rótulo/cor do badge de status de assinatura (readme.txt 4.9/9.2) —
    /// compartilhado entre Pages/Admin/DashAdmin.cshtml e
    /// Pages/SuperAdmin/Dashboard.cshtml, únicos dois lugares que exibem
    /// esse status hoje.
    /// </summary>
    public static class SubscriptionStatusFormatter
    {
        public static (string Label, string CssClass) Format(string? status) => status switch
        {
            "trial" => ("Teste", "bg-info"),
            "pending_payment" => ("Aguardando pagamento", "bg-warning text-dark"),
            "active" => ("Assinatura ativa", "bg-success"),
            "overdue" => ("Pagamento atrasado", "bg-danger"),
            "suspended" => ("Suspenso por billing", "bg-dark"),
            "cancelled" => ("Cancelada", "bg-secondary"),
            _ => ("Sem assinatura", "bg-secondary")
        };
    }
}
