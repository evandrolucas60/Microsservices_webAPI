# Escala 5x2 — Backend

Geração de escalas 5x2 da rede Arco-Mix / Arco-Vita, integrada ao ecossistema da intranet Arco-Net.

.NET 10 · microsserviços · MassTransit 8 + RabbitMQ · PostgreSQL · Serilog · OR-Tools CP-SAT

## Estrutura

```
Escala5x2.sln
├── BuildingBlocks/
│   ├── Escala5x2.BuildingBlocks.Contracts    mensagens, eventos e DTOs entre os serviços
│   ├── Escala5x2.BuildingBlocks.Shared       JWT da intranet, MassTransit, PostgreSQL/migrations, Serilog, núcleo SOAP da LG
│   └── Escala5x2.BuildingBlocks.Compliance   regras de conformidade, avaliador e posicionamento do almoço
├── Services/
│   ├── Employee.Api        colaboradores, contratos, ausências/afastamentos      (https 7021)
│   ├── Store.Api           lojas, setores, turnos, demanda, feriados, /api/me    (https 7022)
│   ├── Compliance.Api      regras de conformidade (cadastro do superAdmin)        (https 7023)
│   ├── Scheduling.Api      escalas por setor, células, visão do dia, sugestões   (https 7024)
│   ├── Notification.Api    avisos                                                 (https 7025)
│   ├── Audit.Api           trilha de eventos                                      (https 7026)
│   └── Optimization.Worker motor CP-SAT (sem porta)
└── Tests/Optimization.Tests, Dimensionamento.Tests, Employee.Tests e Store.Tests (integração LG)
```

## Dependência do repositório da intranet

A Escala 5x2 fica dentro do repositório da intranet e referencia, por caminho relativo, o
BuildingBlocks dela (contratos do MassTransit, bloqueio de usuários e políticas de superAdmin):

```
Microsservices_webAPI\           ← repositório da intranet (Intranet.sln)
├── BuildingBlocks\BuildingBlocks.Contracts
├── BuildingBlocks\BuildingBlocks.Shared
└── Escala5x2\                   ← esta pasta (Escala5x2.sln)
```

Autenticação e entrada passam pela intranet: o login é o da AuthAPI (seção `ApiSettings` igual à dos
serviços da intranet) e o acesso pelo `Intranet.Gateway` (rotas `/api/escala5x2/**`).

## Papéis e lojas (roles por loja)

Papel pelas roles do JWT da intranet: superAdmin → Admin; `RH`, `Juridico`, `Gestor` → mesmo papel; demais → Colaborador.
Lojas permitidas = loja do perfil da intranet (IdentidadeAPI, "01 - CABO") ∪ lojas das **roles por loja**
`Escala5x2-Loja-{code}` do token (ex.: `Escala5x2-Loja-01`, `Escala5x2-Loja-CD`; código comparado normalizado, "1" = "01",
sem diferenciar maiúsculas). Modelo configurável em `RolesLoja:Modelo` (precisa conter `{code}`; use o mesmo valor em
todos os serviços 5x2).

Papel efetivo por loja (`EscopoLojas`, repetido no front em `roleInStore`):

| Usuário | Loja do perfil | Lojas das roles | Demais lojas |
| --- | --- | --- | --- |
| Admin (superAdmin), RH, Jurídico | o próprio papel | o próprio papel | o próprio papel |
| Gestor | Gestor | Gestor | sem acesso |
| Colaborador com role de loja ("operador") | Colaborador (consulta) | **Gestor** (visualizar e gerar/editar/aprovar) | sem acesso |
| Colaborador sem role de loja | Colaborador (consulta) | — | sem acesso |

A role de loja nunca concede Admin/RH (regras, auditoria, dimensionamento, sincronizações continuam só desses papéis).
Claims: `store` (loja principal: a do perfil; sem ela, a primeira das roles), `escala_store` (uma por loja permitida) e
`escala_store_gestor` (uma por loja em que é Gestor). `GET /api/me` devolve também `storeIds` e `managedStoreIds`.

