# Auditoria e validação — 15/11/2026

O código local já continha implementações das fases B–E além do ponto descrito no histórico anexado. A auditoria revisou essa implementação, completou as lacunas abaixo e executou validações de lógica, integração e interface.

## Alterações desta auditoria

- Restauração de migração em uma etapa, incluindo IDs do WinGet fora do catálogo sugerido; rejeição de fontes e identificadores inválidos.
- Validação da versão do manifesto e resultado parcial de importação Wi-Fi.
- Driver Center: classes sem diferenciar maiúsculas; falhas parciais do Windows Update preservadas; bloqueio quando a tentativa de criar ponto de restauração falha.
- Game Boost: gravação atômica de recuperação, identificação antes da suspensão, recuperação preservada quando a restauração falha e encerramento ao trocar de jogo; inclusão manual de executáveis.
- Sessões com mudança de boost marcadas como `Mixed` e excluídas da comparação; 1% Low ausente continua indisponível.
- Limpeza: validação de raízes, arquivos e junções; preservação de temporários recentes e arquivos fora dos padrões de cache.
- Reparo: saída desconhecida do SFC não vira sucesso; filtro de erros de disco exige o provedor correspondente.
- Alertas: sustentação interrompida quando o sensor fica indisponível; alertas recentes no painel.
- Bandeja: recuperação após reiniciar Explorer usa janela oculta capaz de receber a mensagem de broadcast.
- Configurações: controles de fechar para a bandeja e verificar atualizações; download do instalador oficial com SHA-256 conferido antes da execução.
- Instalador NSIS por usuário, desinstalador, atalho no menu Iniciar, preservação do banco e publicação autônoma reproduzível.
- Textos pt-BR das novas telas e testes das famílias dinâmicas.
- Histórico: consulta aguarda a criação dos dois filtros, corrigindo exceção encontrada no teste de interface.
- Rede: comando permite cancelar um teste em andamento.
- Game Boost: campo de executável atualiza o valor enquanto é digitado; a automação exige que o perfil apareça na lista após adicionar.

## Verificação por funcionalidade

