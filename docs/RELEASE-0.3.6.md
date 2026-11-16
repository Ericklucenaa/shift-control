# Shift Control 0.3.6

## Sensores

- A tela abre com cada componente recolhido em uma linha, mostrando só os sensores principais ao vivo: CPU e GPU (temperatura, uso, frequência, potência), memória (uso e quantidade usada), discos (temperatura, atividade, leitura, gravação) e rede (download, upload).
- Clique no componente para ver todos os sensores dele, com mín/máx/média. **Expandir tudo / Recolher tudo** no topo.
- A busca abre os componentes com resultados; Favoritos continuam sempre abertos.
- Em janelas estreitas, os valores menos importantes são omitidos em vez de sobrepor o nome do componente.

## Bandeja

- Clicar no ícone da bandeja com o mini-painel aberto agora fecha o painel. Antes, o painel fechava ao perder o foco e o próprio clique o reabria; um clique também podia chegar como dois eventos do Windows e abrir e fechar o painel no mesmo instante.

## Validação

Testes automatizados aprovados, incluindo a escolha dos sensores principais por componente. Capturas reais da tela de Sensores em 1360 e 900 px (recolhido, um componente expandido, busca). O mini-painel foi exercitado com os eventos reais do ícone da bandeja, comparando a 0.3.5 com esta versão: a 0.3.5 reabria o painel depois da perda de foco; esta fecha, abre e reabre corretamente.