Criação das roles: o Store.Api publica `GarantirRoles` (contrato da intranet em `BuildingBlocks.Contracts.Administracao`)
quando uma loja é criada, depois de cada sincronização de lojas com a LG e 30 s após iniciar
(`RolesLoja:GarantirNaInicializacao`, padrão true); o AuthAPI (`GarantirRolesConsumer`) cria as que faltam (idempotente).
`POST /api/stores/garantir-roles` (Admin) faz o mesmo para todas as lojas aguardando o resultado (botão "Garantir roles das
lojas" no painel de gestão > Roles); `GET /api/stores/roles` (Admin) lista role → loja. Roles de loja inativa **não** são
apagadas (a loja some do seletor dos demais perfis, mas a role continua existindo para quando a loja voltar).
A atribuição é do superAdmin no painel de gestão (Usuários > Roles, grupo "Escala 5x2 — lojas"). A role só vale com um
token novo: no próximo login ou na renovação da sessão (o refresh token relê as roles do usuário).

## Executar

Pré-requisitos: .NET SDK 10, PostgreSQL (10.11.10.4:8082), RabbitMQ (10.11.10.4:9000) e, no Windows,
o **Microsoft Visual C++ Redistributable 2015-2022 x64** (necessário para o OR-Tools).

```
dotnet build Escala5x2.sln
dotnet test Tests/Optimization.Tests
```

Ordem de subida: AuthAPI e IdentidadeAPI (intranet) → Store.Api → Employee, Compliance, Scheduling,
Notification, Audit e Optimization.Worker → Intranet.Gateway. As APIs sobem com
`dotnet run --launch-profile https`; o Worker com `dotnet run`. Cada serviço aplica as próprias
migrations do EF Core na subida.

## Logs

O Serilog vem do `AddSharedSerilog` da intranet: sem `Serilog:WriteTo` no appsettings (padrão dos serviços 5x2)
grava no console e em `{ContentRoot}/logs/{Serviço}-AAAAMMDD.log` (rolagem diária, 14 dias, 50 MB por arquivo).
Cada serviço tem a própria pasta; o superAdmin (papel Admin) lista e baixa os arquivos pela aba **Logs** da tela
ou direto por serviço, no prefixo que o gateway já roteia (`/api/escala5x2/{área}/logs`):

| Endpoint (só Admin) | O que faz |
| --- | --- |
| `GET {prefixo}/logs?servico=&de=&ate=` | lista os arquivos (serviço, nome, dia, tamanho) |
| `GET {prefixo}/logs/{serviço}/{arquivo}` | baixa um arquivo (streaming) |
| `GET {prefixo}/logs/zip?arquivos=Serviço/arquivo.log&...` (ou `servico/de/ate`) | vários arquivos num .zip |

Prefixos: `/api/schedules` (Scheduling.Api e Optimization.Worker), `/api/employees`, `/api/stores`, `/api/rules`,
`/api/notifications`, `/api/audit`, `/api/dimensionamento`. Configuração opcional: `Logs:Pasta` (outra pasta do
próprio serviço, ex.: quando o `Serilog:WriteTo` grava em outro lugar) e `Logs:PastasExtras:{Serviço}` (pasta de
outro processo do mesmo servidor; o Scheduling.Api publica `../Optimization.Worker/logs`). Em contêiner, monte a
pasta do Worker no Scheduling.Api e ajuste o caminho.

Outros downloads do superAdmin: `GET /api/schedules/{id}/solucoes/log?formato=csv|json` (opções avaliadas pelo
motor na última geração) e `GET /api/audit/exportar?type=&scheduleId=&from=&to=` (auditoria em CSV).

## Integração LG (lojas, setores e colaboradores)

O Store.Api (lojas e setores) e o Employee.Api (colaboradores) consultam a LG por SOAP 1.1 com `HttpClient` + `XDocument`
(sem proxy WCF). O núcleo SOAP é compartilhado em `BuildingBlocks/Escala5x2.BuildingBlocks.Shared/Lg` (`LgOptions`,
`LgXml`, `LgSoapCore`): envelope com `LGAutenticacao`/`LGContextoAmbiente`, endpoint e SOAPAction por operação, timeout,
retry com backoff, SOAP Fault, `CodigoDoErro`/`Mensagens`/`Tipo`, XML inválido ("Resposta inválida da LG") e paginação
`PaginaAtual` 1..`TotalDePaginas`. A consulta inteira vem antes de qualquer gravação: se a LG falhar, nada muda.

| Operação | Serviço | Namespace da operação | Para quê |
| --- | --- | --- | --- |
| `ConsultarListaCentroDeCustoCompletoPorDemanda` | Store.Api | `lg.com.br/api/v2` | lojas (centros de custo de loja) |
| `ConsultarListaPorDemanda` (órgão responsável) | Store.Api | `lg.com.br/api/v1` | catálogo de setores da empresa |
| `ConsultarListaHistoricosVigenteNaDataPorDemanda` | Employee.Api | `lg.com.br/api/v2` | colaboradores, situação, loja e setor padrão |
| `ConsultarLista` (ServicoDeFerias v2) | Employee.Api | `lg.com.br/api/v2` | períodos de férias por matrícula (bloqueiam a escala) |

Os serviços WCF da LG não usam a mesma versão: a operação de órgãos responsáveis fica em `lg.com.br/api/v1` (prefixo `v1`
no envelope) e o filtro em `lg.com.br/api/dto/v1` (prefixo `v11`), enquanto as outras duas têm a operação em
`lg.com.br/api/v2` e o filtro em `lg.com.br/api/dto/v1`. `LgOperacao` guarda os dois namespaces de cada operação e
`LgXml.MontarEnvelope` usa os da operação. Por ser outro serviço, **os órgãos responsáveis provavelmente têm outro `.svc`**:
informe `LG:Operacoes:OrgaosResponsaveis:Url` (vazio = `LG:Url`, que costuma ser o serviço de colaboradores e responderia
"sem ConsultarListaPorDemandaResult"/404).

**Ordem: lojas → setores → colaboradores.** A sincronização de lojas (botão, diária e a chamada interna do Employee.Api)
grava as lojas e, logo depois, garante o catálogo de setores em todas as lojas ativas. O colaborador é casado com a loja
primeiro pelo código do centro de custo que a sincronização de lojas grava na loja; centro de custo de loja que ainda não
existe na Escala é ignorado com o aviso "rode a sincronização de lojas".

Seção `LG` (a mesma nos dois serviços) — **o repositório só tem placeholders vazios; credenciais NUNCA vão para o Git
nem para o log**:

| Chave | Padrão | O que é |
| --- | --- | --- |
| `LG:Url` | vazio | endpoint do serviço SOAP da LG (usado quando a operação não tem URL própria) |
| `LG:Operacoes:Colaboradores:SoapAction` | vazio | SOAPAction dos colaboradores; vazio = `LG:SoapAction` (chave antiga, continua valendo) |
| `LG:Operacoes:Colaboradores:Url` | vazio | endpoint próprio dos colaboradores (vazio = `LG:Url`) |
| `LG:Operacoes:CentrosDeCusto:SoapAction` | vazio | SOAPAction dos centros de custo (obrigatório para sincronizar lojas) |
| `LG:Operacoes:CentrosDeCusto:Url` | vazio | endpoint do serviço WCF de centros de custo (vazio = `LG:Url`) |
| `LG:Operacoes:OrgaosResponsaveis:SoapAction` | vazio | SOAPAction de `ConsultarListaPorDemanda` (obrigatório para o catálogo de setores) |
| `LG:Operacoes:OrgaosResponsaveis:Url` | vazio | endpoint do serviço WCF de órgãos responsáveis (vazio = `LG:Url`; em geral é outro `.svc`) |
| `LG:Operacoes:Ferias:Url` | vazio | endpoint do ServicoDeFerias (obrigatório, não usa `LG:Url`). WSDL público: `https://prd-api1.lg.com.br/v2/servicodeferias` |
| `LG:Operacoes:Ferias:SoapAction` | vazio | `lg.com.br/api/v2/ServicoDeFerias/ConsultarLista` (do WSDL) |
| `LG:Ferias:Paralelismo` / `LG:Ferias:FalhasSeguidasParaInterromper` | 6 / 20 | consultas simultâneas (1..16) e falhas seguidas que interrompem a rodada |
| `LG:Ferias:MesesPassados` / `LG:Ferias:ValidadeMinutos` | 12 / 10 | não importa férias terminadas há mais de 12 meses; antes da geração, não consulta de novo quem foi consultado há menos de 10 min |
| `LG:Ferias:AposSincronizarColaboradores` / `LG:Ferias:NamespaceFiltro` | true / vazio | consulta as férias de todos os ativos depois da sincronização; namespace do filtro (vazio = `lg.com.br/api/dto/v1`, o do WSDL) |
| `LG:Usuario`, `LG:Senha`, `LG:GuidTenant` | vazio | `LGAutenticacao/TokenUsuario` (obrigatórios; só por ambiente/secrets) |
| `LG:Ambiente` | 705 | `LGContextoAmbiente/Ambiente` |
| `LG:Empresas` | [1] | colaboradores: filtro `Empresas`; centros de custo: uma consulta por empresa (`Empresa/Codigo`); órgãos: uma consulta por empresa (`CodigoDaEmpresa`) e só vira setor o órgão habilitado para uma delas |
| `LG:TiposDeSituacoes` | [2, 3] | filtro de situações (colaboradores) |
| `LG:TimeoutSegundos` | 60 | limite de cada chamada |
| `LG:TamanhoPagina` | 0 | `QuantidadePorPagina` no filtro dos colaboradores (0 = não envia; a LG usa 50) |
| `LG:Tentativas` / `LG:EsperaInicialMs` | 3 / 1000 | retry com backoff (rede, timeout, HTTP 408/429/5xx sem SOAP Fault) |
| `LG:MaxPaginas` | 1000 | limite de segurança |
| `LG:FusoHorario` | America/Recife | "hoje" (DataVigente, vigência do centro de custo, horários diários) |
| `LG:SincronizacaoDiaria` | 05:00 | "HH:mm" (vazio = desligada) liga a sincronização diária dos colaboradores (Employee.Api) |
| `LG:CentrosDeCusto` | {} | colaboradores: centro de custo → Code da loja (ex.: `{ "900100": "CD" }`) |
| `LG:Lojas:Padrao` | `^(VAREJO\|ATACAREJO\|ATACADO)\b.*\bLOJA\s*(?<numero>\d{1,3})` | regex (sem diferenciar maiúsculas/acentos) que diz quais centros de custo são loja |
| `LG:Lojas:Mapeamento` | {} | centro de custo → Code da loja (precedência sobre o padrão; resolve conflitos; valor vazio = não é loja) |
| `LG:Lojas:SincronizacaoDiaria` | 03:00 | "HH:mm" (vazio = desligada) liga a sincronização diária das lojas e, em seguida, do catálogo de setores (Store.Api); agende antes da de colaboradores |
| `LG:Lojas:SincronizarAntesDosColaboradores` | false | Employee.Api: a sincronização de colaboradores chama antes `POST /internal/stores/sincronizar-lg` (X-Internal-Key) |
| `Seed:LojasRede` | ver abaixo | lojas da lista fixa da intranet na subida do Store.Api |

Sem URL, SOAPAction, `Usuario`, `Senha` ou `GuidTenant` da operação, a sincronização responde **"Integração LG não
configurada"** citando as chaves que faltam (o resto do serviço funciona). Como informar os valores:

```
# Desenvolvimento (user-secrets; repita no Store.Api com --project Services/Store.Api)
dotnet user-secrets set "LG:Url" "https://..." --project Services/Employee.Api
dotnet user-secrets set "LG:Operacoes:Colaboradores:SoapAction" "..." --project Services/Employee.Api
dotnet user-secrets set "LG:Usuario" "..." --project Services/Employee.Api
dotnet user-secrets set "LG:Senha" "..." --project Services/Employee.Api
dotnet user-secrets set "LG:GuidTenant" "..." --project Services/Employee.Api
dotnet user-secrets set "LG:Operacoes:CentrosDeCusto:SoapAction" "..." --project Services/Store.Api
dotnet user-secrets set "LG:Operacoes:CentrosDeCusto:Url" "https://..." --project Services/Store.Api
dotnet user-secrets set "LG:Operacoes:OrgaosResponsaveis:SoapAction" "..." --project Services/Store.Api
dotnet user-secrets set "LG:Operacoes:OrgaosResponsaveis:Url" "https://..." --project Services/Store.Api

# Servidor / contêiner: variáveis de ambiente (ou Docker secrets em /run/secrets/LG__Senha)
LG__Url=https://...   LG__Usuario=...   LG__Senha=...   LG__GuidTenant=...
LG__Operacoes__Colaboradores__SoapAction=...   LG__Operacoes__CentrosDeCusto__SoapAction=...
LG__Operacoes__OrgaosResponsaveis__SoapAction=...   LG__Operacoes__OrgaosResponsaveis__Url=https://...
LG__Lojas__SincronizacaoDiaria=03:00   LG__SincronizacaoDiaria=05:00
LG__Lojas__Mapeamento__305000=35   LG__CentrosDeCusto__900100=CD
```

Endpoints (gateway: `/api/escala5x2/stores/...` e `/api/escala5x2/employees/...`):

| Endpoint | Quem | O que faz |
| --- | --- | --- |
| `POST /api/stores/sincronizar-lg` | Admin, RH | lojas em segundo plano e, em seguida, o catálogo de setores (202; 409 em andamento; 503 não configurada) |
| `GET /api/stores/sincronizar-lg/status` | Admin, RH | andamento e última execução: criadas, atualizadas, inativadas, reativadas, ignorados (lista), conflitos, avisos e `setores` (resumo do catálogo) |
| `POST /internal/stores/sincronizar-lg` | interno | o mesmo, aguardando o fim (usado pelo Employee.Api) |
| `POST /api/stores/sincronizar-setores-lg` | Admin, RH | só o catálogo de setores (órgãos responsáveis) em todas as lojas ativas, em segundo plano (202; 409; 503) |
| `GET /api/stores/sincronizar-setores-lg/status` | Admin, RH | andamento e última execução: órgãos lidos, elegíveis, setores criados/adotados/renomeados/inativados/reativados (total e por loja), órgãos ignorados com o motivo |
| `GET /internal/stores` | interno | lojas com `lgCentroDeCustoCodigo` e `active` (casamento do colaborador) |
| `POST /internal/stores/{storeId}/sectors/garantir-lg` | interno | garante um setor por órgão responsável da LG (cria/adota sem duplicar); devolve código → `sectorId` |
| `POST /api/stores/{id}/sectors` | Gestor da loja, Admin | setor manual (recusa nome repetido pelo nome normalizado) |
| `PUT /api/employees/{id}`, `PUT /api/employees/{id}/sectors` | Admin, RH, Gestor da loja | mudar os setores marca "alterado manualmente" (a LG não troca mais) |
| `POST /api/employees/{id}/sectors/seguir-lg` | Admin, RH, Gestor da loja | voltar a seguir o setor da LG (limpa a marca e aplica o setor do órgão) |
| `POST /api/employees/sincronizar-lg` | Admin, RH | colaboradores em segundo plano (202; 409 em andamento; 503 não configurada) |
| `GET /api/employees/sincronizar-lg/status` | Admin, RH | andamento (página x de y) e última execução: criados, atualizados, ignorados, inativados, movidos, sem setor, setores criados/adotados (por loja), setor manual ≠ LG, pendências, erros, centros de custo ignorados |
| `GET /api/employees/{id}/lg` | quem vê a loja | último registro LG completo (LGPD aplicada no servidor) |
| `PUT /api/employees/{id}/ativo` | Admin, RH, Gestor da loja | inativar/reativar (nada é apagado) |
| `GET /internal/planning/pending?storeId=` | interno | férias/afastamento sem período (aviso na geração) |
| `POST /api/employees/{id}/ferias-lg` | Admin, RH | "Atualizar férias" de um colaborador (consulta já a LG) |
| `POST /internal/planning/ferias-lg` | interno | férias do setor antes da geração; espera até `TimeoutSegundos` e segue em segundo plano |

**Férias (ServicoDeFerias/ConsultarLista)** — uma chamada por empresa + matrícula, só dos colaboradores ativos com vínculo
LG, com paralelismo limitado (`LG:Ferias:Paralelismo`), retry do núcleo SOAP e falha isolada por matrícula (a rodada para
depois de `FalhasSeguidasParaInterromper` falhas seguidas). Cada `FeriasParcial` vira uma ausência **Férias** de origem
`LG` de `DataDaOcorrencia` a `DataFim`, **os dois inclusive**: o colaborador é escalável até a véspera do início, fica
bloqueado do início ao fim (a grade, o Excel/PDF e a consulta mostram "Férias"/FR) e volta no dia seguinte ao fim. Upsert
idempotente por colaborador + início (fim alterado na LG ajusta o registro; período em andamento/futuro que sumiu da LG é
removido; os já terminados ficam); registros manuais nunca são tocados e as férias da LG são somente leitura na tela.
Quando roda: logo depois de cada sincronização de colaboradores (botão e diária; o resumo mostra "férias atualizadas: N,
falhas: M"), no botão "Atualizar férias" do Visualizar e **antes de cada geração** (Scheduling.Api →
`/internal/planning/ferias-lg` com `Scheduling:FeriasLgTimeoutSegundos`, padrão 20 s, até ~25 s pelo limite do HttpClient;
0 desliga). Se a LG falhar ou demorar, a geração **não é bloqueada**: segue com os períodos gravados e o aviso "Férias da
LG não puderam ser atualizadas agora; usando a última sincronização de dd/mm hh:mm". Escalas aprovadas/publicadas não
mudam: célula de trabalho em dia de férias aparece com o alerta de conflito e a violação "indisponibilidade".

Na tela: "Sincronizar lojas com LG" (Admin e RH) em **Setores, turnos e demanda** e em **Colaboradores** (acima de
"Sincronizar com a LG"); "Sincronizar setores com LG" (Admin e RH) no quadro **Setores** de **Setores, turnos e demanda**.
As três sincronizações (e os resumos, com centros de custo ignorados, matrículas, conflitos) são só do superAdmin (Admin)
e do RH (política `lg-sync`); os painéis nem são montados para os demais papéis.

Regras das **lojas** (Store.Api):

- **Quais centros de custo são lojas**: o mapeado em `LG:Lojas:Mapeamento` ou cuja descrição casa com `LG:Lojas:Padrao`
  ("VAREJO LOJA 01 - CABO" → loja "01"). Os demais (PRESIDENCIA, RESIDÊNCIA, INDUSTRIA...) são ignorados e listados no
  status com o motivo. Centro de custo encerrado e sem loja na Escala não cria loja.
- **Mapeamento**: Code = número com 2 dígitos ("01": mantém o vínculo com o perfil da intranet "01 - CABO"); Name = descrição
  completa do centro de custo (sobrescrita a cada sincronização — o Admin pode editar, mas a LG prevalece); Format pela
  primeira palavra (VAREJO → Varejo, ATACAREJO → Atacarejo, ATACADO → Atacado; outra → mantém/Varejo);
  `LgCentroDeCustoCodigo` = código do centro de custo (único). Ativa = `Status` 0 e `DataFim` vazia ou de hoje em diante.
- **Upsert**: casa pelo `LgCentroDeCustoCodigo`; senão pelo código normalizado ("1" = "01"), adotando as lojas já
  cadastradas (lista da intranet ou manuais). Cidade, UF, norma 8h24, setores, turnos, demanda e operação não são tocados.
  O Code da loja nunca muda sozinho (se a LG renumerar, sai um aviso). **Nunca apaga**: centro de custo encerrado
  (`Status` ≠ 0 ou `DataFim` passada) inativa a loja (some dos seletores; o Admin vê marcada "inativa") e ela volta se a LG
  reabrir. Loja vinculada cujo centro de custo não veio na LG: só aviso. A loja DEMO nunca é tocada.
- **Conflito**: dois centros de custo com o mesmo número (ex.: varejo e atacarejo "LOJA 05") não criam loja duplicada:
  ficam no status até `LG:Lojas:Mapeamento` (ex.: `{ "305000": "35" }`). Se um deles já é a loja daquele código, ele
  continua e só o outro fica em conflito.
- **Seed da lista da intranet** (`Seed:LojasRede`): com a LG configurada para centros de custo o padrão passa a ser
  **desligado** — as lojas vêm da LG com o nome do centro de custo, e o seed criaria lojas só com o nome curto (e lojas
  que a LG não tem). Sem a LG configurada continua ligado como fallback. `Seed:LojasRede` true/false explícito prevalece;
  as lojas já semeadas são adotadas pela sincronização.
- **Auditoria**: `StoresLgSyncCompleted` (contagens, ignorados, conflitos) e `StoreLgChanged` (loja criada, atualizada,
  inativada ou reativada, com o resumo de antes).

Regras dos **colaboradores** (Employee.Api):

- **Loja**: 1) `CentroDeCusto.Codigo` = `LgCentroDeCustoCodigo` da loja; 2) `LG:CentrosDeCusto`; 3) número de
  `LOJA\s*(\d{1,3})` na descrição casado com o Code pelo código normalizado — só com loja ainda sem código LG (loja vinculada
  a outro centro de custo não é deste). Sem loja → ignorado (contagem e lista no status); se o centro de custo é de loja
  que ainda não existe, aviso "rode a sincronização de lojas". As lojas são lidas sem cache a cada sincronização.
  Upsert por (loja, matrícula); a mesma pessoa (empresa + matrícula) em outra loja muda de loja (setores anteriores saem,
  aviso e auditoria). Quem já veio da LG e não veio mais é **inativado** (nunca apagado) e volta a ser ativado se
  reaparecer. Se a LG não devolver ninguém, ninguém é inativado.
