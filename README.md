# 💇‍♀️ MarcAi

**Plataforma de agendamento multi-tenant para saloes de estetica.**
Uma unica aplicacao atende varios saloes ao mesmo tempo — cada um com o proprio subdominio, dados 100% isolados e sua propria equipe, agenda e financeiro.

<p align="left">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white">
  <img alt="Razor Pages" src="https://img.shields.io/badge/ASP.NET%20Core-Razor%20Pages-512BD4?logo=dotnet&logoColor=white">
  <img alt="Supabase" src="https://img.shields.io/badge/Supabase-Postgres%20%2F%20PostgREST-3ECF8E?logo=supabase&logoColor=white">
  <img alt="xUnit" src="https://img.shields.io/badge/Testes-xUnit-5C2D91?logo=xunit&logoColor=white">
  <img alt="CI" src="https://github.com/vinnisntos/AndressaLeite/actions/workflows/ci.yml/badge.svg">
  <img alt="Docker" src="https://img.shields.io/badge/Docker-multi--stage-2496ED?logo=docker&logoColor=white">
</p>

> 📌 O codigo/namespace do projeto continua `AndressaLeite` — apenas a marca voltada ao usuario final mudou para **MarcAi**. "Studio Bella" foi o primeiro salao do sistema e segue existindo como um tenant normal.

---

## Sumario

