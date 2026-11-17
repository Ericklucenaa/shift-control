# Shift Control 0.3.7

## Novo: Sistema → Programas instalados

- Lista todos os programas que o Windows mostra em "Aplicativos instalados" (64 bits, 32 bits e só do usuário), com editor, versão, tamanho e data. Componentes do sistema e atualizações ficam ocultos.
- Busca por nome ou editor e ordenação por nome, tamanho, data de instalação ou editor.
- **Desinstalar** abre o desinstalador oficial do programa. Programas MSI são removidos com `msiexec /x` e o código do produto, nunca com o comando de reparo do registro. Quando o programa oferece, há a opção "Desinstalar sem perguntas".
- A tela acompanha o desinstalador até o fim, inclusive os que se copiam para a pasta temporária e encerram o processo original (Inno Setup, NSIS). "Parar de aguardar" só deixa de acompanhar; nunca interrompe o desinstalador.
- Depois da remoção, procura **sobras**: pastas e chaves do registro com o nome exato do programa. Você escolhe o que remover:
  - pastas vão para a **Lixeira**;
  - chaves do registro são **exportadas antes** e podem ser restauradas na **Central de desfazer**.
- Programas cujo desinstalador não existe mais (entradas órfãs) podem ter a entrada removida da lista.

## Proteções

- A própria Shift Control não pode ser removida por esta tela.
- Componentes compartilhados (Visual C++, .NET, WebView2, DirectX e drivers de vídeo) pedem confirmação com aviso.
- Nunca são consideradas sobras: pastas usadas por outro programa instalado, pastas do sistema e do usuário, e pastas com link ou junção no caminho.
- Os backups do registro ficam em `%ProgramData%\ShiftControl\uninstall-backups`, gravável só por Administradores. A restauração recusa arquivos de fora dessa pasta ou criados por outro usuário, para que um processo sem privilégio não consiga trocar um backup e fazer o app elevado importá-lo.

## Validação

- Testes automatizados: leitura do registro, filtros, MSI, separação de comandos, nomes de sobras, pastas compartilhadas, protegidas e com junção, backup e restauração do registro, e desinstalação real de um programa de teste, inclusive com o desinstalador destacado. Uma mutação que desliga o acompanhamento do processo faz o teste falhar.
- Interface: fluxo completo com um programa de teste registrado só no usuário: busca, confirmação, desinstalação, sobras (pasta e chave), Lixeira e backup conferidos no disco e no registro (`tools/programs-uninstall-smoke.ps1`).
- Não foi desinstalado nenhum programa real durante os testes.