- **Setor = `OrgaoResponsavel`** (ver "Setores pelo órgão responsável" abaixo): o setor do órgão é o setor padrão de
  todo colaborador vindo da LG. Órgão nulo/sem código → "sem setor" (não entra na geração até o gestor definir).
- **Dados**: nome em Title Case pt-BR, cargo = `Cargo.Descricao`; contrato padrão para quem é novo (jornada controlada,
  1 domingo a cada 3; a carga semanal vem da regra `carga_horaria_semanal`); contrato existente é preservado.
- **Situação**: código 1 (ATIVIDADE NORMAL) = disponível. Qualquer outro = férias/afastamento: enquanto não houver
  ausência registrada cobrindo `DataInicioSituacaoAtual`, o colaborador fica **fora** de `/internal/planning/employees`
  (geração e dimensionamento) e a geração avisa "N colaboradores com férias/afastamento sem período definido foram
  excluídos da escala: ...". Registrado o período, vale só o período. Se a LG voltar a 1, a pendência some.
  Tipo sugerido: "FERIAS" → Férias; "LICENCA"/"MATERNIDADE" → Licença; demais → Afastamento.
- **LGPD**: CPF e data de nascimento completos só para Admin e RH; Gestor vê `***.***.***-88` e só dia/mês; demais não
  veem (e o Colaborador vê só "Afastado" no lugar da descrição da situação). Senha e CPF nunca vão para o log; os eventos
  de auditoria (`EmployeesLgSyncCompleted`, `EmployeeLgStatusChanged`) não têm CPF.

