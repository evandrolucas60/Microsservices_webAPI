# Deploy do backend no Docker Swarm (Ubuntu Server, ambiente de teste)

O backend roda em containers com **2 réplicas** por serviço. São 19 serviços: o gateway, os 10 da intranet e os 8 da Escala 5x2. As imagens são geradas pelo GitHub Actions e publicadas no GitHub Container Registry (GHCR). O servidor só baixa as imagens e sobe o stack.

```
push na main ──► GitHub Actions ──► ghcr.io/evandrolucas60/arconet-<serviço>
                                               │ docker pull
navegador ──► gateway :5120 (servidor Ubuntu, Swarm) ──► serviços (rede overlay, 2 réplicas)
                                               │
                     Postgres 10.11.10.4:8082 · RabbitMQ 10.11.10.4:9000 · \\10.11.10.5\inetpub
```

| Arquivo | Para quê |
|---|---|
| `../.github/workflows/docker-images.yml` | Gera e publica as 19 imagens a cada push na `main`, ou manualmente. |
| `Dockerfile` | Imagem única. O projeto é escolhido por `--build-arg PROJECT=`. |
| `docker-stack.yml` | Serviços, réplicas, rede, porta do gateway e volume das fotos. |
| `gateway.appsettings.json` | Faz o gateway apontar para `http://<serviço>:8080` em vez de `https://localhost:70xx`. |
| `deploy.sh` | Carrega o `.env` e roda o `docker stack deploy`. |
| `.env.example` | Variáveis do deploy. No servidor, copie para `.env`. |

---

## 1. Publicar as imagens no GitHub (uma vez, e depois a cada mudança)

1. Faça commit e push na `main` do repositório `Microsservices_webAPI`. O commit precisa incluir as pastas `Escala5x2/`, `deploy/`, `.github/` e o arquivo `.dockerignore`.
2. No GitHub, abra **Actions → Imagens Docker** e espere os 19 jobs ficarem verdes. A primeira execução leva alguns minutos.
   - Para rodar sem push: **Actions → Imagens Docker → Run workflow**.
3. As imagens aparecem em **github.com/evandrolucas60 → Packages**, com o nome `arconet-<serviço>`.
4. Os pacotes do GHCR nascem **privados**. O servidor vai precisar de um token para baixá-los (passo 2.5).
   - Para dispensar o token, deixe cada pacote público em **Package settings → Change visibility**.

## 2. Preparar o servidor Ubuntu (uma vez)

**Pré-requisitos.** Ubuntu 22.04 ou 24.04, com 8 GB de RAM ou mais (são cerca de 36 containers .NET). O servidor precisa alcançar:
- o Postgres em `10.11.10.4:8082`;
- o RabbitMQ em `10.11.10.4:9000`;
- o compartilhamento SMB em `10.11.10.5:445`;
- o `ghcr.io`, pela internet.

Confira se essas portas respondem:

```bash
nc -zv 10.11.10.4 8082 && nc -zv 10.11.10.4 9000 && nc -zv 10.11.10.5 445
```

### 2.1 Instalar o Docker

```bash
sudo apt-get update
sudo apt-get install -y ca-certificates curl git cifs-utils
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Usar o docker sem sudo (saia e entre de novo na sessão SSH depois)
sudo usermod -aG docker $USER
```

O `cifs-utils` instalado acima é necessário para montar o compartilhamento das fotos de perfil.

### 2.2 Iniciar o Swarm

```bash
# Servidor do backend: 10.11.10.4 (o mesmo do Postgres e do RabbitMQ)
docker swarm init --advertise-addr 10.11.10.4 --default-addr-pool 172.20.0.0/16
```

> O `--default-addr-pool` evita que as redes internas do Swarm usem `10.x.x.x`. Esse é o padrão do Docker, e ele colidiria com a rede da empresa (`10.11.10.x`). Escolha uma faixa `172.x` que não exista na rede.

### 2.3 Liberar a porta do gateway (se o ufw estiver ativo)

```bash
sudo ufw allow 5120/tcp
```

### 2.4 Baixar os arquivos de deploy

```bash
git clone https://github.com/evandrolucas60/Microsservices_webAPI.git ~/arconet
cd ~/arconet/deploy
```

Se o repositório for privado, o git pede usuário e senha. No lugar da senha, use um token do GitHub.

Sem git, dá para copiar só a pasta `deploy` a partir do Windows:

```powershell
scp -r .\deploy usuario@IP_DO_SERVIDOR:~/arconet/
```

### 2.5 Login no GHCR

Crie um token em **GitHub → Settings → Developer settings → Personal access tokens (classic)** com o escopo `read:packages`. Depois:

```bash
echo '<TOKEN>' | docker login ghcr.io -u evandrolucas60 --password-stdin
```

### 2.6 Configurar o `.env`

```bash
cp .env.example .env
nano .env
```

