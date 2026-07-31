# Arquitetura técnica

## Pipeline

1. `Mount-DiskImage` monta a ISO original.
2. `robocopy` copia a mídia para um diretório de trabalho.
3. O `boot.wim` é montado com DISM quando o bypass do Windows 11 está ativo.
4. Hives SYSTEM offline recebem os valores `HKLM\SYSTEM\Setup\LabConfig`.
5. O XML de resposta é colocado nos destinos escolhidos, incluindo a rota
   `$OEM$` para `Windows\Panther`.
6. Índices selecionados de `install.wim`/`install.esd` são preservados ou
   exportados conforme a seleção do usuário.
7. `oscdimg` recria uma ISO híbrida BIOS + UEFI.
8. Montagens e diretórios temporários são removidos em `finally`, inclusive em
   cancelamento ou erro.

## Componentes

- `MainWindow.xaml`: interface e fluxo de seleção;
- `Services/IsoBuilder.cs`: pipeline de montagem, DISM, WIM/ESD e ISO;
- `Services/ProcessRunner.cs`: execução segura de DISM, REG, PowerShell,
  Robocopy e oscdimg;
- `Services/ToolLocator.cs`: descoberta do oscdimg no ADK/PATH;
- `Assets/`: SVG, PNG e ICO do aplicativo;
- `AboutWindow.xaml`: informações de freeware, autoria e GitHub.

## Segurança operacional

O programa não baixa ISOs, não altera a ISO original e não executa comandos
fornecidos pelo XML fora do fluxo do Windows Setup. Ainda assim, um
`unattend.xml` pode conter configurações destrutivas, como limpeza de discos,
particionamento ou criação de contas. Revise o XML antes de usá-lo.
