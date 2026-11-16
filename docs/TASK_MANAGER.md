# Gerenciador de Tarefas — versão 0.2.1

Disponível em **Sistema → Gerenciador de tarefas**, integrado aos temas e estilos de botões do app.

## Recursos

- Processos em execução com nome, PID, CPU, memória residente e estado (aplicativo, segundo plano ou não responde).
- Resumo de CPU e memória do computador, vindo do monitoramento existente, e contagem dos processos.
- Atualização a cada dois segundos em uma thread de trabalho. A coleta adicional para quando a página sai de uso e não roda durante a janela oculta/minimizada.
- Busca por nome ou PID; ordenação por CPU, memória, nome ou PID; filtro de aplicativos com janela.
- Pausa das atualizações sem suspender nenhum processo; atualização manual disponível.
- Detalhes do executável, fabricante, início, threads e memória privada; opção de abrir a localização no Explorador.
- Encerramento de um processo selecionado, com confirmação explícita. A ação não encerra automaticamente os demais processos do aplicativo.

O botão **Encerrar tarefa** fica sempre visível no topo e habilita ao selecionar um processo permitido. A mesma confirmação pode ser aberta pelo menu de contexto (botão direito/Shift+F10) ou pela tecla Delete na lista. Cancelar preserva a tarefa.

## Dados e limites

CPU é calculada pela diferença do tempo de CPU entre amostras, dividida pelo tempo decorrido e pelo número de processadores lógicos. É relativa à capacidade total do computador, de 0 a 100%. A primeira amostra fica indisponível, em vez de mostrar um zero inventado.

Memória residente pode incluir páginas compartilhadas; a soma de todos os processos não representa necessariamente a RAM física usada. O resumo usa a leitura de memória do sistema. Memória privada é mostrada separadamente nos detalhes.

Os dados de processos protegidos ou elevados podem ficar indisponíveis sem permissão. Processos do sistema na sessão 0, nomes essenciais, instâncias do Shift Control e processos críticos não podem ser encerrados por esta tela.

Antes de encerrar, a implementação abre um handle, confere o horário de criação e verifica `IsProcessCritical`. A consulta e o encerramento usam o mesmo handle. Isso impede encerrar um processo diferente que tenha reutilizado o PID. Falhas na verificação bloqueiam a ação.

## Validação

- **247 testes automatizados aprovados**, sem falhas ou ignorados.
- Testes novos: CPU normalizada, dados indisponíveis, filtros e ordenações, proteção de processos e identidade, coleta real, detalhes e encerramento de um auxiliar exclusivo do teste.
- UI Automation nas janelas ampla e estreita: lista real, ordenação por memória, filtro de aplicativos, busca sem resultado e por PID, pausa, seleção e detalhes, autoencerramento bloqueado, cancelamento e encerramento confirmado de um auxiliar.
- Nenhum processo do usuário foi encerrado pelos testes. O único alvo de encerramento foi um `ping.exe` contra loopback, criado e identificado pelo próprio teste.
- Capturas e relatórios em `artifacts/task-manager-tests/`.

```powershell
dotnet test --project tests/ShiftControl.Tests -c Release --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File tools/task-manager-smoke.ps1 -Executable D:\Projetos\shift-control\artifacts\release-0.2.1\publish\ShiftControl.exe -Narrow
```

Referências: [IsProcessCritical](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-isprocesscritical), [GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes), [TerminateProcess](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess).