**Catálogo de setores** (Store.Api, `ConsultarListaPorDemanda` — `OrgaoResponsavelCompleto`, ~63 órgãos da empresa:
1 GERENCIA, 2 FRENTE DE LOJA, 3 AÇOUGUE, 4 FRIOS...). É a **fonte principal** dos setores das lojas: traz todos os
setores cadastrados na LG, não só os que algum colaborador tem hoje.

- **Consulta**: filtro `PaginaAtual`, `CodigoDaEmpresa` e `ConsultarListaValorDaInformacaoAdicional = 0`; uma consulta por
  empresa de `LG:Empresas`, páginas 1..`TotalDePaginas`; o mesmo órgão em mais de uma empresa vale uma vez (habilitações
  somadas). Tudo é lido antes de gravar.
- **O órgão é da empresa, não da loja**: cada loja **ativa** (a DEMO e as inativas ficam de fora) recebe um setor por órgão
  com `Status` 0 (nulo = 0) **habilitado** para a empresa (`ListaHabilitacaoPorEmpresas` contém uma empresa de
  `LG:Empresas` — a loja não guarda a empresa, então vale a lista configurada).
- **Mesmas regras do garantir-lg** (`LgSetoresRegras.Garantir`): casa pelo código; senão pelo nome normalizado, adotando o
  setor manual; senão cria o setor `Origem = Lg` com o nome = descrição como vem; renomeia o setor da LG quando a descrição
  muda; índice único + repetição em corrida. Segunda execução com o mesmo catálogo não muda nada.
