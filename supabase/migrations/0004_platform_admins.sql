-- 0004_platform_admins.sql
--
-- Contexto: painel de superadmin (MarcAi) — cross-tenant, usado só pelo
-- dono da plataforma pra ativar/desativar a "licença" (tenants.is_active)
-- de qualquer salão. Diferente de admin/employee/client, que são sempre
-- de UM tenant (tabela profiles, com tenant_id obrigatório desde a
-- migration 0002), um superadmin não pertence a nenhum salão — por isso
-- vive numa tabela própria, fora do modelo multi-tenant.
--
-- Rode no SQL Editor do Supabase, depois das migrations 0001-0003.
-- Idempotente (pode rodar de novo sem erro).

begin;

create table if not exists public.platform_admins (
    id text primary key,
    email text not null,
    password_hash text not null,
    full_name text not null,
    created_at timestamptz not null default now()
);

create unique index if not exists platform_admins_email_unique_idx
    on public.platform_admins (lower(email));

-- RLS sem policy — mesmo padrão "fechado por padrão" das outras tabelas
-- (o backend usa a service_role key, que ignora RLS).
alter table public.platform_admins enable row level security;

-- Conta inicial (bootstrap), pra você conseguir logar em /SuperAdmin/Login
-- assim que a migration rodar. Troque a senha depois se quiser — ainda
-- não existe tela de "trocar senha" pro superadmin (ver readme.txt).
insert into public.platform_admins (id, email, password_hash, full_name)
select
    gen_random_uuid()::text,
    'vinnicius.santos2005@gmail.com',
    '$2a$11$.qZwRG0zc27OiV9bMZ3i1eMxu..35T4iHuDPXs5hlKm2Ss1zlCiXu',
    'Vinnicius Santos'
where not exists (
    select 1 from public.platform_admins
    where lower(email) = 'vinnicius.santos2005@gmail.com'
);

commit;
