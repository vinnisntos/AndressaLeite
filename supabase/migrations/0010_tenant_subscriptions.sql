-- 0010_tenant_subscriptions.sql
--
-- Contexto: billing/assinatura via Asaas (readme.txt 4.9/9.2) — ate agora
-- a "assinatura" de um salao era so um toggle manual do superadmin, sem
-- nenhuma cobranca de verdade. Plano unico fixo (R$ 149,90/mes), 14 dias
-- de trial, PIX ou Cartao de Credito via checkout hospedado do Asaas
-- (nunca vejo dado de cartao — ver Services/AsaasService.cs), 5 dias de
-- tolerancia antes de suspender por atraso.
--
-- Tabela separada de tenants — mesmo padrao ja usado por team_invites
-- (0008) e platform_admins (0004) pra "identidade/estado que nao cabe na
-- tabela principal". tenant_id e UNIQUE (relacao 1:1 — um salao tem uma
-- assinatura).
--
-- Rode no SQL Editor do Supabase, depois da migration 0009.
-- Idempotente (pode rodar de novo sem erro).

begin;

create table if not exists public.tenant_subscriptions (
    id text primary key,
    tenant_id text not null unique references public.tenants(id),
    status text not null default 'trial',
    plan_price numeric not null default 149.90,
    trial_ends_at timestamptz not null,
    asaas_customer_id text,
    asaas_subscription_id text,
    asaas_checkout_id text,
    next_due_date timestamptz,
    overdue_since timestamptz,
    last_payment_at timestamptz,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

do $$
begin
    if not exists (
        select 1 from pg_constraint where conname = 'tenant_subscriptions_status_chk'
    ) then
        alter table public.tenant_subscriptions
            add constraint tenant_subscriptions_status_chk
            check (status in ('trial', 'pending_payment', 'active', 'overdue', 'suspended', 'cancelled'));
    end if;
end $$;

create index if not exists tenant_subscriptions_status_idx
    on public.tenant_subscriptions (status);

-- RLS sem policy — mesmo padrao "fechado por padrao" das outras tabelas
-- (o backend usa a service_role key, que ignora RLS; isso e so defesa em
-- profundidade contra as chaves anon/authenticated do PostgREST).
alter table public.tenant_subscriptions enable row level security;

commit;
