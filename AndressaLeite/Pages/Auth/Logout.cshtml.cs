using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AndressaLeite.Pages.Auth
{
    /// <summary>
    /// Handler do logout. Existe porque Program.cs aponta
    /// options.LogoutPath = "/Auth/Logout" — sem este arquivo, o path
    /// retorna 404 e o cookie de autenticação nunca é removido.
    /// </summary>
    public class LogoutModel : PageModel
    {
        public async Task<IActionResult> OnPostAsync()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToPage("/Auth/Login");
        }
    }
}
