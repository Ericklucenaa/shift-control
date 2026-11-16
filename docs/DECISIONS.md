# Decisões

**D-001 Nome dos projetos.** O documento de requisitos lista módulos `NexusPC.*`, mas o produto se chama Shift Control.
Usei `ShiftControl.*` para alinhar código e produto. Renomear é mecânico.

**D-002 Gráficos próprios em vez de biblioteca.** `LineChart` desenha com formas XAML e reaproveita as mesmas formas a
cada atualização. Alternativas avaliadas: LiveCharts2 (SkiaSharp, dezenas de MB, versão WinUI ainda em RC) e Win2D.
Para séries curtas a ~1 Hz, as formas XAML bastam, sem dependência nem custo de memória extra.

**D-003 Sensores sem administrador.**
- LibreHardwareMonitor 0.9.x lê a CPU pelo driver **PawnIO**, instalado à parte. Sem admin + PawnIO, a biblioteca
  devolve 0, então o app marca a leitura como indisponível em vez de exibir 0.
- Frequência da CPU sem driver: `% Processor Performance` × frequência base, indicada como "(est.)".
- Temperatura de discos sem admin: `IOCTL_STORAGE_QUERY_PROPERTY` (StorageDeviceTemperatureProperty) com acesso 0,
  incluindo os limites de alerta/crítico do próprio disco, usados no diagnóstico.
- HDDs são consultados a cada 2 min, para não atrapalhar o desligamento do motor por inatividade.

**D-004 Rede pelo NetworkInterface, não pelo LHM.** O LHM lista adaptadores virtuais e filtros (QoS, WFP). O app usa os
contadores de bytes dos adaptadores físicos ativos.

**D-005 Textos em JSON embutido.** `Strings/pt-BR.json` + extensão `{l:Tr Key=...}`. Um teste garante que toda chave
usada existe. Novos idiomas: `en.json`, `es.json`.

**D-006 FPS pelo PresentMon como processo filho.** Avaliados:
- PresentMon SDK/serviço: exige instalar um serviço;
- consumidor ETW próprio com TraceEvent: reimplementaria a lógica de apresentação do DxgKrnl;
- RTSS: injeção de código.

O executável de console da 2.6.0 (MIT, ~1 MB) entrega o CSV por frame com PID e já trata DXGI, D3D9, DxgKrnl e
Vulkan. Tudo fica atrás de `IFrameMetricsProvider`.

**D-007 Overlay em janela WinUI própria.** Mesmo componente (`OverlayPanel`) na janela do jogo e na prévia, o que
garante prévia fiel. A transparência por pixel e o click-through foram validados antes da implementação: com
click-through ligado, o ponto sob o overlay devolve a janela de baixo.

**D-008 Permissão ETW sem administrador.** Oferecer a inclusão no grupo "Performance Log Users" (UAC uma vez) é menos
invasivo que rodar o app inteiro como administrador. Os dois caminhos ficam disponíveis.

**D-009 Instância única.** Duas instâncias dividiriam banco, atalho global e sessão ETW. A nova espera até 8 s (cobre
"Reiniciar como administrador") e, se a outra continuar aberta, traz a janela dela para a frente.
