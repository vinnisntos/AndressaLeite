-- 0009_drop_legacy_global_email_constraint.sql
--
-- Contexto: achado da rodada de e-mail transacional (readme.txt 12.2.c).
-- A migration 0002 trocou o indice unico de profiles.email de GLOBAL pra
-- COMPOSTO por tenant (tenant_id, lower(email)) — especificamente pra
-- permitir o mesmo e-mail existir em saloes diferentes, como contas
-- separadas (documentado desde entao em varias secoes deste readme).
--
-- Na pratica isso nunca funcionou de verdade: sobrou uma constraint
-- GLOBAL antiga chamada "profiles_email_key" (de antes da serie de
-- migrations 0001+, provavelmente criada automaticamente quando a
-- tabela profiles foi feita originalmente) que nunca foi dropada.
-- Confirmado ao vivo tentando aceitar um convite de equipe (Pages/Auth/
-- AceitarConvite.cshtml.cs) com um e-mail que ja era cliente em outro
-- tenant — erro 23505 "duplicate key value violates unique constraint
-- profiles_email_key".
--
-- Rode no SQL Editor do Supabase, depois da migration 0008. Idempotente
-- (IF EXISTS — pode rodar de novo sem erro mesmo se a constraint ja
-- tiver sido removida).
--
-- Se o nome real da constraint no seu banco for diferente de
-- "profiles_email_key" (confira antes com a query abaixo, se quiser)
-- este DROP simplesmente nao faz nada — troque o nome manualmente nesse
-- caso:
--   select conname, pg_get_constraintdef(oid)
--   from pg_constraint
--   where conrelid = 'public.profiles'::regclass and contype = 'u';

begin;

alter table public.profiles
    drop constraint if exists profiles_email_key;

commit;
