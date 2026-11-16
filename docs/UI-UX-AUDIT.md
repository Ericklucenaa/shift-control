# Auditoria UI/UX — Shift Control

16/11/2026. Escopo: aplicação Windows WinUI 3; primeiro incremento no Format Kit.

## Direção e contrato visual

Ferramenta de controle do computador, com informações verificáveis e ações explícitas. Preservar as superfícies grafite, Segoe UI Variable, acento configurável e estilos de foco existentes. Não transformar o aplicativo em landing page nem adicionar cards de indicadores sem utilidade.

Tokens existentes: `Theme/Tokens.xaml`, `Theme/Styles.xaml`, `ThemeService` e `Appearance`. Base grafite #101112, painel #17181A, superfície elevada #1E2023, texto #ECEDEE, secundário #A0A4AA. Esses exemplos descrevem o tema grafite escuro; outras paletas e o modo claro continuam configuráveis.

Escala do incremento: 8/12/16/24/32 px; títulos de 26 px e texto de 12–14 px conforme o sistema existente. Alinhar informações à esquerda. Priorizar leitura e seleção em listas. Reutilizar foco, estados de botão e animações nativas; não adicionar movimento decorativo.

Protótipo anterior à implementação: [disposição ampla e estreita](ui/format-kit-wireframe.svg). É um wireframe de organização, não uma captura do aplicativo.

## Achados e prioridades

| Prioridade | Achado no código e nas capturas anteriores | Decisão |
|---|---|---|
| P1 | Barra de busca e seleção rola junto com o catálogo | Fixar os controles sobre a lista |
| P1 | Painel de ações ocupa o topo da janela estreita e empurra o catálogo | Mover ações para uma região inferior com rolagem própria |
| P1 | Nome, descrição e versão disputam três colunas | Versão abaixo do nome; texto com quebra e detalhes completos |
| P1 | Busca sem resultados fica vazia | Estado vazio com ação para limpar a busca |
| P1 | Seleções fora do filtro continuam ativas, mas pouco evidentes | Mostrar quantos marcados estão fora da busca |
| P2 | Importar/exportar e perfis podem comprimir o cabeçalho | Empilhar cabeçalho e resumo na janela estreita; perfis com rolagem horizontal |
| P2 | Mudanças na lista e na fila precisam ser compreensíveis pelo teclado/leitor de tela | Nome/status acessíveis, foco nativo e resumo anunciado de forma discreta |
| P2 | Nome e estado do trabalho compartilham área sem colunas explícitas | Separar nome/estado e expor o detalhe completo em tooltip |

Manter a regra da 0.3.4: apenas marcados, instalados e com atualização disponível entram na fila de atualização. A busca não altera a seleção.

## Skills e compatibilidade

Instaladas as cinco skills individuais do kit recomendado e cinco módulos do Design Skills Pack: acessibilidade, responsividade, tokens, casos-limite e QA visual. As orientações de frontend-design, frontend-ui-engineering e UI/UX Pro Max informam este incremento; APIs e validação são nativas de WinUI. Regras HTML/ARIA, Playwright e dimensões de telefone não comprovam uma interface Windows. Figma não foi conectado porque este projeto não fornece um arquivo de design.

## Validação

| Verificação | Resultado |
|---|---|
| Publicação local WinUI Release, win-x64 e self-contained | Aprovada em `artifacts/ui-refresh/publish` |
| Suíte existente, excluindo testes classificados Integration | 262 aprovados, zero falhas |
| Integridade XML do XAML e presença dos textos usados na página | 25 chaves presentes nos cinco arquivos de idioma |
| Renderização/capturas, 1360/800 px, tema escuro | **Aprovada em 16/11/2026** (revisão posterior, abaixo). Tema claro não capturado |

O script `tools/format-kit-layout-smoke.ps1` prepara verificação de busca vazia, seleção fora do filtro, restauração pelo teclado e capturas, usando banco isolado e sem instalar/atualizar programas. Não foi executado até o final; não existe evidência nova de renderização para este incremento. Capturas históricas fundamentam apenas o diagnóstico. A revisão final do foco foi compilada depois da suíte; altera somente a devolução do foco ao campo de busca.

Prévia local: `artifacts/ui-refresh/publish/ShiftControl.exe`. A versão de produto permanece 0.3.4; esta build local tem alterações ainda não publicadas e não substitui a release oficial. Nenhum push, instalador aplicado ou release foi feito neste incremento.

## Revisão posterior — 16/11/2026

Revisão do incremento acima e das operações elevadas do app. Build e 263 testes (excluindo `Integration`) aprovados.

**Format Kit**

- A detecção do WinGet desativava o `ScrollViewer` do catálogo, o que bloqueava a rolagem pela roda do mouse enquanto o catálogo carregava. Agora só a marcação fica bloqueada (`ContentControl` em volta da lista); a rolagem e o "Limpar busca" do estado vazio continuam ativos.
- InfoBars fechados pelo usuário continuavam no `StackPanel` com altura zero e ainda recebiam o `Spacing`. Agora são recolhidos no `Closed`.
- Janela estreita: botões de ação lado a lado e dica movida para o tooltip de "Atualizar marcados". O painel de ações ocupa ~80 px a menos e o catálogo mostra dois itens completos em 800×900 (antes, um e meio).

**Capturas reais** em `artifacts/ui-refresh/qa-20261116/` (largo, estreito, busca vazia, filtro com marcados ocultos), geradas por `tools/format-kit-visual-capture.ps1`. Para não depender do UAC, a captura usou uma cópia com manifest `asInvoker` compilada fora do repositório. Por isso o aviso "Sem administrador" aparece nas imagens; no app real, que é sempre elevado, ele não aparece. A automação confirmou: detecção concluída, estado vazio exibido, contagem de marcados fora da busca e "Limpar busca" ausente quando há resultados. Tema claro e leitor de tela não foram verificados.

**Segurança das operações elevadas**

- Atualizador: o instalador fica em `%LOCALAPPDATA%` (gravável sem elevação) e era executado por nome depois da conferência do SHA-256. `ReleaseClient.OpenVerified` agora trava o arquivo (`FileShare.Read`) e reconfere o hash pelo mesmo handle até o `Process.Start`.
- Limpeza: a exclusão por nome depois da checagem de reparse points deixava uma corrida em que uma junção trocada no Temp redirecionava a exclusão para fora da raiz. `Cleaner.DeleteWithinRoots` agora abre o arquivo como link, confere o caminho real do handle (`GetFinalPathNameByHandle`) e exclui pelo próprio handle. Há teste com junção real.
- Sem alteração: gerenciador de tarefas (handle, identidade e processo crítico já verificados) e kit importado (`ArgumentList`, fontes em whitelist, IDs validados).

**Pendente**

- `dotnet build`/`dotnet run` sem `-p:Platform=x64` (task do VS Code e README) geram executável `asInvoker`; só o caminho com plataforma x64, usado pelo `build-installer.ps1`, embute `requireAdministrator`. Em desenvolvimento, o app roda sem elevação e se comporta diferente da release.
