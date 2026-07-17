using AndressaLeite.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AndressaLeite.Services
{
    /// <summary>
    /// Resolve o tenant (salão) da requisição a partir do subdomínio do
    /// Host e popula o CurrentTenant scoped da requisição. Roda cedo no
    /// pipeline (Program.cs), antes de auth/routing — só depende do Host.
    /// </summary>
    public class TenantResolutionMiddleware
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

        private readonly RequestDelegate _next;
        private readonly IMemoryCache _cache;
        private readonly string _rootDomain;

        public TenantResolutionMiddleware(RequestDelegate next, IMemoryCache cache, IConfiguration configuration)
        {
            _next = next;
            _cache = cache;
            _rootDomain = (configuration["Tenancy:RootDomain"] ?? "localhost").ToLowerInvariant();
        }

        // Supabase.Client e CurrentTenant são scoped por requisição, então
        // entram como parâmetro de InvokeAsync (não no construtor, que só
        // aceita serviços singleton).
        public async Task InvokeAsync(HttpContext context, Supabase.Client supabase, CurrentTenant currentTenant)
        {
            var host = context.Request.Host.Host.ToLowerInvariant();
            var slug = ExtractSlug(host);

            if (slug is null)
            {
                currentTenant.IsPlatformContext = true;
            }
            else
            {
                var lookup = await ResolveTenantAsync(slug, supabase);
                if (lookup is not null)
                {
                    currentTenant.Id = lookup.Id;
                    currentTenant.Slug = lookup.Slug;
                    currentTenant.Name = lookup.Name;
                    currentTenant.IsActive = lookup.IsActive;
                    currentTenant.BusinessOpenTime = lookup.BusinessOpenTime;
                    currentTenant.BusinessCloseTime = lookup.BusinessCloseTime;
                    currentTenant.LunchStartTime = lookup.LunchStartTime;
                    currentTenant.LunchEndTime = lookup.LunchEndTime;
                }
            }

            await _next(context);
        }

        /// <summary>
        /// Extrai o slug do subdomínio, ou null se a requisição chegou pelo
        /// domínio raiz / "www" (contexto plataforma) ou por um host que
        /// não bate com o RootDomain configurado.
        /// </summary>
        private string? ExtractSlug(string host)
        {
            if (host == _rootDomain || host == $"www.{_rootDomain}")
            {
                return null;
            }

            var suffix = "." + _rootDomain;
            if (!host.EndsWith(suffix, StringComparison.Ordinal))
            {
                return null;
            }

            var candidate = host[..^suffix.Length];

            // Subdomínios aninhados (ex.: algo.sub.dominio) não são
            // suportados por ora — trata como "sem tenant" em vez de
            // adivinhar qual segmento é o slug.
            if (candidate.Length == 0 || candidate.Contains('.'))
            {
                return null;
            }

            return candidate;
        }

        /// <summary>
        /// Busca o tenant por slug com cache positivo E negativo em
        /// IMemoryCache (singleton — sem conflito com o Supabase.Client
        /// scoped, que só é usado para popular o cache em caso de miss).
        /// TryGetValue diferencia "nunca cacheado" (retorna false) de
        /// "cacheado como não encontrado" (retorna true, valor null) —
        /// evita 1 query por requisição quando alguém varre subdomínios
        /// aleatórios.
        /// </summary>
        private async Task<TenantLookupEntry?> ResolveTenantAsync(string slug, Supabase.Client supabase)
        {
            var cacheKey = CacheKey(slug);
            if (_cache.TryGetValue<TenantLookupEntry?>(cacheKey, out var cached))
            {
                return cached;
            }

            Tenant? tenant = null;
            try
            {
                // Slugs são sempre gravados em minúsculas na criação
                // (Onboarding/CriarSalao), então uma comparação exata é
                // suficiente aqui.
                tenant = await supabase.From<Tenant>().Where(x => x.Slug == slug).Single();
            }
            catch
            {
                // Não encontrado (ou erro transitório do driver) — trata
                // como "tenant não encontrado" em vez de derrubar a
                // requisição inteira.
            }

            var entry = tenant is null ? null : ToEntry(tenant);

            _cache.Set(cacheKey, entry, CacheTtl);
            return entry;
        }

        private static TenantLookupEntry ToEntry(Tenant tenant) => new(
            tenant.Id,
            tenant.Slug,
            tenant.Name,
            tenant.IsActive,
            TimeSpan.Parse(tenant.BusinessOpenTime),
            TimeSpan.Parse(tenant.BusinessCloseTime),
            string.IsNullOrEmpty(tenant.LunchStartTime) ? null : TimeSpan.Parse(tenant.LunchStartTime),
            string.IsNullOrEmpty(tenant.LunchEndTime) ? null : TimeSpan.Parse(tenant.LunchEndTime)
        );

        /// <summary>
        /// Chave de cache pra um slug — exposta como público porque
        /// quem atualiza o tenant (ex.: DashAdmin salvando o horário de
        /// funcionamento) precisa invalidar a mesma entrada, senão a
        /// mudança só aparece depois do CacheTtl expirar sozinho.
        /// </summary>
        public static string CacheKey(string slug) => $"tenant:{slug}";

        private sealed record TenantLookupEntry(
            string Id,
            string Slug,
            string Name,
            bool IsActive,
            TimeSpan BusinessOpenTime,
            TimeSpan BusinessCloseTime,
            TimeSpan? LunchStartTime,
            TimeSpan? LunchEndTime);
    }
}
