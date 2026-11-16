# Overlay de jogos

## Arquitetura

```
PresentMon.exe (ETW)  ──CSV stdout──▶ PresentMonProvider ──Present()──▶ FrameRouter ──▶ FrameStatistics (jogo alvo)
                                         (IFrameMetricsProvider)          │
                                                                          └─▶ processos que estão renderizando
MonitoringEngine (sensores, já existente) ─────────────────────┐          │
LatencyProbe (ping, só se houver métrica de rede visível) ──────┤          ▼
                                                               OverlayService ──▶ OverlayComposer ──▶ OverlayPanel
GameProcesses (janela em primeiro plano) + GameTargetPolicy ───┘      │                               (janela e prévia)
                                                                      └─▶ SessionAggregator ──▶ GameSessionStore (SQLite)
```

- **Captura de frames**: Intel PresentMon 2.6.0 (licença MIT) como processo filho, com saída CSV em stdout.
  Ele consome eventos ETW do DXGI, D3D9 e DxgKrnl. Assim cobre DirectX 9–12, Vulkan e OpenGL (estes dois via
  apresentação pelo DxgKrnl) e informa o **PID** de cada frame. Uma única sessão ETW observa todos os processos.
  A detecção usa isso para saber quem está renderizando.
- **Abstração**: `IFrameMetricsProvider`. Trocar a fonte (ex.: PresentMon SDK/serviço, RTSS) não afeta estatísticas nem interface.
- **Ciclo de vida**: o PresentMon só roda com o overlay ativo e fica num *Job Object* com "matar ao fechar".
  Se o Shift Control terminar, até travando, o Windows encerra a captura junto. Ao parar, a sessão ETW é fechada
  explicitamente (`--terminate_existing_session`), e a próxima execução usa `--stop_existing_session` para limpar sobras.
- **Renderização**: janela própria do Windows: WinUI 3, sem moldura, *topmost*, fora do Alt+Tab, fundo transparente
  por pixel (backdrop transparente + frame do DWM estendido) e *click-through* (`WS_EX_TRANSPARENT`). Não há injeção
  de DLL, leitura de memória do jogo nem alteração de arquivos.
- **Detecção do jogo**: processo dono da janela em primeiro plano que apresenta ≥ 10 FPS e não está na lista de
  exclusão (launchers, navegadores, ferramentas, mais a lista do usuário). Depois de um Alt+Tab, o jogo continua
  sendo o alvo enquanto existir. Executável fixo opcional. Só usa acesso "limitado de consulta" aos processos
  (o mesmo do Gerenciador de Tarefas).

### Por que não RTSS (opção B)

O RivaTuner Statistics Server desenha *dentro* do jogo por hooks de renderização, ou seja, com injeção de código no
processo. Isso conflita com a regra de não injetar, e a integração é por memória compartilhada não documentada
oficialmente. Fica como integração opcional futura: o Shift Control escreveria texto na área compartilhada de um RTSS
instalado pelo usuário, sem injetar nada. Não foi implementada.

## Metodologia das estatísticas

| Métrica | Cálculo |
| --- | --- |
| Frametime | Intervalo entre `Present()` consecutivos da cadeia de troca principal do jogo (PresentMon `MsBetweenPresents`). Com várias cadeias no mesmo processo (ex.: overlay de terceiros), vale a com mais frames no último segundo. |
| FPS (instantâneo) | Frames do último segundo ÷ soma dos seus frametimes. |
| FPS médio | Total de frames ÷ tempo total = 1000 ÷ frametime médio (não é média de FPS por frame). |
| Mín./Máx. | Menor/maior FPS entre janelas completas de 1 s (um único frame isolado não define o mínimo). |
| 1% / 0,1% Low | 1000 ÷ média dos 1% (0,1%) maiores frametimes da sessão. Exige ≥ 100 (≥ 1000) frames; antes disso, "n/d". |
| Frametime p99 | Percentil 99 dos frametimes (sessões gravadas). |
| Travamento | Frame com frametime > 2,5× a mediana dos 64 anteriores **e** ≥ 10 ms acima dela. (Na 1ª validação, 2× e 8 ms contaram ~0,7 travamento/s a 160 FPS: sensível demais.) |
| Ping/Jitter/Perda | ICMP de 32 bytes, 1/s, janela de 60 sondas. Jitter = média de \|Δ\| entre RTTs consecutivos bem-sucedidos. |

Frames com frametime ≤ 0, não finito ou > 5 s (pausa, Alt+Tab longo) são descartados.
Os percentis usam histograma de 0,05 ms: custo e memória constantes por frame.
Nenhuma métrica é estimada a partir do uso da GPU ou da taxa de atualização do monitor.
Sem dado real, o overlay mostra "n/d".

## Compatibilidade

| Situação | Medição de FPS | Overlay visível |
| --- | --- | --- |
| Janela / janela sem bordas (DX11, DX12, Vulkan) | Sim | Sim |
| Tela cheia com "otimizações de tela cheia" (flip independente) | Sim | Em geral sim (validado: Slay the Spire 2, *Hardware: Independent Flip*) |
| Tela cheia **exclusiva** (*Hardware: Legacy Flip*) | Sim | **Não**: o Windows não compõe outras janelas por cima. A tela Overlay avisa e sugere janela sem bordas. |
| OpenGL | Depende do driver apresentar via DxgKrnl | Em janela, sim |
| Jogos com anticheat | Sim (ETW é do sistema, não do jogo) | Sim (janela externa) |

Não há promessa de compatibilidade universal.

## Permissões

O ETW de apresentação exige administrador **ou** o grupo local "Performance Log Users" (SID S-1-5-32-559).
O botão "Permitir sem administrador" executa `Add-LocalGroupMember` com UAC, uma única vez. A mudança vale a partir
do próximo login. O grupo dá acesso a contadores e eventos de desempenho; não dá privilégios de administrador.

## Validação

**Testado de forma automatizada** (tests/ShiftControl.Tests):
- estatísticas de frames: média correta, 1%/0,1% Low, travamentos, frames inválidos e concorrência;
- parser do CSV do PresentMon (cabeçalho da 2.6, "NA", alternativas de coluna, linhas inválidas);
- roteamento por processo e cadeia de troca, taxa por processo e troca de alvo;
- política de escolha do jogo (primeiro plano, launchers, Alt+Tab, executável fixo, detecção desligada);
- ping, sessões, comparação, persistência e migração do banco, presets e atalhos.

**Validado manualmente nesta máquina** (Ryzen 7 5800X3D, RTX 5070, Windows 11 build 26300, 2560×1440):
- janela transparente e click-through: o ponto sob o overlay retorna a janela de baixo;
- sem permissão: o PresentMon recusa a sessão ETW, e o app mostra o estado e as opções em vez de números falsos;
- como administrador, com Slay the Spire 2 (DXGI, Independent Flip):
  - captura ativa e jogo detectado pelo PID certo;
  - overlay visível sobre o jogo;
  - **FPS 171, igual ao overlay da NVIDIA aberto ao mesmo tempo**;
  - temperatura da CPU com PawnIO;
- custo durante o jogo, medido em 30 s:
  - Shift Control: 0,21% de CPU total, 256 MB de memória privada;
  - PresentMon: 0,07% de CPU, 7 MB.

**Implementado, mas ainda não validado em jogo real**: gravação de sessão ao fechar o jogo, comparação entre sessões
com dados de jogo, atalho Ctrl+Shift+O com o jogo em foco, modo de edição (arrastar), Vulkan/OpenGL, multi-monitor,
tela cheia exclusiva e comparação de desempenho do jogo com e sem overlay.
