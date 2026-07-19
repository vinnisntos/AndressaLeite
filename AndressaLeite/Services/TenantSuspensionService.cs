using AndressaLeite.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AndressaLeite.Services
{
    /// <summary>
    /// Job de suspensão por billing (readme.txt 4.9/9.2) — segundo
    /// BackgroundService do projeto, mesmo molde de
    /// Services/AppointmentReminderService.cs (PeriodicTimer,
    /// IServiceScopeFactory, tick isolado por item). A cada tick:
    ///   1) tenants sem linha em tenant_subscriptions ganham uma nova
    ///      (trial de 14 dias a partir de agora — não retroativo, não
    ///      penaliza quem já usava o app antes desta feature existir);
    ///   2) trial vencido sem assinatura ativa é suspenso;
    ///   3) atraso além dos 5 dias de tolerância é suspenso.
    /// Mesma limitação assumida do AppointmentReminderService: só
    /// funciona certo com uma única instância do container rodando (sem
    /// lock distribuído) — bate com o deploy atual (EC2 single-instance).
    /// </summary>
    public class TenantSuspensionService : BackgroundService
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan TrialLength = TimeSpan.FromDays(14);
        private static readonly TimeSpan GracePeriod = TimeSpan.FromDays(5);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<TenantSuspensionService> _logger;

        // IMemoryCache é singleton (AddMemoryCache() em Program.cs) — pode
        // entrar direto no construtor, diferente do Supabase.Client (scoped,
        // só disponível via CreateScope() a cada tick).
        public TenantSuspensionService(IServiceScopeFactory scopeFactory, IMemoryCache cache, ILogger<TenantSuspensionService> logger)
        {
            _scopeFactory = scopeFactory;
            _cache = cache;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TickInterval);
            do
            {
                try
                {
                    await ProcessSubscriptionsAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha inesperada num tick do TenantSuspensionService");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        private async Task ProcessSubscriptionsAsync()
        {
            using var scope = _scopeFactory.CreateScope();
            var supabase = scope.ServiceProvider.GetRequiredService<Supabase.Client>();

            var tenantsResponse = await supabase.From<Tenant>().Get();
            var subscriptionsResponse = await supabase.From<TenantSubscription>().Get();
            var subscriptionsByTenant = subscriptionsResponse.Models.ToDictionary(s => s.TenantId);

            var now = DateTime.UtcNow;

            foreach (var tenant in tenantsResponse.Models)
            {
                try
                {
                    if (!subscriptionsByTenant.TryGetValue(tenant.Id, out var subscription))
                    {
                        // Backfill: tenant sem linha ainda (criado antes
                        // desta feature existir, ou falha no insert do
                        // onboarding) — trial novo a partir de agora.
                        var newSubscription = new TenantSubscription
                        {
                            Id = Guid.NewGuid().ToString(),
                            TenantId = tenant.Id,
                            Status = "trial",
                            TrialEndsAt = now.Add(TrialLength)
                        };
                        await supabase.From<TenantSubscription>().Insert(newSubscription);
                        continue;
                    }

                    if (subscription.Status == "trial" && subscription.TrialEndsAt < now)
                    {
                        await SuspendAsync(supabase, tenant, subscription);
                    }
                    else if (subscription.Status == "overdue" && subscription.OverdueSince is not null
                        && subscription.OverdueSince.Value.Add(GracePeriod) < now)
                    {
                        await SuspendAsync(supabase, tenant, subscription);
                    }
                }
                catch (Exception ex)
                {
                    // Um tenant com problema não pode travar a checagem
                    // dos demais no mesmo tick.
                    _logger.LogError(ex, "Falha ao processar assinatura do tenant {TenantId}", tenant.Id);
                }
            }
        }

        private async Task SuspendAsync(Supabase.Client supabase, Tenant tenant, TenantSubscription subscription)
        {
            if (tenant.IsActive)
            {
                await supabase.From<Tenant>()
                    .Where(x => x.Id == tenant.Id)
                    .Set(x => x.IsActive, false)
                    .Update();

                // Sem isso, a suspensão só valeria depois do TTL do cache
                // do TenantResolutionMiddleware expirar sozinho (até 60s)
                // — mesma chamada que Dashboard.OnPostToggleTenantAsync já
                // faz pro toggle manual.
                _cache.Remove(TenantResolutionMiddleware.CacheKey(tenant.Slug));
            }

            await supabase.From<TenantSubscription>()
                .Where(x => x.Id == subscription.Id)
                .Set(x => x.Status, "suspended")
                .Update();

            _logger.LogWarning("Tenant {TenantId} ({Slug}) suspenso por billing", tenant.Id, tenant.Slug);
        }
    }
}
