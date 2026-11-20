# Shift Control 0.3.8

## Gerenciador de tarefas: processos agrupados

- Processos com o mesmo nome (ex.: `chrome.exe`, `msedgewebview2.exe`) aparecem numa linha só, com CPU e memória somadas e a quantidade de processos. A caixa **Agrupar por nome** vem ligada e pode ser desligada para ver cada PID.
- **Encerrar** numa linha agrupada fecha todos os processos daquele nome, do mais antigo para o mais novo, com uma segunda passada para os que reabrem enquanto o programa fecha (o que fazia o Chrome "não fechar"). O resultado diz quantos foram encerrados e quantos foram bloqueados.
- Com a lista separada, o menu de contexto de um processo oferece **Encerrar todos os X** quando há outros com o mesmo nome.
- As mesmas proteções de antes valem para cada processo do grupo: processos críticos, de serviço e a própria Shift Control nunca são encerrados.

## Correção: o app fechava ao clicar no ícone da bandeja

- Abrir o mini-painel às vezes falhava com "Invalid window handle". Como isso acontecia dentro do callback nativo do ícone, a exceção encerrava o app inteiro.
- Os comandos do ícone agora rodam na fila da interface, fora do callback, e qualquer falha só é registrada no log. Se o mini-painel falhar ao abrir, a janela é descartada e o próximo clique abre normalmente.

## Validação

- 290 testes automatizados aprovados, incluindo o encerramento real de um grupo de processos de teste.
