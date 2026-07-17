-- 0001_auth_columns_and_rls.sql
--
-- Contexto: o app parou de usar Supabase Auth (Gotrue) para autenticar e
-- passou a validar e-mail/senha (BCrypt) direto na tabela public.profiles,
-- controlando sessão via cookie do ASP.NET. Este script:
--   1) garante que profiles tem as colunas que o backend já espera (email,
--      password_hash), com um índice único case-insensitive em email;
--   2) habilita Row Level Security nas tabelas de negócio.
--
-- Rode no SQL Editor do painel do Supabase (Database > SQL Editor) do
-- projeto. É idempotente: pode ser executado mais de uma vez sem erro.

-- 1) Colunas de autenticação em profiles ------------------------------------

alter table public.profiles
    add column if not exists email text,
    add column if not exists password_hash text;

-- Case-insensitive e único: o backend sempre normaliza e-mail com
-- Trim().ToLowerInvariant() antes de gravar/consultar, então o índice
-- segue a mesma regra. NULLs (perfis legados sem email) não conflitam
-- entre si em um índice único.
create unique index if not exists profiles_email_unique_idx
    on public.profiles (lower(email));

-- 2) Row Level Security ------------------------------------------------------
--
-- O backend (Program.cs) conecta no Supabase com a SecretKey (service_role),
-- que ignora RLS por padrão — então isto não muda o comportamento da
-- aplicação. O que muda: sem nenhuma policy definida, uma tabela com RLS
-- habilitado fica inacessível para as chaves anon/authenticated do
-- PostgREST. É a postura "fechado por padrão" certa aqui, já que hoje
-- nenhum código client-side (JS no browser) fala diretamente com o
-- Supabase — todo acesso passa pelo servidor .NET.

alter table public.profiles enable row level security;
alter table public.services enable row level security;
alter table public.appointments enable row level security;

-- Nenhuma policy é criada de propósito: acesso via anon/authenticated
-- fica bloqueado por padrão. Se algum dia o app precisar de acesso
-- direto do browser (ex.: Realtime), crie policies explícitas aqui em
-- vez de desabilitar RLS.
