# Política de Privacidade — MarcAi

**AVISO:** rascunho inicial baseado na LGPD (Lei 13.709/2018), servindo como ponto de partida. Não substitui revisão por advogado especializado em proteção de dados antes de publicar. Campos entre `[colchetes]` precisam ser preenchidos.

Última atualização: [DATA]

## 1. Controlador e Operador

Para os fins da Lei Geral de Proteção de Dados (LGPD), o Estabelecimento (salão que contrata o MarcAi) é o **Controlador** dos dados pessoais dos seus próprios clientes finais (nome, telefone, histórico de agendamentos) — é ele quem decide coletar e usar esses dados.

O MarcAi atua como **Operador**: processamos esses dados por conta do Estabelecimento, seguindo as instruções dele (as telas que ele preenche e configura), com a finalidade exclusiva de viabilizar o agendamento.

Contato do encarregado (DPO) do MarcAi: [E-MAIL / NOME], conforme Art. 41 da LGPD.

## 2. Quais dados coletamos

| Categoria | Dado | Titular | Finalidade |
|---|---|---|---|
| Cadastro do Estabelecimento | Nome do salão, subdomínio, horário de funcionamento | Estabelecimento | Operação da agenda |
| Conta de acesso | Nome, e-mail, senha (hash BCrypt), telefone | Admin/Profissional/Cliente final | Login e identificação |
| Agendamento | Data/hora, serviço escolhido, valor, forma de pagamento, observações | Cliente final | Execução do agendamento |
| Segurança | IP de origem (rate limiting), logs de erro | Todos | Prevenção de abuso e depuração técnica |
| 2FA (só superadmin) | Segredo TOTP | Superadmin da Plataforma | Autenticação em duas etapas |

Não coletamos dados sensíveis (Art. 5º, II da LGPD — saúde, biometria, etc.) além do que o próprio Estabelecimento eventualmente registrar em "observações" livres — o Estabelecimento é responsável por não inserir dados sensíveis nesse campo sem base legal própria.

## 3. Base legal do tratamento

- **Execução de contrato** (Art. 7º, V): processar o agendamento é o próprio serviço contratado.
- **Legítimo interesse** (Art. 7º, IX): prevenção a fraude/abuso (rate limiting, logs de segurança).
- **Consentimento**, quando aplicável a funcionalidades futuras de comunicação direta (ex.: notificações automáticas por e-mail/SMS, ainda não implementadas nesta versão).

## 4. Como os dados são armazenados

Os dados ficam hospedados no Supabase (Postgres), com isolamento por Estabelecimento (cada salão só acessa seus próprios dados — arquitetura multi-tenant). Senhas nunca são armazenadas em texto puro (hash BCrypt). Segredos de infraestrutura (chaves de acesso ao banco) ficam fora do controle de versão do código.

[Se o servidor de aplicação for hospedado fora do Brasil, declarar aqui a transferência internacional de dados e a salvaguarda usada, conforme Art. 33 da LGPD.]

## 5. Compartilhamento com terceiros

Não vendemos dados pessoais. Dados podem ser compartilhados apenas com:
- Supabase (hospedagem do banco de dados) — atua como suboperador.
- [Provedor de e-mail transacional, quando escolhido] — apenas para envio de notificações/recuperação de senha.
- [Gateway de pagamento, quando integrado] — apenas dados necessários à cobrança do Estabelecimento (não dados do cliente final).
- Autoridades públicas, quando exigido por lei ou ordem judicial.

## 6. Direitos do titular

Nos termos do Art. 18 da LGPD, o titular pode solicitar ao Estabelecimento (Controlador) ou diretamente ao MarcAi: confirmação de tratamento, acesso, correção, anonimização/eliminação, portabilidade, informação sobre compartilhamento, e revogação de consentimento. Solicitações podem ser feitas em [E-MAIL DE CONTATO] e serão respondidas em até [15] dias.

## 7. Retenção e exclusão de dados

Dados são mantidos enquanto a conta do Estabelecimento estiver ativa. Ao encerrar a conta, os dados são [DEFINIR: excluídos em até X dias / retidos por obrigação legal por X anos]. [Este item precisa de decisão de produto — hoje o sistema não tem exclusão automática implementada; documentar o processo real antes de publicar esta política.]

## 8. Segurança

Medidas técnicas já implementadas: senha com hash BCrypt, autenticação por cookie seguro, cabeçalhos de segurança HTTP (CSP, HSTS, X-Frame-Options), 2FA para a conta de superadministrador, rate limiting em login/cadastro, isolamento de dados por Estabelecimento.

## 9. Alterações desta Política

Mudanças serão publicadas nesta página com nova data de atualização. Alterações materiais serão comunicadas por e-mail ao Estabelecimento.

## 10. Contato / Encarregado (DPO)

[NOME / E-MAIL DO ENCARREGADO], responsável por questões de proteção de dados no MarcAi.
