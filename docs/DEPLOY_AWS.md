# Deploy em produção — EC2 + Route53 (DNS wildcard + TLS)

Pré-requisito de código já pronto: `Dockerfile` (app), `Dockerfile.caddy` (proxy TLS), `docker-compose.yml`, `Caddyfile`. Este doc cobre só a parte de infraestrutura que fica **fora** do container.

Assume que `ROOT_DOMAIN` (ou a zona que o contém) está gerenciado no **Route53**. Se o domínio inteiro estiver em outro registrador (Registro.br, GoDaddy, etc.) — caso mais comum no Brasil — três opções, da menos pra mais invasiva:

**(a) Delegar só o subdomínio do MarcAi pro Route53 (recomendado quando a instância já serve outros apps no mesmo domínio raiz).** Não migra o domínio inteiro, não toca em nenhum registro já existente pros outros projetos:
1. Route53 → Hosted zones → Create hosted zone → Domain name = `ROOT_DOMAIN` completo (ex.: `marcai.vinnisantos.com.br`) → Public hosted zone. A AWS gera um registro NS com 4 nameservers e o Hosted Zone ID (é esse ID que entra na policy da seção 1).
2. No painel do registrador (Registro.br: "Edição de Zona DNS"/"DNS Avançado" do domínio raiz, ex. `vinnisantos.com.br`), adicionar um registro **NS** com nome igual ao subdomínio (ex.: `marcai`) apontando pros 4 nameservers do passo 1.
3. Propagação da delegação pode levar mais tempo que um registro comum (minutos a ~24h).
4. Como a hosted zone criada no passo 1 já É `ROOT_DOMAIN` (não a raiz do domínio todo), os registros da seção 4 abaixo usam nome vazio (raiz da zone) e `*`, não `marcai`/`*.marcai`.

**(b) Migrar a zona DNS inteira pro Route53** (grátis, só o registro do domínio continua no registrador) — faz sentido se o domínio raiz inteiro for de uso exclusivo deste projeto.

