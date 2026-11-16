# Temas e botões

Em **Configurações → Temas e botões**, escolha uma das paletas Grafite, Oceano, Floresta, Ameixa ou Areia. Cada paleta funciona com os modos claro, escuro e seguir o Windows, sem modificar sua cor de destaque.

Os estilos Clássico, Suave, Contorno e Liquid button são independentes da paleta. A prévia é interativa, as preferências são salvas automaticamente e a mudança acontece nas telas já abertas. Liquid button usa acabamento arredondado, reflexo e uma faixa de luz que se desloca ao passar o mouse; a animação respeita a opção de animações do Windows.

Os botões usam um template estável com a animação em uma camada separada do texto. O hover não oculta o botão nem troca seu template. Alterações de aparência não alternam temporariamente entre claro e escuro. Os controles de inicialização têm sua largura reservada no layout, sem a margem negativa que cortava o interruptor.

Validação em 15/11/2026:

- 235 testes automatizados aprovados, incluindo persistência das escolhas, normalização de preferências inválidas e contraste do texto nas dez combinações de paleta e modo.
- 18 telas abertas na janela estreita; revisão visual da tela de Inicialização confirmou os interruptores inteiros.
- 40 combinações (5 paletas × 2 modos × 4 estilos) selecionadas pela interface, com capturas durante o hover. A versão final foi conferida em janela estreita; os modos e estilos também foram exercitados em janela ampla.
- Capturas e relatório ficam em `artifacts/appearance-tests/`; dados e preferências de teste ficam isolados dos dados do usuário.

Para repetir a verificação visual:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/appearance-smoke.ps1 -Executable D:\Projetos\shift-control\artifacts\publish\ShiftControl.exe -Narrow
```

Os testes não ativam/desativam programas de inicialização reais. Para usar a nova versão, encerre a anterior pelo menu **Sair** da bandeja e abra `artifacts/publish/ShiftControl.exe`.