- [Visao geral](#-visao-geral)
- [Funcionalidades](#-funcionalidades)
- [Papeis de usuario](#-papeis-de-usuario)
- [Arquitetura multi-tenant](#-arquitetura-multi-tenant)
- [Stack tecnica](#-stack-tecnica)
- [Como rodar localmente](#-como-rodar-localmente)
- [Deploy com Docker](#-deploy-com-docker)
- [Testes e CI](#-testes-e-ci)
- [Estrutura do projeto](#-estrutura-do-projeto)
- [Status e roadmap](#-status-e-roadmap)
- [Divida tecnica conhecida](#-divida-tecnica-conhecida)

---

## 🔎 Visao geral

O MarcAi cobre o fluxo real de operacao de um salao de estetica: cliente escolhe profissional, servico e horario respeitando o expediente e o almoco do salao; a profissional confirma, atende, registra o que foi cobrado e como foi pago; a dona do salao acompanha indicadores financeiros e, se quiser, atende clientes tambem. Tudo isolado por salao (tenant), num unico banco e numa unica aplicacao.

## ✨ Funcionalidades

### Para o cliente
- Cadastro e login proprios (cookie + BCrypt), por salao.
- Agendamento guiado: profissional → servico → data/hora, com preco e duracao **especificos daquela profissional** (nao um valor generico de catalogo).
- Bloqueio automatico de horarios fora do expediente, dentro do almoco ou em conflito com outro agendamento — validado tambem no banco (constraint `EXCLUDE`), nao so no C#.
- Cancelamento com regra de antecedencia minima (bloqueado a menos de 1h do horario, reforcado por trigger no Postgres).
- Historico paginado de agendamentos concluidos/cancelados.

### Para a profissional
- Agenda do dia com confirmacao/conclusao de atendimento em um clique.
- **Conclusao enriquecida**: ao concluir, ajusta o servico prestado, o valor cobrado e registra a forma de pagamento (dinheiro / Pix / debito / credito / outro) e observacoes livres.
- **Agendamento manual**: registra encaixe, ligacao ou walk-in direto na propria agenda, sem precisar que o cliente tenha conta.
- **"Meus Servicos"**: personaliza preco e/ou duracao de qualquer servico do catalogo, sem afetar o valor de outras profissionais.
- Lembrete de atendimento via WhatsApp com um clique (`wa.me` com mensagem pre-pronta).
- Toggle de historico dos proprios atendimentos, com o mesmo padrao de paginacao do cliente.

### Para a dona do salao (admin)
- Gestao de equipe: convite por e-mail (a profissional define a propria senha ao aceitar, sem senha provisoria) e catalogo de servicos (criar/ativar/desativar/remover).
- Configuracao do **horario de funcionamento e do almoco**, por salao — sem precisar de deploy pra mudar.
- **Dashboard de metricas**: atendimentos concluidos hoje, receita esperada do dia, detalhamento de receita por forma de pagamento (mes atual) e painel de observacoes recentes dos atendimentos.
- **Assinatura da plataforma** (`/Admin/Assinatura`): trial de 14 dias, assina via checkout hospedado do Asaas (PIX ou Cartao), acompanha status (trial / ativa / atrasada / suspensa) direto no dashboard.
- Tambem atende como "profissional premium": agenda propria, agendamento manual e "Meus Servicos" — sem duplicar telas.

### Para o dono da plataforma (superadmin)
- Painel cross-tenant (`/SuperAdmin`), fora do modelo multi-tenant, numa tabela propria.
- Ativa/desativa a "licenca" de qualquer salao com um clique.
- Login em duas etapas com **2FA via TOTP** (RFC 6238, compativel com Google Authenticator/Authy), implementado do zero e validado contra os vetores oficiais do RFC.
- Troca de senha e ativacao/desativacao de 2FA em `/SuperAdmin/Security`.

## 👥 Papeis de usuario

| Papel | Escopo | Onde vive |
|---|---|---|
| **Superadmin** | Cross-tenant (dono da plataforma) | tabela `platform_admins` |
| **Admin** | Dono/gestor do salao — gestao + agenda propria | tabela `profiles`, `role = admin` |
| **Employee** (profissional) | Agenda propria, atendimentos | tabela `profiles`, `role = employee` |
| **Client** (cliente) | Agenda, cancela, historico | tabela `profiles`, `role = client` |

## 🏢 Arquitetura multi-tenant

```mermaid
flowchart LR
    A["Requisicao HTTP\n(studio-bella.suaapp.com)"] --> B["TenantResolutionMiddleware\nresolve subdominio -> tenant"]
    B -->|encontrado + ativo| C["CurrentTenant (scoped)\nid, nome, horarios"]
    B -->|nao encontrado| D["Salao nao encontrado"]
    B -->|is_active = false| E["Conta suspensa"]
    C --> F["PageModels\n(todas as queries filtram por tenant_id)"]
    F --> G[("Supabase / Postgres\nprofiles, services, appointments...")]
    H["/SuperAdmin"] -.cross-tenant, sem CurrentTenant.-> G
```

- **Isolamento por subdominio**: cada salao tem seu proprio `studio-bella.suaapp.com`, sem subdominio = area da plataforma (marketing + onboarding self-service de novo salao).
- **Duas camadas de seguranca**: cookie de sessao host-scoped (nao compartilhado entre subdominios) **+** claim `tenant_id` no cookie, checada contra o tenant resolvido pela URL em todo request — sessao trocada de subdominio e encerrada automaticamente.
- **Cache de resolucao de tenant** em `IMemoryCache` (TTL 60s) pra nao bater no banco a cada request.
- Onboarding self-service: qualquer pessoa cria seu proprio salao (slug + dados), sem depender do dono da plataforma.

## 🧱 Stack tecnica

| Camada | Tecnologia |
|---|---|
| Backend | ASP.NET Core **Razor Pages** (.NET 10) |
| Banco de dados | **Supabase** (Postgres + PostgREST, via `postgrest-csharp`/`supabase-csharp`) — sem EF Core |
| Autenticacao | Cookie proprio + **BCrypt** (nao usa Supabase Auth/Gotrue) |
| 2FA | **TOTP (RFC 6238)** implementado do zero, sem pacote externo pro algoritmo |
| QR Code | **QRCoder** (renderer 100% C#, sem `System.Drawing.Common` em runtime) |
| E-mail transacional | **Resend** (esqueci senha, verificacao, convite de equipe, lembrete automatico) |
| Billing | **Asaas** (checkout hospedado — PIX/Cartao, sem dado de cartao passar pelo backend) |
| Testes | **xUnit** (57 testes) |
| CI | **GitHub Actions** (`dotnet build` + `dotnet test` em todo push/PR) |
| Deploy | **Docker Compose** (app + Caddy com TLS wildcard automatico via Route53 DNS-01) |

## 🚀 Como rodar localmente

**1. Segredos do Supabase** (nunca em `appsettings.json` versionado):
```bash
dotnet user-secrets set "Supabase:Url" "https://SEU-PROJETO.supabase.co"
dotnet user-secrets set "Supabase:SecretKey" "sb_secret_..."
```

**2. Rode as migrations**, nesta ordem, no SQL Editor do Supabase:
```
supabase/migrations/0001_auth_columns_and_rls.sql
supabase/migrations/0002_multi_tenant.sql
supabase/migrations/0003_agenda_enriquecida.sql
supabase/migrations/0004_platform_admins.sql
supabase/migrations/0005_superadmin_security.sql
supabase/migrations/0006_cancellation_trigger_and_overlap_constraint.sql
supabase/migrations/0007_email_action_tokens.sql
supabase/migrations/0008_team_invites.sql
supabase/migrations/0009_drop_legacy_global_email_constraint.sql
supabase/migrations/0010_tenant_subscriptions.sql
```
Todas sao idempotentes (podem rodar de novo sem erro). A `0002` semeia o tenant `studio-bella`; a `0004` semeia a conta de superadmin; a `0006` exige a extensao `btree_gist` (a propria migration ja cria); `0007`-`0009` sao da rodada de e-mail transacional; `0010` cria a tabela de assinaturas.

**3. Suba a aplicacao:**
```bash
cd AndressaLeite
dotnet run
```

| URL | O que e |
|---|---|
| `http://localhost:5081/` | Pagina da plataforma (onboarding) |
| `http://localhost:5081/SuperAdmin/Login` | Login do superadmin |
| `http://studio-bella.localhost:5081/` | Salao de exemplo |
| `http://studio-bella.localhost:5081/Auth/Login` | Login do salao |

> Navegadores modernos resolvem `*.localhost` pra `127.0.0.1` nativamente — nao precisa editar `hosts`. Use o CTA "Criar meu salao" na home da plataforma pra testar com um segundo tenant.

## 🐳 Deploy com Docker Compose

```bash
cp .env.example .env
# editar .env: Supabase, dominio, Resend, Asaas, ACME_EMAIL
docker compose up -d --build
docker compose logs -f caddy   # acompanhar emissao do certificado TLS
```

`docker-compose.yml` orquestra dois containers: `app` (Dockerfile multi-estagio, roda como usuario nao-root, escuta em `8080` interno) e `caddy` (Dockerfile.caddy, builda o Caddy com o plugin `caddy-dns/route53` pra emitir certificado **wildcard** via desafio DNS-01 — o unico jeito de cobrir `*.suaapp.com`, ja que HTTP-01 nao cobre wildcard). Passo a passo completo de infraestrutura (IAM Role, Elastic IP, Security Group, DNS) em [`docs/DEPLOY_AWS.md`](./docs/DEPLOY_AWS.md), incluindo o caso de dominio registrado fora da Route53 (ex. Registro.br) via delegacao de subdominio.

## 🧪 Testes e CI

```bash
dotnet test AndressaLeite.Tests
```

57 testes cobrindo:
- **`TotpService`**: os 5 vetores oficiais do RFC 6238 (Appendix B), rejeicao de codigo invalido/fora da janela, round-trip de Base32.
- **`AuthorizationService`**: protecao contra open-redirect, resolucao de landing page por papel, leitura de claims (`tenant_id` incluso).
- **`EmailTokenService`**: geracao/hash/expiracao de token de acao por e-mail (reset de senha, verificacao, convite).

Todo push/PR pra `main` roda `dotnet build` + `dotnet test` via GitHub Actions.

## 📂 Estrutura do projeto

```
AndressaLeite/
├── Models/            Tenant, Profile, Appointement, Service, ProfessionalService, TeamInvite, TenantSubscription, PlatformAdmin...
├── Pages/
│   ├── Onboarding/     Criacao self-service de novo salao
│   ├── Auth/           Cadastro / Login / Logout / esqueci senha / verificacao de e-mail / aceitar convite (por tenant)
│   ├── Cliente/        Agendamento e historico do cliente
│   ├── Profissional/   Agenda, conclusao, "Meus Servicos", agendamento manual
│   ├── Admin/          Gestao do salao, dashboard de metricas, assinatura (Asaas)
│   ├── SuperAdmin/     Painel cross-tenant + 2FA
│   └── Webhooks/       Webhook do Asaas (eventos de pagamento)
├── Services/           CurrentTenant, TenantResolutionMiddleware, AppointmentBookingService, TotpService, ResendEmailService, AsaasService, AppointmentReminderService, TenantSuspensionService...
└── Program.cs

AndressaLeite.Tests/    Testes xUnit (TotpService, AuthorizationService, EmailTokenService)
supabase/migrations/    Schema incremental do Postgres (0001 a 0010)
Dockerfile              Build multi-estagio da aplicacao
Dockerfile.caddy        Build do Caddy com plugin de DNS da Route53 (certificado TLS wildcard)
docker-compose.yml      Orquestra app + Caddy
docs/DEPLOY_AWS.md      Passo a passo de deploy em EC2
```

## 🗺️ Status e roadmap

O roadmap completo da **"Agenda Enriquecida"** (horario dinamico, preco por profissional, agendamento manual, conclusao enriquecida, admin premium, dashboard de metricas, lembrete via WhatsApp) esta **100% implementado**, junto com multi-tenancy, superadmin com 2FA, e-mail transacional (Resend), billing via Asaas, testes automatizados, CI e deploy via Docker Compose.

**Billing (Asaas)**: codigo completo — checkout hospedado, webhook de pagamento, job de suspensao automatica por trial vencido ou atraso — mas **ainda nao testado contra a API real**, aguardando a API key de sandbox. **Deploy em producao**: em andamento numa instancia EC2 real, ver `readme.txt` secao 14 pro progresso atual.

Detalhes de cada decisao, migrations pendentes de rodar em producao e itens em aberto estao documentados em [`readme.txt`](./readme.txt).

## ⚠️ Divida tecnica conhecida

- Isolamento entre tenants e garantido **em nivel de aplicacao** (todo `.Where(tenant_id ==)`), nao por RLS com policies reais no Postgres — o backend usa a service key, que ignora RLS.
- Billing implementado mas nao testado ao vivo contra a API do Asaas (falta API key de sandbox) — o toggle manual do superadmin continua disponivel como override independente de pagamento.
- Sem branding/dominio customizado por tenant.
- Sem lock distribuido nos `BackgroundService` (lembrete de agendamento, suspensao de assinatura) — assume uma unica instancia do container rodando; escalar pra multiplas instancias exigiria mover pra cron externo ou adicionar lock.

---

<p align="center">Feito com 💜 pra donas de salao que merecem um sistema tao capricho quanto o trabalho delas.</p>