- **Inativação** (`Sector.Active`, migration `SetoresAtivos`): setor **criado pela LG** cujo órgão ficou com `Status` ≠ 0,
  perdeu a habilitação ou **sumiu do catálogo** fica **inativo** — nunca é apagado — e volta a ativo se o órgão voltar.
  Setor **manual** (inclusive adotado) nunca é inativado pela LG. Setor inativo não aparece para novas escalas, nova demanda
  (o `POST /demands` recusa) nem nas caixas de setores de quem não o tem; escalas, demandas, operação e colaboradores que já
  o usam continuam funcionando (a fotografia de planejamento continua levando todos os setores).
- **Ignorados** (contados e listados com o motivo): sem código, `Status` ≠ 0, sem habilitação, sem descrição (órgão sem
  descrição não cria setor, mas o setor que já existe com esse código não é inativado).
- **Proteção**: catálogo sem nenhum órgão ativo e habilitado não altera nada (aviso). Falha na LG: nada gravado. Falha no
  meio da gravação: as lojas já processadas ficam (idempotente) e o status mostra a falha.
- **Quando roda**: logo depois de cada sincronização de lojas concluída (botão, diária `LG:Lojas:SincronizacaoDiaria` e a
  chamada interna do Employee.Api) e no botão próprio `POST /api/stores/sincronizar-setores-lg`. Sem
  `LG:Operacoes:OrgaosResponsaveis` configurada, as lojas sincronizam normalmente e o resumo avisa "Setores não
  sincronizados".
