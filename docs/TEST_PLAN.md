# Plano de testes

`dotnet test --project tests/ShiftControl.Tests -c Release`: **232 testes automatizados aprovados** na auditoria de 15/11/2026. A matriz de cada funcionalidade e as limitações estão em [VALIDATION-2026-11-15.md](VALIDATION-2026-11-15.md).

## Automatizados

| Área | Cobertura |
| --- | --- |
| Núcleo | Buffer circular; estatísticas incrementais com concorrência; validação de sensores (valores impossíveis, ausentes, limites); preferências (normalização, clone); extração de métricas (indisponível nunca vira 0; GPU dedicada preferida; PC sem GPU); colunas adaptáveis; escala dos eixos |
| Persistência | Preferências (ida e volta, JSON corrompido, schema mais novo recusado); histórico (amostras brutas, baldes, agregado por minuto entre lotes, retenção, fila limitada, apagar, reabrir arquivo); sessões e migração v1 → v2 |
| Monitoramento | Coleta em thread própria; falha de amostra com recuperação; assinante com erro não afeta os demais; falha de inicialização; Dispose interrompe a espera; ritmo em segundo plano; gravação condicional; leitores concorrentes; custo por rodada (< 200 µs) |
| Diagnóstico | Espaço em disco; limites de temperatura do próprio disco; sem limite universal; SMART; carga sustentada exige amostras suficientes |
| Overlay | Estatísticas de frames; parser do PresentMon; roteamento por PID e cadeia de troca; política de escolha do jogo; ping; sessões e comparação; presets; atalhos |
| Hardware | Leitura do descritor de temperatura; índice de disco do LHM; filtro de adaptadores virtuais; tipos de memória; **integração com o hardware real desta máquina** (marcado `Category=Integration`) |
| Interface | Toda chave de texto usada existe no pt-BR; famílias dinâmicas completas; formatos válidos |

## Manuais (feitos nesta entrega)

- Dashboard, Sensores, Histórico e Configurações capturados em 1500 px, 760 px e no tema claro.
- Diagnóstico acionado via UI Automation.
- Overlay sobre Slay the Spire 2 (detalhes em docs/OVERLAY.md).
- Consumo do app: Release em repouso com o dashboard visível, 0,40% de CPU total; minimizado, 0,13%; durante jogo, 0,21%.

## Manuais pendentes

| Cenário | Como validar |
| --- | --- |
| Múltiplas GPUs (integrada + dedicada) | Seletor no cartão de GPU; overlay usa a dedicada |
| PC sem sensores acessíveis | Cartões com "Indisponível" + explicação |
| Múltiplos monitores / troca de resolução / DPI 150–200% | Overlay no monitor escolhido; layout sem cortes |
| Suspensão e retomada | Coleta continua; captura reinicia se o PresentMon morrer |
| Tela cheia exclusiva | Aviso na tela Overlay; FPS gravado |
| Vulkan e OpenGL | Detecção e FPS |
| Jogo com e sem overlay | Mesmo trecho, FPS médio e 1% Low, ≥ 3 repetições |
| Grupo "Performance Log Users" | Conceder, sair/entrar, medir sem admin |
