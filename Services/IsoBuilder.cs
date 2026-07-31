using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Win11IsoBypass.Services;

public sealed class IsoBuilder
{
    public async Task BuildAsync(
        string sourceIso,
        string outputIso,
        string oscdimgPath,
        BypassOptions options,
        string? customUnattendPath,
        UnattendPlacement unattendPlacement,
        bool applyToInstallImages,
        IReadOnlyCollection<int> selectedInstallIndexes,
        IProgress<BuildProgress> progress,
        CancellationToken cancellationToken)
    {
        var outputFolder = Path.GetDirectoryName(Path.GetFullPath(outputIso))!;
        Directory.CreateDirectory(outputFolder);
        var spaceMultiplier = applyToInstallImages ? 3.0 : 2.0;
        EnsureFreeSpace(sourceIso, outputFolder, spaceMultiplier);

        // O DISM exige que o diretório de montagem esteja em um volume NTFS
        // que aceite pontos de reparse. O destino da ISO pode estar em FAT,
        // exFAT, rede ou outro volume incompatível; por isso o workspace fica
        // sempre no temporário local do Windows.
        var localWorkspace = Path.Combine(Path.GetTempPath(), "WindowsIsoCustomizer");
        Directory.CreateDirectory(localWorkspace);
        if ((File.GetAttributes(localWorkspace) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A pasta temporária local está configurada como ponto de reparse e não pode ser usada pelo DISM.");
        EnsureFreeSpace(sourceIso, localWorkspace, spaceMultiplier);

        var workRoot = Path.Combine(localWorkspace, $"work_{Guid.NewGuid():N}");
        var mediaFolder = Path.Combine(workRoot, "media");
        var mountFolder = Path.Combine(workRoot, "mount");
        var hiveName = $"W11ISO_{Guid.NewGuid():N}";
        string? mountedDrive = null;
        var mountedByApp = false;
        var imageMounted = false;
        var hiveLoaded = false;
        string? outputBackup = null;
        var buildSucceeded = false;

        Directory.CreateDirectory(mediaFolder);
        Directory.CreateDirectory(mountFolder);

        try
        {
            Report(2, "Montando a ISO original", "Montando a imagem somente para leitura...");
            var mountedIso = await MountIsoAsync(sourceIso, cancellationToken);
            mountedDrive = mountedIso.Drive;
            mountedByApp = mountedIso.MountedByApp;

            Report(7, "Copiando os arquivos da ISO", $"Origem montada em {mountedDrive}");
            await ProcessRunner.RunAsync("robocopy.exe",
                [mountedDrive, mediaFolder, "/E", "/COPY:DAT", "/DCOPY:DAT", "/R:2", "/W:1", "/NFL", "/NDL", "/NP"],
                line => Report(12, "Copiando os arquivos da ISO", line), cancellationToken, code => code is >= 0 and <= 7);

            if (mountedByApp)
                await DismountIsoAsync(sourceIso, CancellationToken.None);
            mountedByApp = false;
            mountedDrive = null;

            // Arquivos vindos de uma ISO normalmente mantêm o atributo
            // somente leitura. O DISM precisa gravar nos WIM durante o
            // mount/commit, portanto normalize os atributos antes de usá-los.
            foreach (var imageFile in Directory.EnumerateFiles(Path.Combine(mediaFolder, "sources"), "install.*", SearchOption.TopDirectoryOnly)
                         .Concat([Path.Combine(mediaFolder, "sources", "boot.wim")]))
            {
                if (File.Exists(imageFile)) File.SetAttributes(imageFile, FileAttributes.Normal);
            }

            var hasCustomUnattend = !string.IsNullOrWhiteSpace(customUnattendPath);
            var hasBypass = options.RegistryValues.Count > 0;
            if (hasBypass)
            {
                var bootWim = Path.Combine(mediaFolder, "sources", "boot.wim");
                if (!File.Exists(bootWim))
                    throw new InvalidOperationException("A ISO não contém sources\\boot.wim, necessário para aplicar os bypasses.");

                File.SetAttributes(bootWim, FileAttributes.Normal);
                var indexes = await GetWimIndexesAsync(bootWim, cancellationToken);
                if (indexes.Count == 0)
                    throw new InvalidOperationException("Nenhum índice foi encontrado em sources\\boot.wim.");

                for (var position = 0; position < indexes.Count; position++)
                {
                    var index = indexes[position];
                    var basePercent = 18 + (position * 48 / indexes.Count);
                    Report(basePercent, $"Modificando boot.wim — índice {index}", "Montando o ambiente de instalação...");
                    imageMounted = true;
                    await RunDismAsync(["/Mount-Image", $"/ImageFile:{bootWim}", $"/Index:{index}", $"/MountDir:{mountFolder}"], cancellationToken,
                        LogAt(basePercent + 3, $"Montando boot.wim — índice {index}"));

                    var systemHive = Path.Combine(mountFolder, "Windows", "System32", "config", "SYSTEM");
                    await ProcessRunner.RunAsync("reg.exe", ["load", $"HKLM\\{hiveName}", systemHive], LogAt(basePercent + 4, $"Injetando bypasses — índice {index}"), cancellationToken);
                    hiveLoaded = true;

                    foreach (var value in options.RegistryValues)
                    {
                        await ProcessRunner.RunAsync("reg.exe",
                            ["add", $"HKLM\\{hiveName}\\Setup\\LabConfig", "/v", value, "/t", "REG_DWORD", "/d", "1", "/f"],
                            LogAt(basePercent + 7, $"Injetando bypasses — índice {index}"), cancellationToken);
                    }

                    await ProcessRunner.RunAsync("reg.exe", ["unload", $"HKLM\\{hiveName}"], LogAt(basePercent + 9, $"Finalizando índice {index}"), CancellationToken.None);
                    hiveLoaded = false;

                    Report(basePercent + 12, $"Salvando boot.wim — índice {index}", "Gravando as alterações...");
                    await RunDismAsync(["/Unmount-Image", $"/MountDir:{mountFolder}", "/Commit"], cancellationToken,
                        LogAt(basePercent + 15, $"Salvando boot.wim — índice {index}"));
                    imageMounted = false;
                }
            }

            Report(72, "Adicionando arquivo de resposta", "Criando autounattend.xml como camada adicional...");
            if (hasCustomUnattend)
            {
                if (unattendPlacement is UnattendPlacement.Root or UnattendPlacement.RootAndPanther or UnattendPlacement.All)
                    File.Copy(customUnattendPath!, Path.Combine(mediaFolder, "autounattend.xml"), true);
                if (unattendPlacement is UnattendPlacement.Sources or UnattendPlacement.All)
                    File.Copy(customUnattendPath!, Path.Combine(mediaFolder, "sources", "autounattend.xml"), true);
                if (unattendPlacement is UnattendPlacement.Panther or UnattendPlacement.RootAndPanther or UnattendPlacement.All)
                {
                    var pantherTarget = Path.Combine(mediaFolder, "sources", "$OEM$", "$$", "Panther");
                    Directory.CreateDirectory(pantherTarget);
                    File.Copy(customUnattendPath!, Path.Combine(pantherTarget, "unattend.xml"), true);
                }
                Report(74, "Adicionando arquivo de resposta", $"Arquivo copiado em: {DescribePlacement(unattendPlacement)}");
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(mediaFolder, "autounattend.xml"), CreateUnattend(options), new UTF8Encoding(false), cancellationToken);
            }

            var hasPantherTarget = hasCustomUnattend && IncludesPanther(unattendPlacement);
            var installWim = Path.Combine(mediaFolder, "sources", "install.wim");
            var installEsd = Path.Combine(mediaFolder, "sources", "install.esd");
            var directInstallModification = applyToInstallImages &&
                ((hasCustomUnattend && hasPantherTarget) || hasBypass) &&
                File.Exists(installWim);

            if ((selectedInstallIndexes.Count > 0 || directInstallModification) && (File.Exists(installWim) || File.Exists(installEsd)))
            {
                var installImage = File.Exists(installWim) ? installWim : installEsd;
                await FilterInstallImagesAsync(installImage, selectedInstallIndexes, progress, cancellationToken, directInstallModification);
            }

            if (applyToInstallImages && ((hasCustomUnattend && hasPantherTarget) || hasBypass))
            {

                if (File.Exists(installWim))
                {
                    await ApplyToInstallWimAsync(installWim, mountFolder, customUnattendPath, hasPantherTarget, options, progress, cancellationToken);
                }
                else if (File.Exists(installEsd))
                {
                    progress.Report(new BuildProgress(76, "Preparando imagem ESD", "ESD é somente leitura; o XML será entregue por sources\\$OEM$\\$$\\Panther."));
                }
            }

            var biosBoot = Path.Combine(mediaFolder, "boot", "etfsboot.com");
            var uefiBoot = Path.Combine(mediaFolder, "efi", "microsoft", "boot", "efisys.bin");
            if (!File.Exists(biosBoot) || !File.Exists(uefiBoot))
                throw new InvalidOperationException("A ISO não contém os arquivos de inicialização BIOS/UEFI esperados.");

            if (File.Exists(outputIso))
            {
                outputBackup = $"{outputIso}.backup_{Guid.NewGuid():N}";
                File.Move(outputIso, outputBackup);
            }

            Report(78, "Gerando a nova ISO inicializável", "Criando mídia híbrida BIOS + UEFI...");
            var bootData = $"-bootdata:2#p0,e,b{biosBoot}#pEF,e,b{uefiBoot}";
            await ProcessRunner.RunAsync(oscdimgPath,
                ["-m", "-o", "-u2", "-udfver102", "-lWIN11_BYPASS", bootData, mediaFolder, outputIso],
                line => Report(ParseOscdimgPercent(line) ?? 88, "Gerando a nova ISO inicializável", line), cancellationToken);

            if (!File.Exists(outputIso) || new FileInfo(outputIso).Length < 1_000_000)
                throw new InvalidOperationException("O arquivo de destino não foi gerado corretamente.");

            buildSucceeded = true;
            if (outputBackup is not null) File.Delete(outputBackup);
            Report(100, "ISO criada com sucesso", $"Arquivo: {outputIso}");
        }
        finally
        {
            if (hiveLoaded)
                await TryRunAsync("reg.exe", ["unload", $"HKLM\\{hiveName}"]);
            if (imageMounted)
                await TryRunAsync("dism.exe", ["/English", "/Unmount-Image", $"/MountDir:{mountFolder}", "/Discard"]);
            if (mountedByApp)
                await TryDismountIsoAsync(sourceIso);

            if (!buildSucceeded)
            {
                try
                {
                    if (File.Exists(outputIso)) File.Delete(outputIso);
                    if (outputBackup is not null && File.Exists(outputBackup))
                        File.Move(outputBackup, outputIso);
                }
                catch { }
            }

            TryDeleteDirectory(workRoot);
        }

        void Report(int percent, string status, string? detail = null) =>
            progress.Report(new BuildProgress(Math.Clamp(percent, 0, 100), status, detail));

        Action<string> LogAt(int percent, string status) => line => Report(percent, status, line);
    }

    private static void EnsureFreeSpace(string sourceIso, string outputFolder, double multiplier)
    {
        var root = Path.GetPathRoot(outputFolder);
        if (string.IsNullOrWhiteSpace(root)) return;
        var drive = new DriveInfo(root);
        var isoSize = new FileInfo(sourceIso).Length;
        var required = (long)(isoSize * multiplier) + 1L * 1024 * 1024 * 1024;
        if (drive.AvailableFreeSpace < required)
            throw new InvalidOperationException($"Espaço insuficiente em {root}. Disponível: {FormatBytes(drive.AvailableFreeSpace)}; necessário: {FormatBytes(required)}. Feche outros programas, escolha outro destino/workspace ou desmarque a edição direta de install.wim/install.esd.");
    }

    private static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d / 1024d:0.0} GB";

