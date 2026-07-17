-- 0002_multi_tenant.sql
--
-- Contexto: o app deixou de atender um único salão e passou a ser
-- multi-tenant por subdomínio (studio-bella.suaapp.com, salao-da-ana.
-- suaapp.com, ...). Este script:
--   1) cria a tabela public.tenants;
--   2) semeia um tenant para o salão que já existe (slug 'studio-bella');
--   3) adiciona tenant_id em profiles/services/appointments, faz backfill
--      para o tenant existente e torna a coluna obrigatória;
--   4) troca o índice único de e-mail de global para composto por tenant
--      (o mesmo e-mail passa a poder existir em salões diferentes, como
--      contas totalmente separadas);
--   5) habilita RLS em tenants, no mesmo padrão "fechado por padrão" da
--      migration 0001 (o backend usa a service_role key, que ignora RLS;
--      isso é só defesa em profundidade contra as chaves anon/authenticated
--      do PostgREST).
--
-- Rode no SQL Editor do painel do Supabase. Idempotente na maior parte
-- (pode rodar de novo sem erro), exceto que ela assume que ainda não
-- existe nenhum tenant além do seed 'studio-bella' — não é uma migration
-- de "reset", é uma migration de "primeira execução" do multi-tenant.

begin;

-- 1) Tabela de tenants ------------------------------------------------------

create table if not exists public.tenants (
    id text primary key,
    slug text not null,
    name text not null,
    is_active boolean not null default true,
    created_at timestamptz not null default now(),
    constraint tenants_slug_format_chk check (
        slug ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$'
    )
);

create unique index if not exists tenants_slug_unique_idx
    on public.tenants (lower(slug));

-- 2) Seed do tenant existente (o salão que já estava rodando) --------------

insert into public.tenants (id, slug, name)
select gen_random_uuid()::text, 'studio-bella', 'Studio Bella'
where not exists (
    select 1 from public.tenants where lower(slug) = 'studio-bella'
);

-- 3) tenant_id em profiles/services/appointments ----------------------------

alter table public.profiles add column if not exists tenant_id text;
alter table public.services add column if not exists tenant_id text;
alter table public.appointments add column if not exists tenant_id text;

update public.profiles
    set tenant_id = (select id from public.tenants where lower(slug) = 'studio-bella')
    where tenant_id is null;

update public.services
    set tenant_id = (select id from public.tenants where lower(slug) = 'studio-bella')
    where tenant_id is null;

update public.appointments
    set tenant_id = (select id from public.tenants where lower(slug) = 'studio-bella')
    where tenant_id is null;

alter table public.profiles alter column tenant_id set not null;
alter table public.services alter column tenant_id set not null;
alter table public.appointments alter column tenant_id set not null;

-- FKs idempotentes (ALTER TABLE ... ADD CONSTRAINT não tem "IF NOT EXISTS").
do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'profiles_tenant_fk') then
        alter table public.profiles
            add constraint profiles_tenant_fk foreign key (tenant_id) references public.tenants(id);
    end if;

    if not exists (select 1 from pg_constraint where conname = 'services_tenant_fk') then
        alter table public.services
            add constraint services_tenant_fk foreign key (tenant_id) references public.tenants(id);
    end if;

    if not exists (select 1 from pg_constraint where conname = 'appointments_tenant_fk') then
        alter table public.appointments
            add constraint appointments_tenant_fk foreign key (tenant_id) references public.tenants(id);
    end if;
end $$;

-- Índices: toda query do app agora filtra por tenant_id.
create index if not exists profiles_tenant_id_idx on public.profiles (tenant_id);
create index if not exists services_tenant_id_idx on public.services (tenant_id);
create index if not exists appointments_tenant_id_idx on public.appointments (tenant_id);

-- 4) E-mail único por tenant, não mais globalmente ---------------------------
-- (criado na migration 0001 como lower(email) global; substituído aqui)

drop index if exists profiles_email_unique_idx;
create unique index if not exists profiles_tenant_email_unique_idx
    on public.profiles (tenant_id, lower(email));

-- 5) RLS em tenants -----------------------------------------------------------
-- Sem nenhuma policy: acesso via anon/authenticated do PostgREST fica
-- bloqueado por padrão, igual profiles/services/appointments desde a 0001.

alter table public.tenants enable row level security;

commit;
