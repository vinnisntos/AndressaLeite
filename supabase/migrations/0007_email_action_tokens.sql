-- 0007_email_action_tokens.sql
--
-- Contexto: provedor de e-mail transacional decidido (Resend, readme.txt
-- secao 9.1) — desbloqueia esqueci-minha-senha (4.1), verificacao de
-- e-mail (4.2) e lembrete automatico de agendamento (5.3). Este script
-- adiciona:
--
--   1) em profiles: um PAR UNICO de colunas de token de acao, compartilhado
--      entre reset de senha e verificacao de e-mail (nao dois pares
--      dedicados) — um usuario dificilmente precisa dos dois ao mesmo
--      tempo, e emitir um token novo invalida naturalmente qualquer token
--      antigo pendente (mesmo raciocinio de minimizar colunas do
--      totp_secret/totp_enabled na 0005). O TOKEN E ARMAZENADO COMO HASH
--      SHA-256, nunca em texto puro: ao contrario do totp_secret (inutil
--      sozinho sem o codigo do momento, dois fatores mesmo em repouso), um
--      token de reset/verificacao e a UNICA credencial necessaria pra
--      agir, e viaja inteiro dentro de uma URL de e-mail (logs de proxy,
--      historico de navegador) — um hash no banco nao vira takeover de
--      conta se a tabela vazar sozinha.
--   2) email_verified em profiles, default false — NAO bloqueia login
--      (verificacao "soft", ver readme.txt 4.2), so marca status pra
--      exibir um aviso.
--   3) reminder_sent_at em appointments — idempotencia do lembrete
--      automatico por e-mail (5.3): evita reenviar o mesmo lembrete a
--      cada tick do job de polling (ver Services/AppointmentReminderService.cs).
--
-- Rode no SQL Editor do Supabase, depois da migration 0006.
-- Idempotente (pode rodar de novo sem erro).

begin;

alter table public.profiles
    add column if not exists action_token_hash text null,
    add column if not exists action_token_type text null,
    add column if not exists action_token_expires_at timestamptz null,
    add column if not exists email_verified boolean not null default false;

do $$
begin
    if not exists (
        select 1 from pg_constraint where conname = 'profiles_action_token_type_chk'
    ) then
        alter table public.profiles
            add constraint profiles_action_token_type_chk
            check (action_token_type is null or action_token_type in ('password_reset', 'email_verification'));
    end if;
end $$;

-- Busca por hash de token precisa ser rapida (toda requisicao de
-- reset/verificacao faz essa query) — sem indice, seria table scan.
create index if not exists profiles_action_token_hash_idx
    on public.profiles (action_token_hash)
    where action_token_hash is not null;

alter table public.appointments
    add column if not exists reminder_sent_at timestamptz null;

commit;
