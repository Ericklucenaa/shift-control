# Arquitetura

## Camadas

```
ShiftControl.App (WinUI 3, MVVM)
   │  ViewModels ◀── SnapshotHub (despacha o último snapshot para a UI, no máximo um por vez)
   │  OverlayService ── janela do overlay, captura de frames, sessões
   ▼
ShiftControl.Monitoring ── MonitoringEngine (thread própria, BelowNormal)
   │  LiveHistory (buffers circulares) · SensorStatsTracker · SensorExport
   ▼
ShiftControl.Hardware ── WindowsHardwareSource : IHardwareSource
   │  LibreHardwareMonitor · WMI (inventário) · PDH · IOCTL de temperatura de disco · NetworkInterface
ShiftControl.Storage ── SQLite (preferências, histórico bruto + por minuto, sessões de jogo)
ShiftControl.Diagnostics ── HealthAnalyzer (regras puras sobre o snapshot)
ShiftControl.Overlay ── modelo do overlay e algoritmos puros (frames, detecção, latência, sessões)
ShiftControl.Gaming ── integrações Windows do overlay (PresentMon, permissões ETW, processos, ping)
ShiftControl.Core ── modelos e contratos compartilhados
```

## Fluxo de dados

1. `MonitoringEngine` chama `IHardwareSource.Sample()` a cada intervalo (padrão 1 s; com a janela oculta, no mínimo
   2 s, exceto se o overlay estiver visível).
2. O snapshot imutável alimenta estatísticas, histórico ao vivo e a fila do SQLite (gravada a cada 15 s numa transação).
3. `SnapshotHub` guarda só o snapshot mais recente e agenda um único despacho para a UI. Se a interface estiver ocupada,
   amostras intermediárias são puladas. Com a janela minimizada, nada é despachado.
4. Cada página assina o hub só enquanto está carregada: gráficos fora de vista não custam nada.

## Tolerância a falhas

- Cada componente de hardware é lido isoladamente (`Guard`). A falha de um é registrada uma vez e os demais continuam.
- Leituras impossíveis são descartadas (`SensorValidation`), e uma leitura ausente carrega o motivo (`Unavailable`).
- Sem administrador ou driver, sensores de baixo nível da CPU são marcados "requer admin/driver", e não como 0.
- Falhas de assinantes de eventos não derrubam a coleta. Erros não tratados da UI são registrados e o app continua.
- O PresentMon roda em Job Object: se o app morrer, a captura morre junto.

## Persistência

`%LOCALAPPDATA%\ShiftControl\shiftcontrol.db`, SQLite em WAL, migrações por `PRAGMA user_version`:

| Versão | Tabelas |
| --- | --- |
| 1 | `settings`, `metrics`, `samples_raw`, `samples_minute` |
| 2 | `game_sessions`, `game_session_points` |

Um banco criado por uma versão mais nova é recusado (o app não o sobrescreve).
