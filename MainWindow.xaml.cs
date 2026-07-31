using Microsoft.Win32;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Navigation;
using Win11IsoBypass.Services;

namespace Win11IsoBypass;

public partial class MainWindow : Window
{
    private readonly IsoBuilder _builder = new();
    private readonly ObservableCollection<InstallImageInfo> _imageOptions = new();
    private CancellationTokenSource? _cancellation;
    private string? _oscdimgPath;

    public MainWindow()
    {
        InitializeComponent();
        ImageOptionsList.ItemsSource = _imageOptions;
        _oscdimgPath = ToolLocator.FindOscdimg();
    }

    private async void ChooseSource_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecione a ISO original do Windows 11",
            Filter = "Imagem ISO (*.iso)|*.iso",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true) return;
        SourceIsoTextBox.Text = dialog.FileName;
        await LoadInstallImagesAsync(dialog.FileName);

        if (string.IsNullOrWhiteSpace(OutputIsoTextBox.Text))
        {
            var folder = Path.GetDirectoryName(dialog.FileName)!;
            var name = Path.GetFileNameWithoutExtension(dialog.FileName);
            OutputIsoTextBox.Text = Path.Combine(folder, $"{name}_unrated.iso");
        }
    }

    private async Task LoadInstallImagesAsync(string isoPath)
    {
        _imageOptions.Clear();
        ImageSelectionCard.Visibility = Visibility.Visible;
        ImageSelectionHint.Text = "Detectando imagens de instalação...";
        BuildButton.IsEnabled = false;

        try
        {
            var images = await _builder.GetInstallImagesAsync(isoPath, CancellationToken.None);
            foreach (var image in images) _imageOptions.Add(image);
            ImageSelectionHint.Text = images.Count == 0
                ? "Nenhum install.wim/install.esd foi encontrado nesta ISO."
                : $"{images.Count} imagem(ns) encontrada(s). Desmarque as edições que não deseja incluir.";
        }
        catch (Exception ex)
        {
            ImageSelectionHint.Text = "Não foi possível ler as imagens desta ISO. A seleção ficará indisponível.";
            AppendLog($"Aviso ao detectar imagens: {ex.Message}");
        }
        finally
        {
            BuildButton.IsEnabled = true;
        }
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Salvar a nova ISO",
            Filter = "Imagem ISO (*.iso)|*.iso",
            AddExtension = true,
            DefaultExt = ".iso",
            FileName = string.IsNullOrWhiteSpace(OutputIsoTextBox.Text)
                ? "Windows11_SEM_REQUISITOS.iso"
                : Path.GetFileName(OutputIsoTextBox.Text)
        };

        if (!string.IsNullOrWhiteSpace(OutputIsoTextBox.Text))
            dialog.InitialDirectory = Path.GetDirectoryName(OutputIsoTextBox.Text);

        if (dialog.ShowDialog(this) == true)
            OutputIsoTextBox.Text = dialog.FileName;
    }

    private void ChooseUnattend_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecione o arquivo unattend.xml",
            Filter = "Arquivo XML (*.xml)|*.xml|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
            CustomUnattendTextBox.Text = dialog.FileName;
    }

    private void UnattendGenerator_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Win11BypassCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = Win11BypassCheckBox.IsChecked == true;
        BypassOptionsPanel.IsEnabled = enabled;
        BypassOptionsPanel.Opacity = enabled ? 1.0 : 0.48;
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs()) return;

        var applyWin11Bypass = Win11BypassCheckBox.IsChecked == true;
        var options = new BypassOptions(
            applyWin11Bypass && TpmCheckBox.IsChecked == true,
            applyWin11Bypass && SecureBootCheckBox.IsChecked == true,
            applyWin11Bypass && RamCheckBox.IsChecked == true,
            applyWin11Bypass && CpuCheckBox.IsChecked == true,
            applyWin11Bypass && StorageCheckBox.IsChecked == true);
        var placement = (UnattendPlacement)(UnattendPlacementComboBox.SelectedIndex switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 4,
            _ => 0
        });

        SetBusy(true);
        LogTextBox.Clear();
        _cancellation = new CancellationTokenSource();

        var progress = new Progress<BuildProgress>(value =>
        {
            StatusText.Text = value.Status;
            BuildProgress.Value = value.Percent;
            PercentText.Text = $"{value.Percent}%";
            if (!string.IsNullOrWhiteSpace(value.Detail)) AppendLog(value.Detail);
        });

        try
        {
            await _builder.BuildAsync(
                SourceIsoTextBox.Text,
                OutputIsoTextBox.Text,
                _oscdimgPath!,
                options,
                string.IsNullOrWhiteSpace(CustomUnattendTextBox.Text) ? null : CustomUnattendTextBox.Text,
                placement,
                ApplyToInstallImagesCheckBox.IsChecked == true,
                _imageOptions.Where(image => image.IsSelected).Select(image => image.Index).ToArray(),
                progress,
                _cancellation.Token);

            StatusText.Text = "ISO criada com sucesso";
            BuildProgress.Value = 100;
            PercentText.Text = "100%";
            FooterHint.Text = "Concluído. A ISO original não foi alterada.";

            var result = MessageBox.Show(this,
                $"A nova ISO foi criada com sucesso:\n\n{OutputIsoTextBox.Text}\n\nDeseja abrir a pasta?",
                "Concluído", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{OutputIsoTextBox.Text}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operação cancelada";
            FooterHint.Text = "Cancelado. Os arquivos temporários foram limpos.";
            AppendLog("Operação cancelada pelo usuário.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Não foi possível criar a ISO";
            FooterHint.Text = "Ocorreu um erro. Consulte os detalhes técnicos.";
            AppendLog($"ERRO: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Erro ao criar a ISO", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelando e limpando...";
        _cancellation?.Cancel();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private bool ValidateInputs()
    {
        if (!File.Exists(SourceIsoTextBox.Text) || !SourceIsoTextBox.Text.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Selecione uma ISO válida do Windows 11.", "ISO original", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(OutputIsoTextBox.Text) || !OutputIsoTextBox.Text.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Escolha onde salvar a nova ISO.", "ISO de destino", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (Path.GetFullPath(SourceIsoTextBox.Text).Equals(Path.GetFullPath(OutputIsoTextBox.Text), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "A nova ISO deve ter um nome ou destino diferente da original.", "Destino inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (File.Exists(OutputIsoTextBox.Text))
        {
            var overwrite = MessageBox.Show(this, "A ISO de destino já existe. Deseja substituí-la?", "Confirmar substituição", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (overwrite != MessageBoxResult.Yes) return false;
        }

        if (!string.IsNullOrWhiteSpace(CustomUnattendTextBox.Text))
        {
            if (!File.Exists(CustomUnattendTextBox.Text))
            {
                MessageBox.Show(this, "O arquivo unattend.xml selecionado não existe mais.", "Arquivo de resposta", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                XDocument.Load(CustomUnattendTextBox.Text, LoadOptions.None);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"O XML selecionado não é válido:\n\n{ex.Message}", "Arquivo de resposta", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(_oscdimgPath) || !File.Exists(_oscdimgPath))
        {
            MessageBox.Show(this,
                "Não foi possível extrair o oscdimg.exe embutido no executável. Recompile o projeto ou baixe uma versão íntegra do programa.",
                "Recurso interno indisponível", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        var hasBypass = Win11BypassCheckBox.IsChecked == true &&
            (TpmCheckBox.IsChecked == true || SecureBootCheckBox.IsChecked == true || RamCheckBox.IsChecked == true || CpuCheckBox.IsChecked == true || StorageCheckBox.IsChecked == true);
        var hasImageSelection = _imageOptions.Count > 0 && _imageOptions.Any(image => image.IsSelected);
        if (!hasBypass && string.IsNullOrWhiteSpace(CustomUnattendTextBox.Text) && !hasImageSelection)
        {
            MessageBox.Show(this, "Selecione um unattend.xml, ative um bypass ou escolha as imagens que deseja manter.", "Nenhuma personalização selecionada", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (_imageOptions.Count > 0 && !_imageOptions.Any(image => image.IsSelected))
        {
            MessageBox.Show(this, "Selecione pelo menos uma imagem para incluir no ISO final.", "Nenhuma imagem selecionada", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void SetBusy(bool busy)
    {
        BuildButton.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = true;
        ProgressCard.Visibility = Visibility.Visible;
        SourceIsoTextBox.IsEnabled = !busy;
        OutputIsoTextBox.IsEnabled = !busy;
        CustomUnattendTextBox.IsEnabled = !busy;
        UnattendPlacementComboBox.IsEnabled = !busy;
        ApplyToInstallImagesCheckBox.IsEnabled = !busy;
        Win11BypassCheckBox.IsEnabled = !busy;
        BypassOptionsPanel.IsEnabled = !busy && Win11BypassCheckBox.IsChecked == true;
    }

    private void AppendLog(string text)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }
}
