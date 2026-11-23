# Shift Control 0.4.0

## Hardware → Componentes, refeita

- **Resumo do computador** numa linha (ex.: "Ryzen 7 5800X3D · RTX 5070 12 GB · 32 GB DDR4 3000 MT/s · TUF GAMING B550M-PLUS") e o botão **Copiar especificações**, que copia a lista de peças em texto para colar em fóruns, grupos ou num pedido de suporte.
- **Processador:** núcleos, threads, clock, soquete e cache L3.
- **Placa de vídeo:** memória de vídeo real (o Windows informa no máximo 4 GB pelo WMI; o valor vem do registro do driver), versão e data do driver, resolução e taxa de atualização atuais.
- **Memória RAM:** total e tipo (DDR4/DDR5), slots ocupados, e cada pente com slot, velocidade, fabricante e modelo. Dois avisos práticos:
  - memória rodando abaixo da velocidade nominal dos módulos (XMP/EXPO desligado na BIOS);
  - pentes de modelos diferentes, causa comum de travamentos e desligamentos.
- **Placa-mãe e BIOS** (fabricante, versão e data), **armazenamento** (SSD ou HD, NVMe/SATA/USB), **monitores** (nome pelo EDID), **rede** (velocidade do link) e **áudio**.
- Dispositivos virtuais ficam de fora: tela do Parsec, túneis de VPN (ZeroTier, Tailscale) e áudio virtual.
- Os cartões ficam em duas colunas, cada um com a altura que precisa, e viram uma coluna em janelas estreitas.

## Sistema → Programas instalados

- **Filtro por disco** e coluna **Disco**: veja o que está instalado no C:, D:, E:… O disco vem da pasta de instalação, do ícone, do desinstalador ou, para instaladores MSI sem caminho, de um arquivo que o Windows Installer registrou para o produto.
- **Tamanho de quem não informa:** muitos programas (jogos da Steam, drivers, ferramentas de desenvolvimento) não informam o tamanho ao Windows e ficavam no fim de "Maior tamanho". Agora a pasta deles é medida em segundo plano; o valor medido tem uma dica explicando a origem.
  - Pastas amplas (Arquivos de Programas, AppData, Windows, raiz do disco) nunca são medidas.
  - Programas instalados dentro da pasta de outro (ex.: jogos dentro da pasta da Steam) não são contados duas vezes.
  - Cada pasta tem um limite de tempo; quem passa dele continua sem tamanho, em vez de um valor parcial.

## Validação

- 309 testes automatizados aprovados, incluindo leitura real das peças deste computador e medição de pastas com programas aninhados.
- Telas conferidas em captura, em janela larga e estreita.