- **Auditoria**: `StoresSectorsLgSyncCompleted` (contagens e ignorados), `SectorLgChanged` (criado | adotado | renomeado |
  inativado | reativado) e um `StorePlanningChanged` por loja alterada.

**Setor padrão do colaborador pelo órgão responsável** (`OrgaoResponsavel` de cada `ContratoDeTrabalhoParcial`, ex.: 2
FRENTE DE LOJA, 13 TELEFONIA, 25 DEPOSITO) — continua como antes; o catálogo só garante que os setores já existam:

- **Fluxo**: depois de ler a LG inteira e as lojas, o Employee.Api junta os órgãos por loja e chama, **uma vez por loja e
  antes de gravar qualquer colaborador**, `POST /internal/stores/{storeId}/sectors/garantir-lg` (X-Internal-Key) com
  `{ orgaos: [{ codigo, descricao, status }], por }`. A resposta traz `setores` (código do órgão → `sectorId`), contagens
  (criados, adotados, renomeados), o detalhe por órgão e avisos.
- **Store.Api fora/recusando → a sincronização falha sem gravar colaboradores** ("nada foi alterado", tente de novo),
  coerente com a regra de não gravar parcialmente. Setores já garantidos em outras lojas antes da falha ficam (a
  operação é idempotente).
