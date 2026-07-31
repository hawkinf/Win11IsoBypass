# Windows ISO Customizer

Aplicativo WPF em C# que cria uma **cópia inicializável** de uma ISO oficial do Windows (7, 8.1, 10, 11 ou compatível) e permite inserir um arquivo de resposta `unattend.xml`.

Freeware desenvolvido por **Aguinaldo Liesack Baptistini**.

Projeto: <https://github.com/hawkinf/Win11IsoBypass>

Documentação: [guia de uso](docs/USAGE.md) · [arquitetura](docs/ARCHITECTURE.md) · [changelog](CHANGELOG.md) · [licença](LICENSE)

![Build](https://github.com/hawkinf/Win11IsoBypass/actions/workflows/build.yml/badge.svg)

## O que ele faz

- preserva a ISO original;
- injeta opcionalmente `LabConfig` em todos os índices de `sources\boot.wim` (somente quando o bypass do Windows 11 é ativado);
- permite ignorar TPM 2.0, Secure Boot, RAM, CPU e armazenamento;
- inclui `autounattend.xml` como segunda camada de compatibilidade;
- aceita um `unattend.xml` próprio e o copia para a raiz, `sources` e/ou `Windows\Panther`, preservando o conteúdo original;
- detecta as edições dentro de `install.wim`/`install.esd` e permite manter somente os índices selecionados;
- recria uma ISO híbrida, inicializável em BIOS e UEFI;
- limpa montagens e arquivos temporários após concluir, falhar ou cancelar.

## Pré-requisitos

- Windows 10 ou 11 x64;
- privilégios de administrador;
- `oscdimg.exe` embutido no executável desta versão (ou Windows ADK com **Deployment Tools** para substituir/localizar manualmente);
- espaço livre recomendado: aproximadamente duas vezes o tamanho da ISO mais 1 GB; a edição direta de `install.wim`/`install.esd` pode exigir três vezes.

Download oficial do Windows ADK: <https://learn.microsoft.com/windows-hardware/get-started/adk-install>

## Compilar

```powershell
dotnet build -c Release
```

O projeto usa .NET 10 e WPF, portanto a compilação deve ser feita em Windows.
O workflow do GitHub Actions executa a compilação automaticamente em cada
push ou pull request.

## Gerar executável independente

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Arquivo de resposta

O XML é validado antes da cópia. Quando um arquivo próprio é selecionado, ele não é sobrescrito pelo `autounattend.xml` gerado pelo programa. A opção padrão é `Windows\Panther\unattend.xml`, entregue pela estrutura `sources\$OEM$\$$\Panther` durante a instalação. Também é possível usar a raiz da ISO (`autounattend.xml`), `sources\autounattend.xml` ou combinar os destinos. Se nenhum bypass for selecionado, o aplicativo pode apenas inserir o arquivo de resposta, sem modificar `boot.wim`.

Quando a opção **Aplicar também dentro de install.wim/install.esd** está ativa, cada índice do `install.wim` é montado e recebe diretamente `Windows\Panther\unattend.xml` e os registros de compatibilidade selecionados. Para `install.esd`, o programa não monta o arquivo para escrita: quando necessário, exporta somente os índices selecionados para um novo ESD, entrega o XML pela estrutura `$OEM$` e mantém o bypass no `boot.wim`.

As imagens aparecem após a seleção da ISO. Todas começam marcadas; as desmarcadas são exportadas para fora da imagem final durante a criação do novo ISO.

## Limitações

Este método é voltado à instalação iniciada pela mídia. Ele remove bloqueios do instalador, mas não emula recursos físicos. Windows 11 continua exigindo arquitetura x64, e versões recentes podem não iniciar em processadores sem determinadas instruções. Instalações em hardware não homologado não têm garantia de suporte ou atualizações da Microsoft.

## Créditos e links

- Autor: Aguinaldo Liesack Baptistini;
- Projeto: <https://github.com/hawkinf/Win11IsoBypass>;
- Gerador de `unattend.xml`: <https://schneegans.de/windows/unattend-generator/>;
- `oscdimg`: ferramenta do Windows ADK, documentada pela Microsoft.
