using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AndressaLeite.Services;

namespace AndressaLeite.Pages
{
    public class IndexModel : PageModel
    {
        public IActionResult OnGet()
        {
            // Se o usuário já está autenticado, manda direto pro dashboard da role dele.
            // Antes, este handler olhava um cookie "supabase_session" que nunca era
            // gravado em nenhum lugar do código — o login só emite o cookie de auth do
            // ASP.NET Core. Removido por parecer "inteligente" mas nunca disparar.
            if (User.Identity?.IsAuthenticated == true)
            {
                var role = AuthorizationService.GetRole(User);
                return RedirectToPage(AuthorizationService.GetDefaultLandingForRole(role).TrimStart('/'));
            }
            return Page();
        }
    }
}
