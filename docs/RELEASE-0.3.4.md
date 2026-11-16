# Shift Control 0.3.4

- Format Kit: **Atualizar marcados** usa as mesmas caixas de seleção da lista. Apenas programas marcados, instalados e com atualização disponível entram na fila.
- Desmarcar todos deixa a contagem em zero e desabilita a atualização. Não há fallback para atualizar todos os instalados.
- O contador corresponde à fila completa, incluindo seleções fora do filtro de busca. Buscar não limpa a seleção.
- A fila captura os programas escolhidos ao iniciar; cada programa continua sendo processado individualmente pelo WinGet.
- Instalar marcados conserva o comportamento existente do WinGet, inclusive a possibilidade de atualizar um programa instalado que esteja marcado. A orientação na tela explica a diferença.
- Publicação no mesmo repositório e canal de atualização: as instalações existentes recebem a versão na próxima abertura, conforme o atualizador já implementado.

Validação: testes da seleção cobrem programas desmarcados, programas ausentes, versões já atuais, seleção vazia, múltiplos programas e origem Microsoft Store. Nenhum programa real é atualizado durante esses testes.
