========================================================================
MARCAI — DIAGNOSTICO DO PROJETO E REQUISITOS FALTANTES
Gerado em: 2026-07-16 | Atualizado em: 2026-07-17 (Fases 1-8 + superadmin com
2FA + rebrand + trigger de cancelamento/EXCLUDE + testes automatizados + CI)
========================================================================

1. O QUE E O SISTEMA
------------------------------------------------------------------------
"MarcAi" — nome da plataforma (codigo/namespace do projeto continua
AndressaLeite, so o nome de marca voltado pro usuario mudou). Aplicacao
ASP.NET Core Razor Pages (.NET 10) de agendamento para saloes de estetica
— um produto MULTI-TENANT: uma unica instancia/banco atende varios
saloes ao mesmo tempo, cada um roteado pelo proprio subdominio (ex.:
studio-bella.suaapp.com, salao-da-ana.suaapp.com), com dados completamente
isolados entre si. "Studio Bella" (design de sobrancelhas) foi o primeiro
salao/tenant do sistema e continua existindo como tal, mas o app deixou
de ser exclusivo dele.

Persistencia via Supabase (Postgres/PostgREST, sem EF Core). Autenticacao
propria por cookie, com senha em BCrypt (nao usa mais o Supabase Auth/
Gotrue).

Quatro niveis de usuario:
  - superadmin (dono da plataforma MarcAi): CROSS-TENANT, nao pertence a
    nenhum salao, vive numa tabela propria (platform_admins, nao
    profiles). Ativa/desativa a licenca de qualquer salao pelo painel
    /SuperAdmin — ver secao 3.8.
  - Dentro de CADA tenant (salao), tres papeis (tabela profiles):
    - client   (cliente): agenda horarios, cancela, ve seus agendamentos.
    - employee (profissional): ve a agenda do dia, confirma/conclui
      atendimentos, agenda manualmente, personaliza preco/duracao.
    - admin    (dona/gestora do salao): gerencia equipe, servicos,
      horario de funcionamento e ve indicadores financeiros — e tambem
      "profissional premium" (tem agenda propria igual employee).

------------------------------------------------------------------------
2. DIAGNOSTICO ATUAL — O QUE JA FUNCIONA
------------------------------------------------------------------------
- Cadastro, login e logout via cookie proprio, com senha em BCrypt.
- Autorizacao por pasta (AdminOnly/EmployeeOnly/ClientOnly) e por policy.
- Rate limiting em login (5/min/IP), cadastro (3/hora/IP) e criacao de
  salao novo (3/dia/IP — ver secao 3).
- Headers de seguranca (CSP, X-Frame-Options, HSTS, etc.) no pipeline.
- Fluxo de agendamento completo: cliente escolhe profissional -> servico
  -> data/hora, com validacao de horario comercial + almoço (configurável
  por tenant, ver DashAdmin "Horário de Funcionamento") e de conflito de
  agenda.
- Cancelamento de agendamento com checagem de posse (cliente so cancela
  o proprio agendamento, dentro do proprio tenant).
- Cadastro/remocao de profissionais e cadastro/ativacao/desativacao/
  remocao de servicos pelo admin, tudo restrito ao tenant dele.
- Profissional pode confirmar e concluir um atendimento pela propria
  agenda do dia, alimentando o indicador de faturamento real do admin.
- Segredos do Supabase fora do controle de versao (dotnet user-secrets
  em dev; variaveis de ambiente esperadas em producao).
- Mensagens de erro do Login/Cadastro/Admin nao vazam mais detalhes
  internos do banco (logadas via ILogger, mensagem generica na tela).
- Dashboard do admin com metricas do dia/mes: atendimentos concluidos
  hoje, receita esperada hoje, detalhamento por forma de pagamento (mes
  atual) e painel de observacoes recentes registradas na conclusao dos
  atendimentos (Fase 7 do roadmap, secao 7).
- Painel de superadmin (/SuperAdmin) cross-tenant: lista todos os saloes
  da plataforma e ativa/desativa a "licenca" de qualquer um (secao 3.8).
- Multi-tenancy por subdominio, com onboarding self-service de novo
  salao e isolamento de dados (ver secao 3).
- Bloqueio de cancelamento tardio (<1h) e prevencao de agendamentos
  sobrepostos garantidos tambem no banco (nao so no C#), via trigger +
  EXCLUDE constraint (secao 4.3/4.8).
- Historico paginado de agendamentos concluidos/cancelados, pro cliente
  e pra profissional/admin (secao 5.1/5.4).
- Projeto de testes automatizados (49 testes) + pipeline de CI no GitHub
  Actions (secao 4.5).
- Dockerfile pra deploy (nao verificado com build real neste ambiente —
  ver ressalva na secao 4.6).

------------------------------------------------------------------------
3. MULTI-TENANCY
------------------------------------------------------------------------

3.1. Como funciona
     Toda requisicao passa por TenantResolutionMiddleware
     (Services/TenantResolutionMiddleware.cs), que le o subdominio do
     Host, busca o tenant correspondente na tabela "tenants" (com cache
     positivo/negativo em IMemoryCache, TTL 60s) e popula um CurrentTenant
     scoped (Services/CurrentTenant.cs) usado por toda PageModel. Sem
     subdominio (dominio raiz ou "www") = contexto "plataforma"
     (marketing + onboarding). Subdominio que nao bate com nenhum tenant
     = "salao nao encontrado". Tenant encontrado mas is_active=false =
     "conta suspensa" (unico jeito de desligar um tenant problematico
     antes de existir billing).

3.2. Isolamento de dados
     profiles/services/appointments ganharam coluna tenant_id (migration
     supabase/migrations/0002_multi_tenant.sql). TODA query nessas tabelas
     no codigo C# encadeia um `.Where(x => x.TenantId == tenant.Id)`
     adicional — nunca fundido com `&&` no mesmo lambda de outro filtro,
     por causa de um bug documentado do driver postgrest-csharp (ver
     comentarios em DashProfissional.cshtml.cs). O e-mail de login deixou
     de ser unico globalmente e passou a ser unico por tenant (mesmo
     e-mail pode existir em saloes diferentes como contas separadas, sem
     SSO entre eles — decisao de produto consciente).

3.3. Onboarding self-service
     Pages/Onboarding/CriarSalao — so acessivel em contexto plataforma.
     Dono de um salao novo escolhe um slug (validado contra formato de
     subdominio e lista de slugs reservados: www, api, admin, app, etc.),
     nome do salao e seus proprios dados; isso cria 1 linha em "tenants"
     + 1 Profile com Role=admin. Redireciona para
     https://{slug}.{RootDomain}/Auth/Login (login automatico
     cross-subdominio nao e possivel — ver 3.4 — entao o dono loga de
     novo ja no subdominio certo).

3.4. Seguranca do isolamento (2 camadas)
     (a) O cookie de autenticacao NAO tem Cookie.Domain configurado, entao
     e host-scoped por padrao — um cookie de studio-bella.suaapp.com nao
     e enviado pelo browser para salao-da-ana.suaapp.com. NAO configure
     Cookie.Domain compartilhado entre subdominios sem repensar isso.
     (b) Isso sozinho nao e uma fronteira de seguranca real (uma
     requisicao HTTP arbitraria pode enviar Host + cookie escolhidos a
     mao). Por isso existe uma claim customizada "tenant_id" gravada no
     login/cadastro, checada por um middleware global em Program.cs logo
     apos UseAuthentication(): se a claim nao bater com o CurrentTenant
     resolvido pela URL, a sessao e encerrada (SignOutAsync) e o usuario
     e redirecionado pro login do subdominio atual.