- **Dedupe no Store.Api** (`LgSetoresRegras`): para cada órgão, 1) casa por (loja, `LgOrgaoCodigo`); 2) senão pelo nome
  normalizado (sem acento, maiúsculas e espaços extras) — **adota o setor manual** com o mesmo nome (preenche o código
  LG, mantém o nome do gestor, não duplica); se o setor com esse nome já é de outro órgão, os dois órgãos usam o mesmo
  setor (aviso); 3) só então **cria** o setor com `Origem = Lg`. Código repetido no pedido vale uma vez; órgão sem
  descrição não cria setor (aviso); `Status` ≠ 0 só gera aviso. **Nunca apaga setor.** O garantir-lg dos colaboradores
  não mexe em ativo/inativo (decisão do catálogo) e cobre o órgão que o catálogo ainda não trouxe (ou o setor que o
  catálogo inativou: o colaborador continua nele).
- **Nome do setor criado**: a descrição da LG **como vem** (espaços normalizados, até 100 caracteres), em maiúsculas —
  mesma decisão das lojas (nome do centro de custo): sem inventar acento ou caixa ("DEPOSITO", não "Depósito"). Para
  outro nome, cadastre antes o setor manual com o nome desejado ("Depósito"): a LG o adota pelo nome normalizado.
- **Renomeação**: setor **criado pela LG** acompanha a descrição da LG (renomeado, com aviso no status e auditoria),
  salvo se o nome novo colidir com outro setor da loja (aviso). Setor **manual** (inclusive adotado) **nunca** é
  renomeado pela LG — só aviso quando a descrição muda. (Não há edição de nome de setor na tela; se vier a existir, o
  setor editado deve passar a ser tratado como manual.)