**(c) Trocar o plugin do `Dockerfile.caddy`** por um dos [~150 provedores suportados pelo Caddy](https://caddyserver.com/docs/modules/) — só vale a pena se o registrador atual já tiver API de DNS suportada (Registro.br não tem plugin Caddy conhecido).

**`ROOT_DOMAIN` pode ser o domínio inteiro (`suaapp.com.br`) ou um subdomínio dele (`marcai.suaapp.com.br`), inclusive num domínio que você já usa pra outras coisas.** O MarcAi só precisa do wildcard *daquele nome* — ex. `marcai.suaapp.com.br` + `*.marcai.suaapp.com.br` — então convive sem conflito com registros já existentes na mesma zona (`www.suaapp.com.br`, `outroapp.suaapp.com.br`, etc.). Ajuste os nomes dos registros DNS na seção 4 de acordo — se `ROOT_DOMAIN` não for a raiz da zona, o nome do registro não fica vazio, fica o próprio subdomínio.

**Instância EC2 já em uso por outros apps?** Nada nesse doc exige uma instância dedicada — só cheque antes de cada passo se o recurso (IAM Role, Elastic IP, regra de Security Group, Docker) já existe, pra não duplicar. O único requisito rígido é que as portas 80/tcp, 443/tcp e 443/udp estejam livres no host pro container Caddy do MarcAi — se outro processo já ocupa alguma delas, o `docker compose up` falha ao subir o serviço `caddy`.

## 0. Cheque o que já existe (instância compartilhada com outros apps)

Antes de criar qualquer recurso novo, confirme o que já está lá:

```bash
# a instancia ja tem IAM Role anexada?
curl -s http://169.254.169.254/latest/meta-data/iam/security-credentials/
# (vazio = sem role; um nome = ja existe, so precisa adicionar a policy do Route53 nela)

# portas 80/443 estao livres?
sudo ss -tlnp | grep -E ':80|:443'
# (sem output = livres, seguro pro Caddy do MarcAi usar)

# ja tem Elastic IP associado? (EC2 -> Instancias -> sua instancia -> aba Details -> "Elastic IP address")
# ja tem Docker instalado?
docker --version; docker compose version
```

## 1. IAM Role pra instância EC2 (em vez de chaves estáticas)

Rodar o desafio DNS-01 do Let's Encrypt exige que o Caddy consiga criar/apagar registros TXT no Route53. Anexar uma IAM Role à instância EC2 é mais seguro que colocar `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY` no `.env`.

1. IAM → Policies → Create policy (JSON):
   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       { "Effect": "Allow", "Action": "route53:GetChange", "Resource": "*" },
       { "Effect": "Allow", "Action": "route53:ChangeResourceRecordSets",
         "Resource": "arn:aws:route53:::hostedzone/SEU_HOSTED_ZONE_ID" },
       { "Effect": "Allow", "Action": "route53:ListHostedZonesByName", "Resource": "*" }
     ]
   }
   ```
   (pegue o `SEU_HOSTED_ZONE_ID` em Route53 → Hosted zones → sua zona.)
2. Se a instância **já tem uma Role** (passo 0 devolveu um nome): IAM → Roles → abra essa role → Add permissions → Create inline policy → cole o JSON acima. Se **não tem nenhuma**: IAM → Roles → Create role → AWS service → EC2 → anexar a policy → EC2 → sua instância → Actions → Security → Modify IAM role → selecionar a role criada.

Com isso, **não preencha** `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY` no `.env` — o SDK dentro do container detecta a role automaticamente via metadata da instância.

## 2. Elastic IP

Se o passo 0 já mostrou um Elastic IP associado (provável, já que os outros apps na instância dependem do IP público ser estável), pule este passo. Senão: aloque um e associe à instância (EC2 → Elastic IPs → Allocate → Associate). Sem isso, o IP público muda se a instância reiniciar, e o registro DNS quebra.

## 3. Security Group

Confirme que o Security Group já anexado à instância libera entrada de:
- porta 80/tcp (HTTP — usado só pro redirect automático do Caddy pra HTTPS)
- porta 443/tcp e 443/udp (HTTPS — o /udp é pro HTTP/3)
- porta 22/tcp (SSH, restrita ao seu IP, não `0.0.0.0/0` — já deve estar assim se você já acessa a instância)

Adicione as regras de 80 e 443 que faltarem (as portas dos outros apps continuam abertas do jeito que já estão, isso aqui só adiciona). Não precisa abrir a porta 8080 publicamente — ela só é usada internamente entre os containers `app` e `caddy` na rede Docker Compose.

## 4. DNS na Route53

Se veio pelo caminho **(a)** da introdução (subdomínio delegado, ex.: hosted zone criada já como `marcai.vinnisantos.com.br`) — a zone já É `ROOT_DOMAIN`, então os registros ficam na raiz dela. Hosted zone → Create record:
- Tipo A, nome vazio (raiz da zone = `marcai.vinnisantos.com.br`), valor = Elastic IP.
- Tipo A, nome `*` (= `*.marcai.vinnisantos.com.br`), valor = Elastic IP.

Se em vez disso a zone no Route53 for o domínio inteiro (ex.: `vinnisantos.com.br`) e `ROOT_DOMAIN` for só um subdomínio dela, os nomes ficam `marcai` e `*.marcai` ao invés de vazio e `*`. De qualquer forma isso não toca em nenhum registro já existente pra outros apps (`www`, subdomínio do portfólio, etc.).

Propagação costuma levar minutos, pode levar até algumas horas — a delegação NS (passo 0 da introdução, se usada) costuma ser mais lenta que um registro comum.

## 5. Instalar Docker na EC2 (Amazon Linux 2023 / Ubuntu)

Pule se o passo 0 já confirmou Docker instalado.

```bash
# Amazon Linux 2023
sudo dnf install -y docker
sudo systemctl enable --now docker
sudo usermod -aG docker $USER
# relogar a sessao SSH pra aplicar o grupo docker

# Docker Compose plugin
sudo dnf install -y docker-compose-plugin
```

```bash
# Ubuntu 22.04/24.04 — instala pelo repositorio oficial da Docker (o pacote
# docker.io do apt padrao do Ubuntu costuma ficar bem desatualizado)
sudo apt-get update
sudo apt-get install -y ca-certificates curl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
sudo systemctl enable --now docker
sudo usermod -aG docker $USER
# relogar a sessao SSH (sair e entrar de novo) pra aplicar o grupo docker
```

## 6. Deploy

```bash
git clone <seu-repo> marcai && cd marcai
cp .env.example .env
# editar .env: SUPABASE_URL, SUPABASE_SECRET_KEY, ROOT_DOMAIN=marcai.vinnisantos.com.br, ACME_EMAIL
docker compose up -d --build
docker compose logs -f caddy   # acompanhar emissao do certificado
```

Se o certificado wildcard não emitir, o log do Caddy mostra o motivo (quase sempre: IAM Role sem permissão suficiente, ou zone ID errado).

## 7. Depois do primeiro deploy

- Rodar as migrations pendentes no SQL Editor do Supabase (0001–0010, nesta ordem — ver `readme.txt` seção 6).
- Testar `https://marcai.vinnisantos.com.br/` (contexto plataforma) e `https://qualquer-slug.marcai.vinnisantos.com.br/` (contexto tenant).
- `RESEND_FROM_ADDRESS` e `ASAAS_WEBHOOK_TOKEN` já configurados (ver `.env.example`); `ASAAS_API_KEY` pode ficar vazio por enquanto — a assinatura via Asaas simplesmente não funciona até a chave sandbox ser configurada, o resto do app funciona normalmente.
- Configurar rotação de logs / monitoramento (fora de escopo deste doc — item ainda em aberto, ver análise de prontidão comercial).