3.5. Divida tecnica consciente: isolamento so em nivel de aplicacao
     O backend usa a SecretKey (service_role) do Supabase, que ignora RLS.
     Ou seja, mesmo com RLS habilitado (sem policies, so bloqueando
     anon/authenticated — migrations 0001 e 0002), a UNICA coisa que
     impede uma query vazar dados de outro tenant e alguem ter lembrado
     de encadear o `.Where(TenantId == ...)` naquela query especifica. Nao
     ha rede de seguranca no banco. Escrever RLS com policies reais
     exigiria propagar o tenant via JWT customizado no client Postgrest —
     nao foi feito ainda. Se um dia o app expuser acesso direto ao
     Supabase pelo browser (chave publica/anon), isso passa a ser
     obrigatorio, nao opcional.

3.6. Pre-requisito de infraestrutura (fora do codigo, bloqueante em producao)
     Para o roteamento por subdominio funcionar em producao e preciso:
     (a) DNS wildcard (*.suaapp.com apontando pro host da aplicacao);
     (b) certificado TLS que cubra o wildcard (Let's Encrypt via desafio
     DNS-01, ja que o desafio HTTP-01 nao cobre wildcard). Nada disso e
     codigo — e checklist operacional a resolver antes do primeiro
     cliente pagante.

3.7. Status das migrations: 0001-0005 RODADAS COM SUCESSO, 0006 PENDENTE
     supabase/migrations/0001 a 0005 ja foram aplicadas no projeto Supabase
     (confirmado por voce em 2026-07-17). studio-bella e demais tenants
     resolvem normalmente, a conta de superadmin ja existe e o 2FA dela
     esta ativo. ACAO NECESSARIA: supabase/migrations/
     0006_cancellation_trigger_and_overlap_constraint.sql ainda NAO foi
     rodada — ver secao 4.3/4.8 pro que ela faz e secao 6 pra ordem
     completa.

3.8. Superadmin da plataforma (cross-tenant) — NOVO
     Painel /SuperAdmin, usado so pelo dono da MarcAi, pra ativar/
     desativar a "licenca" (tenants.is_active) de qualquer salao — hoje o
     unico mecanismo de controle de acesso a plataforma, ja que ainda nao
     existe billing (ver 4.9).

     Arquitetura: superadmin NAO e um Profile com role especial — vive
     numa tabela propria, platform_admins (migration
     supabase/migrations/0004_platform_admins.sql), completamente fora do
     modelo multi-tenant (sem tenant_id, sem FK pra tenants). Login em
     /SuperAdmin/Login (so acessivel no dominio raiz — CurrentTenant.
     IsResolved == false — mesma guarda ja usada no Onboarding) busca em
     platform_admins em vez de profiles, e o cookie emitido NAO tem claim
     "tenant_id" nenhuma (superadmin nao pertence a tenant algum). A
     policy "SuperAdminOnly" (RequireRole("superadmin")) protege a pasta
     /SuperAdmin inteira. Dashboard (/SuperAdmin/Dashboard) e a UNICA tela
     do app que intencionalmente NAO filtra por tenant — lista todos os
     saloes de proposito.

     ACAO NECESSARIA: supabase/migrations/0004_platform_admins.sql ainda
     NAO foi aplicada no Supabase. Ela cria a tabela platform_admins E
     semeia a conta inicial (vinnicius.santos2005@gmail.com, com a senha
     combinada em 2026-07-17 — hash BCrypt ja gerado e embutido no
     script). Rode no SQL Editor do Supabase antes de tentar logar em
     /SuperAdmin/Login, senão a busca falha (tabela ainda nao existe) e
     o login sempre retorna "e-mail ou senha invalidos".

     Divida tecnica que sobrou: ainda nao existe segundo superadmin/
     convite — se precisar adicionar outra pessoa, e via SQL direto no
     Supabase por enquanto (so um INSERT em platform_admins). Troca de
     senha e 2FA foram resolvidos — ver 3.9.

3.9. Segurança do superadmin: troca de senha + 2FA (TOTP) — NOVO
     /SuperAdmin/Security (link "Segurança" na navbar do superadmin):
     troca de senha (exige a senha atual) e ativação/desativação de 2FA
     por app autenticador (Google Authenticator, Authy, etc.).

     2FA (TOTP, RFC 6238) implementado do zero em Services/TotpService.cs
     — sem pacote externo pro algoritmo em si (é só HMAC-SHA1 +
     truncamento dinâmico, ambos já no BCL). Validado ANTES de entrar no
     código contra os 5 vetores de teste oficiais do RFC 6238 Appendix B
     (todos bateram) e um round-trip de Base32, num projeto scratch
     isolado — não é "confiar que o RFC foi implementado certo", foi
     testado contra os valores publicados. QR code gerado com o pacote
     QRCoder (classe PngByteQRCode — renderer puro C#, sem depender de
     System.Drawing.Common, importante pra rodar em Linux/produção mais
     tarde; System.Drawing.Common ainda aparece como dependência
     transitiva do pacote, mas não é usada em runtime porque não é essa
     classe que a chama).

     Fluxo de ativação: "Ativar 2FA" gera um segredo novo e grava no
     banco como "pendente" (totp_secret preenchido, totp_enabled=false —
     não exige código ainda). A tela mostra o QR + a chave manual; só
     quando um código de 6 dígitos válido é confirmado é que
     totp_enabled vira true. Isso evita a pessoa ativar por engano com um
     app mal configurado e ficar trancada pra fora.

     Fluxo de login com 2FA ativo: em duas etapas na MESMA página
     (/SuperAdmin/Login) — 1) e-mail+senha; se corretos e totp_enabled,
     2) código de 6 dígitos. Entre as duas etapas, o id do admin viaja
     num token protegido por IDataProtector (built-in do ASP.NET Core,
     sem pacote novo) com validade de 5 minutos — NÃO é uma claim de
     autenticação real nem cookie válido; o login só se completa de fato
     depois do código confirmado em OnPostVerifyTotpAsync.

     Desativar 2FA exige a senha atual de novo (ação que reduz a
     segurança da conta, então pede reautenticação, mesmo padrão de
     "ação sensível pede senha de novo" usado em varios apps).

     ACAO NECESSARIA: supabase/migrations/0005_superadmin_security.sql
     (colunas totp_secret/totp_enabled) ainda NAO foi aplicada no
     Supabase. Sem ela, /SuperAdmin/Security quebra ao tentar ler essas
     colunas. Rode antes de tentar ativar o 2FA.

     Divida tecnica consciente que sobrou: sem códigos de backup/
     recuperação — se você perder o celular com o app autenticador e não
     tiver mais acesso a ele, a única forma de recuperar o acesso é rodar
     `update platform_admins set totp_enabled=false, totp_secret=null`
     direto no Supabase (você tem acesso total ao seu próprio banco, então
     não é um bloqueio permanente, só não é self-service). Sem rate
     limit dedicado pro código TOTP (reaproveita a policy "login", 5/min/
     IP, que já cobre decentemente já que só sobram ~1M combinações por
     janela de 30s de qualquer forma).

