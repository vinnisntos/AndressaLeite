-- 0008_team_invites.sql
--
-- Contexto: substitui o fluxo de "admin cria a conta do profissional com
-- senha direta" (readme.txt 5.6) por convite por e-mail. Tabela nova e
-- separada de profiles — mesmo padrao ja usado por platform_admins (0004)
-- pra "identidade que nao se encaixa na forma de profiles" — em vez de
-- pre-criar uma linha em profiles sem senha usavel. Isso evita introduzir
-- um estado "profile existe mas password_hash vazio" que Login.cshtml.cs
-- e qualquer outro codigo que le profiles precisaria passar a considerar.
-- O profile real da profissional so nasce quando ela de fato aceita o
-- convite e define a senha (ver Pages/Auth/AceitarConvite.cshtml.cs).
--
-- NOTA: profiles.id e' uuid (tabela pre-existente, de antes desta serie
-- de migrations), diferente de tenants.id que este projeto criou como
-- text (migration 0002) — created_by abaixo usa uuid por isso, tenant_id
-- continua text pra bater com tenants.id. O backend em C# grava/le os
-- dois como string de qualquer forma (PostgREST serializa ambos como
-- texto no JSON), entao nao muda nada no codigo .NET.
--
-- Rode no SQL Editor do Supabase, depois da migration 0007.
-- Idempotente (pode rodar de novo sem erro).

begin;

create table if not exists public.team_invites (
    id text primary key,
    tenant_id text not null references public.tenants(id),
    email text not null,
    full_name text not null,
    phone text not null,
    token_hash text not null,
    expires_at timestamptz not null,
    created_by uuid not null references public.profiles(id),
    used_at timestamptz null,
    cancelled_at timestamptz null,
    created_at timestamptz not null default now()
);

create index if not exists team_invites_tenant_id_idx on public.team_invites (tenant_id);
create index if not exists team_invites_token_hash_idx on public.team_invites (token_hash);

-- Um convite pendente por e-mail por tenant (evita 2 convites vivos pro
-- mesmo endereco no mesmo salao "empilhando" tokens). Convites usados/
-- cancelados nao contam pra esse limite (indice parcial).
create unique index if not exists team_invites_pending_email_unique_idx
    on public.team_invites (tenant_id, lower(email))
    where used_at is null and cancelled_at is null;

-- RLS sem policy — mesmo padrao "fechado por padrao" das outras tabelas
-- (o backend usa a service_role key, que ignora RLS; isso e so defesa em
-- profundidade contra as chaves anon/authenticated do PostgREST).
alter table public.team_invites enable row level security;

commit;
