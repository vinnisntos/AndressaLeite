using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Supabase.Gotrue;

namespace AndressaLeite.Pages.Auth
{
    [EnableRateLimiting("signup")]
    public class CadastroModel : PageModel
    {
        [BindProperty]
        [Required(ErrorMessage = "Nome é obrigatório.")]
        [StringLength(120, MinimumLength = 2, ErrorMessage = "O nome deve ter entre 2 e 120 caracteres.")]
        [RegularExpression(@"^[\p{L}\p{M}'\.\- ]+$",
            ErrorMessage = "O nome contém caracteres inválidos.")]
        public string FullName { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "E-mail é obrigatório.")]
        [EmailAddress(ErrorMessage = "Informe um e-mail válido.")]
        public string Email { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "Celular é obrigatório.")]
        [RegularExpression(@"^\+?[1-9]\d{10,14}$",
            ErrorMessage = "Telefone inválido. Use DDI + DDD + número (somente dígitos).")]
        public string Phone { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "Senha é obrigatória.")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "A senha deve ter no mínimo 8 caracteres.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [BindProperty]
        [Required(ErrorMessage = "A confirmação da senha é obrigatória.")]
        [Compare("Password", ErrorMessage = "As senhas informadas não coincidem.")]
        [DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = string.Empty;

        [TempData] public string? InfoMessage { get; set; }
        [TempData] public string? ErrorMessage { get; set; }

        public IActionResult OnGet()
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToPage("/Index");
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync([FromServices] Supabase.Client supabase)
        {
            if (!ModelState.IsValid) return Page();

            // Validação extra de complexidade: pelo menos 1 letra e 1 número.
            // (Não exigimos caractere especial — alinhado com NIST SP 800-63B.)
            if (!Regex.IsMatch(Password, @"[A-Za-z]") || !Regex.IsMatch(Password, @"\d"))
            {
                ModelState.AddModelError(nameof(Password),
                    "A senha deve conter pelo menos uma letra e um número.");
                return Page();
            }

            // Normaliza telefone mantendo só dígitos (já validado pelo regex
            // do ModelState, então é seguro descartar tudo que não for dígito).
            var cleanPhone = Regex.Replace(Phone, @"[^\d]", "");

            var metadata = new Dictionary<string, object>
            {
                { "full_name", FullName },
                { "phone", cleanPhone },
                { "role", "client" }
            };

            var options = new SignUpOptions { Data = metadata };

            try
            {
                var session = await supabase.Auth.SignUp(Email, Password, options);

                if (session?.User is null)
                {
                    ErrorMessage = "Não foi possível concluir o cadastro. E-mail pode já estar cadastrado.";
                    return Page();
                }

                // Valida se o Id foi corretamente gerado
                if (string.IsNullOrWhiteSpace(session.User.Id))
                {
                    ErrorMessage = "Erro ao criar conta. Tente novamente.";
                    return Page();
                }

                // Cria o perfil do cliente no banco
                var profile = new AndressaLeite.Models.Profile
                {
                    Id = session.User.Id,
                    FullName = FullName.Trim(),
                    Phone = cleanPhone,
                    Role = "client"
                };

                try
                {
                    await supabase.From<AndressaLeite.Models.Profile>().Insert(profile);
                }
                catch
                {
                    // Se falhar ao criar profile, continua mesmo assim
                }

                // Auto-login após cadastro (sem exigir email confirmado)
                var claims = new System.Security.Claims.ClaimsIdentity(
                    new[]
                    {
                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, session.User.Id),
                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, Email),
                        new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "client")
                    },
                    CookieAuthenticationDefaults.AuthenticationScheme
                );

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new System.Security.Claims.ClaimsPrincipal(claims),
                    new Microsoft.AspNetCore.Authentication.AuthenticationProperties
                    {
                        IsPersistent = true,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
                    }
                );

                return RedirectToPage("/Cliente/DashCliente");
            }
            catch (Supabase.Gotrue.Exceptions.GotrueException gex) when (gex.StatusCode == 422)
            {
                // 422: e-mail já cadastrado, ou senha fraca do lado Supabase.
                // Mensagem genérica para não confirmar/negaciar a existência do e-mail.
                ErrorMessage = "Não foi possível concluir o cadastro. Verifique os dados e tente novamente.";
                return Page();
            }
            catch (Exception)
            {
                ErrorMessage = "Não foi possível concluir o cadastro. Tente novamente em instantes.";
                return Page();
            }
        }
    }
}
