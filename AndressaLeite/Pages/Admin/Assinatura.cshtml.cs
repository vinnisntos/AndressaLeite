using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using AndressaLeite.Models;
using AndressaLeite.Services;

namespace AndressaLeite.Pages.Admin
{
    /// <summary>
    /// Assinatura do salão (billing via Asaas, readme.txt 4.9/9.2) — página
    /// dedicada, mesmo padrão de Pages/SuperAdmin/Security.cshtml (concern
    /// isolado, não engorda DashAdmin.cshtml.cs). Protegida automaticamente
    /// pela convenção AuthorizeFolder("/Admin", "AdminOnly") já existente.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    public class AssinaturaModel : PageModel
    {
        private readonly Supabase.Client _supabase;
        private readonly IAsaasService _asaasService;
        private readonly CurrentTenant _currentTenant;
        private readonly ILogger<AssinaturaModel> _logger;

        public AssinaturaModel(Supabase.Client supabase, IAsaasService asaasService, CurrentTenant currentTenant, ILogger<AssinaturaModel> logger)
        {
            _supabase = supabase;
            _asaasService = asaasService;
            _currentTenant = currentTenant;
            _logger = logger;
        }

        public string Status { get; set; } = "trial";
        public decimal PlanPrice { get; set; } = 149.90m;
        public int? TrialDaysLeft { get; set; }
        public DateTime? NextDueDate { get; set; }
        public int? GraceDaysLeft { get; set; }

        /// <summary>Mostra o formulário de assinatura enquanto não há uma assinatura paga ativa.</summary>
        public bool ShowSubscribeForm => Status is "trial" or "pending_payment" or "overdue" or "suspended" or "cancelled";

        [BindProperty]
        [Required(ErrorMessage = "Informe seu nome completo.")]
        [StringLength(120, MinimumLength = 2, ErrorMessage = "O nome deve ter entre 2 e 120 caracteres.")]
        public string OwnerName { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "Informe seu CPF ou CNPJ.")]
        [RegularExpression(@"^\d{11}$|^\d{14}$", ErrorMessage = "Informe um CPF (11 dígitos) ou CNPJ (14 dígitos) válido, só números.")]
        public string CpfCnpj { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "Informe seu telefone.")]
        [RegularExpression(@"^\+?[1-9]\d{10,14}$", ErrorMessage = "Telefone inválido. Use DDI + DDD + número (somente dígitos).")]
        public string Phone { get; set; } = string.Empty;

        [TempData] public string? ErrorMessage { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (AuthorizationService.GetRole(User) != "admin") return Forbid();
            if (!_currentTenant.IsResolved) return Forbid();

            var subscription = await GetOrCreateSubscriptionAsync();
            PopulateDisplayFields(subscription);

            if (AuthorizationService.TryGetUserId(User, out var userId))
            {
                var userIdStr = userId.ToString();
                var profile = await _supabase.From<Profile>()
                    .Where(x => x.Id == userIdStr)
                    .Where(x => x.TenantId == _currentTenant.Id)
                    .Single();
                if (profile is not null)
                {
                    OwnerName = profile.FullName;
                    Phone = profile.Phone;
                }
            }

            return Page();
        }

        /// <summary>
        /// POST: cria o checkout hospedado no Asaas e redireciona o admin
        /// pra lá — a Asaas coleta PIX/cartão na própria página deles, eu
        /// nunca vejo dado de cartão (ver Services/AsaasService.cs).
        /// </summary>
        public async Task<IActionResult> OnPostAssinarAsync()
        {
            if (AuthorizationService.GetRole(User) != "admin") return Forbid();
            if (!_currentTenant.IsResolved) return Forbid();
            if (!ModelState.IsValid) return await OnGetAsync();

            var subscription = await GetOrCreateSubscriptionAsync();
            var cleanCpfCnpj = Regex.Replace(CpfCnpj, @"[^\d]", "");
            var cleanPhone = Regex.Replace(Phone, @"[^\d]", "");
            var ownerEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? string.Empty;

            // Se ainda está no trial, a primeira cobrança só acontece
            // quando o trial vencer; se já venceu/está suspenso, cobra já.
            var nextDueDate = subscription.Status == "trial" && subscription.TrialEndsAt > DateTime.UtcNow
                ? subscription.TrialEndsAt
                : DateTime.UtcNow;

            var baseUrl = $"{Request.Scheme}://{Request.Host}/Admin/Assinatura";

            try
            {
                var result = await _asaasService.CreateCheckoutSessionAsync(new AsaasCheckoutRequest(
                    TenantId: _currentTenant.Id!,
                    TenantName: _currentTenant.Name ?? "salão",
                    OwnerName: OwnerName.Trim(),
                    OwnerEmail: ownerEmail,
                    CpfCnpj: cleanCpfCnpj,
                    Phone: cleanPhone,
                    NextDueDate: nextDueDate,
                    Value: subscription.PlanPrice,
                    SuccessUrl: baseUrl,
                    CancelUrl: baseUrl,
                    ExpiredUrl: baseUrl
                ));

                subscription.AsaasCheckoutId = result.CheckoutId;
                subscription.Status = "pending_payment";
                await _supabase.From<TenantSubscription>().Update(subscription);

                return Redirect(result.CheckoutUrl);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao criar checkout Asaas pro tenant {TenantId}", _currentTenant.Id);
                ErrorMessage = "Não foi possível iniciar a assinatura agora. Tente novamente em instantes.";
                return RedirectToPage();
            }
        }

        /// <summary>
        /// Backfill: tenant sem linha em tenant_subscriptions ainda (mesmo
        /// raciocínio do TenantSuspensionService — trial novo a partir de
        /// agora, não retroativo).
        /// </summary>
        private async Task<TenantSubscription> GetOrCreateSubscriptionAsync()
        {
            var existing = await _supabase.From<TenantSubscription>()
                .Where(x => x.TenantId == _currentTenant.Id)
                .Single();
            if (existing is not null) return existing;

            var created = new TenantSubscription
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = _currentTenant.Id!,
                Status = "trial",
                TrialEndsAt = DateTime.UtcNow.AddDays(14)
            };
            await _supabase.From<TenantSubscription>().Insert(created);
            return created;
        }

        private void PopulateDisplayFields(TenantSubscription subscription)
        {
            Status = subscription.Status;
            PlanPrice = subscription.PlanPrice;
            NextDueDate = subscription.NextDueDate;

            if (subscription.Status == "trial")
            {
                var daysLeft = (subscription.TrialEndsAt - DateTime.UtcNow).TotalDays;
                TrialDaysLeft = Math.Max(0, (int)Math.Ceiling(daysLeft));
            }

            if (subscription.Status == "overdue" && subscription.OverdueSince is not null)
            {
                var graceLeft = 5 - (DateTime.UtcNow - subscription.OverdueSince.Value).TotalDays;
                GraceDaysLeft = Math.Max(0, (int)Math.Ceiling(graceLeft));
            }
        }
    }
}
