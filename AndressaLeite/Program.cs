using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Supabase;
using AndressaLeite.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. CONFIGURAÇÃO DO SUPABASE
var supabaseUrl = builder.Configuration["Supabase:Url"];
var supabaseSecretKey = builder.Configuration["Supabase:SecretKey"];
var supabaseOptions = new SupabaseOptions
{
    AutoRefreshToken = true,
    AutoConnectRealtime = true
};
builder.Services.AddScoped(provider => new Supabase.Client(supabaseUrl!, supabaseSecretKey!, supabaseOptions));

// 1b. MULTI-TENANCY — resolução de salão por subdomínio (ver
// Services/TenantResolutionMiddleware.cs e Services/CurrentTenant.cs).
builder.Services.AddMemoryCache();
builder.Services.AddScoped<CurrentTenant>();
builder.Services.AddScoped<AppointmentBookingService>();

// 2. SEGURANÇA E AUTENTICAÇÃO
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";
        options.LogoutPath = "/Auth/Logout";
        options.AccessDeniedPath = "/Auth/AcessoNegado";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        // SameSite=Lax é o padrão correto: o cookie acompanha navegações
        // top-level (clique em link, redirect pós-login) e bloqueia envios
        // cross-site de formulário — o que protege contra CSRF para POSTs.
        // Não usar SameSite=None em localhost a menos que o app seja
        // embarcado em iframe cross-site; aqui não é o caso.
        options.Cookie.SameSite = SameSiteMode.Lax;
        // O COOKIE PRECISA SER COMPATÍVEL COM O ESQUEMA DA REQUISIÇÃO.
        // Se você roda dev em http://localhost:5081 (HTTP puro), o cookie
        // NÃO PODE ser Secure, senão o browser descarta. Em produção
        // (HTTPS sempre) o cookie DEVE ser Secure. SameAsRequest faz
        // exatamente isso: herda do request, sem impor um esquema fixo.
        // Por isso NÃO mude para Always em dev — isso é o que causava
        // o loop (browser rejeitava o cookie de login).
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));
    options.AddPolicy("EmployeeOnly", policy => policy.RequireRole("employee"));
    options.AddPolicy("ClientOnly", policy => policy.RequireRole("client"));
    // Admin é "profissional premium" (Fase 6 do roadmap, readme.txt): tem
    // agenda própria além do painel de gestão. RequireRole já aceita
    // múltiplas roles como OR — não precisa de lógica extra.
    options.AddPolicy("EmployeeOrAdmin", policy => policy.RequireRole("employee", "admin"));
    // Superadmin da plataforma MarcAi — cross-tenant, conta em
    // platform_admins (não em profiles). Ver Models/PlatformAdmin.cs.
    options.AddPolicy("SuperAdminOnly", policy => policy.RequireRole("superadmin"));
});

// 3. RATE LIMITING — defesa contra força bruta em login/cadastro e criação
// em massa de contas. Usa o middleware built-in do .NET 7+ (sem pacote extra).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) =>
    {
        context.HttpContext.Response.Headers["Retry-After"] = "60";
        await context.HttpContext.Response.WriteAsync("Muitas tentativas. Tente novamente em instantes.");
    };

    // Política "login" — 5 tentativas por minuto por IP.
    options.AddPolicy("login", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    // Política "signup" — 3 contas por hora por IP.
    options.AddPolicy("signup", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 3,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    // Política "onboarding" — criar um salão novo é mais "caro" de abusar
    // que um cadastro de cliente comum (spam de tenants, land-grab de
    // slugs bons), então bem mais restrita: 3 por dia por IP.
    options.AddPolicy("onboarding", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 3,
            Window = TimeSpan.FromDays(1),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

// 4. CONVENÇÕES DE ROTAS
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
    options.Conventions.AuthorizeFolder("/Profissional", "EmployeeOrAdmin");
    options.Conventions.AuthorizeFolder("/Cliente", "ClientOnly");
    options.Conventions.AuthorizeFolder("/SuperAdmin", "SuperAdminOnly");

    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToFolder("/Auth");
    options.Conventions.AllowAnonymousToFolder("/Onboarding");
    // Login do superadmin precisa ser acessível sem estar autenticado —
    // AllowAnonymousToPage tem prioridade sobre o AuthorizeFolder acima
    // pra essa página específica (convenção padrão do Razor Pages).
    options.Conventions.AllowAnonymousToPage("/SuperAdmin/Login");

    // Rate limit por página é aplicado direto nas page models (Login.cshtml.cs
    // e Cadastro.cshtml.cs) com [EnableRateLimiting("login"|"signup")].
    // Mais simples e legível que uma convenção, e o atributo é exposto
    // pelo middleware como endpoint metadata — o caminho certo em .NET 7+.
});

var app = builder.Build();

// 5. PIPELINE — a ordem importa: error → security headers → HSTS → HTTPS →
// static → routing → rate limit → auth → antiforgery → endpoint
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    // Em produção: força HTTPS. Em dev, o app roda em http://localhost e
    // forçar HTTPS quebraria o login (cookie seria gravado em HTTP e o
    // browser descartaria por causa do SecurePolicy).
    app.UseHttpsRedirection();
}
else
{
    // Em dev também usamos o handler genérico para não vazar stack no browser.
    app.UseExceptionHandler("/Error");
}

// Security headers middleware. Adiciona cabeçalhos em TODAS as respostas,
// mesmo em 404/erro. Importante colocar antes do pipeline de auth/endpoint.
app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    // Anti-clickjacking
    headers["X-Frame-Options"] = "DENY";
    // Anti-MIME-sniff
    headers["X-Content-Type-Options"] = "nosniff";
    // Referrer mínimo
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    // Permissões mínimas (sem câmera/micro/geo por padrão)
    headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    // CSP. App é simples (Bootstrap via CDN, Google Fonts) — limita origens.
    // Em produção, considere adicionar hashes nonces se injetar scripts inline.
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "img-src 'self' data:; " +
        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "script-src 'self' https://cdn.jsdelivr.net; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";
    await next();
});

// Resolução de tenant (salão) pelo subdomínio. Cedo no pipeline — só
// depende do Host da requisição, e todo o resto (auth, páginas) depende
// do CurrentTenant já estar populado.
app.UseMiddleware<TenantResolutionMiddleware>();

app.UseStaticFiles();
app.UseRouting();

// Rate limit antes de auth — limita tentativas ANTES de qualquer trabalho.
app.UseRateLimiter();

app.UseAuthentication();

// Rede de segurança de isolamento multi-tenant: o cookie de auth já é
// host-scoped por padrão (sem Cookie.Domain configurado, não é enviado
// entre subdomínios diferentes), mas isso é só proteção de browser — uma
// requisição HTTP arbitrária pode enviar um cookie válido de um tenant
// com o Host de outro. Aqui comparamos a claim tenant_id gravada no login
// contra o CurrentTenant resolvido pela URL atual; se divergir, a sessão
// não é confiável para este subdomínio e é encerrada.
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true)
    {
        var currentTenant = ctx.RequestServices.GetRequiredService<CurrentTenant>();
        if (currentTenant.IsResolved &&
            AuthorizationService.TryGetTenantId(ctx.User, out var claimTenantId) &&
            !string.Equals(claimTenantId, currentTenant.Id, StringComparison.Ordinal))
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            ctx.Response.Redirect("/Auth/Login");
            return;
        }
    }
    await next();
});

app.UseAuthorization();

app.MapRazorPages();
app.Run();
