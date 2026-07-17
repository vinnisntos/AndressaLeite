-- 0006_cancellation_trigger_and_overlap_constraint.sql
--
-- Contexto: dois itens do readme.txt (secao 4) fechados aqui.
--
--   1) trigger_check_cancellation — um comentario em DashCliente.cshtml.cs
--      pressupunha essa trigger existindo ("nao cancela com menos de 1h de
--      antecedencia"), mas ela nunca esteve em nenhuma migration deste
--      repositorio. Criada aqui de verdade, com esse nome exato.
--
--   2) EXCLUDE constraint pra conflito de horario — a checagem de
--      double-booking hoje e so em C# (le agendamentos existentes e
--      compara em memoria, ver Services/AppointmentBookingService.cs).
--      Isso cobre o fluxo normal do app mas nao protege contra insercao
--      concorrente (duas reservas quase simultaneas) nem contra escrita
--      direta na tabela. Uma EXCLUDE constraint do Postgres fecha essa
--      lacuna em definitivo, no proprio banco.
--
-- Rode no SQL Editor do Supabase, depois das migrations 0001-0005.
-- Idempotente na maior parte — a unica parte que pode falhar numa
-- segunda execucao e o backfill/constraint de overlap SE ja existirem
-- dados conflitantes (ver observacao no passo 2 abaixo).

begin;

-- 1) Trigger de cancelamento: bloqueia cancelar com menos de 1h de
-- antecedencia. Só age quando o status está de fato mudando PARA
-- "cancelled" (não interfere em outras atualizações, tipo concluir
-- atendimento ou editar valor/forma de pagamento).

create or replace function public.check_cancellation_window()
returns trigger as $$
begin
    if new.status = 'cancelled' and old.status is distinct from 'cancelled' then
        if new.start_time - now() < interval '1 hour' then
            raise exception 'CANCELLATION_TOO_LATE: cancellations require at least 1 hour notice before start_time'
                using errcode = 'P0001';
        end if;
    end if;
    return new;
end;
$$ language plpgsql;

drop trigger if exists trigger_check_cancellation on public.appointments;
create trigger trigger_check_cancellation
    before update on public.appointments
    for each row
    execute function public.check_cancellation_window();

-- 2) EXCLUDE constraint contra sobreposição de horário por profissional --
--
-- Exige a extensão btree_gist (permite índice GiST com operador "=" em
-- colunas text, necessário pra combinar igualdade de tenant_id/
-- employee_id com sobreposição de intervalo no mesmo índice).

create extension if not exists btree_gist;

-- Backfill defensivo: linhas antigas de antes do EndTime ser sempre
-- calculado no booking (ver Fase 1 do roadmap) podem ter end_time nulo
-- ou <= start_time, o que quebraria a criação da constraint abaixo.
-- Sem duração real conhecida pra essas linhas históricas, assume 30min
-- só pra tornar o intervalo válido (não afeta o valor cobrado/estimado,
-- só o intervalo usado pra checar sobreposição).
update public.appointments
    set end_time = start_time + interval '30 minutes'
    where end_time is null or end_time <= start_time;

alter table public.appointments alter column end_time set not null;

do $$
begin
    if not exists (
        select 1 from pg_constraint where conname = 'appointments_no_overlap'
    ) then
        alter table public.appointments
            add constraint appointments_no_overlap
            exclude using gist (
                tenant_id with =,
                employee_id with =,
                tstzrange(start_time, end_time) with &&
            )
            where (status <> 'cancelled');
    end if;
end $$;

commit;

-- Se o COMMIT acima falhar com "conflicting key value violates exclusion
-- constraint", já existem dois agendamentos não cancelados sobrepostos
-- pra mesma profissional no banco (dado histórico anterior à checagem em
-- C# — ver Services/AppointmentBookingService.cs). Rode esta consulta pra
-- achar quais são, decida manualmente qual cancelar/ajustar, e rode a
-- migration de novo:
--
-- select a.id, a.tenant_id, a.employee_id, a.start_time, a.end_time, a.status
-- from public.appointments a
-- where a.status <> 'cancelled'
-- and exists (
--     select 1 from public.appointments b
--     where b.id <> a.id
--       and b.tenant_id = a.tenant_id
--       and b.employee_id = a.employee_id
--       and b.status <> 'cancelled'
--       and tstzrange(a.start_time, a.end_time) && tstzrange(b.start_time, b.end_time)
-- )
-- order by a.employee_id, a.start_time;