    private static async Task<MountedIso> MountIsoAsync(string isoPath, CancellationToken cancellationToken)
    {
        var escaped = isoPath.Replace("'", "''");
        var script = $"$ErrorActionPreference='Stop'; $img=Get-DiskImage -ImagePath '{escaped}' -ErrorAction SilentlyContinue; $wasAttached=[bool]($img -and $img.Attached); if (-not $wasAttached) {{ $img=Mount-DiskImage -ImagePath '{escaped}' -PassThru }}; $vol=$null; for ($i=0; $i -lt 20 -and -not $vol.DriveLetter; $i++) {{ $vol=$img | Get-Volume -ErrorAction SilentlyContinue; if (-not $vol.DriveLetter) {{ Start-Sleep -Milliseconds 250 }} }}; if (-not $vol.DriveLetter) {{ if (-not $wasAttached) {{ Dismount-DiskImage -ImagePath '{escaped}' -ErrorAction SilentlyContinue }}; throw 'A ISO foi montada sem letra de unidade.' }}; Write-Output ($vol.DriveLetter + ':\\|' + (-not $wasAttached))";
        var result = await ProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], null, cancellationToken);
        var match = Regex.Match(result.Output, @"(?im)^([A-Za-z]:\\)\|(True|False)\s*$");
        if (!match.Success) throw new InvalidOperationException("Não foi possível determinar a unidade onde a ISO foi montada.");
        return new MountedIso(match.Groups[1].Value, bool.Parse(match.Groups[2].Value));
    }

    private static async Task DismountIsoAsync(string isoPath, CancellationToken cancellationToken)
    {
        var escaped = isoPath.Replace("'", "''");
        await ProcessRunner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", $"Dismount-DiskImage -ImagePath '{escaped}' -ErrorAction Stop"], null, cancellationToken);
    }

    private static async Task<IReadOnlyList<int>> GetWimIndexesAsync(string bootWim, CancellationToken cancellationToken)
    {
        var result = await RunDismAsync(["/Get-WimInfo", $"/WimFile:{bootWim}"], cancellationToken);
        return Regex.Matches(result.Output, @"(?im)^Index\s*:\s*(\d+)\s*$")
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct().ToArray();
    }

    private static Task<ProcessResult> RunDismAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        Action<string>? onOutput = null) =>
        ProcessRunner.RunAsync("dism.exe", ["/English", .. arguments], onOutput, cancellationToken);

    private static string CreateUnattend(BypassOptions options)
    {
        var commands = options.RegistryValues.Select((value, index) => $"""
                <RunSynchronousCommand wcm:action="add">
                    <Order>{index + 1}</Order>
                    <Path>reg.exe add HKLM\SYSTEM\Setup\LabConfig /v {value} /t REG_DWORD /d 1 /f</Path>
                    <Description>Ignorar verificação: {value}</Description>
                </RunSynchronousCommand>
""");

        return $$"""
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend">
    <settings pass="windowsPE">
        <component name="Microsoft-Windows-Setup" processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
            <RunSynchronous>
{{string.Join(Environment.NewLine, commands)}}
            </RunSynchronous>
        </component>
    </settings>
</unattend>
""";
    }

    private static string DescribePlacement(UnattendPlacement placement) => placement switch
    {
        UnattendPlacement.Sources => "sources\\autounattend.xml",
        UnattendPlacement.Panther => "Windows\\Panther\\unattend.xml (via sources\\$OEM$)",
        UnattendPlacement.RootAndPanther => "autounattend.xml e Windows\\Panther\\unattend.xml",
        UnattendPlacement.All => "raiz, sources e Windows\\Panther",
        _ => "autounattend.xml"
    };

    private static bool IncludesPanther(UnattendPlacement placement) =>
        placement is UnattendPlacement.Panther or UnattendPlacement.RootAndPanther or UnattendPlacement.All;

    private static async Task ApplyToInstallWimAsync(
        string installWim,
        string mountFolder,
        string? customUnattendPath,
        bool applyPanther,
        BypassOptions options,
        IProgress<BuildProgress> progress,
        CancellationToken cancellationToken)
    {
        var indexes = await GetWimIndexesAsync(installWim, cancellationToken);
        if (indexes.Count == 0) return;

        var hiveName = $"W11ISO_INSTALL_{Guid.NewGuid():N}";
        for (var position = 0; position < indexes.Count; position++)
        {
            var index = indexes[position];
            var basePercent = 76 + (position * 10 / indexes.Count);
            var mounted = false;
            var hiveLoaded = false;
            try
            {
                progress.Report(new BuildProgress(basePercent, $"Modificando install.wim — índice {index}", "Montando a imagem instalada..."));
                mounted = true;
                await RunDismAsync(["/Mount-Image", $"/ImageFile:{installWim}", $"/Index:{index}", $"/MountDir:{mountFolder}"], cancellationToken);

                if (applyPanther && customUnattendPath is not null)
                {
                    var panther = Path.Combine(mountFolder, "Windows", "Panther");
                    Directory.CreateDirectory(panther);
                    File.Copy(customUnattendPath, Path.Combine(panther, "unattend.xml"), true);
                }

                if (options.RegistryValues.Count > 0)
                {
                    var systemHive = Path.Combine(mountFolder, "Windows", "System32", "config", "SYSTEM");
                    await ProcessRunner.RunAsync("reg.exe", ["load", $"HKLM\\{hiveName}", systemHive], null, cancellationToken);
                    hiveLoaded = true;
                    foreach (var value in options.RegistryValues)
                    {
                        await ProcessRunner.RunAsync("reg.exe", ["add", $"HKLM\\{hiveName}\\Setup\\LabConfig", "/v", value, "/t", "REG_DWORD", "/d", "1", "/f"], null, cancellationToken);
                    }
                    await ProcessRunner.RunAsync("reg.exe", ["add", $"HKLM\\{hiveName}\\Setup\\MoSetup", "/v", "AllowUpgradesWithUnsupportedTPMOrCPU", "/t", "REG_DWORD", "/d", "1", "/f"], null, cancellationToken);
                    await ProcessRunner.RunAsync("reg.exe", ["unload", $"HKLM\\{hiveName}"], null, CancellationToken.None);
                    hiveLoaded = false;
                }

                progress.Report(new BuildProgress(basePercent + 5, $"Salvando install.wim — índice {index}", "Gravando alterações..."));
                await RunDismAsync(["/Unmount-Image", $"/MountDir:{mountFolder}", "/Commit"], cancellationToken);
                mounted = false;
            }
            finally
            {
                if (hiveLoaded) await TryRunAsync("reg.exe", ["unload", $"HKLM\\{hiveName}"]);
                if (mounted) await TryRunAsync("dism.exe", ["/English", "/Unmount-Image", $"/MountDir:{mountFolder}", "/Discard"]);
            }
        }
    }

    public async Task<IReadOnlyList<InstallImageInfo>> GetInstallImagesAsync(string sourceIso, CancellationToken cancellationToken)
    {
        var mounted = await MountIsoAsync(sourceIso, cancellationToken);
        try
        {
            var wim = Path.Combine(mounted.Drive, "sources", "install.wim");
            var esd = Path.Combine(mounted.Drive, "sources", "install.esd");
            var imagePath = File.Exists(wim) ? wim : File.Exists(esd) ? esd : null;
            if (imagePath is null) return [];

            var result = await RunDismAsync(["/Get-WimInfo", $"/WimFile:{imagePath}"], cancellationToken);
            var format = Path.GetExtension(imagePath).Equals(".esd", StringComparison.OrdinalIgnoreCase) ? "install.esd" : "install.wim";
            return ParseInstallImages(result.Output, format);
        }
        finally
        {
            if (mounted.MountedByApp) await TryDismountIsoAsync(sourceIso);
        }
    }

    private static IReadOnlyList<InstallImageInfo> ParseInstallImages(string output, string format)
    {
        var images = new List<InstallImageInfo>();
        var blocks = Regex.Matches(output, @"(?ms)^Index\s*:\s*(\d+).*?(?=^Index\s*:|\z)");
        foreach (Match block in blocks)
        {
            var index = int.Parse(block.Groups[1].Value, CultureInfo.InvariantCulture);
            var name = Regex.Match(block.Value, @"(?im)^Name\s*:\s*(.+?)\s*$").Groups[1].Value.Trim();
            var description = Regex.Match(block.Value, @"(?im)^Description\s*:\s*(.+?)\s*$").Groups[1].Value.Trim();
            images.Add(new InstallImageInfo
            {
                Index = index,
                Name = string.IsNullOrWhiteSpace(name) ? "Imagem Windows" : name,
                Description = description,
                Format = format
            });
        }
        return images;
    }

    private static async Task FilterInstallImagesAsync(
        string imagePath,
        IReadOnlyCollection<int> selectedIndexes,
        IProgress<BuildProgress> progress,
        CancellationToken cancellationToken,
        bool forceRebuild = false)
    {
        var allIndexes = await GetWimIndexesAsync(imagePath, cancellationToken);
        var selected = selectedIndexes.Count == 0 ? allIndexes.ToArray() : allIndexes.Where(selectedIndexes.Contains).ToArray();
        if (!forceRebuild && selected.Length == allIndexes.Count) return;
        if (selectedIndexes.Any(index => !allIndexes.Contains(index)))
            throw new InvalidOperationException("A seleção de imagens não corresponde mais ao conteúdo da ISO.");

        if (selected.Length == 0) throw new InvalidOperationException("Nenhuma imagem foi selecionada.");

        var extension = Path.GetExtension(imagePath);
        var filteredPath = Path.Combine(Path.GetDirectoryName(imagePath)!, $"install.filtered{extension}");
        if (File.Exists(filteredPath)) File.Delete(filteredPath);
        var compression = extension.Equals(".esd", StringComparison.OrdinalIgnoreCase) ? "recovery" : "max";

        for (var position = 0; position < selected.Length; position++)
        {
            var index = selected[position];
            var percent = 72 + (position * 8 / selected.Length);
            progress.Report(new BuildProgress(percent, "Selecionando imagens de instalação", $"Exportando índice {index} ({position + 1}/{selected.Length})..."));
            await ProcessRunner.RunAsync("dism.exe",
                ["/English", "/Export-Image", $"/SourceImageFile:{imagePath}", $"/SourceIndex:{index}", $"/DestinationImageFile:{filteredPath}", $"/Compress:{compression}", "/CheckIntegrity"],
                null, cancellationToken);
        }

        File.Delete(imagePath);
        File.Move(filteredPath, imagePath);
    }

    private static int? ParseOscdimgPercent(string line)
    {
        var match = Regex.Match(line, @"(\d{1,3})% complete", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var value = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return 78 + (Math.Clamp(value, 0, 100) * 21 / 100);
    }

    private static async Task TryRunAsync(string fileName, IEnumerable<string> arguments)
    {
        try { await ProcessRunner.RunAsync(fileName, arguments, null, CancellationToken.None); } catch { }
    }

    private static async Task TryDismountIsoAsync(string sourceIso)
    {
        try { await DismountIsoAsync(sourceIso, CancellationToken.None); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private sealed record MountedIso(string Drive, bool MountedByApp);
}
