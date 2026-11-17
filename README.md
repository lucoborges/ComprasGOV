# ComprasGOV

Automação em **.NET 8** que busca as licitações **com proposta em aberto**,
aplica a regra de validade do objeto, **baixa todos os arquivos** publicados no
**PNCP** para uma pasta de destino e envia por e-mail, mantendo um histórico em
JSON para nunca processar duas vezes a mesma licitação. Roda 24/7, em ciclos com
pausa de 1h. Cada ciclo executa duas tarefas, nesta ordem:

1. **Tarefa específica** — as UASGs da planilha Excel de clientes;
2. **Tarefa geral** — todas as licitações do PNCP com os filtros
   **UF = ES** e **Fontes/Plataformas = Compras.gov.br e PORTAL GOV SERVICOS
   DIGITAIS LTDA / PORTAL GOV** (os mesmos filtros da tela *Editais* do site do PNCP).

As duas usam as mesmas regras (só licitações **divulgadas** e com **prazo de
proposta em aberto**; suspensas, revogadas, anuladas e canceladas são
ignoradas), a mesma regra de válido/inválido do objeto, o mesmo download e o
mesmo e-mail. O que uma tarefa já processou a outra não repete.

## Fontes de dados

| Fonte | Como | Captcha | Padrão |
|---|---|---|---|
| **API oficial** (`--fonte api`) | [Dados Abertos do Compras.gov.br](https://dadosabertos.compras.gov.br) (UASG → CNPJ) + [API de consulta do PNCP](https://pncp.gov.br/api/consulta) (propostas em aberto e arquivos) | não | **sim** |
| **Portal** (`--fonte portal`) | navegador (Playwright) no [Compras.gov.br](https://cnetmobile.estaleiro.serpro.gov.br/comprasnet-web/public/compras) + PNCP | sim (hCaptcha) | não |

A API não usa navegador: é mais rápida, roda em segundo plano e não depende de
alguém resolver captcha. Ela também traz a **situação oficial** da compra
(divulgada / suspensa / revogada / anulada); só as "Divulgadas" são processadas.
Se a API falhar para uma UASG, nada é gravado no histórico e ela é tentada de
novo no próximo ciclo, então nenhuma licitação se perde.

`--comparar` executa as duas fontes lado a lado (sem baixar nada) e salva um
relatório `comparacao_<data>.md` ao lado do histórico.

## Busca geral (UF + fontes/plataformas)

Depois das UASGs da planilha, o ciclo busca as licitações do **ES** publicadas
no PNCP pelas plataformas **Compras.gov.br** e **PORTAL GOV SERVICOS DIGITAIS
LTDA / PORTAL GOV** — o equivalente aos filtros *Status: A Receber/Recebendo
Proposta*, *UFs* e *Fontes/Plataformas* da tela
[Editais](https://pncp.gov.br/app/editais) do PNCP:

- **Fonte principal:** API oficial de consulta do PNCP
  (`/v1/contratacoes/proposta?uf=ES&idUsuario=<plataforma>`), com as que estão
  recebendo propostas;
- **Complemento:** uma consulta por ciclo à busca do próprio site, que também
  traz as que ainda vão abrir o prazo (*a receber*). Ela é instável (às vezes o
  servidor derruba a conexão); quando falha, essas licitações entram no ciclo em
  que o prazo abrir, pela API oficial.

UF e plataformas ficam no arquivo de configuração (`BuscaGeral.Ufs` e
`BuscaGeral.Plataformas`; o código da plataforma é o do filtro do site, ex.:
`3` = Compras.gov.br, `350` = PORTAL GOV). No e-mail, a linha "UASG" das
licitações da busca geral traz também o nome da unidade compradora, já que o
órgão não vem da planilha de clientes.

> **Primeiro ciclo:** a busca geral avalia de uma vez todas as licitações em
> aberto no ES (≈ 120 em nov/2026) e envia por e-mail as válidas (2 a 4 por
> mensagem); dali em diante, só as novas. Para espalhar esse volume por vários
> ciclos, use `--max <n>`.

## Funcionalidades

- Lê os códigos de **UASG** de uma planilha Excel (coluna `Código UASG`).
- Pesquisa as compras do órgão e considera apenas os cards com
  **`Etapa: Proposta`** e vencimento (ignora `SUSPENSA`/`REVOGADA`).
- Valida o **objeto** da licitação:
  - **Válido** → registro de preço/aquisição/fornecimento/compra de produtos;
  - **Inválido** → serviço, locação, produtos químicos ou fornecimento de medicamentos (não baixa);
  - sem termo reconhecido → `indefinido` (não baixa, fica registrado para revisão).
- Abre a página da compra, acessa o **PNCP**, localiza o arquivo com tipo
  **"Edital"** na aba *Arquivos* e baixa o documento (`.pdf`, `.zip`, etc.)
  preservando o nome original.
- **Histórico JSON** local: cada licitação recebe um status
  (`baixado`, `rejeitado`, `indefinido`, `sem-edital`, `erro`) e só
  `erro` e `sem-edital` são reprocessados no ciclo seguinte (o órgão costuma
  publicar os arquivos no PNCP depois de divulgar a compra). A chave de cada
  licitação é o **número de controle do PNCP** (ex.: `00394452000103-1-023561/2026`).
- Perfil de navegador persistente + medidas anti-detecção para reduzir os
  desafios de **hCaptcha** (com aviso na janela e retry automático).

## Requisitos

- Windows com **.NET 8** (SDK 8 ou mais novo para compilar; runtime 8 para rodar);
- Navegadores do Playwright, só para a fonte `portal` (instalados pelo próprio programa);
- Acesso à planilha de UASGs, à pasta de destino dos editais e à pasta do banco
  (histórico), normalmente pastas de rede.

## Como rodar (desenvolvimento)

```powershell
git clone https://github.com/lucoborges/ComprasGOV.git
cd ComprasGOV
dotnet build ComprasGOV/ComprasGOV.csproj
dotnet test  ComprasGOV.Tests/ComprasGOV.Tests.csproj
```

> **Não use `dotnet run` nem F5** para testar: eles aplicam o
> `Properties/launchSettings.json` local, que pode apontar para as pastas de rede
> e a senha SMTP reais (e-mails de verdade, histórico de produção). Rode o
> executável compilado com histórico e pastas de teste:

```powershell
$t = "$env:TEMP\comprasgov-teste"
.\ComprasGOV\bin\Debug\net8.0\ComprasGOV.exe --uma-vez --sem-email `
    --historico "$t\hist.json" --pasta-saida "$t\editais" --pasta-logs "$t\logs" --uasg 90014
```

`--historico` dispensa a pasta do banco; com `--sem-email` nada é enviado (os
downloads ficam pendentes de e-mail só no histórico de teste).

Para a fonte `portal`, instale o Chromium uma vez:
`.\ComprasGOV\bin\Debug\net8.0\ComprasGOV.exe --instalar-navegadores`.

## Configuração

Cada valor vem da primeira origem que o informar:

1. **argumento** de linha de comando (ex.: `--intervalo 30`);
2. **variável de ambiente** `COMPRASGOV_*`;
3. **arquivo de configuração** `appsettings.json`;
4. **padrão do código** (só valores públicos: URLs das APIs, SMTP da Locaweb, UF ES etc.).

O arquivo é o indicado por `--config`, senão por `COMPRASGOV_CONFIG`, senão o
`appsettings.json` ao lado do executável ou no diretório atual (se existir).
O modelo comentado, com todos os campos, é
[`deploy/appsettings.exemplo.json`](deploy/appsettings.exemplo.json). O arquivo
real (e-mails e caminhos internos) **não vai para o repositório** (`.gitignore`).

| Seção / campo | Variável de ambiente | Padrão |
|---|---|---|
| `Email.Destino` / `Email.Remetente` | `COMPRASGOV_EMAIL_DESTINO` / `COMPRASGOV_EMAIL_REMETENTE` | — (obrigatórios para enviar) |
| `Email.SmtpHost` / `Email.SmtpPorta` | `COMPRASGOV_SMTP_HOST` / `COMPRASGOV_SMTP_PORTA` | `smtp.email-ssl.com.br` / `587` |
| `Email.LimiteAnexosMb` | — | `20` |
| *(senha SMTP)* | `COMPRASGOV_SMTP_SENHA` | **só por variável**; um campo de senha no arquivo é ignorado |
| `Pastas.Banco` | `COMPRASGOV_PASTA_BANCO` | — (obrigatório, ou `--historico`) |
| `Pastas.Planilha` / `Pastas.Saida` | `COMPRASGOV_PLANILHA` / `COMPRASGOV_PASTA_SAIDA` | `planilha_uasgs.xlsx` / `editais` |
| `Execucao.IntervaloMinutos` | `COMPRASGOV_INTERVALO_MINUTOS` | `60` |
| `Execucao.CicloMaximoMinutos` | — | `180` (acima disso a saúde acusa ciclo travado) |
| `BuscaGeral.Ufs` / `BuscaGeral.Plataformas` | `COMPRASGOV_UFS` (ex.: `ES,MG`) | `ES` / `3` e `350` |
| `Apis.*` (Uasg, Propostas, Arquivos, Busca, PortalPesquisa) | — | URLs oficiais |
| `Log.Pasta` | `COMPRASGOV_PASTA_LOGS` | `logs` ao lado do executável |
| `Log.TamanhoMaximoMb` / `RetencaoDias` / `TamanhoTotalMaximoMb` | — | `10` / `30` / `200` |
| `Log.Console` | `COMPRASGOV_LOG_CONSOLE` | `true` |
| `Saude.ArquivoEstado` | `COMPRASGOV_ARQUIVO_ESTADO` | `estado.json` na pasta de logs |

Sem a pasta do banco (nem `--historico`) o programa **não roda**: com histórico
vazio tudo seria reprocessado e os e-mails, repetidos.
`ComprasGOV.exe --validar-config [--config arquivo]` confere a configuração e
devolve 0 (válida) ou 1, sem exibir os valores.

## Logs e saúde

- **Log em arquivo** UTF-8 em `logs\comprasgov-AAAAMMDD.log`: um arquivo por dia;
  ao passar do tamanho máximo abre `-2`, `-3`…; arquivos além da retenção (dias)
  ou do tamanho total são apagados, os mais antigos primeiro. A senha SMTP nunca
  é gravada (é trocada por `***` se aparecer em alguma mensagem).
- **Heartbeat** em `logs\estado.json`: versão, PID, início do processo, situação
  (`iniciando`, `em-ciclo`, `inativo`, `adiado`, `encerrado`), ciclo atual,
  resultado do último ciclo, histórico carregado e próximo ciclo previsto.
  Atualizado a cada etapa e a cada minuto.
- **Verificação de saúde**: `ComprasGOV.exe --saude [arquivo-de-estado]` (em
  produção, `C:\Services\ComprasGOV\saude.cmd`) devolve **0** se o robô está vivo
  e em dia, ou **1** se está parado, travado, encerrado ou com o ciclo adiado
  (banco inacessível). Ciclo "com falhas" só gera aviso.

## Uso

```powershell
# padrão: todos os clientes da planilha + busca geral, 24/7
ComprasGOV.exe

# um órgão específico, sem planilha (e sem a busca geral)
ComprasGOV.exe --uasg 90014

# só a busca geral (ES + plataformas), um ciclo, sem enviar e-mail
ComprasGOV.exe --so-busca-geral --uma-vez --sem-email
```

| Opção | Descrição |
|---|---|
| `--config <arquivo>` | Arquivo de configuração. |
| `--validar-config` | Confere a configuração e encerra (0 válida, 1 inválida). |
| `--saude [arquivo]` | Lê o heartbeat e encerra (0 em dia, 1 problema). |
| `--uasg <codigo>` | Processa apenas o código UASG informado (ex.: `90014`), sem a busca geral. |
| `--sem-busca-geral` | Executa só a tarefa de UASGs da planilha. |
| `--so-busca-geral` | Executa só a busca geral (UF + fontes/plataformas). |
| `--todos` | Sem efeito: o padrão já processa todos os clientes da planilha. |
| `--headless` | Executa sem janela do navegador (padrão: janela visível). **Não recomendado**: em headless o hCaptcha sempre exige o desafio de imagens e a pesquisa não avança. |
| `--fonte <api\|portal>` | Origem das licitações (padrão: `api`, sem navegador/captcha). |
| `--comparar` | Compara portal x API por UASG e salva um relatório (não baixa, não grava histórico). |
| `--uma-vez` | Executa um único ciclo e encerra (padrão: modo contínuo 24/7). |
| `--intervalo <min>` | Pausa entre ciclos no modo contínuo (padrão: 60 minutos). |
| `--max <n>` | Limita a quantidade de licitações processadas por ciclo (somando as duas tarefas). |
| `--planilha <caminho>` | Caminho da planilha de órgãos (xlsx). |
| `--pasta-saida <caminho>` | Pasta onde os editais serão salvos. |
| `--historico <arquivo>` | Arquivo JSON de licitações já processadas. |
| `--pasta-logs <caminho>` | Pasta dos logs e do `estado.json`. |
| `--sem-email` | Não envia os e-mails (os downloads ficam pendentes de envio). |
| `--email-destino <email>` | Altera o destinatário dos e-mails. |
| `--instalar-navegadores` | Instala os navegadores do Playwright e encerra. |
| `--debug` | Log detalhado de rede/console do portal (REQ, HTTP, corpos). |
| `--help` | Exibe a ajuda e a configuração em uso. |
| `--ignorar-objeto` | **Somente teste:** ignora a regra de objeto e baixa tudo. |
| `--testar-pncp <url>` | **Somente teste:** abre essa URL do PNCP e baixa o edital (sem a busca). |

## Produção e deploy

O robô roda no servidor Windows como a tarefa agendada **`ComprasGOV`** (gatilho
de boot, conta em modo S4U: sobe sem sessão aberta e sem senha gravada), que
chama `iniciar.cmd`. Estrutura em `C:\Services\ComprasGOV`:

```
app\            versão em execução (versao.txt = commit publicado)
app-anterior\   versão anterior, usada para desfazer
config\         appsettings.json de produção (criado à mão; o deploy não toca)
logs\           comprasgov-AAAAMMDD.log, estado.json e console.log
iniciar.cmd     lançador (define COMPRASGOV_CONFIG, COMPRASGOV_PASTA_LOGS etc.)
saude.cmd       verificação de saúde (0/1)
```

A senha SMTP, a planilha e a pasta de saída ficam em variáveis de ambiente do
usuário da tarefa (`COMPRASGOV_SMTP_SENHA`, `COMPRASGOV_PLANILHA`,
`COMPRASGOV_PASTA_SAIDA`).

**Deploy automático** ([`.github/workflows/deploy.yml`](.github/workflows/deploy.yml)):

1. Todo push e pull request compila e roda os testes numa máquina Windows do GitHub.
2. Só na `main` (push que não seja apenas `.md`, ou execução manual), o job
   `deploy` roda no servidor (runner self-hosted) e chama
   [`deploy/deploy.ps1`](deploy/deploy.ps1), que:
   - exige `config\appsettings.json` e o valida com a versão nova
     (`--validar-config`) **antes** de mexer no robô;
   - publica em `app-novo\`, pede a parada limpa (`parar.flag`; finaliza à força
     após 90 s), troca `app` → `app-anterior` e `app-novo` → `app`;
   - inicia a tarefa e confere o **heartbeat**: versão nova, processo vivo e
     histórico carregado em até 60 s (ciclo adiado = reprovado);
   - se reprovar, restaura `app-anterior` e termina com erro.

> **Atenção:** cada deploy reinicia o robô, e o reinício roda um ciclo real
> (pode enviar e-mail de licitação nova). Push só de `.md` não faz deploy.

Para testar o desfazer: *Actions → Build e Deploy → Run workflow* com
`simular_falha` marcado. Para um deploy manual no servidor (como administrador):

```powershell
.\deploy\deploy.ps1 -Versao manual
```

Verificar depois: `C:\Services\ComprasGOV\saude.cmd` (código de saída 0) e o log
do dia em `C:\Services\ComprasGOV\logs`.

## Como funciona (fonte API, padrão)

1. **Planilha** → lê a coluna `Código UASG`.
2. **CNPJ do órgão** → consulta a UASG nos Dados Abertos do Compras.gov.br
   (guardado em cache em `uasg_cnpj.json`, ao lado do histórico).
3. **Propostas em aberto** → `GET /v1/contratacoes/proposta` do PNCP filtrado
   pelo CNPJ e pela UASG; ignora compras suspensas/revogadas/anuladas.
4. **Regra do objeto** → igual à do portal.
5. **Arquivos** → lista os arquivos ativos da compra no PNCP e baixa todos.
6. **Busca geral** → lista as licitações do ES nas plataformas configuradas
   (API oficial + busca do site) e aplica os passos 3 a 5 ao que ainda não foi
   processado (inclusive pela tarefa de UASGs no mesmo ciclo).
7. **Histórico e e-mail** → iguais aos do portal; os e-mails saem uma vez, no
   fim do ciclo, com os downloads das duas tarefas.

## Como funciona (fonte portal)

1. **Planilha** → lê a coluna `Código UASG` e monta a lista de clientes.
2. **Pesquisa** → preenche o campo *Unidade compradora* no Compras.gov.br e
   pesquisa; avança nas páginas de resultados.
3. **Filtro de cards** → mantém apenas licitações com `Etapa: Proposta` e
   prazo; descarta suspenso/revogado.
4. **Regra do objeto** → classifica o texto do objeto como
   `VÁLIDO` / `INVÁLIDO` / `indefinido` e decide se segue adiante.
5. **Acompanhar compra** → abre a página de acompanhamento da compra.
6. **PNCP** → clica em *Documentos e demais informações... no PNCP*, aguarda a
   barra de abas, abre **Arquivos**, localiza a linha com tipo **Edital**.
7. **Download** → baixa o arquivo como
   `<número> - <nome original no PNCP><extensão original>` para a pasta de saída.
8. **Histórico** → grava o status em `licitacoes_processadas.json` na pasta do
   banco (`Pastas.Banco`), ou no arquivo indicado por `--historico`.

## Estrutura do projeto

```
ComprasGOV.slnx
deploy/                        # deploy.ps1, iniciar.cmd, saude.cmd, appsettings.exemplo.json
ComprasGOV.Tests/              # testes (xUnit): configuração, saúde, log, histórico, objeto, e-mail
ComprasGOV/
├── Program.cs                 # CLI, ciclos, heartbeat, perfil do Chromium, modo --testar-pncp
├── ExecutadorFluxo.cs         # tarefas do ciclo (UASGs via API/portal, busca geral) e e-mails
├── ComparadorFontes.cs        # modo --comparar (portal x API)
├── Config/                    # Configuracao (precedência) e ArquivoConfiguracao (appsettings.json)
├── Automation/
│   ├── ComprasNetAutomacao.cs # busca no Compras.gov.br, cards, hCaptcha
│   ├── PncpAutomacao.cs       # aba "Arquivos" e localização do edital
│   └── Seletores.cs           # seletores CSS/Playwright centralizados
├── Models/                    # ClienteUasg, LicitacaoEncontrada, LicitacaoHistorico
└── Services/
    ├── PncpApiService.cs      # APIs: UASG->CNPJ, propostas abertas (UASG ou UF+plataforma), busca do site, arquivos
    ├── PlanilhaService.cs     # leitura da planilha (ClosedXML)
    ├── ClassificadorObjeto.cs # regra VÁLIDO/INVÁLIDO/indefinido
    ├── DownloadEditalService.cs
    ├── HistoricoService.cs    # persistência do histórico JSON (trava de instância única)
    ├── EmailService.cs        # lotes, limite de tamanho, envio SMTP
    ├── EstadoService.cs       # heartbeat (estado.json) e verificação de saúde
    └── Log.cs / TextoUtil.cs  # log com rotação; utilitários de texto
```

## Notas práticas

- **Banco de dados no servidor**: o histórico, o cache `uasg_cnpj.json` e os
  relatórios de comparação ficam na pasta do banco (`Pastas.Banco` ou a
  variável `COMPRASGOV_PASTA_BANCO`), uma pasta de rede. Se a pasta estiver
  inacessível ou o histórico ilegível, o ciclo **não roda** e é tentado de novo
  no próximo: com histórico vazio, tudo seria reprocessado e os e-mails já
  enviados seriam repetidos.
- **Chave do histórico**: a chave antiga `UASG-número/ano` colidia quando o
  mesmo órgão usa o mesmo número em modalidades diferentes (ex.: Pregão
  90015/2026 e Concorrência 90015/2026 da mesma UASG) e a segunda licitação era
  descartada como "já processada". Agora a chave é o número de controle do
  PNCP. Na primeira carga, os registros antigos são migrados automaticamente
  (a partir do link do PNCP gravado em cada um) e o arquivo original é copiado
  para `licitacoes_processadas.json.antes-migracao-<data>`. Registros antigos
  sem link (rejeitados/indefinidos do fluxo do portal) são reavaliados uma vez,
  sem download nem e-mail. O arquivo passa a ter um formato novo
  (`{"Versao":2,"Itens":[...]}`): uma versão antiga do programa não consegue
  lê-lo e adia o ciclo (em vez de reprocessar e reenviar tudo) — atualize
  **todas** as máquinas que rodam o sistema. `--comparar` e `--testar-pncp`
  só leem o histórico, sem regravá-lo.

- **hCaptcha** (só na fonte `portal`): o portal exige desafio de vez em quando. Quando aparece, um
  aviso é impresso no console e a janela do navegador fica visível — resolva o
  desafio e a execução continua sozinha; o perfil persistente
  (`%LOCALAPPDATA%\ComprasGOV\perfil`) reduz a frequência nos próximos
  usos. Se os desafios ficarem constantes, apague essa pasta para "zerar" o perfil.
- **Objeto `indefinido`**: nada é baixado; a licitação fica no histórico para
  revisão manual da regra em `Services/ClassificadorObjeto.cs`.
- **PNCP sem tipo "Edital"**: se a compra não publicar arquivo com esse tipo,
  a licitação é registrada como `sem-edital`, a execução segue normalmente e
  ela é verificada de novo nos próximos ciclos.
- **Diagnóstico**: em falha do PNCP, screenshot e HTML da página são salvos em
  `%TEMP%\ComprasGOV` para inspeção.