| Funcionalidade | Validação executada | Limite |
| --- | --- | --- |
| Painel e sensores | testes existentes de hardware real; navegação automatizada | CPU exige elevação/PawnIO para alguns sensores |
| Histórico | SQLite, retenção, consultas e abertura da tela | observação por vários dias não repetida |
| Overlay, aparência e posição | testes existentes de métricas/posição; tela e prévia | jogo real não repetido nesta auditoria; validação anterior em OVERLAY.md |
| Sessões e comparação | gravação, estatísticas, comparação e sessão mista | ganho real de boost exige cenas equivalentes |
| Catálogo, detecção e perfis Format Kit | catálogo, parser, fila simulada e tela | nenhuma instalação real nesta auditoria |
| Instalar, atualizar, cancelar e desfazer programas | fila, códigos de saída, cancelamento e diário | programas reais não instalados/desinstalados |
| Exportar/importar programas | ida e volta dos kits, fonte/ID inválido | pacotes dependem da disponibilidade no WinGet |
| Checagem gamer e pós-formatação | cenários e leitura real do PC | BIOS não é alterada automaticamente |
| Inventário de drivers | integração com dispositivos reais, classes em maiúsculas/minúsculas | sem troca de drivers |
| Windows Update de drivers | revisão do fluxo/resultados e tela | busca/instalação real não repetida |
| GPU do fabricante | conversão da versão, correspondência exata do modelo e tela | NVIDIA depende da API; AMD/Intel usam páginas oficiais |
| Backup/restauração de drivers e ponto de restauração | comandos PnPUtil, bloqueios de elevação/proteção revisados | operação real não executada |
| Pacote antes de formatar | pacote real só de preferências em pasta descartável; manifesto | exportação de senhas Wi-Fi não executada |
| Restauração após formatar | manifesto, preferências, listas e fluxo sequencial | reinstalação completa deve ser ensaiada em VM limpa |
| Perfis Game Boost e energia | parser, escolha de plano, recuperação e inventário real dos planos | não suspendeu programas do usuário nem trocou planos reais |
| Evidência térmica | dados insuficientes, clock estável e queda térmica | indício por sensores, não diagnóstico conclusivo |
| Limpeza segura | exclusão real de arquivos descartáveis, recentes preservados, limites de caminhos | caches e lixeira do usuário preservados |
| Analisador de armazenamento | árvore descartável, soma recursiva, maiores arquivos, cancelamento | pastas protegidas podem gerar leitura parcial |
| Inicialização | parser, StartupApproved e inventário real só de leitura | registro real não alterado |
| Reparo SFC/DISM/CHKDSK | resultados conhecidos, falhas e saída desconhecida | reparos reais não executados |
| Histórico de erros | leitura real de sete dias, validação do período e tela | logs inacessíveis não comprovam ausência de falhas |
| Saúde SSD/HDD | MSFT_PhysicalDisk real e estados válidos | contadores detalhados podem exigir administrador |
| Rede e histórico | ping real de loopback, perda/jitter e persistência | não mede servidor de jogo |
| Bandeja e mini-painel | mini-painel aberto, janela principal ocultada e restaurada via mensagens da bandeja na instância isolada | Explorer não foi reiniciado para teste |
| Alertas configuráveis | sustentação, sensor ausente, desligar/ligar, cooldown, SQLite; notificações observadas | entrega depende das configurações do Windows |
| Instalador | publicação autônoma, compilação NSIS e SHA-256 | instalação/desinstalação no PC não executadas |
| Atualizações | metadados e download HTTP simulados, hash correto/corrompido, URL externa, release inexistente | distribuição depende de release pública com instalador |

## Comandos e resultados

```powershell
dotnet test --project tests/ShiftControl.Tests -c Release --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ui-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ui-smoke.ps1 -Narrow
```

Suíte completa: **232 testes aprovados, zero falhas e zero ignorados**, incluindo integração de leitura com o PC real. O ambiente restrito impediu duas leituras de hardware; a execução fora dele passou. Nenhum teste instala programas, troca drivers, altera inicialização ou exporta senhas.

Navegação automatizada: **18/18 telas amplas e 18/18 estreitas**. Capturas diretas ficam em `artifacts/screenshots`; relatórios em `artifacts/ui-report*.json`. A instância de teste usa `--data-dir=...`, banco e mutex separados; o app já aberto pelo usuário é preservado. Capturas não comprovam todos os fluxos interativos.

Fluxos seguros adicionais pela interface (`tools/ui-smoke.ps1 -Exercise`): adição de perfil por executável persistida no SQLite; teste de rede contra `127.0.0.1` com **40 respostas de 40 envios** e histórico persistido; consulta de atualização retornando ausência de release pública; mini-painel e ocultar/restaurar pela bandeja. As execuções finais ampla e estreita passaram **19 verificações cada** (18 telas mais bandeja), com perfil e ping conferidos no SQLite e nenhum erro/falha fatal nos logs.

Artefatos: `artifacts/publish/ShiftControl.exe`, `artifacts/ShiftControl-0.1.0-setup.exe` e checksum. Nenhum commit, push ou publicação de release foi realizado.

SHA-256 do instalador: disponível no arquivo `artifacts/ShiftControl-0.1.0-setup.exe.sha256`, gerado junto ao executável e conferido antes da entrega.

## Referências técnicas

- [GitHub: assets de release e digest](https://docs.github.com/en/rest/releases/assets).
- [Microsoft: TaskbarCreated e broadcast](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar).
- [Microsoft: resultados de instalação do Windows Update](https://learn.microsoft.com/en-us/windows/win32/wua_sdk/searching--downloading--and-installing-updates).