| Variável | Exemplo | Observação |
|---|---|---|
| `REGISTRY` | `ghcr.io/evandrolucas60` | Dono do repositório no GitHub. |
| `TAG` | `latest` | Ou `sha-1a2b3c4` para fixar uma versão. |
| `GATEWAY_PORT` | `5120` | Porta aberta no servidor. |
| `PUBLIC_GATEWAY_URL` | `https://intranet.arcomix.com.br/gateway` | Endereço do gateway visto pelo navegador. Com o portal em HTTPS, o IIS repassa `/gateway/...` para a porta 5120 (veja `Frontend/DEPLOY-IIS-HTTPS.md`). |
| `FRONTEND_ORIGIN` | `https://intranet.arcomix.com.br` | Endereço do front, exatamente como aparece no navegador, sem `/` no fim. Fica liberado no CORS e no WebSocket do chat, e vai nos links dos e-mails. |
| `SMB_USER` / `SMB_PASSWORD` / `SMB_DOMAIN` | | Conta com escrita em `\\10.11.10.5\inetpub\wwwroot\intranetManagementFiles\intranet`. A senha não pode ter vírgula. |

## 3. Deploy

```bash
cd ~/arconet/deploy
bash deploy.sh
```

Acompanhe até todos os serviços mostrarem `2/2` na coluna REPLICAS. O `comunicacao`, o `notificacao`, o `escala5x2-employee` e o `escala5x2-store` mostram `1/1` (veja [Limitações](#limitações)).

```bash
watch docker stack services arconet
```

## 4. Conferir

```bash
# Tarefas de um serviço (estado, nó, erro se houver)
docker service ps arconet_auth --no-trunc

# Logs (das 2 réplicas juntas)
docker service logs -f arconet_gateway
docker service logs -f arconet_auth

# Gateway → AuthAPI respondendo (espera-se 400: corpo vazio, mas a cadeia funciona)
curl -i -X POST http://localhost:5120/auth/api/auth/login -H 'Content-Type: application/json' -d '{}'
```

## 5. Front-end (no outro servidor)

O portal roda em HTTPS no IIS do 10.11.10.5 e chama o gateway pelo próprio endereço (`/gateway`, repassado pelo IIS para `http://10.11.10.4:5120`). O `Frontend/.env.production` já traz `VITE_GATEWAY_URL=/gateway`. Passo a passo do IIS em `Frontend/DEPLOY-IIS-HTTPS.md`.

O endereço em que o front é aberto no navegador precisa ser igual ao `FRONTEND_ORIGIN` do `.env` do backend. Se não for, o WebSocket do chat é recusado.

## 6. Atualizar

1. Faça push na `main` e espere o Actions terminar.
2. No servidor:

```bash
cd ~/arconet/deploy
git pull          # traz mudanças do docker-stack.yml, se houver
bash deploy.sh    # baixa as imagens novas e faz o rolling update
```

O rolling update troca uma réplica por vez. A nova sobe antes de a antiga sair, e há 30 s entre uma réplica e outra. Se a nova não subir, o Swarm volta a versão anterior sozinho.

Para atualizar só um serviço:

```bash
docker service update --with-registry-auth --image ghcr.io/evandrolucas60/arconet-auth:latest arconet_auth
```

## 7. Voltar versão, escalar, remover

```bash
docker service rollback arconet_auth          # volta o serviço para a versão anterior
# ou fixe a versão no .env (TAG=sha-1a2b3c4) e rode bash deploy.sh

docker service scale arconet_auth=3           # muda o número de réplicas na hora
                                              # (o próximo deploy.sh volta ao valor do docker-stack.yml)

docker stack rm arconet                       # remove tudo (os bancos não são afetados)
```

## Problemas comuns

| Sintoma | Causa provável |
|---|---|
| `No such image` ou `unauthorized` em `docker service ps` | Faltou o `docker login ghcr.io` (passo 2.5), ou o nome em `REGISTRY` está errado. |
| `identidade` não sobe: `mount error(13)` ou `error(2)` | Usuário ou senha do SMB errados, falta o `cifs-utils`, ou o caminho do compartilhamento não existe. |
| Serviço reinicia no primeiro deploy com erro de migration | As 2 réplicas tentaram migrar o banco juntas. O Swarm reinicia a réplica e ela sobe, porque a migration já foi aplicada. |
| Front com erro de CORS | `FRONTEND_ORIGIN` diferente da origem real (http/https, IP ou porta). Corrija e rode `bash deploy.sh`. |
| `429` no login | Limite de login (veja abaixo). Aumente `LOGIN_RATE_LIMIT` no `.env`. |
| Serviço não acha o Postgres ou o RabbitMQ | Teste de dentro da rede do Swarm: `docker run --rm --network arconet_backend busybox nc -zv 10.11.10.4 8082` |

## Limitações

- **`comunicacao` (chat) fica com 1 réplica.** As conexões WebSocket ficam em memória, sem backplane, então com 2 réplicas parte das mensagens e da presença se perde.
- **`notificacao` fica com 1 réplica.** O worker de envio não trava as notificações pendentes, então com 2 réplicas os e-mails sairiam duplicados.
- **`escala5x2-employee` e `escala5x2-store` ficam com 1 réplica.** A sincronização diária com a LG (lojas às 03:00, colaboradores às 05:00) roda em cada réplica, e o andamento fica em memória. Com 2 réplicas a LG seria consultada duas vezes e a tela de andamento poderia mostrar "nada em execução".
- **IP do cliente:** atrás do ingress do Swarm, o gateway vê o IP interno. Por isso o limite de login vale para todos juntos e foi subido para 300 por minuto. Os IPs da auditoria também saem internos.
- **Logs:** ficam em `/app/logs` de cada container e somem quando ele é recriado. Use `docker service logs`.
- **Segredos:** as senhas do Postgres e do RabbitMQ e a chave do JWT continuam nos `appsettings.json` versionados.
