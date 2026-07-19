using AndressaLeite.Services;
using Postgrest.Models;

namespace AndressaLeite.Models
{
    /// <summary>
    /// Assinatura do salão (billing via Asaas, readme.txt 4.9/9.2) — tabela
    /// separada de tenants, relação 1:1 (mesmo padrão de TeamInvite/
    /// PlatformAdmin: identidade/estado que não cabe na tabela principal).
    /// </summary>
    [Postgrest.Attributes.Table("tenant_subscriptions")]
    public class TenantSubscription : BaseModel
    {
        [Postgrest.Attributes.PrimaryKey("id", true)]
        public string Id { get; set; } = string.Empty;

        [Postgrest.Attributes.Column("tenant_id")]
        public string TenantId { get; set; } = string.Empty;

        /// <summary>trial | pending_payment | active | overdue | suspended | cancelled</summary>
        [Postgrest.Attributes.Column("status")]
        public string Status { get; set; } = "trial";

        [Postgrest.Attributes.Column("plan_price")]
        public decimal PlanPrice { get; set; } = 149.90m;

        // Todos os campos DateTime/DateTime? abaixo passam por
        // PostgrestTime.ToTrueUtc no setter — ver o mesmo comentário em
        // Models/Appointement.cs (readme.txt 12.2.a). Sem isso, a conta
        // de tolerância de atraso (5 dias) fica errada em dev fora de UTC.

        private DateTime _trialEndsAt;
        [Postgrest.Attributes.Column("trial_ends_at")]
        public DateTime TrialEndsAt
        {
            get => _trialEndsAt;
            set => _trialEndsAt = PostgrestTime.ToTrueUtc(value);
        }

        [Postgrest.Attributes.Column("asaas_customer_id")]
        public string? AsaasCustomerId { get; set; }

        [Postgrest.Attributes.Column("asaas_subscription_id")]
        public string? AsaasSubscriptionId { get; set; }

        [Postgrest.Attributes.Column("asaas_checkout_id")]
        public string? AsaasCheckoutId { get; set; }

        private DateTime? _nextDueDate;
        [Postgrest.Attributes.Column("next_due_date")]
        public DateTime? NextDueDate
        {
            get => _nextDueDate;
            set => _nextDueDate = PostgrestTime.ToTrueUtc(value);
        }

        private DateTime? _overdueSince;
        [Postgrest.Attributes.Column("overdue_since")]
        public DateTime? OverdueSince
        {
            get => _overdueSince;
            set => _overdueSince = PostgrestTime.ToTrueUtc(value);
        }

        private DateTime? _lastPaymentAt;
        [Postgrest.Attributes.Column("last_payment_at")]
        public DateTime? LastPaymentAt
        {
            get => _lastPaymentAt;
            set => _lastPaymentAt = PostgrestTime.ToTrueUtc(value);
        }

        private DateTime _createdAt;
        [Postgrest.Attributes.Column("created_at")]
        public DateTime CreatedAt
        {
            get => _createdAt;
            set => _createdAt = PostgrestTime.ToTrueUtc(value);
        }

        private DateTime _updatedAt;
        [Postgrest.Attributes.Column("updated_at")]
        public DateTime UpdatedAt
        {
            get => _updatedAt;
            set => _updatedAt = PostgrestTime.ToTrueUtc(value);
        }
    }
}
