# Roteiro

Escopo escolhido em 15/11/2026: 34 itens, em 5 fases (cada uma usa a base da anterior).

| Fase | Conteúdo | Estado |
| --- | --- | --- |
| Base | Painel, sensores, histórico, overlay com FPS, sessões de jogo | Concluída |
| **A** | Central de desfazer + ponto de restauração; Format Kit (catálogo WinGet, runtimes oficiais, perfis, detectar/atualizar, exportar/importar, relatório); Checagem gamer (monitor, XMP/EXPO, saída de vídeo, recursos de jogo do Windows, energia); checklist pós-formatação | Concluída (ver "Validação") |
| B | Driver Center e migração antes/depois de formatar, com restauração em uma etapa | Implementada; operações reais de drivers pendentes em VM |
| C | Perfis automáticos por jogo, energia, pausa de apps, comparação e indícios térmicos | Implementada; benchmark em jogo pendente |
| D | Limpeza, armazenamento, inicialização, reparo, erros, saúde de discos, alertas e rede | Implementada; reparos e mudanças reais não executados na auditoria |
| E | Bandeja, mini-painel, alertas, instalador e atualização verificada | Implementada; publicação de release pendente |

Detalhes e limites: [auditoria e validação de 15/11/2026](VALIDATION-2026-11-15.md). Há 232 testes aprovados e navegação automatizada nas 18 telas em duas larguras. Isso não equivale a executar todas as operações administrativas no PC real.

## Fase A: validação

Feito nesta máquina:
- **Checagem:** com dados reais.
  - Monitores a 180 e 144 Hz, no máximo.
  - Saída de vídeo na RTX 5070, com o adaptador virtual do Parsec ignorado.
  - Memória a 3000 MT/s, igual ao código do módulo.
  - HAGS ligado, lido do driver via D3DKMT.
  - Windows ativado e 1 atualização pendente.
- **Format Kit:**
  - Detecção dos programas instalados, inclusive os de fora do WinGet (Chrome, Discord) e o WebView2 (pelo registro).
  - Atualizações disponíveis (Teams, WinRAR, VC++, .NET 8).
  - Os 33 IDs do catálogo existem no WinGet.
- **Instalação, cancelamento, falhas e registro na Central de desfazer:** testados com um executor simulado.
- **Telas:** revisadas por captura.

Ainda não feito com programas reais:
- uma instalação e a reversão pela Central de desfazer (o app não instalou nada sem pedido);
- a criação de ponto de restauração (exige administrador).