- **Corrida**: índice único filtrado (loja, `LgOrgaoCodigo`). Duas sincronizações simultâneas: a segunda viola o índice
  (PostgreSQL 23505), relê os setores e casa pelo código (até 3 tentativas).
- **Cadastro manual** (`POST /api/stores/{id}/sectors`): recusa nome repetido pelo nome normalizado, inclusive de setor
  da LG ("Já existe o setor DEPOSITO nesta loja (órgão 25 da LG)").
- **Auditoria**: `SectorLgChanged` (criado | adotado | renomeado) e um `StorePlanningChanged` com o resumo por loja.
- **Colaborador** (`LgRegras.AplicarSetorLg`): guarda sempre o órgão atual (`LgOrgaoCodigo`, `LgOrgaoDescricao`,
  `LgSetorId`). Enquanto os setores **não** forem alterados manualmente, setor = o do órgão (troca se o órgão mudar na
  LG; órgão vazio mantém o que tem). Alterar os setores em `PUT /api/employees/{id}` ou `PUT /{id}/sectors` (e o
  cadastro manual) marca `SetoresManuais` (+ `SetoresAlteradosPor/Em`): a LG não sobrescreve mais, e a tela mostra
  "Setor da LG: X" e o aviso de divergência. `POST /api/employees/{id}/sectors/seguir-lg` ("Voltar a seguir o setor da
  LG") limpa a marca e aplica o setor do órgão atual. Mudança de loja (centro de custo) limpa a marca (os setores eram da
  loja anterior).
- **Migration `SetoresLg` (Employee.Api)**: colaboradores existentes **com setor** ficam marcados como manuais (preserva
  o comportamento anterior, em que a LG não mexia em quem tinha setor); na primeira sincronização, a marca sem autor/data
  cai sozinha quando o setor já é exatamente o do órgão. Os demais aparecem com a divergência para o gestor decidir.
- **Status** da sincronização: setores criados/adotados (por loja nos detalhes), "Setor manual ≠ LG" e avisos.
- **Tela**: em **Setores, turnos e demanda** a lista de setores (LG + manuais, ~63 por loja) tem busca pelo nome/código,
  selo **LG** (código/descrição do órgão no tooltip) ou "manual", selo "inativo" e "Mostrar inativos"; o Admin tem
  "Sincronizar setores com LG" com o resumo. Em **Colaboradores**, a edição lista os setores **ativos** da loja (com busca)
  e os que o colaborador já tem, mesmo inativos (marcados), mostra "Setor da LG" e o botão "Voltar a seguir o setor da
  LG"; a coluna Setores tem um ícone quando foi alterado manualmente e "LG: X" quando diverge. Na **visão do mês**,
  "Todos os setores" mostra só os setores com colaborador ativo ou escala no mês ("Mostrar setores vazios" mostra os
  demais ativos); na consulta de escalas, só os com escala publicada. Seletores de setor ordenados pelo nome, com busca
  nos formulários, sem os inativos (exceto quando em uso).

Testes (sem chamar a LG real; `HttpMessageHandler` falso): `Tests/Employee.Tests` (colaboradores, setor padrão e edição
manual, cliente do garantir-lg, Store.Api fora sem gravar, envelope v2 inalterado) e `Tests/Store.Tests` (centros de
custo/lojas; setores: dedupe por código e nome, adoção, renomeação, corrida/índice, setor manual duplicado recusado;
catálogo de órgãos: parse, envelope v1/v11 com `CodigoDaEmpresa`, paginação por empresa, todas as lojas sem duplicar,
inativação/reativação por `Status` e por sumiço, habilitação, setor manual nunca inativado, DEMO intocada).

## Migrations

```
dotnet tool install -g dotnet-ef
dotnet ef migrations add NomeDaMudanca --project Services/<Serviço>.Api --output-dir Migrations
```
