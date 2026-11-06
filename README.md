# ComprasGOV

Automação em **.NET 8** que busca as licitações **com proposta em aberto** de
cada UASG (lida de uma planilha Excel), aplica a regra de validade do objeto,
**baixa todos os arquivos** publicados no **PNCP** para uma pasta de destino e
envia por e-mail, mantendo um histórico local em JSON para nunca processar
duas vezes a mesma licitação. Roda 24/7, em ciclos com pausa de 1h.

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
  publicar os arquivos no PNCP depois de divulgar a compra).
- Perfil de navegador persistente + medidas anti-detecção para reduzir os
  desafios de **hCaptcha** (com aviso na janela e retry automático).

## Requisitos

- Windows com **.NET 8 SDK** (ou runtime + SDK para compilar);
- Navegadores do Playwright (instalados pelo próprio projeto);
- Acesso à planilha de UASGs e à pasta de destino dos editais
  (no uso real, pastas de rede corporativas).

## Instalação

```powershell
git clone https://github.com/lucoborges/ComprasGOV.git
cd ComprasGOV

# compila
dotnet build ComprasGOV/ComprasGOV.csproj

# instala o Chromium usado pelo Playwright (uma única vez)
dotnet run --project ComprasGOV/ComprasGOV.csproj --no-build -- --instalar-navegadores
```

## Configuração

Os caminhos da **planilha** e da **pasta de saída** não vêm fixos no código.
Eles são resolvidos nesta ordem:

1. argumentos de linha de comando (`--planilha`, `--pasta-saida`);
2. variáveis de ambiente `COMPRASGOV_PLANILHA` e `COMPRASGOV_PASTA_SAIDA`;
3. valores locais: `planilha_uasgs.xlsx` no diretório atual e pasta `editais/`.

Para o uso diário sem digitar os caminhos toda vez, defina as variáveis uma
única vez (opção A) ou use o `Properties/launchSettings.json` (opção B):

**Opção A — variáveis de ambiente (usuário/máquina):**

```powershell
setx COMPRASGOV_PLANILHA "\\SEU_SERVIDOR\compartilhamento\Planilha_Orgaos_ES_UASG.xlsx"
setx COMPRASGOV_PASTA_SAIDA "\\SEU_SERVIDOR\compartilhamento\LICITACOES PARA IMPRIMIR"
```

**Opção B — `Properties/launchSettings.json` (usado pelo `dotnet run` e pelo F5
do Visual Studio). Este arquivo é ignorado pelo Git justamente porque contém
caminhos internos:**

```json
{
  "profiles": {
    "ComprasGOV": {
      "commandName": "Project",
      "environmentVariables": {
        "COMPRASGOV_PLANILHA": "\\\\SEU_SERVIDOR\\compartilhamento\\Planilha_Orgaos_ES_UASG.xlsx",
        "COMPRASGOV_PASTA_SAIDA": "\\\\SEU_SERVIDOR\\compartilhamento\\LICITACOES PARA IMPRIMIR"
      }
    }
  }
}
```

## Uso

```powershell
# primeiro cliente da planilha (padrão)
dotnet run --project ComprasGOV/ComprasGOV.csproj --no-build

# todos os clientes da planilha
dotnet run --project ComprasGOV/ComprasGOV.csproj --no-build -- --todos

# um órgão específico, sem planilha
dotnet run --project ComprasGOV/ComprasGOV.csproj --no-build -- --uasg 90014
```

| Opção | Descrição |
|---|---|
| `--uasg <codigo>` | Processa apenas o código UASG informado (ex.: `90014`). |
| `--todos` | Processa todos os clientes da planilha (padrão: só o primeiro). |
| `--headless` | Executa sem janela do navegador (padrão: janela visível). **Não recomendado**: em headless o hCaptcha sempre exige o desafio de imagens e a pesquisa não avança. |
| `--fonte <api\|portal>` | Origem das licitações (padrão: `api`, sem navegador/captcha). |
| `--comparar` | Compara portal x API por UASG e salva um relatório (não baixa, não grava histórico). |
| `--uma-vez` | Executa um único ciclo e encerra (padrão: modo contínuo 24/7). |
| `--intervalo <min>` | Pausa entre ciclos no modo contínuo (padrão: 60 minutos). |
| `--max <n>` | Limita a quantidade de licitações processadas por ciclo. |
| `--planilha <caminho>` | Caminho da planilha de órgãos (xlsx). |
| `--pasta-saida <caminho>` | Pasta onde os editais serão salvos. |
| `--historico <arquivo>` | Arquivo JSON de licitações já processadas. |
| `--instalar-navegadores` | Instala os navegadores do Playwright e encerra. |
| `--debug` | Log detalhado de rede/console do portal (REQ, HTTP, corpos). |
| `--help` | Exibe a ajuda. |
| `--ignorar-objeto` | **Somente teste:** ignora a regra de objeto e baixa tudo. |
| `--testar-pncp <url>` | **Somente teste:** abre essa URL do PNCP e baixa o edital (sem a busca). |

## Como funciona (fonte API, padrão)

1. **Planilha** → lê a coluna `Código UASG`.
2. **CNPJ do órgão** → consulta a UASG nos Dados Abertos do Compras.gov.br
   (guardado em cache em `uasg_cnpj.json`, ao lado do histórico).
3. **Propostas em aberto** → `GET /v1/contratacoes/proposta` do PNCP filtrado
   pelo CNPJ e pela UASG; ignora compras suspensas/revogadas/anuladas.
4. **Regra do objeto** → igual à do portal.
5. **Arquivos** → lista os arquivos ativos da compra no PNCP e baixa todos.
6. **Histórico e e-mail** → iguais aos do portal.

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
8. **Histórico** → grava o status em
   `Documents\ComprasGOV\licitacoes_processadas.json`
   (ou o caminho indicado por `--historico`).

## Estrutura do projeto

```
ComprasGOV.slnx
ComprasGOV/
├── Program.cs                 # CLI, perfil persistente do Chromium, modo --testar-pncp
├── ExecutadorFluxo.cs         # orquestração do fluxo (API ou portal) e e-mails
├── ComparadorFontes.cs        # modo --comparar (portal x API)
├── Config/Configuracao.cs     # argumentos, variáveis de ambiente e padrões
├── Automation/
│   ├── ComprasNetAutomacao.cs # busca no Compras.gov.br, cards, hCaptcha
│   ├── PncpAutomacao.cs       # aba "Arquivos" e localização do edital
│   └── Seletores.cs           # seletores CSS/Playwright centralizados
├── Models/                    # ClienteUasg, LicitacaoEncontrada, LicitacaoHistorico
└── Services/
    ├── PncpApiService.cs      # APIs oficiais: UASG->CNPJ, propostas abertas, arquivos
    ├── PlanilhaService.cs     # leitura da planilha (ClosedXML)
    ├── ClassificadorObjeto.cs # regra VÁLIDO/INVÁLIDO/indefinido
    ├── DownloadEditalService.cs
    ├── HistoricoService.cs    # persistência do histórico JSON
    └── Log.cs / TextoUtil.cs
```

## Notas práticas

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
