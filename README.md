# Shift Control

Central de monitoramento, diagnóstico e overlay de jogos para Windows 10/11.
Monitoramento local, sem conta e sem telemetria. Atualizações consultam as releases públicas do GitHub.

## O que já funciona

| Área | Estado |
| --- | --- |
| Dashboard (CPU, GPU, RAM, discos, rede, gráficos de 60 s) | Funcionando com dados reais |
| Diagnóstico rápido (espaço, saúde/temperatura de discos, carga sustentada) | Funcionando |
| Sensores (lista completa, mín/máx/média, favoritos, exportar CSV/JSON) | Funcionando |
| Histórico (60 s → 7 dias, agregação por minuto, retenção configurável) | Funcionando |
| Overlay de jogos (FPS, 1%/0,1% Low, frametime, hardware, ping) | Funcionando — validado em jogo real (ver docs/OVERLAY.md) |
| Sessões de jogo (gravação e comparação) | Implementado; ver "Validação" em docs/OVERLAY.md |
| Configurações persistentes, tema claro/escuro, cor de destaque, iniciar com o Windows | Funcionando |
| Driver Center e migração antes/depois de formatar | Implementados; operações de sistema exigem confirmação/elevação |
| Game Boost, energia, pausa de programas e evidência térmica | Implementados; comparação usa sessões reais gravadas |
| Limpeza, armazenamento, inicialização, reparo, erros e rede | Implementados; ver matriz de validação |
| Bandeja, mini-painel, alertas, instalador e atualizador | Implementados; distribuição de atualizações depende de release pública |
| Gerenciador de Tarefas (processos, CPU/RAM, busca, filtros, detalhes e encerramento) | Implementado e validado com dados reais; processos críticos protegidos |
| Programas instalados (busca, desinstalador oficial, sobras com Lixeira e backup do registro) | Implementado; fluxo completo validado com programa de teste (ver docs/RELEASE-0.3.7.md) |

Auditoria de 15/11/2026: **232 testes aprovados** e navegação nas **18 telas** em janela ampla e estreita. Veja [a validação por funcionalidade](docs/VALIDATION-2026-11-15.md), incluindo operações que não foram executadas no sistema real.

Entrega 0.3.0: executável em `artifacts/release-0.3.0/publish/ShiftControl.exe`, instalador `artifacts/release-0.3.0/ShiftControl-0.3.0-setup.exe` e pacote portátil `artifacts/release-0.3.0/ShiftControl-0.3.0-portable.zip`.

Esta versão adiciona **Sistema → Gerenciador de tarefas**, com 247 testes aprovados e validação da interface em janela ampla/estreita. Veja [os recursos e a validação](docs/TASK_MANAGER.md). A correção do botão **Criar pacote** da versão anterior está preservada ([detalhes](docs/RELEASE-0.1.2.md)).

As opções visuais ficam em **Configurações → Temas e botões**. Veja [os estilos e a validação da interface](docs/APPEARANCE.md).

Para gerar novamente: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1` (requer NSIS e acesso ao NuGet). O instalador preserva preferências e histórico em `%LOCALAPPDATA%\ShiftControl`.

## Requisitos

- Windows 10 2004 (build 19041) ou mais novo, x64.
- [.NET 10 SDK](https://dotnet.microsoft.com/download).
- (Opcional) VS Code com a extensão **C# Dev Kit**.

O Windows App SDK é incluído pela própria compilação (modo *self-contained*): não é preciso instalar runtime.

## Executar no VS Code

1. Abra a pasta `shift-control` no VS Code.
2. `Ctrl+Shift+B` → **build** (ou `dotnet build src/ShiftControl.App`).
3. `F5` → **Shift Control (Debug)**.

Pelo terminal:

```powershell
dotnet run --project src/ShiftControl.App
# abrir direto numa tela: overview | history | sensors | overlay | sessions | settings
dotnet run --project src/ShiftControl.App -- --page=overlay
```

Testes:

```powershell
dotnet test --project tests/ShiftControl.Tests
```

O componente de medição de FPS (Intel PresentMon, MIT) fica em `src/ShiftControl.App/Tools/PresentMon.exe`.
Se faltar, baixe com hash conferido: `powershell -File tools/fetch-presentmon.ps1`.

## Permissões

O app abre como usuário comum. Algumas leituras exigem mais:

| Recurso | Sem administrador | Com administrador |
| --- | --- | --- |
| Uso de CPU/GPU, VRAM, clocks e temperatura da GPU, RAM, discos, rede | Sim | Sim |
| Temperatura de SSD NVMe/HDD | Sim (IOCTL do próprio disco) | Sim |
| Temperatura, clock real e consumo da CPU | Não | Sim, com o driver **PawnIO** instalado |
| SMART/vida útil dos SSDs | Não | Sim |
| FPS e frametime (ETW) | Só no grupo "Performance Log Users" | Sim |

A tela Overlay oferece incluir a conta no grupo "Performance Log Users" (uma confirmação de UAC; depois é preciso sair e entrar no Windows) para medir FPS sem administrador.

## Estrutura

```
src/
  ShiftControl.App          Interface WinUI 3 (MVVM), overlay, composição dos serviços
  ShiftControl.Core         Modelos, contratos, utilitários (sem dependências)
  ShiftControl.Hardware     Sensores: LibreHardwareMonitor, WMI, contadores, IOCTL de discos
  ShiftControl.Monitoring   Motor de coleta, histórico ao vivo, estatísticas, exportação
  ShiftControl.Storage      SQLite: preferências, histórico, sessões de jogo
  ShiftControl.Diagnostics  Diagnóstico rápido baseado em evidências
  ShiftControl.Overlay      Modelo do overlay, estatísticas de frames, detecção de jogo (puro, testável)
  ShiftControl.Gaming       Integrações Windows do overlay: PresentMon, permissões ETW, ping
tests/ShiftControl.Tests    xUnit v3
docs/                       Arquitetura, decisões, overlay, plano de testes, roteiro
```

Dados e logs: `%LOCALAPPDATA%\ShiftControl` (banco `shiftcontrol.db`, pasta `logs`).

## Atualização automática (0.3.0)

Consulte [como funciona e como publicar novas versões](docs/AUTO-UPDATES.md). Aumente a versão em `Directory.Build.props` e envie para `main`: o GitHub Actions testa, compila e publica uma release completa automaticamente.
