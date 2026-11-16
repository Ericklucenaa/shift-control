# Shift Control 0.3.5

## Segurança

- **Atualizador:** o instalador baixado fica travado contra escrita, troca e exclusão até iniciar, e o SHA-256 é conferido de novo pelo mesmo handle. Antes, um programa sem privilégio podia trocar o arquivo depois da conferência e herdar a elevação do Shift Control.
- **Limpeza:** cada arquivo agora é excluído pelo próprio handle, depois de confirmado que o caminho real está dentro da pasta limpa. Antes, uma pasta do Temp trocada por uma junção podia redirecionar a exclusão para fora dela, por exemplo para `System32`.

## Format Kit

- A busca fica fixa sobre o catálogo, mostra quantos programas aparecem e quantos marcados estão fora do filtro (eles continuam valendo para a ação escolhida).
- Busca sem resultados mostra um estado vazio com **Limpar busca**. A seleção é preservada.
- O catálogo pode ser rolado durante a detecção de programas; só a marcação fica bloqueada.
- Avisos fechados não deixam mais um espaço vazio no topo.
- Janela estreita: ações em uma faixa compacta abaixo do catálogo, com os botões lado a lado.

## Validação

Testes automatizados aprovados, incluindo exclusão através de uma junção real e a trava do instalador contra escrita, exclusão e renomeação. Capturas reais do Format Kit em 1360 e 800 px (catálogo, busca vazia e filtro com marcados ocultos). Tema claro e leitor de tela não foram verificados.
