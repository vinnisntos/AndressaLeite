using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AndressaLeite.Services;

namespace AndressaLeite.Pages
{
    public class IndexModel : PageModel
    {
        private readonly CurrentTenant _currentTenant;

        public IndexModel(CurrentTenant currentTenant) => _currentTenant = currentTenant;

        /// <summary>
        /// Estado da página, resolvido a partir do CurrentTenant (subdomínio
        /// da requisição): "platform" (domínio raiz, marketing/onboarding),
        /// "not_found" (subdomínio que não bate com nenhum salão),
        /// "suspended" (salão existe mas está desativado), "tenant" (salão
        /// ativo normal).
        /// </summary>
        public string ViewState { get; private set; } = "platform";

        public IActionResult OnGet()
        {
            if (_currentTenant.IsPlatformContext)
            {
                ViewState = "platform";
            }
            else if (!_currentTenant.IsResolved)
            {
                ViewState = "not_found";
            }
            else if (!_currentTenant.IsActive)
            {
                ViewState = "suspended";
            }
            else
            {
                ViewState = "tenant";

                // Antes olhava um cookie "supabase_session" que nunca era
                // gravado em nenhum lugar do código — o login só emite o
                // cookie de auth do ASP.NET Core. Removido por parecer
                // "inteligente" mas nunca disparar.
                if (User.Identity?.IsAuthenticated == true)
                {
                    var role = AuthorizationService.GetRole(User);
                    return RedirectToPage(AuthorizationService.GetDefaultLandingForRole(role).TrimStart('/'));
                }
            }

            return Page();
        }
    }
}