------------------------------------------------------------------------
4. REQUISITOS FALTANTES — IMPORTANTES
   (necessarios para operar em producao com clientes reais)
------------------------------------------------------------------------

4.1. [BLOQUEADO — decisao de produto pendente, nao de codigo] Nao existe
     fluxo de "esqueci minha senha"
     Autenticacao 100% manual (BCrypt na tabela profiles), sem nenhum
     caminho de recuperacao de senha. Vale por tenant: cada salao tem
     seus proprios usuarios travados igualmente sem esse fluxo. BLOQUEIO
     REAL: precisa de um provedor de e-mail transacional pra enviar o
     token de reset — perguntado explicitamente em 2026-07-17 (qual
     provedor usar: SendGrid, Resend, SMTP proprio, etc.) e a resposta foi
     "pular por agora", ou seja, adiado de proposito, nao esquecido. Sem
     essa escolha, nao ha como implementar este item (nao e um problema de
     falta de tempo/codigo, e falta de uma decisao de infraestrutura que
     so voce pode tomar). Enquanto isso nao existe, a unica forma de
     recuperar acesso e reset manual de senha direto no Supabase (voce
     gera um novo hash BCrypt e atualiza a linha em profiles).

4.2. [BLOQUEADO — mesmo motivo do 4.1] Sem verificacao de e-mail no
     cadastro/onboarding
     Nem o cadastro de cliente nem a criacao de um salao novo confirmam
     que o e-mail informado pertence a pessoa. Mesmo bloqueio do item
     4.1: exige provedor de e-mail pra enviar o link/codigo de
     verificacao, decisao adiada em 2026-07-17. Assim que um provedor for
     escolhido, os itens 4.1, 4.2, 5.3 e 5.6 (todos os gaps que dependem
     de enviar e-mail) podem ser desenvolvidos juntos, ja que
     compartilhariam a mesma configuracao/servico de envio.

4.3. [RESOLVIDO] Trigger de cancelamento — agora existe de verdade
     O comentario em DashCliente.cshtml.cs pressupunha uma
     trigger_check_cancellation que nao existia em nenhuma migration —
     confirmado que era so um comentario aspiracional, nao algo ja rodado
     no Supabase. Escrita e implementada na migration supabase/migrations/
     0006_cancellation_trigger_and_overlap_constraint.sql: funcao PL/pgSQL
     check_cancellation_window() + trigger BEFORE UPDATE em appointments
     que bloqueia cancelamento com menos de 1h de antecedencia do
     start_time, levantando excecao customizada
     ('CANCELLATION_TOO_LATE: ...', SQLSTATE P0001).
     DashCliente.cshtml.cs (OnPostCancelAsync) agora tem um catch
     especifico que reconhece essa mensagem (via ex.Message ou
     InnerException) e mostra "Esse agendamento nao pode mais ser
     cancelado — faltam menos de 1h para o horario marcado.", com um
     catch generico separado (logado via ILogger) pra qualquer outro erro.
     ACAO NECESSARIA: rodar a migration 0006 no Supabase — ver secao 6.

4.4. [RESOLVIDO] Dependencia com vulnerabilidade conhecida
     NU1902 em Microsoft.IdentityModel.JsonWebTokens/System.IdentityModel.
     Tokens.Jwt 7.0.3 era transitiva de supabase-csharp/Gotrue (Gotrue nao
     e mais usado pro fluxo de auth, mas o pacote continua referenciado
     por outras partes do SDK). Corrigido fixando PackageReference
     explicita das duas (8.19.2) em AndressaLeite.csproj — isso sobrepoe a
     versao transitiva vulneravel. Confirmado com rebuild limpo sem
     nenhum warning NU1902.

