# Entrega 0.1.1

Correção dos interruptores cortados na Inicialização, animação de hover em camada separada e cinco paletas combináveis com quatro estilos de botão, incluindo Liquid button, em claro/escuro.

Validação da versão entregue:

- 235 testes automatizados aprovados; zero falhas e zero ignorados.
- 40 combinações de aparência selecionadas pela interface, com capturas, verificação de cores distintas e persistência das preferências.
- 18 telas e o mini-painel/bandeja aprovados na navegação automatizada.
- Perfil por executável salvo e teste de ping local com 40 respostas de 40 envios, conferidos no SQLite isolado.
- Nenhum erro ou falha fatal no log final da interface.
- Instalador compilado com NSIS e pacote portátil conferido por CRC; SHA-256 de ambos verificado.

Arquivos em `artifacts/release-0.1.1/`:

- `ShiftControl-0.1.1-setup.exe` — instalador.
- `ShiftControl-0.1.1-portable.zip` — extrair toda a pasta e abrir `ShiftControl/ShiftControl.exe`.
- `publish/ShiftControl.exe` — executável com dependências na mesma pasta.
- `validation.json` — resumo e hashes da validação.

Para reproduzir o empacotamento:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-installer.ps1 -OutputDirectory D:\Projetos\shift-control\artifacts\release-0.1.1
python tools/package-portable.py artifacts/release-0.1.1 --version 0.1.1
```

A instalação/desinstalação no Windows real não foi executada nesta validação. Os testes usaram dados isolados, preservaram a instância aberta pelo usuário e não alteraram seus programas de inicialização.
