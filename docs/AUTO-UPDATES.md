# Releases e atualizacao automatica

A partir da versao 0.3.0, o app consulta a ultima release estavel publica do repositorio `Ericklucenaa/shift-control` 20 segundos depois de abrir e a cada 2 minutos enquanto estiver aberto, inclusive na bandeja. Nao ha push instantaneo do GitHub para os computadores: a deteccao depende desse intervalo, da internet e dos limites da API publica.

O instalador e baixado do GitHub e conferido com o SHA-256 informado pelo proprio GitHub. Apenas URLs HTTPS das releases oficiais sao aceitas. Downloads incompletos ou com hash divergente nunca sao executados. Releases draft, pre-release e versoes iguais/anteriores sao ignoradas.

Com **Instalar atualizacoes automaticamente** habilitado (padrao), o app aguarda jogos detectados, gravacao de sessoes, Game Boost, operacoes de manutencao e dialogos terminarem; avisa na bandeja, aguarda 30 segundos, encerra normalmente, instala silenciosamente na mesma pasta e reabre. Preferencias e historico ficam fora da pasta de instalacao. O usuario pode desativar a instalacao automatica ou toda verificacao em Configuracoes. Desativar durante o aviso cancela essa tentativa.

O modo portatil atualiza a propria pasta sem criar atalhos nem registrar desinstalador. A pasta precisa permitir escrita. Instancias de desenvolvimento/teste com `--data-dir` nao fazem instalacao automatica.

Quem usa uma versao anterior deve instalar a 0.3.0 uma vez: as versoes antigas apenas notificavam novas releases. Usuarios offline ou com o app fechado recebem a atualizacao na proxima abertura/conexao. A consulta envia ao GitHub requisicoes de versao/download (com IP e user agent), sem inventario, historico ou sensores. Nao se distribui token GitHub no aplicativo; as releases precisam ser publicas.

## Publicar as proximas versoes

1. Altere `<Version>` em `Directory.Build.props` para uma versao maior, como `0.3.1`.
2. Envie o commit para `main`.
3. O workflow `.github/workflows/release.yml` executa testes, compila o instalador e ZIP portatil, gera SHA-256 e publica a release `v0.3.1` apenas depois de carregar os arquivos. O GitHub Actions precisa estar habilitado.

Repetir uma versao ja publicada nao substitui os binarios: aumente a versao. O workflow pode ser executado manualmente em Actions. Os testes de integracao com hardware real rodam localmente; o CI exclui essa categoria por depender de dispositivos fisicos.

## Validacao local

`dotnet test --project tests/ShiftControl.Tests -c Release` verifica comparacao de versoes, selecao de instalador oficial, hash correto/incorreto, limpeza de download corrompido, bloqueio durante operacoes e argumentos de instalacao em pasta com espacos.

`tools/update-installer-smoke.ps1` instala e atualiza em uma pasta isolada em `artifacts`, em modo portatil, sem alterar a instalacao, os atalhos ou as preferencias do usuario.

Referencias: [GitHub Releases API](https://docs.github.com/en/rest/releases/releases), [NSIS command line](https://nsis.sourceforge.io/Docs/Chapter3.html).
