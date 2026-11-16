# Entrega 0.1.2 — criação de pacotes de migração

O seletor compartilhado de pastas executava um diálogo COM via `Task.Run` e engolia falhas, retornando `null`. A alternativa UWP usada no fallback não suporta aplicativos elevados. Para o usuário, uma falha nessa etapa tinha o mesmo efeito de cancelar: o botão Criar pacote parecia não fazer nada.

A seleção agora usa `Microsoft.Windows.Storage.Pickers.FolderPicker` do Windows App SDK, associada ao `WindowId` da janela principal. A API suporta aplicações desktop elevadas. Somente cancelamento retorna `null`; falhas passam para a mensagem da página e são registradas no log.

Outras correções no mesmo fluxo:

- mensagem explícita quando nenhuma opção está selecionada;
- proteção contra cliques simultâneos durante a seleção de pasta;
- cada pacote recebe uma pasta com segundos e identificador único, evitando sobrescrever um pacote criado no mesmo minuto;
- seletores de criação e abertura compartilham a correção;
- identificadores de acessibilidade para testar os controles diretamente.

## Validação

- Compilação Release sem erros.
- 235 testes automatizados aprovados, zero falhas e zero ignorados.
- Teste real via UI Automation: nenhuma opção selecionada mostra mensagem; cancelar seletor não cria pacote; duas criações consecutivas geram duas pastas; manifesto e preferências contêm os dados esperados; Abrir pacote reconhece o pacote gerado; cancelar consentimento de Wi-Fi não exporta arquivos.
- A criação real testada incluiu somente preferências. Não houve exportação de senhas, alteração de drivers ou instalação de programas.
- A automação rodou como usuário comum com dados isolados. A compatibilidade com elevação é fornecida pela API oficial; o teste completo elevado não foi executado nesta auditoria.

Reprodução:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/migration-smoke.ps1 -Executable D:\Projetos\shift-control\artifacts\release-0.1.2\publish\ShiftControl.exe
```

Entregáveis em `artifacts/release-0.1.2/`: instalador, publicação autônoma e ZIP portátil, com arquivos SHA-256.

Referências: [seletores de pasta do Windows App SDK](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.windows.storage.pickers), [requisitos COM do diálogo antigo](https://learn.microsoft.com/en-us/windows/win32/shell/common-file-dialog).
