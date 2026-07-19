using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using AndressaLeite.Models;
using AndressaLeite.Services;

namespace AndressaLeite.Pages.Webhooks
{
    /// <summary>
    /// Recebe eventos de pagamento do Asaas (billing, readme.txt 4.9/9.2)
    /// — endpoint público (pasta /Webhooks liberada em Program.cs via
    /// AllowAnonymousToFolder), autenticado por um header compartilhado
    /// (asaas-access-token), não por cookie de sessão. A Asaas não manda o
    /// antiforgery token do ASP.NET, então [IgnoreAntiforgeryToken] é
    /// necessário — a validação de segurança real é o header.
    ///
    /// A Asaas não tem webhook específico de assinatura, só de cobrança —
    /// rastreio qual tenant é qual cobrança via externalReference (setado
    /// = tenant.Id na criação do checkout, ver Services/AsaasService.cs).
    /// </summary>
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("asaas-webhook")]
    public class AsaasModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AsaasModel> _logger;

        public AsaasModel(Supabase.Client supabase, IMemoryCache cache, IConfiguration configuration, ILogger<AsaasModel> logger)
        {
            _supabase = supabase;
            _cache = cache;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var expectedToken = _configuration["Asaas:WebhookToken"];
            var receivedToken = Request.Headers["asaas-access-token"].ToString();
            if (string.IsNullOrEmpty(expectedToken) || !string.Equals(expectedToken, receivedToken, StringComparison.Ordinal))
            {
                _logger.LogWarning("Webhook Asaas rejeitado: token inválido ou ausente.");
                return Unauthorized();
            }

            AsaasWebhookPayload? payload;
            try
            {
                using var reader = new StreamReader(Request.Body);
                var raw = await reader.ReadToEndAsync();
                payload = JsonSerializer.Deserialize<AsaasWebhookPayload>(raw,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao desserializar payload do webhook Asaas.");
                // 200 mesmo em erro de parsing — evita a Asaas re-tentar
                // pra sempre um payload que nunca vai conseguir processar.
                return new OkResult();
            }

            var tenantId = payload?.Payment?.ExternalReference;
            if (payload?.Payment is null || string.IsNullOrWhiteSpace(tenantId))
            {
                _logger.LogWarning("Webhook Asaas sem externalReference reconhecível (event={Event}).", payload?.Event);
                return new OkResult();
            }

            var subscription = await _supabase.From<TenantSubscription>()
                .Where(x => x.TenantId == tenantId)
                .Single();
            if (subscription is null)
            {
                _logger.LogWarning("Webhook Asaas: nenhuma assinatura encontrada pro tenant {TenantId}.", tenantId);
                return new OkResult();
            }

            switch (payload.Event)
            {
                case "PAYMENT_CONFIRMED":
                case "PAYMENT_RECEIVED":
                    await MarkActiveAsync(subscription, payload.Payment, tenantId);
                    break;
                case "PAYMENT_OVERDUE":
                    await MarkOverdueAsync(subscription);
                    break;
                // Outros eventos (reembolso, chargeback, etc.) só são
                // reconhecidos e ignorados por enquanto — fora do escopo
                // desta rodada.
            }

            return new OkResult();
        }

        private async Task MarkActiveAsync(TenantSubscription subscription, AsaasPaymentPayload payment, string tenantId)
        {
            subscription.Status = "active";
            subscription.AsaasCustomerId = payment.Customer;
            subscription.AsaasSubscriptionId = payment.Subscription;
            subscription.LastPaymentAt = DateTime.UtcNow;
            // payment.DueDate vem do JSON do webhook via System.Text.Json,
            // não do Postgrest — não passou pela conversão de fuso
            // "bugada" que PostgrestTime.ToTrueUtc corrige (ver
            // Services/PostgrestTime.cs). Marca como Utc explicitamente
            // antes de atribuir, senão o setter do modelo trataria como
            // "precisa corrigir" e deslocaria o valor por engano.
            subscription.NextDueDate = payment.DueDate.HasValue
                ? DateTime.SpecifyKind(payment.DueDate.Value, DateTimeKind.Utc)
                : null;
            subscription.OverdueSince = null;
            await _supabase.From<TenantSubscription>().Update(subscription);

            var tenant = await _supabase.From<Tenant>().Where(x => x.Id == tenantId).Single();
            if (tenant is not null && !tenant.IsActive)
            {
                await _supabase.From<Tenant>()
                    .Where(x => x.Id == tenant.Id)
                    .Set(x => x.IsActive, true)
                    .Update();
                // Sem isso, a reativação só valeria depois do TTL do cache
                // do TenantResolutionMiddleware expirar sozinho (até 60s).
                _cache.Remove(TenantResolutionMiddleware.CacheKey(tenant.Slug));
            }
        }

        private async Task MarkOverdueAsync(TenantSubscription subscription)
        {
            // Não reseta a contagem de tolerância a cada webhook repetido
            // — só marca overdue_since na PRIMEIRA vez que ficar atrasado.
            if (subscription.OverdueSince is not null) return;

            subscription.Status = "overdue";
            subscription.OverdueSince = DateTime.UtcNow;
            await _supabase.From<TenantSubscription>().Update(subscription);
        }

        private class AsaasWebhookPayload
        {
            [JsonPropertyName("event")]
            public string? Event { get; set; }

            [JsonPropertyName("payment")]
            public AsaasPaymentPayload? Payment { get; set; }
        }

        private class AsaasPaymentPayload
        {
            [JsonPropertyName("customer")]
            public string? Customer { get; set; }

            [JsonPropertyName("subscription")]
            public string? Subscription { get; set; }

            [JsonPropertyName("externalReference")]
            public string? ExternalReference { get; set; }

            [JsonPropertyName("dueDate")]
            public DateTime? DueDate { get; set; }
        }
    }
}