4.5. [RESOLVIDO] Testes automatizados + pipeline de CI
     Novo projeto AndressaLeite.Tests (xUnit, referenciado em
     AndressaLeite.slnx) cobrindo os dois candidatos mais criticos que
     nao dependem do Supabase pra rodar:
       - TotpServiceTests.cs: valida TotpService (2FA do superadmin, secao
         3.9) contra os 5 vetores oficiais do RFC 6238 Appendix B, rejeicao
         de codigo errado/malformado/fora da janela de drift, round-trip de
         Base32, e um teste de round-trip completo (gera segredo novo,
         calcula o codigo correto pro instante atual com o mesmo helper
         interno que ValidateCode usa, confirma que e aceito). ComputeCode/
         Base32Encode/Base32Decode/StepSeconds viraram "internal" (via
         InternalsVisibleTo pro projeto de teste) so pra permitir isso, sem
         expor nada na API publica do servico.
       - AuthorizationServiceTests.cs: cobre IsLocalSafeUrl (protecao
         open-redirect — vetores //evil.com, /\evil.com, URLs absolutas),
         GetDefaultLandingForRole, TryGetUserId/TryGetTenantId (leitura de
         claims, incluindo a claim "tenant_id" que a checagem cross-tenant
         da secao 3.4 depende) e GetRole/KnownRoles.
     49 testes, todos passando (dotnet test). A logica de resolucao de
     tenant (TenantResolutionMiddleware) e o filtro por tenant_id em cada
     query dependem do Supabase real pra testar de verdade — ficaram fora
     deste projeto (exigiriam um Supabase de teste ou mocks do client, nao
     feito nesta rodada); continuam sendo o maior risco sem cobertura
     automatizada (ver 3.5).
     CI: .github/workflows/ci.yml roda dotnet build + dotnet test em todo
     push/PR pra main.

4.6. [RESOLVIDO — NAO VERIFICADO COM BUILD REAL] Dockerfile / doc de deploy
     Dockerfile multi-estagio (build + runtime) na raiz do repositorio,
     mais .dockerignore. Ver secao 6.1 pra instrucoes completas de build/
     run/deploy. RESSALVA IMPORTANTE: este ambiente de desenvolvimento nao
     tem Docker instalado (docker --version falhou), entao o Dockerfile
     foi escrito com cuidado mas NUNCA rodou de fato — nao ha garantia de
     que a imagem builda sem erro ou que o container sobe corretamente.
     ACAO NECESSARIA: rodar `docker build` e `docker run` localmente (onde
     Docker estiver disponivel) antes de confiar nisso pra deploy real,
     em especial a tag "10.0-preview" das imagens base (mcr.microsoft.com/
     dotnet/sdk e /aspnet) — .NET 10 ainda e preview, a tag exata pode
     mudar ou nao existir mais quando voce for rodar; conferir em
     https://mcr.microsoft.com/en-us/product/dotnet/sdk/tags e trocar pra
     "10.0" simples assim que a versao estavel sair.

     EM ANDAMENTO (2026-07-17): instalando Docker Desktop na sua maquina
     de dev pra poder rodar a verificacao acima. Descoberto no caminho que
     a maquina precisa de WSL2, que por sua vez precisa de virtualizacao
     habilitada na BIOS/UEFI (Intel VT-x/AMD-V) — vinha desabilitada.
     Passos ja identificados, na ordem:
       1. `wsl.exe --install --no-distribution` (PowerShell administrador)
          — habilita o recurso do Windows "Plataforma da Maquina Virtual".
       2. Reiniciar o computador.
       3. Entrar na BIOS/UEFI (tecla do fabricante no boot, ou Windows >
          Configuracoes > Sistema > Recuperacao > Inicializacao avancada >
          Reiniciar agora > Solucionar problemas > Configuracoes de
          firmware UEFI) e habilitar Intel VT-x / AMD-V (SVM Mode).
       4. Voltar pro Windows, rodar `wsl --install` de novo (agora deve
          completar e baixar o Ubuntu).
       5. Instalar o Docker Desktop (https://www.docker.com/products/docker-desktop/),
          deixando a opcao "Use WSL 2 instead of Hyper-V" marcada.
       6. So entao rodar a verificacao pendente: `docker build -t
          marcai:latest .` e `docker run` (ver 6.1).
     PROXIMO PASSO (retomar daqui): reiniciar a maquina apos o passo 1 e
     entrar na BIOS pra habilitar a virtualizacao (passo 3).

4.7. [RESOLVIDO — ver secao 7, Fase 2] Horario comercial fixo no codigo,
     nao configuravel por tenant
     Cada salao agora configura seu proprio horario de abertura,
     fechamento e intervalo de almoço pelo Admin ("Horário de
     Funcionamento" em DashAdmin). Dias da semana fechados continuam
     fixos (so domingo) — extensao natural futura, nao pedida ainda.

4.8. [RESOLVIDO] Checagem de conflito de horario ganhou backstop no banco
     A checagem em C# (le agendamentos existentes e compara em memoria)
     continua sendo a primeira linha — e o que da a mensagem de erro
     amigavel pro usuario antes de qualquer round-trip extra. Mas ela
     sozinha nao protegia contra insercao concorrente (duas requisicoes
     simultaneas passando pela checagem em memoria antes de qualquer uma
     gravar). Fechado na migration 0006 (mesma da 4.3) com uma constraint
     EXCLUDE USING gist no Postgres — extensao btree_gist, intervalo
     tstzrange(start_time,end_time) com operador &&, particionada por
     tenant_id+employee_id, ignorando agendamentos cancelados (WHERE
     status <> 'cancelled'). end_time virou NOT NULL (com backfill
     defensivo de +30min pra qualquer linha antiga sem end_time) porque a
     constraint precisa de um intervalo fechado dos dois lados. Se essa
     constraint disparar (caso extremamente raro, so em corrida real), o
     C# hoje nao tem um catch especifico pra ela — cairia no catch
     generico como erro tecnico; nao tratado ainda porque exigiria
     reproduzir uma condicao de corrida real pra testar, considerado baixo
     risco o suficiente pra nao bloquear o resto do roadmap.

4.9. Billing/assinatura ainda nao existe (decisao consciente)
     Combinado explicitamente com o usuario: multi-tenancy primeiro,
     billing fica pra depois. [PARCIALMENTE RESOLVIDO] A desativacao de
     tenant deixou de ser so via SQL direto — agora tem uma tela
     (/SuperAdmin/Dashboard, secao 3.8), mas continua sendo um toggle
     manual feito por voce, sem nenhuma cobranca, plano ou trial
     associado.

4.10. Sem branding customizavel por tenant, sem dominio proprio
     Cada salao usa o subdominio da plataforma e o mesmo layout/cores;
     nao ha logo/paleta por tenant nem suporte a dominio customizado do
     cliente (ex.: agenda.studiobella.com.br apontando pro tenant dele).

------------------------------------------------------------------------
5. REQUISITOS FALTANTES — RECOMENDADOS
   (qualidade e robustez, podem esperar)
------------------------------------------------------------------------

5.1. [PARCIALMENTE RESOLVIDO] Paginacao nas listas
     As telas de historico de agendamentos (5.4 — DashCliente e
     DashProfissional) ganharam paginacao leve: 10 itens por pagina,
     navegacao "mais recentes/mais antigos" via query string
     (HistoryPage), paginada EM MEMORIA (busca tudo do periodo e faz
     Skip/Take em C#, nao no PostgREST) — aceitavel no volume atual, mas
     nao escala se o historico de um tenant crescer muito (viraria um
     `Range`/`.Limit()` do lado do PostgREST). As listas de profissionais,
     servicos e a agenda do dia (nao-historico) continuam sem paginacao —
     baixo volume esperado nessas (poucos profissionais/servicos por
     salao, poucos agendamentos por dia), risco menor que o historico
     acumulado.
5.2. [RESOLVIDO — ver secao 7, Fase 3] Campo de observacoes/notas no
     agendamento, preenchido pela profissional na conclusao do atendimento.
5.3. [BLOQUEADO — mesmo motivo do 4.1/4.2] Sem notificacao por e-mail/SMS
     de confirmacao ou lembrete de agendamento
     O lembrete via WhatsApp da secao 7.5 cobre parte disso na pratica
     (o app ja monta a mensagem e o link, so nao envia sozinho), mas
     continua sendo um link manual que a profissional clica, nao um envio
     automatico por e-mail/SMS/WhatsApp Business API. Mesmo bloqueio:
     nenhum provedor de e-mail/SMS escolhido ainda (decisao adiada em
     2026-07-17 — ver 4.1).
5.4. [RESOLVIDO] Tela de historico de agendamentos concluidos/cancelados
     DashCliente ganhou um passo "historico" (link "Ver historico de
     agendamentos ->") listando agendamentos completed/cancelled, com
     badge de status. DashProfissional ganhou um toggle "Ver historico ->"
     na propria tela (em vez de pagina separada) mostrando o mesmo tipo de
     lista pros agendamentos da profissional/admin logada. Os dois
     paginados conforme 5.1. Logica de resolucao de nome de servico/
     cliente extraida pra um helper privado compartilhado em cada
     PageModel (ResolveNamesAsync/BuildAppointmentViewsAsync) pra nao
     duplicar entre a view "hoje" e a view "historico".
5.5. Buscas de nome de servico/profissional/cliente fazem uma query por
     id distinto em memoria (sem JOIN nativo do PostgREST) — aceitavel no
     volume de poucos tenants pequenos, revisar se crescer muito.
5.6. [BLOQUEADO — mesmo motivo do 4.1/4.2/5.3] Convite de equipe por
     e-mail
     Hoje o admin cria a conta do profissional direto com uma senha —
     funciona, mas nao e o fluxo "convide por e-mail e a pessoa define a
     propria senha". Mesmo bloqueio de provedor de e-mail (ver 4.1).
     Continua fora do escopo do roadmap da secao 7 (que ja foi
     inteiramente concluido — ver 7.8) e so pode avancar quando o provedor
     for escolhido.

------------------------------------------------------------------------
6. COMO RODAR O PROJETO LOCALMENTE (resumo)
------------------------------------------------------------------------
1. Segredos do Supabase via user-secrets (nao editar appsettings.json
   com valores reais):
     dotnet user-secrets set "Supabase:Url" "https://SEU-PROJETO.supabase.co"
     dotnet user-secrets set "Supabase:SecretKey" "sb_secret_..."
2. Aplique, NESTA ORDEM (ver convencao de migrations na secao 7.7), no
   SQL Editor do painel do Supabase:
     supabase/migrations/0001_auth_columns_and_rls.sql
     supabase/migrations/0002_multi_tenant.sql
     supabase/migrations/0003_agenda_enriquecida.sql
     supabase/migrations/0004_platform_admins.sql
     supabase/migrations/0005_superadmin_security.sql
     supabase/migrations/0006_cancellation_trigger_and_overlap_constraint.sql  <- AINDA NAO RODADA
   (idempotentes, podem rodar mais de uma vez). A 0002 semeia
   automaticamente o tenant "studio-bella" com os dados que ja existiam;
   a 0004 semeia sua conta de superadmin (ver secao 3.8); a 0005 adiciona
   as colunas de 2FA (ver secao 3.9); a 0006 cria a trigger de bloqueio de
   cancelamento tardio e a EXCLUDE constraint anti-overlap (ver 4.3/4.8) —
   exige a extensao btree_gist (a propria migration ja faz o `create
   extension if not exists`).
3. appsettings.Development.json ja tem "Tenancy:RootDomain": "localhost".
   Navegadores modernos resolvem qualquer *.localhost para 127.0.0.1
   nativamente, sem editar hosts file.
4. dotnet run (dentro da pasta AndressaLeite/). Acesse:
     http://localhost:5081/                        -> pagina da plataforma (onboarding)
     http://localhost:5081/SuperAdmin/Login         -> login do superadmin (so voce)
     http://studio-bella.localhost:5081/            -> salao existente
     http://studio-bella.localhost:5081/Auth/Login  -> login do salao
   Pra criar um segundo salao de teste, use o CTA "Criar meu salao" na
   home da plataforma.
5. Cadastre pelo menos um servico ativo pelo Painel Admin de cada salao
   (Admin > Cadastrar Servico) antes de testar o agendamento do cliente.

Em producao, defina Supabase__Url, Supabase__SecretKey e
Tenancy__RootDomain como variaveis de ambiente do host — nunca em
arquivo versionado — e resolva o checklist de DNS wildcard + TLS
wildcard (item 3.6) antes de vender pro primeiro cliente.

------------------------------------------------------------------------
6.1. DEPLOY VIA DOCKER (Dockerfile na raiz do repositorio) — ver ressalva 4.6
------------------------------------------------------------------------
Build multi-estagio: estagio "build" usa a imagem SDK do .NET (so pra
compilar/publicar), estagio final usa a imagem "aspnet" (runtime puro,
bem menor, sem ferramentas de build expostas). Container roda como
usuario nao-root (usuario "app" ja embutido na imagem aspnet oficial) e
escuta na porta 8080 dentro do container.

Build da imagem (rodar na raiz do repositorio, onde esta o Dockerfile):
     docker build -t marcai:latest .

Rodar localmente, passando os segredos via variavel de ambiente (nunca
em arquivo versionado, mesma regra do dotnet user-secrets em dev):
     docker run -p 8080:8080 ^
       -e Supabase__Url="https://SEU-PROJETO.supabase.co" ^
       -e Supabase__SecretKey="sb_secret_..." ^
       -e Tenancy__RootDomain="suaapp.com" ^
       marcai:latest
   (sintaxe de continuacao de linha "^" e do cmd.exe do Windows; em
   PowerShell use crase "`" no final da linha, em bash use "\").

Pre-requisitos que continuam fora do container (nao resolvidos por
Docker sozinho):
  - Reverse proxy/terminador TLS na frente do container (nginx, Caddy,
    Traefik, ou o load balancer do provedor de cloud) — o Kestrel dentro
    do container serve so HTTP puro na porta 8080; certificado wildcard
    (item 3.6) fica no proxy, nao no app.
  - DNS wildcard apontando pro host/proxy (item 3.6).
  - As migrations em supabase/migrations/ continuam sendo aplicadas
    manualmente no SQL Editor do Supabase (o container nao roda
    migrations sozinho — nao ha ferramenta de migration automatica neste
    projeto, ver 7.7).
  - Variaveis de ambiente do container: Supabase__Url, Supabase__SecretKey,
    Tenancy__RootDomain (equivalentes as chaves de appsettings.json, com
    "__" no lugar de ":" — convencao padrao do ASP.NET Core pra
    configuracao via variavel de ambiente).

NAO TESTADO NESTE AMBIENTE: ver ressalva completa na secao 4.6 — este
ambiente de desenvolvimento nao tem Docker instalado, entao nem o build
nem o run acima foram executados de fato. Rodar os dois comandos acima
como primeira verificacao antes de qualquer deploy real.

------------------------------------------------------------------------
7. ROADMAP — AGENDA ENRIQUECIDA (PROXIMA FRENTE DE DESENVOLVIMENTO)
------------------------------------------------------------------------
Ainda NAO implementado — desenho da solucao + plano de fases combinado
com o usuario em 2026-07-16/17, pra guiar a proxima rodada de
desenvolvimento. Objetivo: o app deixa de ser so "cliente agenda, sistema
registra" e passa a cobrir o fluxo real de operacao de um salao: horario
comercial (com almoço) e duracao de servico realmente respeitados na hora
de agendar, cada profissional podendo personalizar preço/duracao do que
ela oferece, encaixes feitos direto pela profissional, registro do que
de fato foi cobrado e como foi pago, lembrete pra cliente, e a dona do
salao podendo atender clientes tambem (nao so gerenciar).

7.1. Requisito: logica de horario comercial + duracao de servico
     A regra "um servico de 1h30 nao pode ser agendado se o salao fecha
     as 18h e sobram so 40min" JA EXISTE hoje (DashCliente.OnPostBookAsync
     calcula o fim do atendimento — inicio + duracao do servico — e
     rejeita se ultrapassar o horario de fechamento). O que falta:
     (a) os horarios de abertura/fechamento sao constantes fixas no
     codigo (BusinessOpenHour/BusinessCloseHour), iguais pra todo tenant;
     (b) nao existe horario de almoço — um agendamento pode cair bem no
     meio do intervalo da profissional.

     Solucao proposta:
     - tenants ganha 4 colunas: business_open_time, business_close_time,
       lunch_start_time, lunch_end_time (tipo `time`, os dois ultimos
       nullable — null = sem intervalo de almoço configurado).
     - CurrentTenant (Services/CurrentTenant.cs) passa a carregar esses 4
       valores, populados pelo TenantResolutionMiddleware junto com o
       resto do tenant — vira a fonte unica de verdade, sem precisar
       buscar de novo em cada PageModel.
     - DashCliente.OnPostBookAsync troca as constantes fixas por
       CurrentTenant.BusinessOpenTime/BusinessCloseTime, e ganha uma nova
       checagem: o intervalo [inicio,fim) do agendamento nao pode
       sobrepor [lunch_start_time, lunch_end_time) quando configurado
       (mesma logica de sobreposicao ja usada pro conflito de agenda,
       reaproveitada).
     - Tela de configuracao no Admin ("Horario de Funcionamento"): 4
       campos de hora (abertura, fechamento, inicio e fim do almoço,
       este ultimo par opcional) com um handler OnPostUpdateBusinessHoursAsync.
     - Fora de escopo por ora: dias da semana fechados continuam fixos
       (hoje so domingo) — dar isso pelo Admin tambem e extensao natural
       futura, nao pedida nesta rodada.

7.2. Requisito: cada profissional personaliza preço e duracao do proprio serviço
     Hoje "services" e um catalogo unico por tenant (Admin cadastra Nome/
     Preço/Duração/Ativo) — o mesmo valor vale pra qualquer profissional
     que atenda aquele serviço. O pedido e que cada profissional possa
     ter seu proprio preço e tempo medio pro mesmo tipo de servico (ex.:
     "Design de sobrancelha" custa R$60 e leva 45min com uma profissional,
     mas R$70 e 60min com outra).

     Solucao proposta:
     - Nova tabela professional_services: tenant_id, employee_id
       (profile), service_id, price (nullable), duration_minutes
       (nullable), is_active. Unique (employee_id, service_id). E um
       OVERRIDE, nao substitui o catalogo: se a profissional nao tiver
       uma linha pra aquele servico (ou os campos ficarem nulos), vale o
       preço/duracao padrao definido pelo Admin em "services".
     - O catalogo em si (nomes dos servicos, criar/desativar/remover)
       continua exclusivo do Admin — assim uma profissional personalizar
       o proprio preço nao muda o valor de ninguem mais.
     - Nova secao "Meus Servicos" na agenda da profissional
       (DashProfissional): lista os servicos ativos do tenant, cada um
       com um campo de preço e duracao que, se preenchidos, viram a
       override dela; vazio = usa o padrao do catalogo.
     - DashCliente precisa mudar: ao escolher a profissional e depois o
       servico, o preço/duracao exibidos e usados no calculo (Fase 1 do
       horario, EstimatedRevenue) passam a ser o EFETIVO (override da
       profissional escolhida, se existir, senao o padrao do catalogo) —
       nao mais so Service.Price/DurationMinutes direto.
     - Mesma logica vale pro agendamento manual da profissional (7.3) e
       pra conclusao de atendimento (7.4): o valor sugerido/pre-preenchido
       deve ser o efetivo dela, nao o do catalogo generico.

7.3. Requisito: agendamento manual pela profissional
     Hoje SO o cliente cria agendamento (DashCliente.OnPostBookAsync); a
     profissional so visualiza. Ela precisa poder registrar direto na
     propria agenda um encaixe, uma ligacao telefonica ou um walk-in.

     Solucao proposta:
     - Novo handler (ex.: OnPostBookManualAsync) em
       Pages/Profissional/DashProfissional.cshtml.cs, com um formulario
       simples: Servico (dropdown dos servicos ativos do tenant, com
       preço/duracao efetivos dela — ver 7.2), Data/Hora, Nome do
       cliente, Telefone do cliente. EmployeeId e sempre a propria
       profissional logada — nao precisa escolher.
     - Reaproveita os campos BookedForName/BookedForPhone que ja existem
       no model Appointment (hoje usados so pro cliente agendar "para
       outra pessoa"), em vez de criar campos novos.
     - Mudanca de dado necessaria: Appointment.ClientId hoje e
       [Required]; precisa virar opcional (string?), porque um
       agendamento criado pela profissional pode nao ter nenhuma conta
       de cliente vinculada. Sem ClientId, o agendamento corretamente
       nao aparece em nenhuma tela "Meus agendamentos" de cliente.
     - Reuso importante: a validacao de horario comercial + almoço (7.1)
       + conflito de agenda (hoje dentro de DashCliente.OnPostBookAsync)
       deve ser extraida para um servico compartilhado (ex.:
       Services/AppointmentBookingService.cs) e chamada tanto pelo fluxo
       do cliente quanto por este novo fluxo da profissional — evita
       duplicar (e um dia dessincronizar) a mesma regra de negocio em
       dois lugares.

7.4. Requisito: editar valor, servico prestado e forma de pagamento
     Hoje "Concluir atendimento" (OnPostCompleteAsync) e um botao de um
     clique que so copia Service.Price pra ActualRevenue — sem ajuste
     possivel (desconto, servico diferente do agendado) e sem registrar
     como foi pago.

     Solucao proposta:
     - Trocar o botao "Concluir atendimento" por um mini-formulario
       (inline ou modal Bootstrap) com: Servico prestado (dropdown,
       pre-selecionado com o servico originalmente agendado, mas
       editavel), Valor cobrado (numero, pre-preenchido com o preço
       efetivo da profissional pra aquele servico — ver 7.2 — editavel),
       Forma de pagamento (dropdown: Dinheiro / Pix / Cartao de Debito /
       Cartao de Credito / Outro), Observacoes (texto livre, opcional).
     - Ao confirmar: grava ServiceId (se foi trocado), ActualRevenue =
       valor informado, PaymentMethod, Notes, Status = completed.
     - Mudanca de dado: Appointment ganha PaymentMethod (text, nullable)
       e Notes (text, nullable).

7.5. Requisito: lembrete via WhatsApp
     Solucao proposta (deliberadamente simples, sem integracao paga):
     - Link `https://wa.me/{telefone}?text={mensagem}` ao lado de cada
       agendamento na agenda da profissional (e da admin, que tambem
       atende — ver 7.6). Telefone: BookedForPhone se preenchido, senao
       o Phone do Profile vinculado a ClientId.
     - Mensagem pre-preenchida sugerida: nome da cliente, data/hora do
       atendimento e nome do salao (via CurrentTenant.Name).
     - E so um link `<a target="_blank">` — abre o WhatsApp Web/app e a
       profissional confirma o envio manualmente. Nao usa WhatsApp
       Business API (custo/homologacao fora de escopo por ora).
     - Mudanca de codigo: AppointmentView (DashProfissional) ganha
       ClientPhone; a view monta a URL do link.

7.6. Requisito: admin como "profissional premium" + dashboard separado
     A admin deve conseguir atender clientes como qualquer profissional
     (agenda propria, agendamento manual, concluir atendimento, "Meus
     Servicos"), MAS manter um dashboard proprio de gestao/metricas
     separado da agenda.

     Solucao proposta:
     (a) Agenda propria: trocar a policy da pasta /Profissional de
         "EmployeeOnly" para uma nova policy "EmployeeOrAdmin"
         (`policy.RequireRole("employee", "admin")` — RequireRole ja
         aceita multiplas roles como OR, nao precisa de logica extra).
         Admin passa a acessar /Profissional/DashProfissional com sua
         propria agenda (EmployeeId = id do profile dela).
     (b) Aparecer como opcao pro cliente escolher: a etapa "pro" do
         agendamento do cliente (hoje filtra so Role=="employee") passa
         a incluir Role=="admin" tambem — duas queries separadas
         concatenadas em memoria (mesmo padrao ja usado hoje pro filtro
         de status pending/confirmed, pra evitar o bug de OR do driver
         postgrest-csharp).
     (c) Dashboard de metricas (DashAdmin, mantido separado da agenda):
         novos indicadores, todos calculados a partir de Appointment
         filtrado por tenant:
           - Atendimentos concluidos hoje (count de Status=="completed"
             com StartTime no dia atual).
           - Receita esperada ate o fim do dia (soma de EstimatedRevenue
             dos agendamentos de hoje, nao cancelados).
           - Detalhamento por forma de pagamento (soma de ActualRevenue
             agrupada por PaymentMethod dos atendimentos concluidos —
             periodo inicial sugerido: mes atual).
           - Painel de observacoes recentes (Notes preenchidas nos
             ultimos atendimentos concluidos), pra dar visibilidade de
             comentarios registrados no atendimento.

------------------------------------------------------------------------
7.7. MIGRATIONS: CONVENCAO E ESCOPO DA 0003 [RODADA COM SUCESSO]
------------------------------------------------------------------------
Convencao do projeto (ja seguida desde a 0001): todo script de schema
fica em supabase/migrations/, numerado em sequencia (0001, 0002, 0003...)
com um nome curto descrevendo o conteudo, e escrito pra ser idempotente
sempre que possivel (IF NOT EXISTS, blocos DO com checagem em
pg_constraint, etc.) — assim pode rodar de novo sem erro se precisar.
Eu nao tenho acesso direto ao banco (so a API REST via service key),
entao NUNCA aplico migration sozinho: cada uma fica pronta na pasta pra
voce rodar manualmente no SQL Editor do Supabase, na ordem dos numeros.

supabase/migrations/0003_agenda_enriquecida.sql JA FOI ESCRITA (Fase 1
concluida no codigo) — falta so voce rodar no SQL Editor do Supabase,
depois da 0001 e da 0002, antes de continuar pra Fase 2. Escopo:
  - tenants: add column business_open_time time not null default '09:00',
    business_close_time time not null default '19:00',
    lunch_start_time time null, lunch_end_time time null.
  - appointments: add column payment_method text null (com check
    constraint pros valores dinheiro/pix/cartao_debito/cartao_credito/
    outro), add column notes text null, alter column client_id drop not
    null.
  - nova tabela professional_services (tenant_id, employee_id, service_id,
    price numeric null, duration_minutes int null, is_active boolean),
    unique (employee_id, service_id), FKs pra tenants/profiles/services,
    RLS habilitado sem policy (mesmo padrao das outras tabelas).
  - indice em appointments (tenant_id, start_time) pra acelerar os
    agregados "hoje"/"por dia" do dashboard da admin (Fase 7).

Models atualizados junto (Tenant.cs, Appointement.cs, novo
ProfessionalService.cs) — os horarios de tenant sao guardados como
string ("HH:mm:ss") em vez de TimeSpan/TimeOnly, pra nao depender de como
o driver postgrest-csharp serializa esses tipos (nao validado neste
projeto); parsear com TimeSpan.Parse ao usar (Fase 2).

CORRIGIDO apos primeira tentativa: a 0003 original quebrava no Supabase
com "foreign key constraint... incompatible types: text and uuid". Causa:
profiles.id e services.id sao "uuid" de verdade no banco (schema
original, de quando o projeto usava Supabase Auth) — o C# so enxerga
esses ids como string, mas por baixo o Postgres e uuid. professional_
services.employee_id/service_id foram ajustados pra "uuid" (tenants.id
continua "text", porque essa tabela e nossa, criada do zero na 0002).
Como todo o script roda dentro de um unico `begin/commit`, o erro
reverteu a transacao inteira — nada da 0003 ficou aplicado pela metade,
pode rodar o arquivo corrigido do zero. LICAO pra proximas migrations que
criem FK apontando pra profiles/services/appointments: usar "uuid", nao
"text", nessas colunas.

0003 RODADA COM SUCESSO em 2026-07-17 (apos a correcao acima).

------------------------------------------------------------------------
7.8. PLANEJAMENTO CRONOLOGICO (FASES SUGERIDAS)
------------------------------------------------------------------------
Ordem pensada por dependencia (cada fase assume as anteriores prontas).
Tamanho relativo (P/M/G) no lugar de datas fixas, ja que isso depende de
quantas pessoas/horas por semana vao tocar o desenvolvimento — ajuste a
duracao real com base nisso, a ordem e as dependencias e que importam.

FASE 1 — Fundacao de dados (migration 0003 completa)     [M] [FEITA]
  Tudo listado em 7.7: colunas de horario/almoço em tenants, payment_
  method/notes/client_id-nullable em appointments, nova tabela
  professional_services — migration escrita, RODADA COM SUCESSO no
  Supabase em 2026-07-17, e models atualizados.

FASE 2 — Horario comercial dinamico + almoço             [M] [FEITA]
  CurrentTenant (Services/CurrentTenant.cs) ganhou BusinessOpenTime/
  BusinessCloseTime (TimeSpan) e LunchStartTime/LunchEndTime (TimeSpan?),
  populados pelo TenantResolutionMiddleware a partir do Tenant (parse das
  strings "HH:mm:ss"). DashCliente.OnPostBookAsync trocou as constantes
  fixas por esses valores e ganhou a checagem de sobreposicao com o
  almoço (mesma logica de overlap ja usada pro conflito de agenda).
  DashAdmin ganhou a secao "Horário de Funcionamento" (4 campos de hora,
  validacao server-side: abertura < fechamento, almoço dentro do
  expediente, os dois campos de almoço juntos ou nenhum) — ao salvar,
  invalida a entrada no cache do TenantResolutionMiddleware
  (TenantResolutionMiddleware.CacheKey), senao a mudança só valeria depois
  do TTL de 60s expirar sozinho.

FASE 3 — Conclusao de atendimento enriquecida            [M] [FEITA]
  "Concluir atendimento" virou um mini-formulario (collapse Bootstrap por
  agendamento, sem precisar de modal): Servico prestado (dropdown com
  todo o catalogo do tenant, incluindo inativos — a profissional pode ter
  atendido algo que ja saiu do catalogo de agendamento do cliente),
  Valor cobrado (pre-preenchido com EstimatedRevenue, editavel), Forma de
  pagamento (dinheiro/pix/cartao_debito/cartao_credito/outro — mesmos
  valores do check constraint da migration 0003) e Observacoes. O
  servidor revalida o servico (pertence ao tenant?) e a forma de
  pagamento (esta na lista permitida?) em vez de confiar no que veio do
  form. O preço sugerido no formulario de conclusao (EstimatedRevenue)
  ja reflete o valor efetivo da profissional desde a Fase 4, porque e
  gravado no momento do agendamento.

FASE 4 — Personalizacao de serviço por profissional      [M/G] [FEITA]
  Nova seção "Meus Serviços" em DashProfissional: cada profissional
  define preço e/ou duração próprios por serviço do catálogo (campos
  independentes — dá pra personalizar só o preço, só a duração, ou os
  dois). Upsert manual (busca se já existe override pra aquele par
  profissional+serviço, atualiza; senão insere) — sem usar upsert nativo
  do driver, consistente com o resto do código. DashCliente ganhou
  GetEffectiveServiceValuesAsync (busca o override, se existir, senão
  cai no padrão do catálogo) usado tanto na listagem "Escolha o Serviço"
  quanto no cálculo de horário/receita em OnPostBookAsync — o preço/
  duração mostrados e gravados agora são sempre os da profissional
  escolhida, não o valor genérico do catálogo. (Esse método foi movido
  pra Services/AppointmentBookingService.cs na Fase 5, ver abaixo.)

FASE 5 — Agendamento manual pela profissional            [M] [FEITA]
  Nova Services/AppointmentBookingService.cs (scoped, registrado no
  Program.cs) reúne GetEffectiveServiceValuesAsync (movido de DashCliente)
  + ValidateBookingAsync (horário comercial/almoço/conflito de agenda,
  antes duplicado inline em OnPostBookAsync) — usado agora tanto por
  DashCliente quanto pelo novo agendamento manual da profissional, sem
  repetir a regra em dois lugares. DashProfissional ganhou a seção
  "Agendar Manualmente" (collapse) com o handler OnPostBookManualAsync:
  ClientId nulo (sem conta de cliente — usa BookedForName/BookedForPhone,
  revalidados com o mesmo regex de telefone do Cadastro), EmployeeId
  sempre a própria profissional (não precisa escolher), e Status nasce
  "confirmed" direto (diferente do fluxo do cliente, que nasce "pending"
  até a profissional confirmar) — ela mesma está garantindo o horário ao
  criar.

FASE 6 — Admin como profissional premium                 [M] [FEITA]
  Nova policy "EmployeeOrAdmin" (RequireRole aceita múltiplas roles como
  OR) trocando o AuthorizeFolder("/Profissional") e o [Authorize] de
  DashProfissionalModel — admin acessa a mesma agenda que a profissional,
  ganhando de graça agendamento manual, "Meus Serviços" e conclusão de
  atendimento (Fases 2-5), sem duplicar nada. DashCliente na etapa "pro"
  passa a buscar Role=="employee" e Role=="admin" em duas queries
  separadas concatenadas (mesmo padrão já usado pro filtro de status
  pending/confirmed, evita o bug de OR do driver). Validação de posse em
  OnPostBookAsync (proCheck.Role) e navbar ("Minha Agenda") atualizadas
  pra aceitar as duas roles. DashAdmin continua sendo o painel de gestão
  separado — a Fase 7 é quem adiciona métricas nele.

FASE 7 — Dashboard de metricas da admin                  [M/G] [FEITA]
  DashAdmin ganhou: atendimentos concluidos hoje (StartTime dentro do dia
  atual, UTC), receita esperada hoje (EstimatedRevenue nao cancelados de
  hoje), detalhamento por forma de pagamento (ActualRevenue agrupado por
  PaymentMethod, mes atual) e painel de observacoes recentes (10 ultimos
  atendimentos concluidos com Notes preenchido, com servico/cliente
  resolvidos — mesmo padrao de lookup ja usado em DashCliente/
  DashProfissional). Tudo calculado em memoria a partir da MESMA lista de
  appointments do tenant que ja era buscada pro card de faturamento — sem
  query extra, so mais LINQ.

FASE 8 — Lembrete via WhatsApp                             [P] [FEITA]
  Botao "📲 Lembrar via WhatsApp" ao lado de cada atendimento pendente/
  confirmado na agenda da profissional (e da admin, que usa a mesma
  tela). Link `https://wa.me/{telefone}?text=...` — telefone vem de
  BookedForPhone ou do Profile do cliente vinculado; mensagem com nome da
  cliente, nome do servico, data/hora (local, corrigida — ver bugfix
  abaixo) e nome do salao. So abre o WhatsApp Web/app com o texto
  pronto — a profissional confirma o envio manualmente, sem nenhuma
  integracao paga com WhatsApp Business API.

  BUG ENCONTRADO E CORRIGIDO no caminho: os horarios exibidos na agenda
  (DashCliente, DashProfissional, observacoes recentes do DashAdmin) e os
  filtros de "hoje"/"este mes" (Fase 7) estavam em UTC, nao no horario
  local do salao — confirmado com um teste isolado (fuso do servidor:
  America/Sao_Paulo, UTC-3): um agendamento feito pra 14h aparecia como
  17h em todo lugar, e a virada do dia/mes usava a data UTC em vez da
  local. Corrigido com .ToLocalTime() nas exibicoes e recalculando os
  limites de "hoje"/"mes atual" a partir de DateTime.Now (Kind=Local) em
  vez de DateTime.UtcNow — mesma premissa ja usada no booking (SelectedDateTime.
  ToUniversalTime() assume Kind=Local). Sem essa correcao, a mensagem do
  lembrete no WhatsApp diria a hora errada pra cliente, o que anularia o
  proposito da Fase 8.

Sugestao de execucao: 1 -> 2 -> 3 -> 4 -> 5 -> 6 -> 7, com a Fase 8
encaixada em paralelo assim que a Fase 1 estiver pronta (nao bloqueia
nem e bloqueada pelas demais, so precisa do telefone que ja existe hoje).

FORA DO PLANO ORIGINAL, feito junto com a Fase 7 a pedido do usuario em
2026-07-17: rebrand da plataforma pra "MarcAi" (secao 1) e painel de
superadmin cross-tenant (secao 3.8) — nao estavam nas 8 fases desenhadas
originalmente, foram encaixados porque dependiam do mesmo momento
(dashboard do admin) e porque o superadmin precisava existir antes de
fazer sentido ter "licenca" de salao pra controlar.

------------------------------------------------------------------------
8. STATUS GERAL E PROXIMOS PASSOS (fechamento da rodada "todos os itens
   pendentes do readme, um por um" — 2026-07-17)
------------------------------------------------------------------------
Todos os itens da secao 4 (importantes) e 5 (recomendados) que dependiam
so de codigo/decisao tecnica foram resolvidos nesta rodada: migration
0006 (trigger de cancelamento + EXCLUDE anti-overlap, 4.3/4.8), NU1902
(4.4), testes automatizados + CI (4.5), Dockerfile + doc de deploy (4.6),
historico paginado (5.1/5.4). O roadmap inteiro da Agenda Enriquecida
(secao 7, Fases 1-8) ja estava concluido antes desta rodada.

O que continua genuinamente em aberto, e por que:

  1. ACAO NECESSARIA SUA (migrations pendentes de rodar no Supabase):
     - supabase/migrations/0006_cancellation_trigger_and_overlap_constraint.sql
       (ver 4.3/4.8/6).
  2. ACAO NECESSARIA SUA (verificacao com ferramenta que este ambiente
     nao tem):
     - `docker build` + `docker run` do Dockerfile novo (ver 4.6/6.1) —
       nunca executado aqui por falta de Docker instalado neste ambiente
       de desenvolvimento.
  3. BLOQUEADO por decisao de produto (nao de codigo, ja perguntado e
     respondido "pular por agora" em 2026-07-17): 4.1 (esqueci minha
     senha), 4.2 (verificacao de e-mail), 5.3 (notificacao automatica por
     e-mail/SMS), 5.6 (convite de equipe por e-mail) — todos exigem
     escolher um provedor de e-mail transacional primeiro. Quando essa
     decisao for tomada, os quatro podem ser desenvolvidos juntos numa
     unica rodada (compartilham a mesma configuracao de envio).
  4. DECISAO CONSCIENTE DE ESCOPO, adiada de proposito (nao bloqueada,
     so nao priorizada ainda): 4.9 (billing/assinatura — so o toggle
     manual de ativar/desativar existe), 4.10 (branding/dominio proprio
     por tenant).
  5. DIVIDA TECNICA CONSCIENTE, registrada mas nao um bloqueio imediato:
     3.5 (isolamento de tenant so em nivel de aplicacao, sem RLS com
     policies reais), a parte de 4.5 que fica de fora dos testes
     automatizados (logica de resolucao de tenant/filtro por tenant_id,
     que dependeria de um Supabase de teste ou mocks pra cobrir de
     verdade), e o catch especifico faltando pra quando a EXCLUDE
     constraint da 4.8 disparar de fato (caso raro de corrida).

Nao ha mais itens de codigo pendentes fora dessas cinco categorias.
