# PokeIdle Deck para Linux / Docker

O deck roda no Linux e é controlado pelo navegador. Não usa Windows, Wine, máquina virtual, KVM nem um iframe do jogo. Cada conta tem seu próprio processo e perfil persistente do Chromium completo; as quatro janelas são incorporadas ao deck via X11. O navegador do usuário recebe a interface por noVNC. Fechar a aba da WebUI não interrompe as contas.

As barras, cores, grade 2×2, colunas, foco e janelas de configuração reproduzem o aplicativo original. Os controles WinForms foram portados para HTML/CSS; a fonte de fallback no Linux e os diálogos do próprio Chromium podem ter diferenças visuais em relação ao Windows. Os sites do jogo e das lives são os originais.

## Iniciar

Requer Docker Engine e Docker Compose em um host Linux. A imagem é validada em `linux/amd64`. Não exige modo privilegiado nem acesso ao Docker socket. Reserve memória para as quatro instâncias do jogo; o consumo depende das contas, extensões e páginas abertas.

Na raiz do repositório:

```sh
cp .env.example .env
# Edite .env e defina WEB_PASSWORD com uma senha não vazia.
docker compose up -d --build
```

Abra **http://localhost:8080**. O usuário padrão é `pokeidle`; a senha é a definida no `.env`. Entre nas contas do jogo individualmente. As automações de recompra, PvP e resgate começam desligadas, como no aplicativo original.

O Compose publica a porta apenas em `127.0.0.1`. Para acessar de outro computador, use um túnel SSH ou um proxy com HTTPS e WebSocket. Também é possível configurar `BIND_ADDRESS` no `.env` para a interface da sua rede. O áudio requer HTTPS ou acesso por localhost, por exigência do navegador.

O menu **☰**, no canto superior direito da WebUI, oferece tela cheia, áudio, envio/download de arquivos, área de transferência e reconexão. O botão de som do cabeçalho continua controlando cada conta; **Ligar áudio** autoriza a reprodução do áudio do container no navegador.

O healthcheck fica pronto quando a interface e as janelas das contas abrem; o carregamento do site continua em segundo plano. Uma resposta lenta do jogo não deve manter o container em “Deploying”. Os logs `[startup]` mostram as etapas de inicialização. Ao recriar o container, os links de bloqueio antigos do Chromium são removidos após adquirir o bloqueio exclusivo do volume; cookies, extensões e dados das contas são preservados.

```sh
docker compose logs -f pokeidle
docker compose stop
docker compose up -d --build       # atualizar sem apagar os dados
```

## Dados e migração

O volume `deck-data` guarda `/data/deck.json`, os quatro perfis em `/data/profiles`, extensões, configurações e registros. Reiniciar ou recriar o container preserva o volume. `docker compose down -v` apaga o volume e os logins.

O `deck.json` mantém os nomes e enumerações do Windows e passa pela mesma normalização do C# original. Para migrar preferências, pare o serviço e copie seu `deck.json` para `/data/deck.json` no volume. Faça backup antes. Os cookies do WebView2 no Windows usam o armazenamento protegido daquele sistema; **entre novamente nas contas no Linux**, em vez de copiar o perfil do WebView2.

Para um backup consistente, pare o serviço antes de copiar o volume. O volume contém sessões autenticadas e deve permanecer privado. `.env`, perfis e arquivos enviados não entram no Git nem no contexto de build.

### Extensões e configurações

- **Chrome Web Store:** cole o link em **🧩 Extensões → Da Chrome Web Store…**. O instalador mantém a chave pública e o ID do CRX.
- **ZIP, CRX ou pasta:** envie o arquivo pelo menu **☰ → Arquivos**, copie o caminho `/data/imports/...` e use **Arquivo / pasta descompactada…**. Uma pasta pode ser disponibilizada nesse diretório pelo administrador do container.
- **Importar do meu Chrome:** feche o Chrome, exporte a pasta `Extensions` do perfil para um ZIP, envie o arquivo pela WebUI e use **Importar do meu Chrome… → Localizar extensões**. Selecione as extensões desejadas. O container não pode procurar arquivos diretamente no computador que está acessando a WebUI.
- **Trazer configurações:** copie de outra conta do deck ou envie um ZIP da pasta `Local Extension Settings/<id-da-extensao>` do Chrome, IdleDeck ou WebView2. A conta é fechada durante a cópia do LevelDB e reaberta depois; o banco anterior fica em `/data/backups`.

Extensões são executadas pelo Chromium completo, incluindo content scripts e service workers. Os botões abrem o documento de popup da extensão no perfil correspondente, seguindo o comportamento de `PopupWindow` no Windows. Recarregue a conta depois de instalar uma extensão. Extensões que dependem de um programa nativo do Windows podem exigir adaptação desse programa para Linux.

## Funcionalidades e validação

