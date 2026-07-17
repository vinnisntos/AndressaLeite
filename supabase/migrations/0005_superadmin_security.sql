-- 0005_superadmin_security.sql
--
-- Contexto: endurecer a conta de superadmin (a mais poderosa do sistema —
-- liga/desliga qualquer salão) com 2FA (TOTP). O segredo TOTP fica
-- "pendente" (totp_secret preenchido, totp_enabled=false) até o
-- superadmin confirmar um código válido em /SuperAdmin/Security — só aí
-- vira exigido no login.
--
-- Rode no SQL Editor do Supabase, depois das migrations 0001-0004.
-- Idempotente (pode rodar de novo sem erro).

begin;

alter table public.platform_admins
    add column if not exists totp_secret text null,
    add column if not exists totp_enabled boolean not null default false;

commit;
