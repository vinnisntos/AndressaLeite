-- 0003_agenda_enriquecida.sql
--
-- Contexto (roadmap "Agenda enriquecida", ver readme.txt secao 7):
--   1) tenants ganha horario de funcionamento configuravel (abertura,
--      fechamento e um intervalo de almoço opcional), no lugar das
--      constantes fixas que hoje valem pra todo mundo;
--   2) appointments ganha forma de pagamento e observacoes registradas na
--      conclusao do atendimento, e client_id deixa de ser obrigatorio
--      (agendamento manual feito pela profissional pode nao ter conta de
--      cliente vinculada — usa booked_for_name/booked_for_phone);
--   3) nova tabela professional_services: override opcional de preço/
--      duracao por profissional pra cada serviço do catalogo do tenant.
--
-- Rode no SQL Editor do painel do Supabase, depois das migrations 0001 e
-- 0002. Idempotente na maior parte (pode rodar de novo sem erro).

begin;

-- 1) Horario de funcionamento por tenant -------------------------------------

alter table public.tenants
    add column if not exists business_open_time time not null default '09:00',
    add column if not exists business_close_time time not null default '19:00',
    add column if not exists lunch_start_time time null,
    add column if not exists lunch_end_time time null;

-- 2) appointments: pagamento, observacoes, client_id opcional ---------------

alter table public.appointments
    add column if not exists payment_method text null
        check (payment_method is null or payment_method in (
            'dinheiro', 'pix', 'cartao_debito', 'cartao_credito', 'outro'
        )),
    add column if not exists notes text null;

alter table public.appointments
    alter column client_id drop not null;

-- Acelera os agregados "hoje"/"por dia" do dashboard da admin (Fase 7).
create index if not exists appointments_tenant_start_idx
    on public.appointments (tenant_id, start_time);

-- 3) professional_services: override de preço/duracao por profissional -----
-- Tabela nova — constraints/FKs podem ir direto no CREATE TABLE porque
-- "IF NOT EXISTS" faz o statement inteiro ser pulado numa segunda execucao
-- (não precisa do padrão de bloco DO usado na migration 0002 pra FK em
-- tabela já existente).
--
-- employee_id/service_id são "uuid", não "text": profiles.id e
-- services.id já existiam antes das nossas migrations (schema original,
-- de quando o projeto ainda usava Supabase Auth) e são uuid de verdade —
-- só o C# trata esses ids como string, o Postgres por baixo é uuid. Uma
-- FK exige o mesmo tipo dos dois lados, daí o ajuste aqui. tenants.id
-- continua "text" porque essa tabela é nossa, criada do zero na 0002.

create table if not exists public.professional_services (
    id text primary key,
    tenant_id text not null references public.tenants(id),
    employee_id uuid not null references public.profiles(id),
    service_id uuid not null references public.services(id),
    price numeric null,
    duration_minutes integer null,
    is_active boolean not null default true,
    constraint professional_services_employee_service_unique
        unique (employee_id, service_id)
);

create index if not exists professional_services_tenant_id_idx
    on public.professional_services (tenant_id);

-- RLS sem policy — mesmo padrão "fechado por padrão" das migrations
-- 0001/0002 (o backend usa a service_role key, que ignora RLS).
alter table public.professional_services enable row level security;

commit;
