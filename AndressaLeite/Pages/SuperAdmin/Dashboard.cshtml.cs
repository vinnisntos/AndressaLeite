using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using AndressaLeite.Models;
using AndressaLeite.Services;

namespace AndressaLeite.Pages.SuperAdmin
{
    /// <summary>
    /// Painel cross-tenant do dono da plataforma MarcAi: lista TODOS os
    /// salões (única tela do app que intencionalmente não filtra por
    /// tenant) e permite ativar/desativar a "licença" de cada um.
    /// </summary>
    [Authorize(Policy = "SuperAdminOnly")]
    public class DashboardModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        private readonly ILogger<DashboardModel> _logger;
        private readonly IMemoryCache _cache;

        public DashboardModel(Supabase.Client supabase, ILogger<DashboardModel> logger, IMemoryCache cache)
        {
            _supabase = supabase;
            _logger = logger;
            _cache = cache;
        }

        public List<Tenant> Tenants { get; set; } = new();

        /// <summary>Status de billing por tenant.Id (readme.txt 4.9/9.2) — tenant sem linha ainda não aparece aqui.</summary>
        public Dictionary<string, string> SubscriptionStatusByTenant { get; set; } = new();

        [TempData] public string? SuccessMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            var response = await _supabase.From<Tenant>().Get();
            Tenants = response.Models.OrderBy(t => t.Name).ToList();

            var subscriptionsResponse = await _supabase.From<TenantSubscription>().Get();
            SubscriptionStatusByTenant = subscriptionsResponse.Models.ToDictionary(s => s.TenantId, s => s.Status);

            return Page();
        }


        /// <summary>
        /// POST: ativa/desativa a licença de um salão (tenants.is_active).
        /// Sem billing ainda (ver readme.txt), é o único jeito de desligar
        /// um tenant problemático.
        /// </summary>
        public async Task<IActionResult> OnPostToggleTenantAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                ErrorMessage = "Salão inválido.";
                return RedirectToPage();
            }

            try
            {
                var tenant = await _supabase.From<Tenant>().Where(x => x.Id == id).Single();
                if (tenant is null)
                {
                    ErrorMessage = "Salão não encontrado.";
                    return RedirectToPage();
                }

                await _supabase.From<Tenant>()
                    .Where(x => x.Id == id)
                    .Set(x => x.IsActive, !tenant.IsActive)
                    .Update();

                // Sem isso, a mudança só valeria depois do TTL do cache do
                // TenantResolutionMiddleware expirar sozinho (até 60s) —
                // mesmo cuidado já tomado em
                // DashAdmin.OnPostUpdateBusinessHoursAsync.
                _cache.Remove(TenantResolutionMiddleware.CacheKey(tenant.Slug));

                SuccessMessage = tenant.IsActive
                    ? $"Salão \"{tenant.Name}\" desativado."
                    : $"Salão \"{tenant.Name}\" reativado.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao alternar status do tenant {TenantId}", id);
                ErrorMessage = "Não foi possível atualizar o salão.";
            }

            return RedirectToPage();
        }
    }
}
