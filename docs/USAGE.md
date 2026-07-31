# Guia de uso

## 1. Instalar e abrir

Baixe o executável portátil da página de Releases ou compile o projeto. O
programa solicita elevação de administrador porque precisa montar ISOs e usar
DISM para editar imagens WIM/ESD.

Também é necessário ter `oscdimg.exe`. O executável publicado inclui uma cópia
para a geração da ISO; a interface também permite apontar para o `oscdimg.exe`
do Windows ADK.

## 2. Selecionar a ISO

Escolha a ISO original e um nome diferente para a ISO de saída. A original é
montada apenas para leitura e nunca é editada diretamente.

Depois da seleção, o aplicativo lista as edições encontradas em
`install.wim`/`install.esd`. Todas ficam marcadas inicialmente. Desmarque as
edições que não deseja manter.

## 3. Inserir um arquivo de resposta

Clique em **Escolher XML**. Se ainda não tiver um arquivo, use o link integrado
para o [Windows Unattend Generator](https://schneegans.de/windows/unattend-generator/).

Destinos disponíveis:

- **Raiz:** cria `Autounattend.xml`, detectado durante o boot do Setup;
- **sources:** cria `sources\Autounattend.xml`;
- **Windows\Panther:** entrega `sources\$OEM$\$$\Panther\unattend.xml`, que chega a `C:\Windows\Panther\unattend.xml` no Windows instalado;
- combinações dos destinos anteriores.

O conteúdo do XML é validado e copiado sem ser reescrito.

## 4. Bypass do Windows 11

Deixe **Aplicar bypass de requisitos do Windows 11** desmarcado para ISOs do
Windows 7, 8.1, 10 ou para uma personalização somente com `unattend.xml`.

Quando ativado, o aplicativo injeta os valores `LabConfig` selecionados em
todos os índices de `sources\boot.wim`. Esse recurso não emula hardware e não
remove limitações arquiteturais ou instruções ausentes do processador.

## 5. Criar a ISO

Clique em **Criar ISO modificada**. O processo copia a mídia, aplica as
alterações, recria a imagem inicializável BIOS + UEFI e limpa os temporários.
Se já existir um arquivo de destino, ele é movido temporariamente e restaurado
se a operação falhar.

## Diagnóstico rápido

- **oscdimg não encontrado:** instale o Windows ADK com Deployment Tools ou use
  **Localizar oscdimg**;
- **espaço insuficiente:** mantenha pelo menos duas a três vezes o tamanho da
  ISO livres no volume de destino;
- **XML inválido:** valide o arquivo no Windows SIM ou no gerador antes de
  selecionar;
- **instalação não inicia em hardware antigo:** bypasses não adicionam suporte
  a CPU 32-bit, instruções ausentes ou firmware incompatível.
