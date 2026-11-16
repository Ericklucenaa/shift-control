Shift Control 0.3.0

- Atualizacoes automaticas: verifica novas releases a cada 2 minutos, baixa o instalador oficial, confere SHA-256 e reinicia para atualizar.
- Aguarda jogos detectados, Game Boost, operacoes e dialogos terminarem. Avisa 30 segundos antes do reinicio; pode ser desativado nas Configuracoes.
- Atualiza na mesma pasta, preservando preferencias e historico; suporta modo portatil.
- GitHub Actions cria futuras releases ao aumentar a versao em Directory.Build.props e enviar para main.
- Inclui o Gerenciador de Tarefas com encerramento pelo botao no topo, menu de contexto ou Delete, alem das correcoes e temas anteriores.

Validacao: 256 testes automatizados aprovados. Testes da interface usam somente processos auxiliares proprios. A instalacao silenciosa e validada em uma pasta isolada.

Usuarios das versoes anteriores precisam instalar esta versao uma vez para ativar o novo atualizador. A deteccao depende de internet e do app aberto (inclusive na bandeja).

Downloads: instalador para Windows x64 e ZIP portatil. Arquivos .sha256 acompanham ambos.