| Recurso | Implementação Linux |
|---|---|
| Quatro contas, cookies e extensões isolados | Quatro perfis e processos completos do Chromium |
| Grade, colunas, foco, zoom, renomear, som, ligar/desligar | Controles do deck e integração X11/Chromium |
| Eco de cliques | Scripts originais de captura e repetição; digitação não é repetida |
| ULTRA, menos animações, FPS e sprites | Scripts originais e emulação de mídia do Chromium |
| Recompra e PvP | `recompra.js` e `pvp.js` originais, injetados antes do jogo |
| Twitch/Kick leves e notificações | `TwitchWatcher`, `KickWatcher` e `TwitchPubSub` originais compilados para Linux |
| Resgate, baú, recusas e devolução de pontos | `PointsRedeemer` original e scripts executados no perfil da conta |
| Guarda de bônus | Seleção original, monitoramento do jogo e confirmação após resgate |
| Resgate em lote | Scripts originais, conferência de saldo, intervalo e parada no primeiro erro |
| Voto TopIdle | Página original com o nick, cookies da conta e detecção da resposta do voto |
| Registros e diagnóstico | Volume persistente; downloads em **☰ → Arquivos** |
| Recuperação de carga travada | Recarga progressiva e `/data/aberturas.log` |

Os testes automatizados usam um jogo local, perfis exclusivos de teste e uma extensão de teste. Eles verificam a execução dentro do Docker, isolamento, persistência após reinício, layouts, desligar/religar contas, eco, digitação, recompra, PvP, leitura de bônus, extensões e acesso noVNC autenticado. Não gastam ouro nem pontos de contas reais.

**Limite de validação:** login e comportamento de ponta a ponta em contas reais do PokeIdle, Twitch, Kick e TopIdle precisam ser conferidos pelo titular dessas contas. Os serviços podem impor desafios ou mudar suas APIs. Reutilizar o código original reduz divergências, mas não comprova paridade integral sem essa validação. O indicador de memória informa a soma do RSS dos processos Chromium; não reproduz a divisão de memória por processo que o WebView2 oferece no Windows.

## Desenvolvimento e testes

O projeto Windows permanece em `PokeIdleMulti/`. O port está em `linux/`; `bridge/DeckBridge.csproj` usa .NET 10 LTS e compila diretamente os arquivos C# que não dependem de WinForms. Os scripts que estavam embutidos em classes de interface são extraídos por um gerador verificável:

```sh
python3 linux/tools/sync-upstream.py
python3 linux/tools/sync-upstream.py --check
cd linux
npm ci
npm test
```

Build e integração real em container:

```sh
docker build -t pokeidle-deck:test .
docker run -d --name pokeidle-integration --shm-size=2g \
  -e WEB_PASSWORD=integration-only-password \
  -e DECK_TEST_MODE=1 \
  -v pokeidle-integration-data:/data pokeidle-deck:test
docker exec pokeidle-integration npm run smoke
docker restart pokeidle-integration
# Aguarde o healthcheck ficar healthy:
docker exec pokeidle-integration node test/smoke.mjs --persistence
docker rm -f pokeidle-integration
docker volume rm pokeidle-integration-data
```

`DECK_TEST_MODE=1` substitui o jogo por uma fixture local e habilita as ações de integração apenas na API interna. Não use esse modo para jogar. O Chromium usa um pipe privado para DevTools; nenhuma porta DevTools ou VNC é publicada. A API de controle fica no loopback do container e exige um token aleatório por execução. A WebUI exige autenticação também nos WebSockets e aplica proteção de origem e CSRF no envio de arquivos.

O Chromium roda como usuário sem privilégios. Para funcionar com o perfil padrão de isolamento do Docker, ele usa `--no-sandbox`; o container é a fronteira de isolamento e não deve receber volumes sensíveis do host. Nenhum socket do host ou modo privilegiado é necessário.

### Verificação do Cloudflare

Os scripts do jogo são limitados ao documento principal da origem do jogo; eles não alteram APIs nos iframes de login ou verificação. Isso evita interferências nesses componentes, mas não garante que o Cloudflare aceite este ambiente. A [documentação do Cloudflare](https://developers.cloudflare.com/cloudflare-challenges/reference/supported-browsers/) informa que navegadores automatizados, incluindo Puppeteer, não têm suporte para resolver desafios de produção.

Se aparecer “Verification failed”, abra “Troubleshoot” e anote o código de erro. Compare com o jogo aberto em um navegador atualizado no seu computador, na mesma rede do TrueNAS, e verifique extensões, VPN/proxy e bloqueios de DNS para `challenges.cloudflare.com`. Se persistir, envie o código e o Ray ID ao suporte do jogo, conforme as [orientações do Cloudflare](https://developers.cloudflare.com/cloudflare-challenges/troubleshooting/challenge-solve-issues/). Não é necessário alterar a porta ou a senha da WebUI para esse diagnóstico.
